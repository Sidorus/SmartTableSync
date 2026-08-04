# ADR-0002: Build a custom sync engine instead of working around Data Sync's limits in place

## Status
Accepted

## Date
2026-07-13 (initial commit)

## Context
Azure SQL Database doesn't support cross-database queries — no `JOIN`/`SELECT` across two
separate Azure SQL databases in one query. Azure SQL Data Sync is Microsoft's managed answer:
scheduled table replication, no custom code. It works for most tables, but has two hard,
undocumented-workaround limits:
- Can't use `nvarchar(max)`/`varbinary(max)` as part of an **indexed key**.
- Each synced row must stay under a **24 MB batch limit**.

A handful of real tables in the target schema hit one or both limits (a search-key column
that's `nvarchar(max)`, a document/media table with large binary content) and simply can't be
put in a Data Sync sync group at all — not a performance problem to tune around, a hard
exclusion.

## Decision
Build a small, purpose-specific custom transport + stored-procedure `MERGE` engine for exactly
the tables Data Sync can't handle, running as a timer-triggered Azure Function alongside Data
Sync (which continues to handle every other table). Not a Data-Sync replacement at this
point — a patch for its specific gaps.

## Alternatives Considered
- **Reshape the excluded tables to fit Data Sync's limits** (e.g. move `nvarchar(max)` out of
  the key, chunk oversized rows). Rejected — requires schema changes to the customer's live
  application tables for a sync tool's convenience; the tail wagging the dog.
- **Accept the gap and sync those tables manually/never.** Rejected — silently stale data in
  the target database undermines trust in the whole replication story.

## Consequences
**Positive:** the excluded tables get correct, verified sync without touching their schema.
The scope is deliberately narrow (a handful of exception tables), so the initial build is small
and shippable fast.

**Negative:** two sync mechanisms now run side by side (Data Sync + this Function), each with
its own timing/consistency model — a real operational surface to reason about. This tension is
revisited directly in [ADR-0013](0013-position-as-general-data-sync-replacement.md) once Data
Sync's own retirement made "just handle the exceptions" a shrinking target.
