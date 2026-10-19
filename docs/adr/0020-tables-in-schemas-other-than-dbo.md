# ADR-0020: Sync tables in schemas other than dbo, keeping the schema in the child

## Status
Accepted

## Date
2026-10-19

## Context
Every SQL statement and generated script assumed `[dbo]`: the page and key queries, bulk copy,
the delete path, the merge procedures and table types, and the schema reads (`OBJECT_ID` with
an unqualified name). Many databases keep their tables in other schemas (`sales`, `hr`, ...),
and two schemas can hold tables with the same name. Those tables couldn't be synced at all.

## Decision
- A table is named `schema.Table` in `SmartTableSync:TableList`, the per-table settings and the
  scaffold tool. A plain `Table` still means `dbo.Table`, and `dbo.Table` is normalized to
  `Table` (`SyncTableName.Normalize`) wherever a name enters: settings binding and the scaffold
  tool's arguments. The tracking tables keep the normalized name, so rows written before this
  change still match, and the 100-character `TableName` columns don't change.
- **The child keeps the parent's schema.** `[sales].[Order]` is copied into `[sales].[Order]`.
  The scaffold tool's `CREATE TABLE` script creates the schema when the child lacks it (Auto mode
  runs the same script).
- **A table's procedures and table type live in the table's schema**: `[sales].[sp_SyncOrderBatch]`
  and `[sales].[OrderTableType]`. Their names keep the old pattern built from the table's own
  name, so `dbo.Order` and `sales.Order` never collide, and nothing changes for dbo tables.
- **The tracking tables stay in dbo.**
- Every SQL name is built by `SqlIdentifier.Table` / `InTableSchema`, which quote schema and name
  separately; schema reads pass the quoted two-part name to `OBJECT_ID`.
- A name with more than one dot, or longer than 100 characters, is rejected when the settings are
  bound, with a message that says why.

## Alternatives Considered
- **Map every parent schema into dbo in the child, with a prefix (`sales_Order`).** Rejected:
  the child would no longer look like the parent, queries written for the parent wouldn't run on
  the child, and the prefixed names could collide with real dbo tables.
- **Keep the procedures and types in dbo, with the schema in their names
  (`sp_Sync_sales_Order`).** Rejected: the names get long, they still need an escaping rule for
  odd characters, and a schema-level grant on the table's schema wouldn't cover them.
- **Store the schema in a separate column of the tracking tables.** Rejected: it needs a
  migration of three tables and their primary keys for no functional gain over the qualified
  name.

## Consequences
- Existing dbo setups are unaffected: the same names, scripts, procedures and tracking rows.
- Auto mode's documented child grant is per schema (`GRANT ... ON SCHEMA::dbo`). A table in
  another schema needs the same grant on its schema, so an administrator creates that schema in
  the child and grants on it before adding the table. Auto mode creates a missing schema itself,
  but the runtime identity can't use it until it's granted.
- A table name that contains a dot can't be configured any more; such names didn't occur in
  practice and would have been ambiguous with `schema.Table`.
- Tests: unit tests for normalization and the generated SQL (including a hostile schema name),
  and a least-privilege Auto mode integration test with `[dbo].[Order]` and `[sales].[Order]`
  synced side by side.
