# Set up in Auto mode, step by step

In Auto mode the engine creates the child's tracking tables, synced tables, table types and
procedures **itself**, the first time it sees a table, and adds columns that appear in the parent
later. You don't run the scaffold tool, and adding a table is a settings change.

The price is that the host can change the child's schema, through a second identity. Use Auto
mode for a child database that only SmartTableSync writes to, for dev and test, or to get started
quickly. [Manual or Auto mode](../../README.md#manual-or-auto-mode) compares the two.

On Azure SQL Database, the [quick start](../../quickstart/README.md) does all of this with one
script. This page shows each step, for other setups or to understand what the quick start does.

The example syncs `Product` and `Customer` in `dbo`, and `Order` in the `sales` schema.

## What you need

- **A way to run SQL** as an account that can create users in both databases (SSMS, Azure Data
  Studio, or [go-sqlcmd](https://learn.microsoft.com/sql/tools/sqlcmd/go-sqlcmd-utility)).
- **Two identities for the host:**
  - the **runtime** identity: reads the parent, writes rows into the child, no DDL;
  - the **provisioning** identity: creates and changes objects in the child.

  On Azure: two user-assigned managed identities, both attached to the Function App (or App
  Service, container, VM). Elsewhere: two SQL logins or Windows accounts.

## 1. Parent: the runtime user and read access

```sql
-- Parent
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;

GRANT SELECT, VIEW DEFINITION ON dbo.Product   TO [smarttablesync-runtime];
GRANT SELECT, VIEW DEFINITION ON dbo.Customer  TO [smarttablesync-runtime];
GRANT SELECT, VIEW DEFINITION ON sales.[Order] TO [smarttablesync-runtime];
GRANT VIEW DATABASE STATE TO [smarttablesync-runtime];
-- For each table with Change Tracking:
-- GRANT VIEW CHANGE TRACKING ON dbo.Product TO [smarttablesync-runtime];
```

`FROM EXTERNAL PROVIDER` finds the managed identity by its name in Microsoft Entra ID, which must
then be unique in the tenant. To tie the user to one exact identity instead, create it from the
identity's client ID, as the quick start does:

```sql
-- SID = the client ID as bytes; in PowerShell: '0x' + (([guid]'<client id>').ToByteArray() | % { $_.ToString('X2') }) -join ''
CREATE USER [smarttablesync-runtime] WITH SID = <the 0x... value from that line>, TYPE = E;
```

## 2. Child: both users, and their rights

```sql
-- Child
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
CREATE USER [smarttablesync-child-provisioning] FROM EXTERNAL PROVIDER;

-- The provisioning identity creates tables, table types, procedures (and missing schemas)
ALTER ROLE db_ddladmin ADD MEMBER [smarttablesync-child-provisioning];

-- The runtime identity writes rows in objects that don't exist yet, so it gets its rights on the
-- schema: they cover what the engine creates later, and still allow no DDL
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dbo TO [smarttablesync-runtime];

-- One schema grant per other schema you sync; create the schema first
CREATE SCHEMA sales;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::sales TO [smarttablesync-runtime];
```

These grants reach every table in those schemas, so keep the child database for SmartTableSync
only. There's no step to create child tables: the engine does it.

## 3. Optional: give tables a better way to find changes

The same as in Manual mode: [step 3 there](manual-mode.md#3-optional-give-tables-a-better-way-to-find-changes).
Do it before the first run if you can: the strategy is chosen when a table is first set up.

## 4. Configure and start the host

The settings, for the example Function (app settings on Linux use `__` for `:`):

```text
ParentDbConnectionString          = Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Managed Identity;User Id=<runtime client id>;Encrypt=True
ChildDbConnectionString           = Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Managed Identity;User Id=<runtime client id>;Encrypt=True
ChildProvisioningConnectionString = Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Managed Identity;User Id=<provisioning client id>;Encrypt=True
SmartTableSync__TableList         = Product,Customer,sales.Order
SmartTableSync__Mode              = Auto
```

The two child connection strings differ only in `User Id`: which identity signs in. Without
`ChildProvisioningConnectionString`, Auto mode stops with *"ChildProvisioningConnectionString is
not configured - required when SmartTableSync:Mode is Auto."*

Publish the example Function:

```bash
cd SmartTableSyncFunction
func azure functionapp publish <function app name>
```

[`quickstart/terraform/main.tf`](../../quickstart/terraform/main.tf) creates a Function App with
both identities attached and these settings. In your own app, use the same settings and see
[hosting in a background service, Hangfire or Quartz.NET](hosting.md).

## 5. What the first run does

1. Creates `dbo.SyncTableConfig`, `dbo.CustomSyncState` and `dbo.SyncBatchLog` if they're missing
   (a cheap check on every run).
2. For each table seen for the first time: reads its schema and size in the parent, chooses the
   strategy, creates the child table (same schema, same column types, no `IDENTITY`), its table
   type and merge procedure, and logs `<Table>: child DDL objects verified/created`.
3. Copies the table in full. Later runs copy only changes.

Check it on the **child**:

```sql
SELECT TableName, IncrementalStrategy, MergeStrategy FROM dbo.SyncTableConfig;
SELECT TableName, LastSyncDate, IncrementalPosition FROM dbo.CustomSyncState;
SELECT s.name AS [Schema], t.name AS [Table], t.create_date FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id ORDER BY t.create_date;
```

## Later changes

**Add a table:** give the runtime identity read access on the parent, then add it to the list.

```sql
-- Parent
GRANT SELECT, VIEW DEFINITION ON dbo.Supplier TO [smarttablesync-runtime];
```

```text
SmartTableSync__TableList = Product,Customer,sales.Order,Supplier
```

For a table in a new schema, also run step 2's `CREATE SCHEMA` and schema grant on the child.

**A column is added in the parent:** nothing to do. The next run adds it to the child, recreates
the table type and procedure, and copies the table once more so existing rows get the values.

**Changes that could lose data** (a changed type or length, a changed key, a removed `NOT NULL`
column, a column that now allows `NULL` in the parent but not in the child, a different `decimal`
precision or scale) are never applied automatically. The table stops with an error naming the
change, on every run, until you change the child table yourself; the scaffold tool writes the
script for most of them. Other tables keep syncing. See
[when the parent schema changes](../setup.md#when-the-parent-schema-changes).

**Copy a table again, or choose its strategy again:** the same SQL as in
[Manual mode](manual-mode.md#later-changes).
