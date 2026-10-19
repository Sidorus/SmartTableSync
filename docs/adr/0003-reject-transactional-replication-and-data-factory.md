# ADR-0003: Reject Transactional Replication, Azure Data Factory, and Database Migration Service as alternatives

## Status
Accepted

## Date
2026-07-18

## Context
Before committing to a hand-built Azure Function as the sync mechanism (see
[ADR-0002](0002-build-custom-sync-engine-for-data-sync-gaps.md)), three existing Azure-native
alternatives were evaluated: SQL Server Transactional Replication, Azure Data Factory (ADF),
and Azure Database Migration Service (DMS). All three are mature, Microsoft-supported products
that could plausibly replace custom code.

## Decision
Reject all three. Use a custom Azure Function instead (already decided in ADR-0002; this ADR
records *why* the obvious managed alternatives don't work).

## Alternatives Considered
- **Transactional Replication.** Architecturally excluded, not just a tradeoff: Azure SQL
  Database can only be a **push subscriber** for transactional replication - never a Publisher
  or Distributor. Since this project's whole topology is Azure SQL Database → Azure SQL
  Database, there is no way to wire this up without introducing a third, separate SQL
  Server/Managed Instance purely to act as Publisher+Distributor - a much larger piece of
  infrastructure than the problem justifies.
- **Azure Data Factory.** Viable in principle (and is one of the Microsoft-named alternatives in
  the official Data Sync retirement guide, alongside Azure Functions - see
  [ADR-0013](0013-position-as-general-data-sync-replacement.md)), but a poor fit for *this*
  workload specifically: separate billed infrastructure, a per-run cost model built for chunky
  periodic ETL rather than a lightweight "did anything change" check run every 5 minutes,
  Mapping Data Flows spin up a full Spark cluster for what's fundamentally row-level `MERGE`
  logic, no equivalent of this project's actual value-add (per-table merge-strategy selection,
  resumable keyset-paginated loads, batch-size backoff), and JSON/GUI pipeline authoring instead
  of testable C#.
- **Azure Database Migration Service (DMS).** Wrong tool shape at a more fundamental level than
  ADF: DMS is built around a **migration project with a defined end state** - move a database
  (schema + data) from one place to another, then cut over. Even its "continuous sync"/minimal-
  downtime mode exists to keep a target caught up *until the cutover happens*, not to maintain
  an indefinite, ongoing coexistence between a live source app and a live target that keeps
  being queried directly forever. It has no notion of "sync only these specific tables, forever,
  and leave everything else alone" - the exact shape this project actually needs (a handful of
  exception tables, running alongside Data Sync handling the rest). It's also operated and
  priced as a migration-duration service instance, not a lightweight recurring per-table delta
  job. Reusable in principle if this project ever needed a genuine one-time full-database
  migration/cutover - a different problem than the one it solves today.

## Consequences
**Positive:** a lightweight, cheap-to-run, fully testable, source-controlled solution instead
of GUI-authored pipelines or infrastructure this project's actual topology can't support at all.

**Negative:** everything the managed products would have handled (retry policies, monitoring
dashboards, connector maintenance) is now this project's own responsibility to build and keep
correct - accepted deliberately, and largely covered by
[ADR-0012](0012-verify-against-real-disposable-sql-server.md)'s testing discipline.
