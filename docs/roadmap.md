# Roadmap

What is planned next. **Nothing on this page is built yet**; what works today is in the
[README](../README.md), and the open problems are under
[known limitations](limitations.md). Order is a priority, not a promise of dates.

## 1. Fixes for open problems

Data correctness and scale come first.

| Priority | Problem today | Planned fix |
|---|---|---|
| 1 | The delete check loads every child key and every parent key into memory, then deletes missing rows one at a time. On tables with tens of millions of rows that is a lot of memory for a small host and a very slow delete. | Bulk-copy the parent keys into a temporary table on the child connection and delete the missing rows with one set-based statement, in batches. Temporary tables need no extra rights. |
| 2 | Timestamp strategy: a change committed more than a minute after its datetime column was set is never copied (see [limitations](limitations.md#change-detection)). | A per-table overlap setting, and an optional periodic full re-scan of Timestamp tables as a safety net. |
| 3 | The run lock's connection is idle during a run, and Azure SQL Database can close idle connections after about 30 minutes, so two very long runs could overlap: correct data, double work. | Keep the lock connection active between tables, and check that the lock is still held before each table. |
| 4 | The time of the last delete check is kept in memory, so a host that scales to zero repeats the full key comparison on most cold starts. | Store it per table in `CustomSyncState`, so every host instance and restart sees the same time. |
| 5 | The delete check compares keys ignoring case. With a case-sensitive collation, `abc` and `ABC` are different rows, so a deleted one can stay in the child. | Compare in SQL (fix 1), so the column's collation decides. |
| 6 | Change Tracking applies deletes one row at a time: deleting 100,000 rows in the parent means 100,000 round trips to the child. | Delete a page's keys in one statement, and fall back to one row at a time only when that fails. |
| 7 | Changing `IncrementalStrategyOverrides`, `TimestampColumns`, `AllowFullRescanFor` or `KeyColumns` for a table that is already set up has no effect until its schema changes. | Include those settings in what decides that a table must be planned again. |
| 8 | In Manual mode a new plan is saved before the reviewed child scripts are applied, so the table can fail until they are. | Check that the child matches the plan before saving it, and wait with a clear message until it does. |
| 9 | In Manual mode, rows synced before a column was added get its value only when they next change. | Re-scan the table once, automatically, after the child gains the column. |

## 2. Trust: prove the copy is right

| Feature | What it gives |
|---|---|
| **Verify command** | A Scaffold command (for example `verify Product`) that compares row counts and checksums per key range between parent and child and prints the first ranges that differ. Can also run on a schedule and publish a metric, so drift raises an alert. |
| **Repair rows that failed to write** | Today a row that can't be written (other than a foreign-key timing error) is logged and skipped, and stays out of date until it changes again. Planned: record failed keys in a small `SyncFailedRow` table, read them again on the next runs, and report how many are waiting as a metric. |
| **Migrating from Azure SQL Data Sync** | A step-by-step guide for teams moving off Data Sync before it retires on 30 September 2027: what maps to what, and how to switch over table by table. |
| **1.0 release** | A stated compatibility policy for settings and the child's tracking tables, so an upgrade never needs manual changes within a major version. |

## 3. Control API

Today the sync is controlled through settings and the host's schedule. Planned: an HTTP API to
see what the sync is doing and act on it, as a separate package (`SmartTableSync.Control`) that
any ASP.NET Core host adds with `AddSmartTableSyncControl()` and `MapSmartTableSyncControl()`.

| Endpoint | What it does |
|---|---|
| `GET /sync/tables` | Every table with its strategy, merge strategy, key, last successful sync, lag, last error and whether it is paused |
| `GET /sync/tables/{table}` | One table in detail, with its recent batches and runs |
| `GET /sync/runs` | Recent runs: start, end, rows copied, deleted and failed per table |
| `POST /sync/run` | Run now, for all tables or a list of tables |
| `POST /sync/tables/{table}/pause` and `/resume` | Skip a table on every run until it is resumed |
| `POST /sync/tables/{table}/rescan` | Read the whole table once on the next run |
| `POST /sync/tables/{table}/reset-plan` | Pick the strategy again with the current settings (today: delete its `SyncTableConfig` row by hand) |
| `POST /sync/tables/{table}/retry-failed` | Read the rows that failed to write again (needs the repair feature in section 2) |
| `GET /sync/settings` | The settings in effect, without connection strings |

How it fits the existing design:

- **Commands go through the child database.** The API writes a request (run now, rescan, pause)
  into a `SyncControlCommand` table, and the engine picks it up at the start of its next run, or
  right away when the host reacts to it. That way the API works with any host, including a timer
  Function that isn't running between runs, and several host instances never act on the same
  command twice.
- **Least privilege stays.** The API gets its own identity, with read access to the tracking
  tables and write access to the command table only. It never needs DDL rights or access to the
  parent.
- **Signed-in users only.** Microsoft Entra ID sign-in, with two roles: *Reader* (all `GET`
  endpoints) and *Operator* (also the actions). Every action is written to an audit table: who,
  what, when.
- The existing metrics stay the source for dashboards and alerts; the API adds per-table state
  that metrics can't show.

## 4. Web page

A web page on top of the control API, for people who don't want to call an API:

- **Overview:** every table with a status badge (in sync, behind, failing, paused), lag, last
  sync and rows copied in the last hour.
- **Table page:** strategy and why it was picked, key columns, schema changes seen, recent
  batches and errors, rows waiting for a retry.
- **Actions with a confirmation step:** run now, pause and resume, re-scan, reset the plan,
  retry failed rows. Shown only to *Operators*.
- **History:** runs and errors over time, from the same data as the API.

Planned as a small ASP.NET Core + Blazor app that can be hosted next to the sync or on its own,
with the same Entra ID sign-in and roles as the API.

## 5. Other planned work

- **Row filters and column selection:** copy only some rows (a `WHERE` condition per table) or
  some columns, as Data Sync allows.
- **Alerts and dashboards out of the box:** example Azure Monitor alert rules and a Grafana
  dashboard for lag, failed rows and runs that stop, using the existing `smarttablesync.*`
  metrics ([observability](observability.md) already shows the queries).
- **More column types:** `xml`, `sql_variant`, `geography`, `geometry`, `hierarchyid` and
  `image`.
- **One parent, several children:** sync the same parent tables into more than one child database
  from one host.
- **A different schema in the child:** map `sales.Order` in the parent to, for example,
  `copy.Order` in the child.
- **CodeQL in CI**, now that the repository is public (Semgrep runs today).
- **Release checks:** run the [Azure demo](../demo/README.md) end to end on every release.

Two-way sync is not planned: the child is a read-only copy by design.
