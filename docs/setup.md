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
  it first sees a table, using a separate provisioning identity. It creates missing tables but
  never alters an existing one; a later column change still needs a reviewed script.

## Identities and their rights

Three identities, each with only what its job needs. Prefer managed identities or Microsoft
Entra accounts, so there are no passwords to store. [`sql/07_permissions.sql`](../sql/07_permissions.sql)
contains the full script for the example tables; replace the table names with yours.

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
- **Tables with an `IDENTITY` column**: keeping the parent's identity values uses
  `SET IDENTITY_INSERT`, which SQL Server only allows with `ALTER` on the table. Until the engine
  stops needing it, grant the runtime identity `ALTER` on those child tables, and only on those.

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
- **A column changes in the parent**: the Function notices (the table's schema fingerprint
  changes) and re-plans the table. Run the scaffold tool for the table again: it skips
  `CREATE TABLE` because the child table exists, so add the column to the child table by hand,
  then apply the regenerated table type and procedure.
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

What Auto mode doesn't do: it never alters an existing child table, and it doesn't recreate an
existing table type. When a column changes in the parent, update the child table and the table
type with a reviewed script (the scaffold tool generates the new versions), as in Manual mode.
Tables with an `IDENTITY` column also need `ALTER` for the runtime identity, as in Manual mode.

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| *Cannot find an authentication provider for 'ActiveDirectoryManagedIdentity'* | Since Microsoft.Data.SqlClient 7.0, Entra sign-in is a separate package | Your host needs `Microsoft.Data.SqlClient.Extensions.Azure` (the bundled Function and scaffold tool have it) |
| *VIEW DATABASE PERFORMANCE STATE permission denied* | Profiling a table without `VIEW DATABASE STATE` | Grant it on the parent to the runtime and introspection identities |
| *The EXECUTE permission was denied on the object '\<Table\>TableType'* | TVP batches without `EXECUTE` on the type | `GRANT EXECUTE ON TYPE::dbo.<Table>TableType TO [smarttablesync-runtime]` |
| *Cannot find the object ... or you do not have permissions* on `SET IDENTITY_INSERT` | Table with an `IDENTITY` column | `GRANT ALTER ON dbo.<Table> TO [smarttablesync-runtime]` on the child |
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
