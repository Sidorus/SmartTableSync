# ADR-0010: Incremental-strategy selection hierarchy, with an explicit per-table override

## Status
Accepted - updated 2026-10-17 for SqlChangeTracking ([ADR-0016](0016-implement-change-tracking-as-third-strategy.md))
and the strategy registry ([ADR-0017](0017-strategy-registry-and-template-method-runners.md)).

## Date
2026-08-01

## Context
After the first full copy, every table needs a way to find *what changed* since the last cycle.
Several mechanisms exist (a maintained datetime column, an engine-maintained `rowversion`
column, SQL Server Change Tracking, Change Data Capture, transactional replication), each with
different setup cost, reliability, and blast radius on the parent database.

## Decision
A layered, auto-detected hierarchy, checked in this order per table:
1. **Explicit override** (`SmartTableSync:IncrementalStrategyOverrides:<Table>`), if set -
   wins over everything below, but must be applicable to the table (forcing `RowVersion` on a
   table with no rowversion column throws, rather than silently producing a plan with nothing
   to query). An override to `FullRescan` counts as consent to re-scan a large table.
2. **SqlChangeTracking**, if a DBA enabled SQL Server Change Tracking on the table - a fact, and
   the strongest mechanism: deletes reported directly, versions assigned at commit
   ([ADR-0016](0016-implement-change-tracking-as-third-strategy.md)).
3. **RowVersion**, if the table has a `rowversion`/`timestamp` column - a *fact*, not a guess
   (the engine unconditionally maintains it on every write), so it wins over a configured
   datetime column whenever both exist.
4. **Timestamp**, if a configured (`SmartTableSync:TimestampColumns:<Table>`, default
   `"LastUpdate"`) datetime column exists - an operator-asserted fact, not auto-detected, since a
   column merely existing doesn't prove the application maintains it on every `UPDATE`.
5. **FullRescan**, for a small table (≤ a row-count threshold) with none of the above, or a
   table listed in `SmartTableSync:AllowFullRescanFor` - safe, since `MERGE` is idempotent.
6. Otherwise: **throw**, naming the exact setting to fix, rather than silently re-scanning a
   large table every 5 minutes.

The order lives in one place, `IncrementalStrategies.All`, and each step's applicability check
is the strategy's own `WhyNotApplicable` - used both for auto-selection and to validate an
override ([ADR-0017](0017-strategy-registry-and-template-method-runners.md)).

Why Change Tracking rather than CDC: [ADR-0014](0014-reject-cdc-prefer-change-tracking.md).
RowVersion's selection and the bugs its implementation surfaced:
[ADR-0015](0015-implement-rowversion-as-second-strategy.md). Transactional replication was
rejected outright for this project's topology -
[ADR-0003](0003-reject-transactional-replication-and-data-factory.md).

## Alternatives Considered
- **One fixed mechanism for every table.** Rejected - no single mechanism is simultaneously
  zero-setup, reliable, and safe for the parent database; different tables have different
  constraints (whether the parent schema can be touched at all, whether a datetime column is
  actually trustworthy).
- **Always require explicit configuration, no auto-detection at all.** Rejected for the
  RowVersion case specifically - a rowversion-typed column detected is a fact, not a guess;
  requiring configuration for something already provably true would be friction with no safety
  benefit. Auto-detection was explicitly rejected for the *datetime* case, for the opposite
  reason - a column merely existing under the right name doesn't prove the application
  actually keeps it current.

## Consequences
**Positive:** zero-config for the common cases (small tables, or tables with a real
`LastUpdate`/rowversion column already); a table only needs operator attention when it
genuinely doesn't fit any automatic case, and the error message says exactly what to do.

**Negative:** the priority order (override > SqlChangeTracking > RowVersion > Timestamp >
FullRescan) is a real
piece of implicit knowledge an operator must understand to predict what a given table will do -
worth documenting clearly for operators, not something this hierarchy eliminates by itself.
