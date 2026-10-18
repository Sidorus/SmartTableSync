# ADR-0018: Create child tables without IDENTITY, so the runtime identity needs no ALTER

## Status
Accepted

## Date
2026-10-18

## Context
A parent table with an `IDENTITY` column was copied into a child table that also had
`IDENTITY`. To keep the parent's key values, the initial bulk copy used `KeepIdentity` and the
merge procedures switched `SET IDENTITY_INSERT` on. SQL Server allows `IDENTITY_INSERT` only to
the table's owner or a principal with `ALTER` on the table, so the always-on runtime identity
needed `ALTER` on every such child table: DDL rights, the one exception to the least-privilege
model of [ADR-0008](0008-three-identity-least-privilege-security-model.md). Tests that run the
documented grants as a least-privilege user confirmed it: without `ALTER`, the child stayed
empty.

## Decision
- The scaffold tool and Auto mode create the child column as a plain key column, **without
  `IDENTITY`**. The child never generates key values; every one is copied from the parent with an
  ordinary `INSERT`, so the runtime identity needs no `ALTER`.
- The plan carries the CHILD table's identity column (`SyncPlan.ChildIdentityColumn`, persisted in
  `SyncTableConfig.IdentityColumn`), read from the child when a table is (re)provisioned.
  `KeepIdentity` and `IDENTITY_INSERT` are used only when the child table has one.
- `TableProfile.IdentityColumn` keeps describing the parent, so schema fingerprints, and with them
  every existing table's plan, are unchanged.

## Alternatives Considered
- **Keep `IDENTITY` in the child and generate the merge procedures `WITH EXECUTE AS OWNER`.**
  Rejected: the procedures would run with owner rights, and the initial bulk copy would still
  need `ALTER` unless it also went through a procedure, which is slower for large tables.
- **Keep granting `ALTER` on identity tables.** Rejected: it gives the always-on identity the
  right to change or drop those tables.

## Consequences
- New setups need no DDL rights for the runtime identity at all; the least-privilege tests run
  `sql/07_permissions.sql` without any `ALTER` grant.
- Child tables created with `IDENTITY` by an earlier version keep working unchanged: the child is
  checked, and they still get `IDENTITY_INSERT`, so they still need `ALTER` until recreated from
  the scaffold tool's `CREATE TABLE` script.
- The child's schema differs from the parent's in this one property. Code that inserts its own
  rows into the child would have to supply key values, which a one-way copy doesn't support
  anyway; before turning the child into a writable database, `IDENTITY` or a sequence must be
  added back.
