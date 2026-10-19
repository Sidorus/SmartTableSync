# ADR-0009: Manual-by-default, Auto opt-in DDL provisioning

## Status
Accepted

## Date
2026-07-19

## Context
Before this decision, the *only* way to get a new table's child-side objects (its
`CREATE TABLE`, TVP type, `MERGE` stored procedure) in place was a human running the Scaffold
CLI and applying the generated SQL by hand - `SyncTableProvisioner` already automated deriving
*what* the plan should be, but never touched physical DDL. Some operators want that human
review gate; others want zero-touch provisioning for fast iteration.

## Decision
One setting, `SmartTableSync:Mode` (`Manual` default / `Auto`). In Manual mode, nothing
changes - Scaffold generates, a human applies. In Auto mode, `SyncOrchestrator` detects a table
was just freshly (re-)provisioned and runs `ChildDdlProvisioner` itself, executing the exact
same generated SQL Scaffold would otherwise only write to a file - automatically, no review
step, using the opt-in `ChildProvisioningConnectionString`
([ADR-0008](0008-three-identity-least-privilege-security-model.md)'s child-provisioning
identity), never widening the always-on runtime identity's own rights.

## Alternatives Considered
- **Auto-provision by default.** Rejected - a schema-change-executing background job with no
  human in the loop by default is a bigger blast-radius default than this project wants; Manual
  stays the safe, review-first default, Auto is an explicit choice.
- **Manual-only, no Auto mode at all.** Rejected - real friction for fast iteration/dev-test
  environments where a review step for every new table adds no value.

## Consequences
**Positive:** existing Manual-mode deployments are completely unaffected by Auto mode's
existence (opt-in, and the connection string it needs isn't even validated unless Auto mode is
selected). Auto mode reuses the fingerprint/re-provisioning signal
(`WasFreshlyProvisioned`) `SyncTableProvisioner` already had, rather than adding new tracking
state for "have I auto-provisioned this table already."

**Negative:** Auto mode has a real, documented scope boundary - it creates a missing table and
ensures the TVP type/procedure exist, but does **not** `ALTER TABLE` an existing table to match
a schema that drifted. That's a deliberate limitation, not a bug, but worth knowing before
relying on it for schema evolution.
