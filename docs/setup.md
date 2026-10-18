# Setting up SmartTableSync

This guide takes you from two Azure SQL databases to a running sync: which identities to
create, exactly which rights each one needs, and the steps with the scaffold tool. The
[Azure demo](../demo/README.md) runs every step below with one script; the
[last section](#the-demo-as-a-worked-example) maps its steps to this guide.

## The pieces

| Piece | What it is |
|---|---|
| **Parent database** | Where the data is written by your applications. SmartTableSync only reads it. |
| **Child database** | Where SmartTableSync keeps copies of the parent's tables, plus three tracking tables. |
| **Function** (`SmartTableSyncFunction`) | Runs a sync every 5 minutes. Or host the engine yourself: reference the `SmartTableSync` package and call `ISyncOrchestrator.RunAsync`. |
| **Scaffold tool** (`SmartTableSync.Scaffold`, command `smarttablesync-scaffold`) | Reads the parent's schema and **writes** the SQL the child needs, for you to review and apply. It never runs anything against a database. See its [README](../SmartTableSync.Scaffold/README.md). |

### Manual or Auto mode

Set with `SmartTableSync:Mode`.

- **Manual** (default): a person creates the child's tables and procedures from the scaffold
  tool's scripts. The Function only ever reads and writes data.
- **Auto**: the Function creates the child's tracking tables, tables and procedures itself when
  it first sees a table, using a separate provisioning identity, and adds columns that appear in
  the parent later. Changes that could lose data still need a reviewed script.

## Identities and their rights

Three identities, each with only what its job needs. Prefer managed identities or Microsoft
Entra accounts, so there are no passwords to store. [`sql/07_permissions.sql`](../sql/07_permissions.sql)
contains the full script for the example tables; replace the table names with yours. The
integration tests run that script as written, for Manual and for Auto mode, and then sync as
these identities with nothing more, so the rights below are tested on every build.

| Identity | Used by | Database | Rights |
|---|---|---|---|
| **Runtime** (`smarttablesync-runtime`) | the Function, always on | parent | `SELECT`, `VIEW DEFINITION` on each synced table; `VIEW DATABASE STATE`; `VIEW CHANGE TRACKING` on tables with Change Tracking |
| | | child | `SELECT, INSERT, UPDATE, DELETE` on each synced table; `SELECT, INSERT, UPDATE` on `CustomSyncState`, `SyncBatchLog`, `SyncTableConfig`; `EXECUTE` on each `sp_Sync<Table>` / `sp_Sync<Table>Batch` procedure; `EXECUTE` on each `<Table>TableType` type |
| **Parent introspection** | the scaffold tool | parent | `VIEW DEFINITION`; `SELECT` on each synced table; `VIEW DATABASE STATE` |
| **Child provisioning** | the scaffold tool (existence check only), applying the child scripts, the Function in Auto mode | child | `db_ddladmin` |

Why the less obvious ones are needed:

- **`VIEW DATABASE STATE`**: row counts and row sizes come from `sys.dm_db_partition_stats`.
  Without it, profiling a table fails with *"VIEW DATABASE PERFORMANCE STATE permission denied"*.
- **`EXECUTE` on the table type**: passing a table-valued parameter to `sp_Sync<Table>Batch` is
  checked against the type too; without it every batch fails with *"The EXECUTE permission was
  denied on the object '<Table>TableType'"*.
- **Tables with an `IDENTITY` column need nothing extra.** The child is a copy: its key values
  come from the parent, so the scaffold tool and Auto mode create the child column as a plain key
  column without `IDENTITY`, and the runtime writes the parent's values into it with ordinary
  `INSERT`. Only a child table that does have `IDENTITY`, created by an earlier SmartTableSync
  version or by hand, needs `SET IDENTITY_INSERT` and therefore `ALTER` on that table. Recreate
  it from the scaffold tool's `CREATE TABLE` script, or grant `ALTER` on that table only. Before
  ever turning the child into a writable database, add `IDENTITY` (or a sequence) back first.

Two more steps are applied by a **DBA on the parent**, never by SmartTableSync: enabling Change
Tracking (`ALTER DATABASE` and `ALTER TABLE`) and adding a rowversion column. The scaffold tool
writes those scripts too (step 2).

## Step by step (Manual mode)

You need the scaffold tool ([how to run it](../SmartTableSync.Scaffold/README.md#install)) and
a way to run SQL scripts: SSMS, Azure Data Studio or `sqlcmd`.

### 1. Create the identities

Create the three database users and grant the rights in the table above. For managed
identities and Entra accounts:

```sql
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
```

Then grant the **parent** rights for the runtime and introspection identities, and
`db_ddladmin` on the **child** for the provisioning identity. The runtime identity's **child**
rights come in step 5, once the objects they refer to exist.

### 2. Optional: prepare the parent tables

SmartTableSync picks how to detect changes per table, in this order:

1. **Change Tracking**, if a DBA enabled it on the table: also catches deletes on every run.
2. **RowVersion**, if the table has a `rowversion` column.
3. **Timestamp**, if the table has a datetime column updated on every change (`LastUpdate` by
   default; another name through `SmartTableSync:TimestampColumns:<Table>`).
4. **Full re-scan**, for small tables, or large ones listed in `SmartTableSync:AllowFullRescanFor`.

To give a table a better strategy, generate the script and have a DBA review and apply it on
the **parent**:

```bash
smarttablesync-scaffold --change-tracking-setup Product      # enables Change Tracking, grants VIEW CHANGE TRACKING
smarttablesync-scaffold --rowversion-setup Customer          # adds a rowversion column and its index
```

### 3. Create the tracking tables in the child

```bash
smarttablesync-scaffold --infrastructure
```

Apply `sql/generated/00_infrastructure.sql` to the **child** as the provisioning identity.
Once per child database.

### 4. Generate and apply the child tables

Set the two connection strings (see [the scaffold README](../SmartTableSync.Scaffold/README.md#connection-strings))
and list the tables:

```bash
smarttablesync-scaffold Product Customer Document
```

For each table it prints the plan it derived, the same plan the Function will derive, and
writes:

| File | What it creates | When |
|---|---|---|
| `<Table>.sql` | the child table | only if the child table doesn't exist yet |
| `<Table>TableType.sql` | the table type for TVP batches | TVP tables |
| `sp_Sync<Table>Batch.sql` or `sp_Sync<Table>.sql` | the merge procedure | always |

Review them, then apply them to the **child** in this order: tables, table types, procedures.

### 5. Grant the runtime identity access to the child objects

Now that the tables, types and procedures exist, grant the runtime identity's **child** rights
from the table above: data rights on the synced and tracking tables, `EXECUTE` on every
procedure, and `EXECUTE` on every table type.

### 6. Configure and deploy the Function

App settings (on Linux, write `:` as `__`, for example `SmartTableSync__TableList`):

| Setting | Value |
|---|---|
| `ParentDbConnectionString` | runtime identity → parent |
| `ChildDbConnectionString` | runtime identity → child |
| `SmartTableSync:TableList` | `Product,Customer,Document` |
| `SmartTableSync:Mode` | `Manual` |
| `SmartTableSync:TimestampColumns:<Table>`, `AllowFullRescanFor`, `IncrementalStrategyOverrides:<Table>`, `ReconcileIntervalHours`, `ChangeTrackingReconcileIntervalHours`, `ConnectionOpenAttempts` | optional, see [`local.settings.template.json`](../SmartTableSyncFunction/local.settings.template.json) |

A managed identity connection string looks like:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Managed Identity;User Id=<client id of a user-assigned identity>;Encrypt=True
```

Leave out `User Id` for a system-assigned identity. A host other than the bundled Function also
needs the `Microsoft.Data.SqlClient.Extensions.Azure` package for this to work.

Publish the Function, for example with `func azure functionapp publish <app-name>`.

### 7. Check the first run

The first run copies each table in full; later runs copy only changes. In the **child**:

```sql
SELECT TableName, IncrementalStrategy, MergeStrategy FROM dbo.SyncTableConfig;   -- the plan per table
SELECT TableName, LastSyncDate, IncrementalPosition FROM dbo.CustomSyncState;    -- where each table stopped
SELECT TOP (20) * FROM dbo.SyncBatchLog ORDER BY LogId DESC;                     -- recent batches
```

Logs and the `smarttablesync.*` metrics go to Application Insights.

## Later changes

- **Add a table**: steps 2 to 5 for that table, then add it to `SmartTableSync:TableList`.
- **A column is added in the parent**: the Function notices (the table's schema fingerprint
  changes) and re-plans the table. Run the scaffold tool for the table again: for an existing
  child table it writes `<Table>_AddColumns.sql` instead of `CREATE TABLE`, and a table type
  script that drops and recreates the type when it's out of date. Apply them in that order, then
  the procedure. Existing rows get values in the new columns when they next change; Auto mode
  re-scans the table instead, so they get them at once.
- **Other column changes** (type, length, primary key, a removed `NOT NULL` column): the scaffold
  tool prints `NEEDS A MANUAL CHANGE` with the details; change the child table yourself, then
  apply the regenerated table type and procedure.
- **Enable Change Tracking later**: step 2; the table switches to `SqlChangeTracking` on its
  next run by itself.

## Auto mode

In Auto mode the Function does steps 3 and 4 itself, so adding a table is a settings change.

1. Create the identities as in step 1. On the **child**, give the runtime identity its rights on
   the `dbo` schema instead of per object, before the first run. Schema grants also cover the
   tables, table types and procedures the Function creates later, and still allow no DDL:

   ```sql
   ALTER ROLE db_ddladmin ADD MEMBER [smarttablesync-child-provisioning];
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dbo TO [smarttablesync-runtime];
   ```

   These are broader than the per-object grants of Manual mode: the runtime identity can read
   and write every `dbo` table in the child, so keep the child database for SmartTableSync only.
2. Optional: prepare parent tables as in step 2.
3. Add two settings to the Function, next to those of step 6:

   | Setting | Value |
   |---|---|
   | `SmartTableSync:Mode` | `Auto` |
   | `ChildProvisioningConnectionString` | provisioning identity → child |

4. Deploy. On each run the Function first makes sure the tracking tables exist (a cheap
   `IF NOT EXISTS` check with the provisioning identity). The first time it sees a table, or
   after the table's parent schema changed, it creates the missing child table, the table type and
   the merge procedure, logs `<Table>: child DDL objects verified/created` and counts it in the
   `smarttablesync.table_provisioned` metric. Then it syncs.

To add a table later: grant the runtime identity read access to it on the **parent** (step 1),
then add it to `SmartTableSync:TableList`.

### When the parent schema changes

| Change in the parent | What Auto mode does |
|---|---|
| A column is added | Adds it to the child table (nullable, since existing rows have no value yet), recreates the table type and procedure, then **re-scans the table once** so every row gets the new values. Logged as a full re-scan with reason `schema_changed`. |
| A column is removed | Leaves a nullable child column in place (it doesn't block inserts) and logs a warning. A `NOT NULL` child column would block inserts, so that's reported as below. |
| A column's type or length changes, the primary key changes, or a column now allows `NULL` that the child doesn't | **Nothing is changed.** The table stops with an error naming the change, on every run, until you apply a reviewed script (the scaffold tool writes the new table type and procedure). Other tables keep syncing. |
| Change Tracking enabled or disabled | No child change; the table switches strategy and is re-scanned once. |

The new plan is saved only after the child changes and the re-scan succeeded. If anything fails
in between, the next run sees the change again and repeats the steps, which are safe to repeat.
The re-scan copies every row once, so on a very large table a schema change costs one full pass.

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| *Cannot find an authentication provider for 'ActiveDirectoryManagedIdentity'* | Since Microsoft.Data.SqlClient 7.0, Entra sign-in is a separate package | Your host needs `Microsoft.Data.SqlClient.Extensions.Azure` (the bundled Function and scaffold tool have it) |
| *VIEW DATABASE PERFORMANCE STATE permission denied* | Profiling a table without `VIEW DATABASE STATE` | Grant it on the parent to the runtime and introspection identities |
| *The EXECUTE permission was denied on the object '\<Table\>TableType'* | TVP batches without `EXECUTE` on the type | `GRANT EXECUTE ON TYPE::dbo.<Table>TableType TO [smarttablesync-runtime]` |
| *Cannot find the object ... or you do not have permissions* on `SET IDENTITY_INSERT` | A child table that has an `IDENTITY` column (created by an earlier version or by hand) | Recreate it from the scaffold tool's `CREATE TABLE` script (no `IDENTITY`), or `GRANT ALTER ON dbo.<Table> TO [smarttablesync-runtime]` on the child |
| *VIEW CHANGE TRACKING permission denied* | Change Tracking table without the grant | Apply the scaffolded `<Table>_ChangeTrackingSetup.sql`, which includes it |
| *... not currently available* on the first connection | A serverless database resuming from pause | Retried automatically (`SmartTableSync:ConnectionOpenAttempts`) |

## The demo as a worked example

[`demo/run-demo.ps1`](../demo/README.md) performs this guide end to end against new Azure
resources:

| Demo step | Guide step |
|---|---|
| 1/6 Terraform: SQL server, parent and child databases, managed identity `smarttablesync-runtime`, Function App | prerequisites, identity of step 1 |
| 2/6 creates and seeds the parent tables, `sql/02_parent_grants.sql` | step 1, parent rights |
| 3/6 `--change-tracking-setup Product`, applied to the parent | step 2 |
| 4/6 `--infrastructure` and `Product Customer Document`, applied to the child | steps 3 and 4 |
| 4/6 `sql/03_child_grants.sql` | step 5 |
| 5/6 publishes the Function with the settings Terraform wrote | step 6 |
| 6/6 triggers a sync and compares both databases | step 7 |

To keep the demo to one person and one script, it runs the scaffold tool and the SQL as you,
the SQL server's Entra admin, instead of separate introspection and provisioning identities.
The Function itself uses only the runtime identity, as in a real setup.
