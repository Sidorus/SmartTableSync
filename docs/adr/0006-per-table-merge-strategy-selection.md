# ADR-0006: Derive merge strategy (TVP vs row-by-row) per table instead of a fixed global choice

## Status
Accepted

## Date
2026-07-18

## Context
Merging a batch of changed rows into the child table can be done as one table-valued-parameter
(TVP) call per batch (efficient, but one bad row fails the *whole* batch) or one stored-procedure
call per row (slower, but isolates a single bad row). The three original example tables actually
needed different answers for different reasons: `ProductAttributeValue` has an FK to a
Data-Sync-managed table and can hit a timing race (SQL error 547) that only isolates cleanly
row-by-row; `DocumentAsset` has no FK risk at all but rows can reach ~50 MB, where one huge row
failing shouldn't sink an entire batch either.

## Decision
`SyncPlanHeuristic` picks `RowByRow` when a table **either** has incoming foreign keys **or**
its max observed row size exceeds a threshold - **else** `Tvp`. A rule keyed on FK presence
alone would have picked `Tvp` for `DocumentAsset` (no FKs) and been wrong; this was caught and
fixed before it shipped, not discovered as a production bug.

## Alternatives Considered
- **Always TVP.** Rejected - reintroduces the exact FK-timing and oversized-row failure modes
  the original hand-written `DocumentAsset`/`ProductAttributeValue` syncers already had to solve
  individually.
- **Always row-by-row.** Rejected - throws away the batching efficiency `ProductCatalogItem`
  (no FK risk, small rows) doesn't need to give up.
- **FK presence alone as the sole rule.** Rejected - the regression case above; row size matters
  independently of FK presence.

## Consequences
**Positive:** each table gets the isolation level it actually needs, derived from real schema
facts (FK graph, observed row size) rather than a guess, with a regression test locking in the
two-condition rule specifically.

**Negative:** an operator who disagrees with the heuristic's choice for a specific table needs
the override mechanism ([ADR-0011](0011-persisted-per-table-config-with-overrides.md)) - the
heuristic itself has no manual escape hatch built in.
