# SmartTableSync - Architecture Decision Records

An ADR captures one significant architectural decision: the problem, the choice made, the
alternatives rejected, and the trade-offs accepted - so a future reader (including future you)
understands *why*, not just *what*. Format: lightweight [MADR](https://adr.github.io/madr/)
style (Status / Context / Decision / Alternatives Considered / Consequences).

**Note on dates:** most of these decisions were made during earlier development and are being
recorded retroactively here, not at the moment they were decided. Dates are approximate,
reconstructed from commit history - treat them as "roughly when," not a precise record.

**Statuses used:** `Accepted` - decided and shipped, still in force. `Proposed` - decided in
direction but not fully executed yet. `Superseded` - replaced by a later ADR (linked).

## Index

| # | Title | Status | Date |
|---|---|---|---|
| [0001](0001-use-architecture-decision-records.md) | Use Architecture Decision Records | Accepted | 2026-08-04 |
| [0002](0002-build-custom-sync-engine-for-data-sync-gaps.md) | Build a custom sync engine instead of working around Data Sync's limits in place | Accepted | 2026-07-13 |
| [0003](0003-reject-transactional-replication-and-data-factory.md) | Reject Transactional Replication, Azure Data Factory, and Database Migration Service as alternatives | Accepted | 2026-07-18 |
| [0004](0004-decouple-core-engine-from-azure-functions.md) | Decouple the sync engine from Azure Functions into a standalone library | Accepted | 2026-07-18 |
| [0005](0005-introspection-and-heuristic-driven-generic-engine.md) | Replace hand-written per-table syncers with an introspection + heuristic-driven generic engine | Accepted | 2026-07-18 |
| [0006](0006-per-table-merge-strategy-selection.md) | Derive merge strategy (TVP vs row-by-row) per table instead of a fixed global choice | Accepted | 2026-07-18 |
| [0007](0007-keyset-pagination-for-initial-sync.md) | Keyset pagination instead of `OFFSET` for initial sync | Accepted | 2026-07-18 |
| [0008](0008-three-identity-least-privilege-security-model.md) | Three separate least-privilege identities; the runtime never holds DDL rights | Accepted | 2026-07-18 |
| [0009](0009-manual-default-auto-opt-in-ddl-provisioning.md) | Manual-by-default, Auto opt-in DDL provisioning | Accepted | 2026-07-19 |
| [0010](0010-incremental-strategy-selection-hierarchy.md) | Incremental-strategy selection hierarchy, with an explicit per-table override | Accepted | 2026-08-01 |
| [0011](0011-persisted-per-table-config-with-overrides.md) | Persist per-table config with hand-set overrides that survive re-provisioning | Accepted | 2026-07-18 |
| [0012](0012-verify-against-real-disposable-sql-server.md) | Verify against a real, disposable SQL Server instead of mocks | Accepted | 2026-07-18 |
| [0013](0013-position-as-general-data-sync-replacement.md) | Position SmartTableSync as a general-purpose Data Sync replacement, not just an exception-table patch | Proposed | 2026-08-04 |
| [0014](0014-reject-cdc-prefer-change-tracking.md) | Reject transaction-log-based CDC; prefer Change Tracking for the deferred third incremental strategy | Accepted | 2026-08-01 |
| [0015](0015-implement-rowversion-as-second-strategy.md) | Implement Rowversion as the second incremental strategy | Accepted | 2026-08-01 |
| [0016](0016-implement-change-tracking-as-third-strategy.md) | Implement SQL Server Change Tracking as the third incremental strategy | Accepted | 2026-10-17 |
| [0017](0017-strategy-registry-and-template-method-runners.md) | Strategy registry, template-method runners, and a minimal public API | Accepted | 2026-10-17 |
| [0018](0018-child-tables-without-identity.md) | Create child tables without IDENTITY, so the runtime identity needs no ALTER | Accepted | 2026-10-18 |
| [0019](0019-auto-mode-schema-evolution.md) | Auto mode evolves the child schema for added columns, and nothing riskier | Accepted | 2026-10-18 |
| [0020](0020-tables-in-schemas-other-than-dbo.md) | Sync tables in schemas other than dbo, keeping the schema in the child | Accepted | 2026-10-19 |
| [0021](0021-key-fallback-for-tables-without-a-primary-key.md) | Key tables without a primary key by a unique index or configured columns | Accepted | 2026-10-19 |

See also: the top-level [README](../../README.md) for architecture and getting-started
instructions.
