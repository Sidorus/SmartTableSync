# ADR-0007: Keyset pagination instead of `OFFSET` for initial sync

## Status
Accepted

## Date
2026-07-18

## Context
Initial sync bulk-copies a table from empty, potentially millions of rows, across multiple
batches. A crash, timeout, or Function cold-start restart mid-load needs to resume without
starting over from row 1 — restarting a large initial load from scratch on every transient
failure is untenable.

## Decision
Use keyset pagination (`WHERE [Key] > @LastKey ORDER BY [Key]`) with the resume key
checkpointed in `SyncBatchLog` after every completed batch, never `OFFSET N ROWS FETCH NEXT`.
Generalized via `KeysetPredicateBuilder`/`CompositeKey` to work for any number of ordered
primary-key columns (single or composite), not just the original hand-written 2-column case.

## Alternatives Considered
- **`OFFSET`/`FETCH NEXT` pagination.** Rejected — `OFFSET` re-scans and discards every row
  before the offset on each page, getting progressively slower as the offset grows, and a
  resumed load after a crash still has to re-count from row 1 to find where it left off.
  Keyset pagination jumps straight to the resume position via an index seek regardless of how
  far into the table it is.

## Consequences
**Positive:** resuming a crashed/timed-out initial load is O(remaining rows), not O(table size),
and stays that way at any table size. Works uniformly for single and composite keys via the
same generalized builder.

**Negative:** requires the primary key (or an equivalent unique, ordered key) to be usable as a
seek predicate — not a concern for any table this project targets (all have a real PK), but a
structural assumption worth naming.
