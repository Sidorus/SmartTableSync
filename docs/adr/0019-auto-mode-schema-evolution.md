# ADR-0019: Auto mode evolves the child schema for added columns, and nothing riskier

## Status
Accepted

## Date
2026-10-18

## Context
Auto mode created a missing child table, table type and procedure, but on a later parent schema
change it changed nothing except the procedure. After a column was added to the parent, the
regenerated procedure referenced a column the child table didn't have, so the table failed on
every run, and the table type kept its old shape because a table type can't be altered.
The new plan was also saved before the child DDL ran: once the DDL failed, the next run found a
matching fingerprint, skipped the DDL, and never retried it. And even with the column added,
rows synced before the change would have kept `NULL` in it, because incremental strategies only
copy rows that change.

## Decision
- **Plan, then save.** `ISyncTableProvisioner` derives a new plan (`PlanAsync`) without saving
  it; `SyncOrchestrator` saves it (`SaveAsync`) only after Auto mode's child DDL and re-scan
  succeeded. A failure leaves the old plan, so the next run detects the change again and repeats
  the steps, all of which are idempotent.
- **Compare, then evolve only what is safe.** `ChildSchemaComparer` (pure, unit-tested) compares
  the parent with the existing child table. Added parent columns are added to the child as
  nullable. A nullable child column the parent no longer has is left in place. A changed type or
  length, a changed primary key, a removed `NOT NULL` column or a column that now allows `NULL`
  only in the parent stops the table with an error naming the change.
- **Recreate an out-of-date table type.** When the type's columns differ, by name, type,
  nullability or position (SqlClient maps a table-valued parameter by position), its procedure
  and the type are dropped and created again.
- **Re-scan once after a schema change.** A table that was already synced runs the FullRescan
  runner once before its normal pass, so every existing row is merged with the new shape. It runs
  for every detected change, not only an added column, so a crash between adding the column and
  finishing the re-scan can't leave rows unfilled: the unsaved plan makes the next run re-scan.
- The scaffold tool uses the same comparison in Manual mode and writes `<Table>_AddColumns.sql`
  and a drop-and-recreate table type script for review.

## Alternatives Considered
- **Recreate only the table type.** Rejected: the procedure still references the missing column,
  so a column change keeps failing.
- **Apply every change automatically (`ALTER COLUMN`, drop removed columns).** Rejected: a
  narrowed type or a dropped column can lose data; those stay a reviewed, human decision.
- **Re-scan only when columns were added in the current run.** Rejected: after a failure between
  adding the column and the re-scan, the retry finds no column to add and would skip the re-scan.

## Consequences
- Adding a parent column needs no manual step in Auto mode.
- A schema change costs one full pass over that table, which matters only for very large tables.
- Risky changes stop one table with a clear, repeating error; other tables keep syncing.
- Manual mode still relies on a person applying the scripts; rows synced before an added column
  get its values when they next change.
