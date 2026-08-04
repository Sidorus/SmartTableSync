# ADR-0011: Persist per-table config with hand-set overrides that survive re-provisioning

## Status
Accepted

## Date
2026-07-18

## Context
A container/serverless host can't rely on local files surviving a redeploy or cold start, so
the heuristic-derived `SyncPlan` (merge strategy, batch size, timeout, retryable errors) needs
somewhere durable to live — and needs to tolerate the heuristic occasionally getting a table
wrong, without an operator's manual correction being silently clobbered the next time the
schema is re-profiled.

## Decision
Persist one row per table in `dbo.SyncTableConfig` (child database), keyed by table name, with
a schema fingerprint (`SchemaFingerprint.Compute` — a hash of columns/types/PK/identity) used to
detect drift cheaply on every check without re-running the expensive full profile every time.
Alongside the heuristic-derived values, add nullable *override* columns
(`MergeStrategyOverride`, `BatchSizeOverride`, etc.) that an operator can hand-set directly in
the database; `SyncTableProvisioner` always preserves existing overrides across re-provisioning
instead of overwriting them with a fresh heuristic guess.

## Alternatives Considered
- **Recompute the plan from scratch on every cold start, no persistence.** Rejected — expensive
  (full schema profiling includes row/size sampling) to redo constantly, and gives an operator
  no way to correct a wrong heuristic choice that survives the next restart.
- **A local config file.** Rejected — doesn't survive a container/Function redeploy or scale-out
  to multiple instances; the child database is the one thing guaranteed to persist and be
  shared.

## Consequences
**Positive:** an operator who disagrees with the heuristic for a specific table has a real,
durable escape hatch that isn't clobbered by normal operation; the fingerprint check keeps the
common case (no schema drift) cheap.

**Negative:** this pattern and [ADR-0010](0010-incremental-strategy-selection-hierarchy.md)'s
`SmartTableSync:IncrementalStrategyOverrides` setting are now two structurally different
override mechanisms for conceptually the same "operator overrides the heuristic" idea — one
lives in a database row hand-edited directly, the other in app configuration, with different
edit workflows and no cross-reference pointing a reader from one to the other. A real,
identified inconsistency; unifying them (or at least documenting the split explicitly wherever
either is described) is future work, not resolved by this ADR.
