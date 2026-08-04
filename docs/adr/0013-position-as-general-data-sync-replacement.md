# ADR-0013: Position SmartTableSync as a general-purpose Data Sync replacement, not just an exception-table patch

## Status
Proposed

## Date
2026-08-04

## Context
[ADR-0002](0002-build-custom-sync-engine-for-data-sync-gaps.md) framed this project as a narrow
patch: handle the handful of tables Azure SQL Data Sync structurally can't sync, and let Data
Sync keep handling everything else. That framing assumed Data Sync would remain available
indefinitely for the "everything else" majority.

It won't. **Azure SQL Data Sync is being retired on 30 September 2027** (per Microsoft's own
retirement announcement). Microsoft's official migration guide names two viable replacements
for Azure SQL Database → Azure SQL Database sync: **Azure Data Factory** and **Azure
Functions** — the latter being, structurally, exactly what this project already is. This isn't
an odd one-off architecture; it's the path Microsoft is steering every Data Sync customer
toward.

Separately: this project's own hand-written Data Sync integration surfaced *why* Data Sync
itself is being retired from a design-quality standpoint — it's confirmed to be trigger-based
(its own tracking side-table plus INSERT/UPDATE/DELETE triggers per synced table), which is
exactly what caused `ProductAttributeValue`'s FK-timing race in the first place (racing against
Data Sync's own eventually-consistent trigger-based replication). Its design has real,
documented sharp edges beyond just the two hard limits ADR-0002 was built to patch.

## Decision
Treat "sync every table between two Azure SQL databases, not just the exception cases" as the
project's actual long-term scope, not a narrow patch alongside a permanent Data Sync. Concretely,
this means:
- Continue investing in [ADR-0004](0004-decouple-core-engine-from-azure-functions.md)'s
  standalone-package structure — a general replacement can't assume it's always the *minority*
  case sitting next to Data Sync.
- Prioritize closing the gaps that matter for "any table," not just the three original example
  shapes — broader `SqlTypeMapping` coverage, the configurable/auto-detected incremental
  strategies in [ADR-0010](0010-incremental-strategy-selection-hierarchy.md), and eventually
  Change Tracking support (specced, not yet built) — over narrow, exception-table-specific
  features.
- Treat the still-open production-readiness item (real Azure SQL Database / Managed Identity /
  deployed-Function verification, tracked in the [README](../../README.md)'s Status section) as
  a genuine blocker for this broader claim, not an optional nice-to-have — a "general
  replacement" that's never run against real Azure infrastructure isn't credibly one yet.

## Alternatives Considered
- **Keep the narrow "exception tables only" scope and let every other table go unsynced after
  September 2027.** Rejected — leaves a real, dated cliff with no plan, for a problem this
  project is already most of the way toward solving generally.
- **Migrate wholesale to Azure Data Factory instead of extending this engine.** Rejected for the
  same reasons recorded in
  [ADR-0003](0003-reject-transactional-replication-and-data-factory.md) — ADF is a worse fit
  for this specific access pattern (frequent small-delta checks, not chunky periodic ETL), and
  this project's own value (per-table strategy selection, resumable loads, tested C#) would be
  discarded, not reused.

## Consequences
**Positive:** a clear, deliberate answer to "what happens when Data Sync goes away" instead of
an implicit one; roadmap prioritization (which gaps matter) now has an explicit forcing
function tied to a real external date.

**Negative:** genuinely broadens scope — supporting "any table" reliably is a bigger, more
open-ended commitment than "these three known shapes," and this ADR doesn't itself resolve
[ADR-0011](0011-persisted-per-table-config-with-overrides.md)'s override-mechanism
inconsistency or [ADR-0004](0004-decouple-core-engine-from-azure-functions.md)'s public-API
versioning risk — both become more consequential, not less, the more broadly this project is
actually relied on.
