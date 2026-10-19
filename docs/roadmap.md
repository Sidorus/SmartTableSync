# Roadmap

What is planned next. **Nothing on this page is built yet**; what works today is in the
[README](../README.md), and the open problems are under
[known limitations](../README.md#known-limitations). Order is a priority, not a promise of dates.

## 1. Fixes for open problems

| Priority | Problem today | Planned fix |
|---|---|---|
| 1 | The run lock's connection is idle during a run; whether it survives more than 30 minutes on Azure SQL Database is unverified. Two runs could then overlap: correct data, double work. | Keep the lock connection active between tables, check that the lock is still held before each table, and add a long-run test. |
| 2 | The time of the last delete check is kept in memory, so a host that scales to zero repeats the full key comparison on most cold starts. | Store it per table in `CustomSyncState`, so every host instance and restart sees the same time. |
| 3 | A row that can't be written (other than a foreign-key timing error) is logged and skipped, and stays out of date until it changes again. | Record failed keys in a small `SyncFailedRow` table, read them again on the next runs, and report how many are waiting as a metric. |
| 4 | Changing `IncrementalStrategyOverrides`, `TimestampColumns`, `AllowFullRescanFor` or `KeyColumns` for a table that is already set up has no effect until its schema changes. | Include those settings in what decides that a table must be planned again. |
| 5 | In Manual mode a new plan is saved before the reviewed child scripts are applied, so the table can fail until they are. | Check that the child matches the plan before saving it, and wait with a clear message until it does. |
| 6 | In Manual mode, rows synced before a column was added get its value only when they next change. | Re-scan the table once, automatically, after the child gains the column. |

## 2. Tables with no unique columns

A table with no primary key, no unique index and no columns that are unique together can't be
synced today. Planned: a **Snapshot** strategy, chosen only for such tables and only within the
same size limit as a full re-scan (50,000 rows, or listed in `AllowFullRescanFor`). Each run, in
one transaction on the child: delete all rows, bulk-copy every parent row, commit. Readers keep
seeing the previous copy until the commit, duplicate rows come across as they are, and deletes
need no separate check. It needs only `DELETE` and `INSERT` for the runtime identity, so the
least-privilege model stays as it is.

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
| `POST /sync/tables/{table}/retry-failed` | Read the rows that failed to write again (needs fix 3) |
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

- **Alerts and dashboards out of the box:** example Azure Monitor alert rules and a Grafana
  dashboard for lag, failed rows and runs that stop, using the existing `smarttablesync.*`
  metrics ([observability](observability.md) already shows the queries).
- **More column types:** `xml`, `sql_variant`, `geography`, `geometry`, `hierarchyid` and
  `image`.
- **One parent, several children:** sync the same parent tables into more than one child database
  from one host.
- **A different schema in the child:** map `sales.Order` in the parent to, for example,
  `copy.Order` in the child.
- **Release:** publish the `SmartTableSync` and `SmartTableSync.Scaffold` packages to NuGet, and
  run the [Azure demo](../demo/README.md) end to end on every release.

Two-way sync is not planned: the child is a read-only copy by design.
