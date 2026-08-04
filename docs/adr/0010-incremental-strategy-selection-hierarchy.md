# ADR-0010: Incremental-strategy selection hierarchy, with an explicit per-table override

## Status
Accepted

## Date
2026-08-01

## Context
After the first full copy, every table needs a way to find *what changed* since the last cycle.
Several mechanisms exist (a maintained datetime column, an engine-maintained `rowversion`
column, SQL Server Change Tracking, Change Data Capture, transactional replication), each with
different setup cost, reliability, and blast radius on the parent database.

## Decision
A layered, auto-detected hierarchy, checked in this order per table:
1. **Explicit override** (`SmartTableSync:IncrementalStrategyOverrides:<Table>`), if set —
   wins over everything below, but validated against the table's real columns (forcing
   `RowVersion` on a table with no rowversion column throws, rather than silently producing a
   plan with nothing to query).
2. **RowVersion**, if the table has a `rowversion`/`timestamp` column — a *fact*, not a guess
   (the engine unconditionally maintains it on every write), so it wins over a configured
   datetime column whenever both exist.
3. **Timestamp**, if a configured (or default `"LastUpdate"`) datetime column exists — an
   operator-asserted fact, not auto-detected, since a column merely existing doesn't prove the
   application actually maintains it on every `UPDATE`.
4. **FullRescan**, for a small table (≤ a row-count threshold) with neither of the above — safe
   and free of friction, since `MERGE` is already idempotent everywhere.
5. Otherwise: **throw**, naming the exact setting to fix, rather than silently re-scanning a
   large table every 5 minutes.

Change Tracking (a future 5th option) is specced but not implemented — see
[ADR-0014](0014-reject-cdc-prefer-change-tracking.md) for why it, not CDC, is the mechanism
chosen for that slot. RowVersion's own selection over alternatives (and the correctness bugs
its implementation surfaced) is covered in
[ADR-0015](0015-implement-rowversion-as-second-strategy.md). Transactional replication was
rejected outright for this project's topology, independent of this hierarchy — see
[ADR-0003](0003-reject-transactional-replication-and-data-factory.md).

## Alternatives Considered
- **One fixed mechanism for every table.** Rejected — no single mechanism is simultaneously
  zero-setup, reliable, and safe for the parent database; different tables have different
  constraints (whether the parent schema can be touched at all, whether a datetime column is
  actually trustworthy).
- **Always require explicit configuration, no auto-detection at all.** Rejected for the
  RowVersion case specifically — a rowversion-typed column detected is a fact, not a guess;
  requiring configuration for something already provably true would be friction with no safety
  benefit. Auto-detection was explicitly rejected for the *datetime* case, for the opposite
  reason — a column merely existing under the right name doesn't prove the application
  actually keeps it current.

## Consequences
**Positive:** zero-config for the common cases (small tables, or tables with a real
`LastUpdate`/rowversion column already); a table only needs operator attention when it
genuinely doesn't fit any automatic case, and the error message says exactly what to do.

**Negative:** the priority order (override > RowVersion > Timestamp > FullRescan) is a real
piece of implicit knowledge an operator must understand to predict what a given table will do —
worth documenting clearly for operators, not something this hierarchy eliminates by itself.
