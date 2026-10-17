# ADR-0017: Strategy registry, template-method runners, and a minimal public API

## Status
Accepted

## Date
2026-10-17

## Context
By 0.2.0 the engine had four incremental strategies, and knowledge about each was spread across
the code: the selection `if` chain and a separate override validation in `SyncPlanHeuristic`, a
`switch` in `IncrementalRunnerFactory`, a special case in `TableSyncerFactory` for Change
Tracking's baseline, and four runners that each re-implemented the same steps (read, merge,
advance the position only on success). Adding a fifth strategy meant editing about six places,
and the runners read the whole incremental window into one batch. Nearly every type was public,
so any internal change was a breaking change for package users.

## Decision
1. **Strategy registry** (`Strategies/`): each incremental strategy is one
   `IIncrementalStrategy` class that states when it applies (`WhyNotApplicable`, worded for an
   operator), which column it reads, which runner executes it, and which baseline it records
   before the initial copy. `IncrementalStrategies.All` lists them in auto-selection priority;
   `SyncPlanHeuristic` takes the override (validated by the same `WhyNotApplicable`) or the first
   applicable entry. Adding a strategy means one class and one list entry.
2. **Template Method** (`ChangeWindowSyncRunner<TPosition>`): the base class owns the algorithm
   every strategy shares — open a window, read it page by page in key order, delete rows flagged
   as deleted, merge the rest, record the new position only if no retryable error happened.
   Subclasses supply only `OpenWindowAsync`, `ReadPageAsync` and `CommitWindowAsync` (plus
   `RunFullRescanAsync` for Change Tracking's fallback). Paging bounds memory and TVP size after
   an outage. `BaselineCapturingSyncRunner` decorates the initial copy for strategies with their
   own position.
3. **`SyncOutcome`** replaces the `(synced, errored, retryable)` tuples and counts deletes
   separately, so `SyncResult.RowsDeleted` and the deletes metric are accurate.
4. **Minimal public API**: only what a host calls, configures or replaces stays public —
   `AddSmartTableSync`, `SyncSettings` / `SyncMode` / `IncrementalStrategy`, `ISyncOrchestrator`,
   `ISyncRunLock`, `IDbConnectionFactory`, `ISyncMetricsReporter` and its two implementations.
   Everything else is internal; the Scaffold CLI and the test projects use
   `InternalsVisibleTo`. Shipped as 0.3.0.

## Alternatives Considered
- **Keep the `switch`/`if` dispatch and document it.** Rejected: the cost of a new strategy (and
  the risk of updating five places but not the sixth) grows with every strategy, and the order
  of the `if` chain was implicit knowledge.
- **Strategies registered in DI** instead of a static list. Rejected for now: the priority order
  is part of the engine's behavior and must not depend on registration order; nothing needs to
  replace a built-in strategy yet. Can be revisited if third-party strategies become a goal.
- **Composition (a runner taking a "change source" object) instead of inheritance.** Viable,
  but the strategies differ in exactly three steps around one fixed algorithm — the case Template
  Method fits, with less indirection to read.
- **Keep everything public.** Rejected: every refactor would be a breaking change for package
  users, and a large public surface invites depending on internals.

## Consequences
**Positive:** one place to read or change per strategy; the shared algorithm (including the
"advance only after success" rule) can't drift between strategies; incremental reads are bounded;
internals can evolve without breaking users.

**Negative:** 0.3.0 is not source-compatible with code that used engine types directly (hosts
that only call `AddSmartTableSync` are unaffected). Generic `ChangeWindowSyncRunner<TPosition>` is
one more concept for contributors to learn — mitigated by each subclass being short.
