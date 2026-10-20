# Set up in Manual mode, step by step

Manual mode is the default and the recommended setup for production: **you** create the child's
tables, table types and procedures from scripts the scaffold tool writes, after reviewing them.
The sync itself only ever reads the parent and writes rows into the child; it has no right to
change any schema.

This page is the recipe: every command and every SQL statement, in order. The reasons behind each
right are in the [setup guide](../setup.md); the other ways to set up are listed in the
[README](../../README.md#guides).

The example syncs three tables: `Product` and `Customer` in `dbo`, and `Order` in the `sales`
schema. Replace them with yours.

## What you need

- **The scaffold tool**, from NuGet (needs the .NET 10 SDK):
  `dotnet tool install --global SmartTableSync.Scaffold`. In a clone of the repository you can run
  it from source instead: `dotnet run --project SmartTableSync.Scaffold -- <arguments>`.
- **A way to run SQL:** SSMS, Azure Data Studio, or `sqlcmd`. The examples use
  [go-sqlcmd](https://learn.microsoft.com/sql/tools/sqlcmd/go-sqlcmd-utility) with your Microsoft
  Entra sign-in:
  ```bash
  sqlcmd -S myserver.database.windows.net -d ParentDb --authentication-method ActiveDirectoryDefault -i script.sql
  ```
- **An account that can create users** in both databases (the server's Entra admin, or
  `db_owner`). The steps below run as that account unless they say otherwise.
- **The runtime identity** the host will sign in with. On Azure: a user-assigned managed identity
  attached to the Function App (or App Service, container, VM). Elsewhere: a SQL login or a
  Windows account.

## 1. Create the database users

The runtime identity syncs; the introspection and provisioning identities are used by people
(you, a DBA, a pipeline) to generate and apply scripts. To keep it simple you can use your own
account for those two, as the [demo](../../demo/README.md) does; the runtime identity should always
be separate.

**Parent:**

```sql
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;                -- the host's managed identity
CREATE USER [smarttablesync-parent-introspection] FROM EXTERNAL PROVIDER;   -- optional: an Entra group for people
```

**Child:**

```sql
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
CREATE USER [smarttablesync-child-provisioning] FROM EXTERNAL PROVIDER;     -- optional: an Entra group for people
ALTER ROLE db_ddladmin ADD MEMBER [smarttablesync-child-provisioning];
```

`FROM EXTERNAL PROVIDER` finds the identity by its name in Microsoft Entra ID. For SQL logins use
`CREATE USER [name] FOR LOGIN [name]`; for Windows accounts `CREATE USER [DOMAIN\name]`.

## 2. Give the runtime identity read access on the parent

```sql
-- Parent
GRANT SELECT, VIEW DEFINITION ON dbo.Product   TO [smarttablesync-runtime];
GRANT SELECT, VIEW DEFINITION ON dbo.Customer  TO [smarttablesync-runtime];
GRANT SELECT, VIEW DEFINITION ON sales.[Order] TO [smarttablesync-runtime];
GRANT VIEW DATABASE STATE TO [smarttablesync-runtime];          -- row counts and sizes, when a table is profiled

-- Only if you created the introspection user
GRANT VIEW DEFINITION, VIEW DATABASE STATE TO [smarttablesync-parent-introspection];
GRANT SELECT ON dbo.Product   TO [smarttablesync-parent-introspection];
GRANT SELECT ON dbo.Customer  TO [smarttablesync-parent-introspection];
GRANT SELECT ON sales.[Order] TO [smarttablesync-parent-introspection];
```

## 3. Optional: give tables a better way to find changes

SmartTableSync picks, per table: Change Tracking, else a `rowversion` column, else a `LastUpdate`
datetime column, else a full re-scan (up to 50,000 rows). [How it finds what
changed](../sync-strategies.md) helps you choose. To add Change Tracking or a `rowversion` column,
let the scaffold tool write the script, review it, and have a DBA apply it **to the parent**:

```bash
smarttablesync-scaffold --change-tracking-setup Product --output sql/generated/parent
smarttablesync-scaffold --rowversion-setup Customer --output sql/generated/parent
```

```bash
sqlcmd -S myserver.database.windows.net -d ParentDb --authentication-method ActiveDirectoryDefault -i sql/generated/parent/Product_ChangeTrackingSetup.sql
sqlcmd -S myserver.database.windows.net -d ParentDb --authentication-method ActiveDirectoryDefault -i sql/generated/parent/Customer_RowVersionSetup.sql
```

The Change Tracking script also grants `VIEW CHANGE TRACKING` to `smarttablesync-runtime`.

## 4. Give the scaffold tool its connection strings

It reads the parent's schema, and the child's existing schema to decide what to write. It never
changes either database.

PowerShell:

```powershell
$env:ParentIntrospectionConnectionString = "Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Default;Encrypt=True"
$env:ChildProvisioningConnectionString  = "Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Default;Encrypt=True"
```

bash:

```bash
export ParentIntrospectionConnectionString="Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Default;Encrypt=True"
export ChildProvisioningConnectionString="Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Default;Encrypt=True"
```

`Active Directory Default` signs in as you (Azure CLI, Visual Studio). If you use per-table
settings such as `SmartTableSync__TimestampColumns__Customer` or `SmartTableSync__KeyColumns__<Table>`,
set them here too, so the scripts match what the engine will do.

## 5. Create the tracking tables in the child

Once per child database:

```bash
smarttablesync-scaffold --infrastructure --output sql/generated/child
sqlcmd -S myserver.database.windows.net -d ChildDb --authentication-method ActiveDirectoryDefault -i sql/generated/child/00_infrastructure.sql
```

This creates `dbo.SyncTableConfig`, `dbo.CustomSyncState` and `dbo.SyncBatchLog`.

## 6. Generate and apply the child tables

```bash
smarttablesync-scaffold Product Customer sales.Order --output sql/generated/child
```

For each table it prints the plan (strategy and merge method) and writes:

| File | Creates |
|---|---|
| `Product.sql`, `sales.Order.sql` | the child table (only when it doesn't exist yet) |
| `ProductTableType.sql`, `sales.OrderTableType.sql` | the table type for batch merges (not for tables merged row by row) |
| `sp_SyncProductBatch.sql`, `sales.sp_SyncOrderBatch.sql` (or `sp_Sync<Table>.sql`) | the merge procedure |

Review them, then apply them to the **child** in this order: tables, table types, procedures.

```bash
cd sql/generated/child
for f in Product.sql Customer.sql sales.Order.sql; do sqlcmd -S myserver.database.windows.net -d ChildDb --authentication-method ActiveDirectoryDefault -i "$f"; done
for f in *TableType.sql; do sqlcmd -S myserver.database.windows.net -d ChildDb --authentication-method ActiveDirectoryDefault -i "$f"; done
for f in *sp_Sync*.sql; do sqlcmd -S myserver.database.windows.net -d ChildDb --authentication-method ActiveDirectoryDefault -i "$f"; done
```

## 7. Give the runtime identity access to the child objects

Now that they exist:

```sql
-- Child: the synced tables and the tracking tables
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Product   TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Customer  TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON sales.[Order] TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE ON dbo.CustomSyncState TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE ON dbo.SyncBatchLog    TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE ON dbo.SyncTableConfig TO [smarttablesync-runtime];

-- Child: every merge procedure and table type the scaffold step created
DECLARE @grants NVARCHAR(MAX) = N'';
SELECT @grants += N'GRANT EXECUTE ON ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' TO [smarttablesync-runtime];' + CHAR(10)
FROM sys.procedures WHERE name LIKE N'sp[_]Sync%';
SELECT @grants += N'GRANT EXECUTE ON TYPE::' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' TO [smarttablesync-runtime];' + CHAR(10)
FROM sys.table_types WHERE name LIKE N'%TableType';
PRINT @grants;
EXEC sys.sp_executesql @grants;
```

No DDL right anywhere: the runtime identity can change rows, never a schema.

## 8. Configure and start the host

The settings, for the example Function (app settings on Linux use `__` for `:`):

```text
ParentDbConnectionString   = Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Managed Identity;User Id=<runtime identity client id>;Encrypt=True
ChildDbConnectionString    = Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Managed Identity;User Id=<runtime identity client id>;Encrypt=True
SmartTableSync__TableList  = Product,Customer,sales.Order
SmartTableSync__Mode       = Manual
```

Publish the example Function:

```bash
cd SmartTableSyncFunction
func azure functionapp publish <function app name>
```

The Function App itself (with the runtime identity attached and its storage) can be created like
[`quickstart/terraform/main.tf`](../../quickstart/terraform/main.tf) does. In your own app instead
of the Function, use the same settings in `appsettings.json` and see
[hosting in a background service, Hangfire or Quartz.NET](hosting.md). Every setting is in the
[configuration reference](../configuration.md).

## 9. Check the first run

The first run copies each table in full; later runs copy only changes. On the **child**:

```sql
SELECT TableName, IncrementalStrategy, MergeStrategy FROM dbo.SyncTableConfig;   -- the plan per table
SELECT TableName, LastSyncDate, IncrementalPosition FROM dbo.CustomSyncState;    -- where each table stopped
SELECT TOP (20) * FROM dbo.SyncBatchLog ORDER BY LogId DESC;                     -- first-copy batches
```

Compare a table on both sides (run on the parent and on the child; the results must match):

```sql
SELECT COUNT(*) AS [Rows], CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS [Checksum] FROM dbo.Product;
```

Leave a `rowversion` column out of the checksum: the child has its own values.

## Later changes

**Add a table** (`dbo.Supplier`):

```sql
-- Parent
GRANT SELECT, VIEW DEFINITION ON dbo.Supplier TO [smarttablesync-runtime];
```

```bash
smarttablesync-scaffold Supplier --output sql/generated/child
# apply Supplier.sql, SupplierTableType.sql (if written), sp_SyncSupplier*.sql to the child, then the step 7 grants for it
```

Then add it to `SmartTableSync__TableList`.

**A column is added in the parent:** run the scaffold tool for that table again. For an existing
child table it writes `<Table>_AddColumns.sql` instead of `CREATE TABLE`, and a table type script
that recreates an out-of-date type. Apply `<Table>_AddColumns.sql`, the table type, then the
procedure. Until then the table stops with an error; the others keep syncing.

**Other column changes** (type, length, key): the scaffold tool prints `NEEDS A MANUAL CHANGE`
with what to change; a `decimal` precision or scale change gets `<Table>_AlterColumns.sql`. See
[later changes](../setup.md#later-changes) in the setup guide.

**Copy a table again from scratch** (on the child, between two runs):

```sql
DELETE FROM dbo.SyncBatchLog    WHERE TableName = N'Product';
DELETE FROM dbo.CustomSyncState WHERE TableName = N'Product';
```

**Make the engine choose the strategy again** after changing a strategy setting:

```sql
DELETE FROM dbo.SyncTableConfig WHERE TableName = N'Product';
```
