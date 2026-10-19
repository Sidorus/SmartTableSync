# Known limitations

What SmartTableSync doesn't do yet, or does with a catch. What changed and which problems were
fixed is in the [changelog](../CHANGELOG.md); what's planned is in the [roadmap](roadmap.md).

## Scope

- **One-way only, parent → child.** The child is a read-only copy; rows written directly into it
  are overwritten or removed by the next sync.
- **Every synced table needs a key.** Its primary key; without one, the columns named in
  `SmartTableSync:KeyColumns:<Table>` (`NOT NULL`, unique together), or else a unique index whose
  key columns are all `NOT NULL` (not filtered, not disabled). Configured columns are checked for
  duplicates only when the table is set up or its schema changes; duplicates that appear later
  aren't caught: the child keeps one row per key value, and a batch that holds both rows can
  fail. Change Tracking still needs a real primary key
  ([setup](setup.md#tables-without-a-primary-key)). A table with no unique columns at all
  (duplicate rows possible) can't be synced yet; a whole-table copy for it is
  [planned](roadmap.md).
- **Same schema in the child.** Tables can be in any schema (`sales.Order`), and the child keeps
  the parent's schema, so a parent table can't be copied into a different schema.
- **Column types.** Columns of type `xml`, `sql_variant`, `geography`, `geometry`, `hierarchyid`
  or `image` aren't supported; key columns must be numeric, string, date/time or
  `uniqueidentifier`.

## Change detection

- **Timestamp strategy:** a row is only seen as changed when the application updates its datetime
  column on every `UPDATE` (a column default fires only on `INSERT`). Rows where that column is
  `NULL` are copied by the first sync but not picked up as changed later. The column must hold
  **UTC** time (for example `SYSUTCDATETIME()`): it is compared with the parent's UTC clock, so
  local time in a time zone behind UTC makes changes look older than they are and they are missed.
- **Deletes** reach the child on every run only for Change Tracking tables. RowVersion, Timestamp
  and full re-scan tables get them from the delete reconciliation: by default at most once an
  hour per host process, and also on the first run after the host starts. On a host that scales
  to zero, that means a full key comparison of those tables on most cold starts. See
  [delete reconciliation](sync-strategies.md#delete-reconciliation).
- **Skipped rows.** A row that can't be written into the child for any reason other than a
  foreign-key timing error is logged and skipped. It is corrected only when it changes again in
  the parent (a full re-scan table: on the next run), so watch the error logs or the
  `smarttablesync.rows_errored` metric.
- **The strategy is chosen once.** A table's strategy is picked when it is set up. Changing
  `IncrementalStrategyOverrides`, `TimestampColumns` or `AllowFullRescanFor` later has no effect
  until the parent table's schema changes or you
  [reset its plan](how-it-works.md#changing-a-tables-strategy-later).

## Schema changes

- **Auto mode** adds new parent columns by itself, but these need a reviewed script, and until
  then that table stops with an error:
  - a changed column type or length;
  - a changed primary key;
  - a removed `NOT NULL` column;
  - a column that now allows `NULL` in the parent but not in the child;
  - a different precision or scale of a `decimal` or `numeric` column.
- In Auto mode, a schema change costs **one full re-scan** of that table.
- In **Manual mode**, rows synced before a column was added get its value only when they next
  change.

## Security and setup

- Auto mode's runtime identity needs **schema-level grants** on the child (broader than
  per-object grants), so use a child database dedicated to the sync.
- Child tables created **with** `IDENTITY` by an earlier version still need `ALTER` for the
  runtime identity until they're recreated from the scaffold tool's script.
- Before turning a child into a writable database (for example in disaster recovery), add
  `IDENTITY` or a sequence back to its key columns: child tables are created without them.
- The [quick start](../quickstart/README.md) covers Azure SQL Database with Microsoft Entra
  sign-in and public endpoints only; other setups follow the [setup guide](setup.md).

## Not yet verified

- Whether the run lock's connection, which stays idle during a run, survives a run longer than
  30 minutes on Azure SQL Database (idle connections can be closed after about 30 minutes). If
  it doesn't, two runs could overlap: the data stays correct, because every write is an
  idempotent `MERGE`, but the work is done twice.
- The Hangfire and Quartz.NET [hosting examples](guides/hosting.md) haven't been compiled in a
  real host.
