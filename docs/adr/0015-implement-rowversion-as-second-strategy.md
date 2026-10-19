# ADR-0015: Implement Rowversion as the second incremental strategy

## Status
Accepted

## Date
2026-08-01

## Context
Strategy 1 ([ADR-0010](0010-incremental-strategy-selection-hierarchy.md)) depends entirely on
trusting that the parent application actually maintains its tracking column on every `UPDATE` -
a bare `DEFAULT` constraint only fires on `INSERT`, and SmartTableSync has no way to verify from
outside whether a datetime column is genuinely kept current or was just named right. A
stronger mechanism was wanted: one where "the column exists and is the right type" is itself
sufficient proof of correctness, no trust required.

## Decision
Implement a `rowversion`/`timestamp` column as Strategy 2, auto-detected via
`TableProfile.RowVersionColumn` and **preferred over Strategy 1 whenever present** - unlike
Strategy 1's column, detecting a `rowversion`-typed column is a fact, not a guess: SQL Server
itself unconditionally maintains it on every write, with no way for an application to opt out
or forget to update it.

## Alternatives Considered
- **Keep only Strategy 1, improve trust some other way** (e.g. a periodic consistency check).
  Rejected - no practical way to *verify* a datetime column is actually maintained short of
  comparing against a known-correct source, which defeats the purpose of a lightweight sync
  tool; a trigger-enforced column was considered and rejected too, since SmartTableSync
  installing triggers on the parent's own tables is a much bigger footprint than an operator
  adding one column.
- **Skip straight to Change Tracking instead of rowversion.** Rejected at the time: CT requires
  a database-level admin action to enable (`ALTER DATABASE ... SET CHANGE_TRACKING = ON`) that
  the *parent* DBA must grant, whereas rowversion only needs an `ALTER TABLE` the same operator
  already has authority to run themselves - a lower-friction, incrementally-adoptable first step
  toward zero-schema-change mechanisms. (CT remains planned as Strategy 3 - see
  [ADR-0014](0014-reject-cdc-prefer-change-tracking.md) for that decision.)

## Consequences
**Positive:** once a table has the column, its incremental correctness no longer depends on
trusting application code at all - engine-guaranteed, cheap to detect (one catalog query), tiny
storage footprint (8 bytes/row).

**Negative - real correctness bugs surfaced during implementation, not in the original
comparison, each caught by [ADR-0012](0012-verify-against-real-disposable-sql-server.md)'s
failing-test-first discipline before shipping:**
- SQL Server rejects any explicit value written into a rowversion column, via `INSERT`,
  `UPDATE`, **and** as a TVP column - the generated `MERGE`/TVP/bulk-copy paths all had to
  exclude it explicitly, discovered via a real SQL error (200/544) the first time it was tried.
- The incremental query needed `>=`, not `>`, against the persisted position - an off-by-one
  against `MIN_ACTIVE_ROWVERSION()`'s exact semantics (it returns the value the *next* write
  will receive when nothing is in flight, so the very first write after a position was captured
  can land exactly on it).
- Requires an actual `ALTER TABLE` against the **parent** database - a real schema change
  SmartTableSync never automates (consistent with
  [ADR-0009](0009-manual-default-auto-opt-in-ddl-provisioning.md)'s "never touch parent schema"
  stance); adding it to a table that already has rows is a one-time, size-of-data cost worth
  planning an off-peak window for on a large table. `SmartTableSync.Scaffold --rowversion-setup`
  generates the script; applying it is still entirely manual.
- A table can have only one rowversion column - if already used for app-level optimistic
  concurrency, sync must reuse that same column rather than adding a second.
