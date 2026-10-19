# ADR-0021: Key tables without a primary key by a unique index or configured columns

## Status
Accepted

## Date
2026-10-19

## Context
Every part of a sync is driven by a table's key: the `MERGE` match in the generated procedures,
keyset paging (`KeysetPredicateBuilder`), the resume key of the initial copy (`CompositeKey`),
delete reconciliation (`ChildRowDeleter`) and the child table's own `PRIMARY KEY`. The key was
read only from the `PRIMARY KEY` constraint (`sys.key_constraints` type `PK`), so a table without
one couldn't be synced: it failed in its first page query with an error about a missing primary
key column. Such tables are common in databases another team owns: heaps with a unique index that
plays the key's role, or staging tables whose rows are identified by a column combination nobody
declared.

## Decision
- **The sync key is chosen in this order** (`SyncKeyResolver`, pure and unit-tested):
  1. The `PRIMARY KEY` constraint. Unchanged for every table that has one.
  2. The columns in `SmartTableSync:KeyColumns:<Table>` (`"ColA,ColB"`, in key order), for a table
     without a primary key. An explicit choice wins over an inferred one, so it also picks the
     key when the table has a unique index.
  3. A unique index whose key columns are all `NOT NULL` and not `rowversion`, that isn't
     filtered, disabled or hypothetical; the fewest key columns win, then the lowest `index_id`,
     so the choice is stable. Included columns don't count.
- **Configured columns are validated where the table's columns are known**, when the table is
  profiled: they must exist, be listed once, be `NOT NULL`, and not be `(max)` or `rowversion`
  columns. All problems are reported in one message. Setting `KeyColumns` for a table that has a
  primary key fails instead of being ignored: a setting that silently does nothing hides a
  mistake.
- **Uniqueness of configured columns is checked against the data once per provisioning**: when the
  full profile is read (new table or changed schema), `SELECT TOP (1) 1 ... GROUP BY <key> HAVING
  COUNT(*) > 1` runs on the parent and a hit stops the table before anything is created in the
  child. The check isn't repeated on every run, because it scans the table.
- **The child gets the key as its `PRIMARY KEY`**, whatever its source. The rest of the engine
  doesn't distinguish the sources: `TableProfile.PrimaryKeyColumns` remains "the key".
- **Change Tracking needs a real primary key.** SQL Server won't enable it without one and reports
  changes by the primary key columns, so `SqlChangeTrackingStrategy` and the Change Tracking setup
  script refuse a key from any other source. RowVersion, Timestamp and full re-scan work as before.
- **Where the key came from is part of the schema fingerprint**, appended only when it isn't the
  primary key (`|KeySource:UniqueIndex` / `|KeySource:Configured`). Tables with a primary key keep
  the exact fingerprint they had, so nothing re-provisions on upgrade, while a table that later
  gains a primary key over the same columns is re-provisioned and can move to Change Tracking.
- **A table with no key at all stops at provisioning** (`SyncPlanHeuristic`) with a message that
  names the setting, instead of failing in the first page query.

## Alternatives Considered
- **Sync heaps without any key (`EXCEPT`-based diffs, or replacing the child table on every
  run).** Rejected: no incremental update, no resumable initial copy, and a full copy of every
  row on each run, which is what this engine exists to avoid.
- **Use the clustered index, or any unique index, regardless of nullability.** Rejected: a `NULL`
  key value never satisfies the keyset predicate (`NULL > x` is unknown), so such rows would be
  skipped silently, and the child's `PRIMARY KEY` can't contain nullable columns.
- **Prefer the unique index over configured columns.** Rejected: an operator who sets the key has
  a reason (for example a narrower or more stable index), and should not have to drop an index to
  be heard.
- **Check configured columns for duplicates on every run.** Rejected: it scans the whole parent
  table each time. The one-time check catches a wrong choice of columns, which is the common
  mistake; duplicates created later are a data problem the parent's owner has to fix.
- **Store the key source in `dbo.SyncTableConfig`.** Rejected: it is read from the parent on every
  run anyway, and adding a column would need a migration of the tracking table for no gain.

## Consequences
- Heaps with a suitable unique index sync without any configuration; others need one setting.
- Duplicates that appear in configured key columns after provisioning aren't detected: the child
  keeps one row per key value, and a batch holding both rows can fail. This is documented.
- Changing `KeyColumns`, or the index the key came from, changes the fingerprint; the child's
  primary key then differs, which Auto mode and the scaffold tool already report as a change that
  needs a person (ADR-0019).
- `ISchemaReader.GetFingerprintInputsAsync` and `ProfileTableAsync` take the configured key
  columns; reading a child table passes none, since the child's key is always its primary key.
- Tests: unit tests for the resolver rules, the fingerprint, the settings binding, the heuristic
  and the generated scripts; least-privilege Auto mode integration tests for a heap keyed by its
  best unique index, a heap keyed by configured columns, configured columns with duplicates, and
  a table with no key.
