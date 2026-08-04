# ADR-0012: Verify against a real, disposable SQL Server instead of mocks

## Status
Accepted

## Date
2026-07-18

## Context
This engine's correctness lives almost entirely in real SQL Server behavior: `MERGE` semantics,
`SqlBulkCopy`, TVP type rules, rowversion engine guarantees (`MIN_ACTIVE_ROWVERSION()`), FK
timing, keyset pagination against real indexes. Mocking `SqlConnection`/`SqlCommand` would
verify that code *calls* ADO.NET correctly, not that the SQL it sends actually does the right
thing against a real engine — and several bugs found later in this project (a TVP column
rejecting an explicit rowversion value, an off-by-one against `MIN_ACTIVE_ROWVERSION()`'s exact
semantics, a stale `TvpTypeName` producing invalid generated DDL) are exactly the class of bug
a mock-based test would never have caught.

## Decision
`SmartTableSync.IntegrationTests` runs every non-trivial scenario against a real, disposable SQL
Server via Testcontainers — Parent/Child databases created fresh per run, real schema, real
stored procedures, real data. Reserve pure unit tests (`SmartTableSync.Tests`) for genuinely
pure functions with no I/O (`SyncPlanHeuristic`, `SqlTypeMapping`, `KeysetPredicateBuilder`,
etc.). As a working discipline: when a suspected bug is found, write a test that demonstrably
fails first, confirm the failure, then fix and confirm it passes — applied repeatedly across
this project's history to catch real bugs before they shipped rather than after.

## Alternatives Considered
- **Mock `SqlConnection`/ADO.NET.** Rejected — `SqlConnection`/`SqlCommand` aren't designed to
  be mocked cleanly, and even if they were, the actual risk in this codebase is SQL Server
  *behavior*, not C# control flow — a mock can't tell you `MIN_ACTIVE_ROWVERSION()`'s exact
  boundary semantics are wrong.
- **Test only against a shared, long-lived dev SQL Server.** Rejected — shared mutable state
  between test runs (and between developers) is exactly the kind of flakiness Testcontainers'
  per-run disposable instance avoids.

## Consequences
**Positive:** high confidence that what's tested is what will actually happen in production —
several real, non-obvious SQL Server behavior bugs were caught this way before shipping, each
documented with the exact SQL error it reproduced.

**Negative:** integration tests are slower and need Docker/Testcontainers available in CI and
locally; test-isolation discipline (shared `[Collection("SqlServer")]` vs dedicated
`IClassFixture<MsSqlTestFixture>` per test class) has to be actively maintained, since a test
that mutates shared tracking-table state can silently break an unrelated test sharing the same
container.
