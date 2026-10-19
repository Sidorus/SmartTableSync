# ADR-0005: Replace hand-written per-table syncers with an introspection + heuristic-driven generic engine

## Status
Accepted

## Date
2026-07-18

## Context
The original engine was three hand-written subclasses (`ProductAttributeValueSyncer`,
`ProductCatalogItemSyncer`, `DocumentAssetSyncer`), each with its own hardcoded SQL, column
list, and batch size. Adding a fourth table meant writing a fourth hand-authored class - no
scaling path, and no way for a stranger to "just give it a table name."

## Decision
Replace all three with one generic `ConfigurableTableSyncer`, driven by a `TableProfile`
(introspected schema: columns/types, PK, identity, incoming FKs, row/LOB sizing) and a
`SyncPlan` (merge strategy, batch size, timeout, retryable errors - all derived by a pure
`SyncPlanHeuristic` function from the profile, not hand-declared). `SchemaReader` queries
`sys.columns`/`sys.indexes`/`sys.key_constraints`/`sys.identity_columns`/`sys.foreign_keys`/
`sys.dm_db_partition_stats` to build the profile.

## Alternatives Considered
- **Keep hand-writing one class per table.** Rejected - the actual goal ("give it a table
  name, it figures out the rest") is unreachable this way; every new table is a code change.
- **Fully dynamic, no heuristic at all - always use the same strategy for every table.**
  Rejected - the three original tables already needed genuinely different merge strategies for
  different reasons (FK-timing isolation vs. large-row isolation), so a single fixed strategy
  would be wrong for at least one of the three known cases on day one.

## Consequences
**Positive:** adding a table is now "introspect it, derive a plan, generate DDL" - zero
hand-written sync code per table. The heuristic is a pure function, fully unit-testable without
a database. See [ADR-0006](0006-per-table-merge-strategy-selection.md) for the specific merge-
strategy rule this heuristic derives.

**Negative:** a heuristic can guess wrong for an unusual table shape - mitigated by
[ADR-0011](0011-persisted-per-table-config-with-overrides.md)'s hand-set override mechanism,
not by making the heuristic itself more complex.
