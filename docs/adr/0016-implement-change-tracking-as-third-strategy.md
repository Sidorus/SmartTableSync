# ADR-0016: Implement SQL Server Change Tracking as the third incremental strategy

## Status
Accepted

## Date
2026-10-17

## Context
[ADR-0014](0014-reject-cdc-prefer-change-tracking.md) chose SQL Server Change Tracking (CT)
over CDC for the third incremental-strategy slot but left it unbuilt.
[ADR-0013](0013-position-as-general-data-sync-replacement.md) makes "any table" the project's
scope, which raises CT's value: it is the only mechanism that needs **no schema change on the
tracked table**, reports **deletes directly**, and costs a run in proportion to rows *changed*
rather than table size.

The two existing strategies each carry a workaround CT doesn't need:
- **Timestamp** trusts the application to maintain its datetime column and uses a one-minute
  overlap window against transactions that commit after stamping the column.
- **RowVersion** needs an `ALTER TABLE` on the parent plus an index, and
  `MIN_ACTIVE_ROWVERSION()` because rowversion values are assigned at *write* time.

CT versions are assigned at **commit** time from one database-wide counter, so a transaction
still running when a position is recorded always lands after that position.

## Decision
Implement CT as `IncrementalStrategy.SqlChangeTracking`:

1. **Detection**: `SchemaReader` reads `sys.change_tracking_tables`; `TableProfile` gains
   `IsChangeTrackingEnabled`, which is part of `SchemaFingerprint` only when true - enabling or
   disabling CT re-provisions the table, while every other table keeps its fingerprint.
2. **Selection** ([ADR-0010](0010-incremental-strategy-selection-hierarchy.md)): chosen
   **automatically** when CT is enabled on the table, ahead of RowVersion. CT being enabled is a
   fact a DBA established, and CT is strictly better for this engine than rowversion.
   `SmartTableSync:IncrementalStrategyOverrides:<Table>` always wins, so an operator can keep a
   table on another strategy; forcing `SqlChangeTracking` on a table without CT fails at
   provisioning.
3. **Runner**: `ChangeTrackingSyncRunner` reads `CHANGETABLE(CHANGES ...)` joined back to the
   table, page by page, each page in its own `SNAPSHOT` transaction when the database allows it
   (the default in Azure SQL Database), so the version check and the page see one point in time
   and parent writers aren't blocked. Rows whose key no longer exists are deleted on the child;
   the rest are merged. The position (`CustomSyncState.IncrementalPosition` = `CT:<version>`) is
   advanced only after the window was applied without retryable errors.
4. **Self-healing**: when the version chain is broken - no position yet; a position older than
   `CHANGE_TRACKING_MIN_VALID_VERSION` (retention exceeded, `TRUNCATE TABLE`, CT re-enabled); or a
   position **newer** than `CHANGE_TRACKING_CURRENT_VERSION()` (parent restored from a backup) -
   the runner captures the current version, runs one full re-scan **plus a delete
   reconciliation** (a re-scan only inserts and updates), and resumes from the captured version.
   The `smarttablesync.full_rescans` metric records each fallback with a reason category.
5. **Baseline at initial sync**: the current version is recorded before the initial copy starts,
   and kept when an interrupted initial copy resumes.
6. **Delete reconciliation** runs daily for CT tables instead of hourly
   (`SmartTableSync:ChangeTrackingReconcileIntervalHours`, default 24, 0 = only after a fallback
   re-scan): it is only a safety net for a failed child delete or a manual change in the child.
7. **Trust boundary unchanged**: SmartTableSync never enables CT or snapshot isolation and runs
   no DDL on the parent. A DBA enables CT and grants `VIEW CHANGE TRACKING` to the runtime
   identity; `smarttablesync-scaffold --change-tracking-setup <Table>` writes that script for review.
8. `TRACK_COLUMNS_UPDATED` stays `OFF`: the engine re-merges whole rows, so a per-update column
   bitmask would be write cost on the parent for data never read.

## Alternatives Considered
- **Leave CT unbuilt and rely on RowVersion.** Rejected: RowVersion needs an `ALTER TABLE` per
  table on the parent, which many parent owners won't allow, and leaves deletes to the periodic
  key-set diff.
- **Name the enum value `ChangeTracking`.** Rejected: the Timestamp strategy's datetime column
  was called the "change-tracking column" in code and configuration, which made the word
  ambiguous. 0.3.0 renamed that to `IncrementalColumn` / `SmartTableSync:TimestampColumns` (the
  old key is still read).
- **Use CT only when requested through an override.** Rejected: every table would need
  configuration even when CT is clearly the best choice, inconsistent with RowVersion's
  auto-detection. The override covers the opposite case - pinning a table to another strategy.
- **Turn `TRACK_COLUMNS_UPDATED` on and merge only changed columns.** Deferred: only worth it for
  very large binary tables, and it needs partial-column MERGE procedures.
- **CDC.** Rejected in [ADR-0014](0014-reject-cdc-prefer-change-tracking.md) (transaction-log
  holdup risk).

## Consequences
**Positive:**
- No schema change on the parent table; deletes arrive within one run.
- Commit-time versions need no long-running-transaction workaround.
- The read cost of a run scales with changed rows - a large reduction for tables that would
  otherwise be `FullRescan`.
- No child schema change (reuses `CustomSyncState.IncrementalPosition`).

**Negative:**
- Every write to a tracked table pays a small synchronous cost on the parent (one side-table row
  per change), comparable to maintaining one more nonclustered index. The DBA who enables CT
  makes that trade-off.
- Requires parent-side DBA action: database- and table-level enable, the `VIEW CHANGE TRACKING`
  grant, and outside Azure SQL Database, `ALLOW_SNAPSHOT_ISOLATION` (recommended).
- Requires a `PRIMARY KEY` constraint - as every strategy does in this engine.
- CT's automatic cleanup can fall behind on very busy tables (a documented SQL Server issue);
  SmartTableSync would see it as slower `CHANGETABLE` reads.
