# ADR-0008: Three separate least-privilege identities; the runtime never holds DDL rights

## Status
Accepted

## Date
2026-07-18

## Context
This tool needs three genuinely different kinds of database access at different times: reading
the parent's schema, creating/altering child-side tables and stored procedures, and the routine
always-on read/write sync traffic. Using one identity for all three means the always-on
Function process — the thing running unattended, continuously, against production — would hold
schema-change rights it only actually needs rarely, if at all.

## Decision
Three separate principals, each scoped to exactly one job (documented and provisioned via
`sql/07_permissions.sql`):
- **Runtime** — `SELECT` on parent's synced tables; `SELECT`/`INSERT`/`UPDATE`/`DELETE` on
  child's synced + tracking tables; `EXECUTE` on the generated procedures. No DDL, ever.
- **Parent-introspection** — `VIEW DEFINITION` + `SELECT` on parent, no DDL. Used by the
  Scaffold CLI and the Function's schema-drift check.
- **Child-provisioning** — `db_ddladmin` on the child only. Used by the Scaffold CLI, and,
  opt-in, by the Function itself only when Auto mode is enabled
  ([ADR-0009](0009-manual-default-auto-opt-in-ddl-provisioning.md)).

## Alternatives Considered
- **One identity for everything.** Rejected — the always-on process would hold `db_ddladmin`
  permanently, for schema changes it makes rarely (Auto mode) or never (Manual mode); a
  compromised or buggy runtime process could then alter schema, not just data.

## Consequences
**Positive:** the always-on Function is structurally limited to data operations even in the
worst case (a bug, a compromised dependency) — it simply has no schema-change rights to abuse,
even opt-in Auto mode only opens that connection briefly, on the same infrequent occasions
provisioning already re-runs.

**Negative:** three identities to provision and keep straight instead of one, and the boundary
is enforced by connection-string discipline / SQL Server's own permission model, not by
anything in the type system — nothing today would catch a future contributor accidentally
wiring the wrong connection into `ChildDdlProvisioner`. A startup assertion (e.g. querying
`sys.fn_my_permissions` to confirm the runtime connection actually lacks DDL rights) would
close this gap; identified but deliberately deferred as lower-value than the fixes it would
have to compete with for attention.
