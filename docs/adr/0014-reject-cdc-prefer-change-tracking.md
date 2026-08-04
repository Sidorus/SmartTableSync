# ADR-0014: Reject transaction-log-based CDC; prefer Change Tracking for the deferred third incremental strategy

## Status
Accepted

## Date
2026-08-01

## Context
Both mechanisms Strategy 1 and Strategy 2 use ([ADR-0010](0010-incremental-strategy-selection-hierarchy.md))
require a schema change to the tracked table — a maintained datetime column, or a `rowversion`
column. A third class of mechanism needs **no schema or data change to the tracked table at
all**, by tracking changes at the database engine level instead: SQL Server Change Tracking
(CT) and Change Data Capture (CDC). Both were evaluated as the candidate for that role.

## Decision
When a third, zero-schema-change strategy is built, it will be **Change Tracking**, not CDC.
(Not yet implemented — this ADR records the *mechanism* decision made ahead of building it,
consistent with how Strategy 1/2 were each decided before being built.)

## Alternatives Considered — the actual pros/cons that decided it

**Change Data Capture (CDC)**
- *Pros:* genuinely asynchronous, log-based (`sp_replcmds`); captures full before/after row
  data (no `LEFT JOIN` needed back to the base table); on Azure SQL Database specifically, no
  SQL Server Agent dependency (Azure runs its own capture/cleanup scheduler); reports deletes
  with full historical detail.
- *Cons — decisive:* **log truncation is held back until the capture process catches up.** If
  capture falls behind (high write volume, long-running transactions), the transaction log
  grows — Microsoft has a dedicated troubleshooting article for exactly this failure mode, and
  if the log fills, **writes to the entire database can fail**, not just slow down. CDC also
  **explicitly disables Accelerated Database Recovery's aggressive log truncation** on Azure SQL
  Database, with Microsoft's own guidance suggesting a pricier tier (Hyperscale) to compensate.
  Requires `db_owner` to enable (parent-database admin action, same category as CT). No SLA on
  latency. Heavier storage footprint than CT (full before-images, not just tracking bytes).

**Change Tracking (CT)**
- *Pros:* zero schema/data change to the tracked table (the actual reason to consider either
  mechanism at all); synchronous but low, predictable overhead ("similar to maintaining an
  index" per Microsoft's own docs); **never touches the transaction log** — no log-truncation
  or growth risk of any kind; reports deletes directly; simple `bigint`-version-based querying.
- *Cons:* retention window (commonly 2 days) requiring a `FullRescan` fallback if exceeded;
  documented risk that auto-cleanup falls behind in practice (Microsoft has a troubleshooting
  article for this too, but the failure mode is storage growth/slower queries, not the whole
  database refusing writes); doesn't return row data directly, needs a join; no
  historical/intermediate values (irrelevant to this project's needs — it only cares "did this
  row change," not the in-between states).

**Net assessment:** CDC's theoretical lower per-write overhead (genuinely async vs. CT's
synchronous bookkeeping) doesn't outweigh its log-truncation failure mode, which can degrade
into database-wide write failures — a categorically worse failure shape than anything CT can
cause. This tracks the project's overall philosophy (see
[ADR-0008](0008-three-identity-least-privilege-security-model.md)): never risk the parent
database for this tool's own convenience.

## Consequences
**Positive:** when built, the third strategy will never risk transaction-log growth or
database-wide write failures — its worst case (retention window exceeded) degrades gracefully
to the existing `FullRescan` fallback, already built for exactly this kind of "can't do the
cheap thing, fall back to the safe thing" case.

**Negative:** CT's own real limitations (retention window, auto-cleanup falling behind in
practice) are inherited, not avoided — explicitly SmartTableSync's problem only at the "handle
the retention-exceeded error" level, never CT's own database-level enablement or maintenance,
which stays the parent DBA's responsibility (SmartTableSync never enables CT itself, matching
[ADR-0009](0009-manual-default-auto-opt-in-ddl-provisioning.md)'s "never touch parent schema
automatically" stance). If CT's limitations prove blocking in practice once built, CDC remains
theoretically revisitable — this ADR is not a permanent foreclosure, just today's decision.
