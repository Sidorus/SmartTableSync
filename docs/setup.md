# Setting up SmartTableSync

This guide takes you from two databases to a running sync: which identities to create, exactly
which rights each one needs, and the steps with the scaffold tool. The examples use Azure SQL
Database with Microsoft Entra sign-in; for SQL Server on-premises, create SQL logins or Windows
users instead and grant them the same rights. The [Azure demo](../demo/README.md) runs every step
below with one script; the [last section](#the-demo-as-a-worked-example) maps its steps to this
guide.

## The pieces

| Piece | What it is |
|---|---|
| **Parent database** | Where the data is written by your applications. SmartTableSync only reads it. |
| **Child database** | Where SmartTableSync keeps copies of the parent's tables, plus three tracking tables. |
| **Host** | The app that runs the sync engine on a schedule. `SmartTableSyncFunction` in this repository is an example host: an Azure Function that runs every 5 minutes; change its timer to any interval you need. Or host the engine yourself, for example in a background service or a Hangfire or Quartz.NET job ([how](#hosting-the-engine-yourself)). |
| **Scaffold tool** (`SmartTableSync.Scaffold`, command `smarttablesync-scaffold`) | Reads the parent's schema and **writes** the SQL the child needs, for you to review and apply. It never changes a database. See its [README](../SmartTableSync.Scaffold/README.md). |

### Manual or Auto mode

Set with `SmartTableSync:Mode`.

- **Manual** (default): a person creates the child's tables and procedures from the scaffold
  tool's scripts. The engine only ever reads and writes data.
- **Auto**: the engine creates the child's tracking tables, tables and procedures itself when it
  first sees a table, using a separate provisioning identity, and adds columns that appear in the
  parent later. Changes that could lose data still need a reviewed script.

## Identities and their rights

Three identities, each with only what its job needs. [`sql/07_permissions.sql`](../sql/07_permissions.sql)
contains the full script for the example tables; replace the table names with yours. The
integration tests run that script as written, for Manual and for Auto mode, and then sync as
these identities with nothing more, so the rights below are tested on every build.

| Identity (name used in this guide) | Used by | Database | Rights |
|---|---|---|---|
| **Runtime** (`smarttablesync-runtime`) | the host, on every run | parent | `SELECT`, `VIEW DEFINITION` on each synced table; `VIEW DATABASE STATE`; `VIEW CHANGE TRACKING` on tables with Change Tracking |
| | | child | `SELECT, INSERT, UPDATE, DELETE` on each synced table; `SELECT, INSERT, UPDATE` on `CustomSyncState`, `SyncBatchLog`, `SyncTableConfig`; `EXECUTE` on each `sp_Sync<Table>` / `sp_Sync<Table>Batch` procedure; `EXECUTE` on each `<Table>TableType` type |
| **Parent introspection** (`smarttablesync-parent-introspection`) | the scaffold tool | parent | `VIEW DEFINITION`; `SELECT` on each synced table; `VIEW DATABASE STATE` |
| **Child provisioning** (`smarttablesync-child-provisioning`) | the scaffold tool (reads the existing child schema to decide what to write), whoever applies the child scripts, the host in Auto mode | child | `db_ddladmin` |

**Which kind of identity to use.** Prefer identities without passwords. The runtime identity
belongs to the host: on Azure, a managed identity of the Function App. The introspection and
provisioning identities are used by people, from a laptop with the scaffold tool, SSMS or
`sqlcmd`, where a managed identity can't sign in: use a Microsoft Entra user or group for them.
In Auto mode the host also signs in as the provisioning identity, so give that one a managed
identity too (a second one on the Function App), or add the host's identity to the same group.

Why the less obvious rights are needed:

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

You need:

- **The scaffold tool.** Until it is published on NuGet, run it from the repository with
  `dotnet run --project SmartTableSync.Scaffold -- <arguments>` wherever this guide writes
  `smarttablesync-scaffold <arguments>` ([more ways to run it](../SmartTableSync.Scaffold/README.md#install)).
- **Its two connection strings**, as environment variables (see
  [the scaffold README](../SmartTableSync.Scaffold/README.md#connection-strings)):
  `ParentIntrospectionConnectionString` (introspection identity → parent) and
  `ChildProvisioningConnectionString` (provisioning identity → child).
- **A way to run SQL scripts:** SSMS, Azure Data Studio or `sqlcmd`.

### 1. Create the identities

Create the database users, then grant the rights in the table above. For managed identities and
Entra accounts (for SQL logins, use `CREATE USER [name] FOR LOGIN [name]` instead):

```sql
-- On the parent
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
CREATE USER [smarttablesync-parent-introspection] FROM EXTERNAL PROVIDER;

-- On the child
CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
CREATE USER [smarttablesync-child-provisioning] FROM EXTERNAL PROVIDER;
ALTER ROLE db_ddladmin ADD MEMBER [smarttablesync-child-provisioning];
```

Then grant the **parent** rights for the runtime and introspection identities. The runtime
identity's **child** rights come in step 5, once the objects they refer to exist.

### 2. Optional: prepare the parent tables

SmartTableSync picks how to detect changes per table, in this order:

1. **Change Tracking**, if a DBA enabled it on the table: also catches deletes on every run.
2. **RowVersion**, if the table has a `rowversion` column.
3. **Timestamp**, if the table has a datetime column, in UTC, updated on every change
   (`LastUpdate` by default; another name through `SmartTableSync:TimestampColumns:<Table>`).
4. **Full re-scan**, for tables of up to 50,000 rows, or larger ones listed in
   `SmartTableSync:AllowFullRescanFor`.

[How SmartTableSync finds what changed](sync-strategies.md) shows each strategy step by step with
a diagram, to help you decide which one a table should use. Decide before the table's first
sync: the choice is kept until the table's schema changes (see
[changing a table's strategy later](how-it-works.md#changing-a-tables-strategy-later)).

To give a table a better strategy, generate the script and have a DBA review and apply it on
the **parent**:

```bash
smarttablesync-scaffold --change-tracking-setup Product      # enables Change Tracking, grants VIEW CHANGE TRACKING
smarttablesync-scaffold --rowversion-setup Customer          # adds a rowversion column and its index
```

The Change Tracking script grants `VIEW CHANGE TRACKING` to `smarttablesync-runtime`. If your
runtime identity has another name, change it in the script before applying.

#### Tables without a primary key

Rows are matched, paged and deleted by a key, and the child table gets that key as its
`PRIMARY KEY`. The key is chosen like this:

1. The table's **primary key**, when it has one.
2. Otherwise the **columns you name**, in key order, in `SmartTableSync:KeyColumns:<Table>`, for
   example `SmartTableSync:KeyColumns:sales.Ledger` = `Region,Code`. The columns must exist, be
   `NOT NULL` and not be `(max)` or `rowversion` columns. When the table is set up (and again
   whenever its schema changes), SmartTableSync checks the parent's data with a
   `GROUP BY ... HAVING COUNT(*) > 1` and stops the table with a clear message if any combination
   of values repeats. That check scans the table once; it isn't repeated on every run, so
   duplicates added later aren't caught.
3. Otherwise a **unique index** whose key columns are all `NOT NULL`, that isn't filtered and
   isn't disabled. With several, the one with the fewest key columns wins, then the oldest
   (lowest `index_id`). Included columns don't count. No setting is needed.

A table with none of these stops with a message naming the setting. Setting `KeyColumns` for a
table that has a primary key is an error too, rather than being ignored.

**Change Tracking** needs a real primary key: SQL Server won't enable it without one, and the
Change Tracking script refuses such a table. Tables keyed by a unique index or by `KeyColumns`
use RowVersion, Timestamp or a full re-scan. If you add a primary key to the parent later (and
remove the table's `KeyColumns` setting, if it has one), the next run notices the schema change
and picks the strategy again; Change Tracking becomes possible.

The key is part of the schema check, so changing `KeyColumns` (or the index it came from) makes
the next run set the table up again. The child's primary key then no longer matches: Auto mode
stops the table with `primary key changed`, and the scaffold tool prints `NEEDS A MANUAL CHANGE`
until you change the child table's key yourself.

### 3. Create the tracking tables in the child

```bash
smarttablesync-scaffold --infrastructure
```

Apply `sql/generated/00_infrastructure.sql` to the **child** as the provisioning identity.
Once per child database.

### 4. Generate and apply the child tables

List the tables:

```bash
smarttablesync-scaffold Product Customer Document
```

For each table it prints the plan it derived, the same plan the engine will derive, and writes:

| File | What it creates | When |
|---|---|---|
| `<Table>.sql` | the child table | only if the child table doesn't exist yet |
| `<Table>TableType.sql` | the table type for TVP batches | TVP tables |
| `sp_Sync<Table>Batch.sql` or `sp_Sync<Table>.sql` | the merge procedure | always |

Review them, then apply them to the **child** as the provisioning identity, in this order:
tables, table types, procedures.

**Tables in another schema.** Name them `schema.Table`, here and in `SmartTableSync:TableList`:
`smarttablesync-scaffold sales.Order`. The child gets the same schema: the table script creates
it if it's missing, and the table type and procedure are created in it too
(`[sales].[OrderTableType]`, `[sales].[sp_SyncOrderBatch]`). Their files start with the schema
(`sales.Order.sql`, `sales.OrderTableType.sql`, `sales.sp_SyncOrderBatch.sql`), so tables with
the same name in two schemas don't overwrite each other's scripts. Grant on them in step 5 with
their schema, for example `GRANT EXECUTE ON [sales].[sp_SyncOrderBatch] TO [smarttablesync-runtime]`.

### 5. Grant the runtime identity access to the child objects

Now that the tables, types and procedures exist, grant the runtime identity's **child** rights
from the table above: data rights on the synced and tracking tables, `EXECUTE` on every
procedure, and `EXECUTE` on every table type.

### 6. Configure and deploy the Function

This step uses the example Function; for your own host, use the same settings and see
[hosting the engine yourself](#hosting-the-engine-yourself).

The Function App must exist first, with the runtime identity attached (a user-assigned managed
identity), a storage account (`AzureWebJobsStorage`) and, optionally, Application Insights.
[`demo/terraform/functions.tf`](../demo/terraform/functions.tf) creates exactly that and can be
used as a template.

App settings (on Linux, write `:` as `__`, for example `SmartTableSync__TableList`):

| Setting | Value |
|---|---|
| `ParentDbConnectionString` | runtime identity → parent |
| `ChildDbConnectionString` | runtime identity → child |
| `SmartTableSync:TableList` | `Product,Customer,Document` |
| `SmartTableSync:Mode` | `Manual` |
| `SmartTableSync:TimestampColumns:<Table>`, `AllowFullRescanFor`, `IncrementalStrategyOverrides:<Table>`, `KeyColumns:<Table>`, `ReconcileIntervalHours`, `ChangeTrackingReconcileIntervalHours`, `ConnectionOpenAttempts` | optional, see [`local.settings.template.json`](../SmartTableSyncFunction/local.settings.template.json) |

A managed identity connection string looks like:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Managed Identity;User Id=<client id of a user-assigned identity>;Encrypt=True
```

Leave out `User Id` for a system-assigned identity. A host other than the bundled Function also
needs the `Microsoft.Data.SqlClient.Extensions.Azure` package for this to work.

The example Function runs every 5 minutes. For another interval, change the schedule in its
`TimerTrigger` (in `SmartTableSyncTimerFunction`) before publishing. Publish it, for example with
`func azure functionapp publish <app-name>`.

### 7. Check the first run

The first run copies each table in full; later runs copy only changes. In the **child**:

```sql
SELECT TableName, IncrementalStrategy, MergeStrategy FROM dbo.SyncTableConfig;   -- the plan per table
SELECT TableName, LastSyncDate, IncrementalPosition FROM dbo.CustomSyncState;    -- where each table stopped
SELECT TOP (20) * FROM dbo.SyncBatchLog ORDER BY LogId DESC;                     -- recent batches
```

### Logs and metrics

When the Function App is connected to Application Insights, the example Function sends its logs,
traces and the SmartTableSync metrics there through OpenTelemetry (`Program.cs`). Azure provides
the connection through the `APPLICATIONINSIGHTS_CONNECTION_STRING` setting; without it nothing is
exported and the sync runs as usual. In the Application Insights resource, open **Logs**:

```kusto
// What each run did, per table: "Product: 12 synced, 1 deleted, 0 errors, 0.8s (incremental)"
traces
| where message has "synced," or severityLevel >= 2
| project timestamp, severityLevel, message
| order by timestamp desc

// The SmartTableSync metrics: rows_synced, rows_errored, rows_deleted, sync_duration_seconds,
// sync_lag_minutes, full_rescans, table_provisioned
customMetrics
| where name startswith "smarttablesync."
| summarize sum(valueSum) by name, bin(timestamp, 15m)
```

`severityLevel` 2 is a warning and 3 an error; a failing table always shows up there. The
metrics can also be charted and alerted on under **Metrics**.

**In your own host** the choice is yours: the engine only writes through `ILogger` and
`System.Diagnostics.Metrics`, and has no reference to any monitoring product. With OpenTelemetry,
as in the example Function, register the `SmartTableSync` meter with the exporter you use
(Azure Monitor, OTLP, Prometheus, ...). With the older Application Insights SDK, remove its
default rule that keeps only warnings from your code, or the per-table summaries won't arrive;
that SDK doesn't collect the `SmartTableSync` meter, so the metrics still need OpenTelemetry.
Both send to the same Application Insights tables, so the portal looks the same either way.

## Later changes

- **Add a table**: grant the step 1 parent rights for that table (runtime: `SELECT`,
  `VIEW DEFINITION`; introspection: `SELECT`), then steps 2, 4 and 5 for it, then add it to
  `SmartTableSync:TableList`. Step 3 is needed only once per child database.
- **A column is added in the parent**: the engine notices (the table's schema fingerprint
  changes) and re-plans the table right away, so apply the child changes together with the
  parent change: until they are applied, that table's sync can fail. Run the scaffold tool for
  the table again: for an existing child table it writes `<Table>_AddColumns.sql` instead of
  `CREATE TABLE`, and a table type script that drops and recreates the type when it's out of
  date. Apply them in that order, then the procedure. Existing rows get values in the new columns
  when they next change; Auto mode re-scans the table instead, so they get them at once.
- **Other column changes** (type, length, primary key, a removed `NOT NULL` column, a column that
  now allows `NULL` in the parent but not in the child): the scaffold tool prints `NEEDS A MANUAL
  CHANGE` with the details; change the child table yourself, then apply the regenerated table
  type and procedure.
- **Enable Change Tracking later**: step 2; the table switches to `SqlChangeTracking` on its
  next run by itself, starting with a full re-scan because there is no saved Change Tracking
  version yet.
- **Change a strategy setting** (`IncrementalStrategyOverrides`, `TimestampColumns`,
  `AllowFullRescanFor`) for a table that is already set up: see
  [changing a table's strategy later](how-it-works.md#changing-a-tables-strategy-later).

## Auto mode

In Auto mode the engine does steps 3 and 4 itself, so adding a table is a settings change.

1. Create the identities as in step 1. On the **child**, give the runtime identity its rights on
   the `dbo` schema instead of per object, before the first run. Schema grants also cover the
   tables, table types and procedures the engine creates later, and still allow no DDL:

   ```sql
   ALTER ROLE db_ddladmin ADD MEMBER [smarttablesync-child-provisioning];
   GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dbo TO [smarttablesync-runtime];
   ```

   These are broader than the per-object grants of Manual mode: the runtime identity can read
   and write every `dbo` table in the child, so keep the child database for SmartTableSync only.
2. Optional: prepare parent tables as in step 2.
3. Give the host a way to sign in as the provisioning identity. On Azure: attach a second
   user-assigned managed identity to the Function App and create its user in the child as
   `smarttablesync-child-provisioning`. Then add two settings next to those of step 6:

   | Setting | Value |
   |---|---|
   | `SmartTableSync:Mode` | `Auto` |
   | `ChildProvisioningConnectionString` | provisioning identity → child (its own `User Id`, the client id of that second identity) |

4. Deploy. On each run the engine first makes sure the tracking tables exist (a cheap
   `IF NOT EXISTS` check with the provisioning identity). The first time it sees a table, or
   after the table's parent schema changed, it creates the missing child table, the table type and
   the merge procedure, logs `<Table>: child DDL objects verified/created` and counts it in the
   `smarttablesync.table_provisioned` metric. Then it syncs.

To add a table later: grant the runtime and introspection identities read access to it on the
**parent** (step 1), then add it to `SmartTableSync:TableList`.

For a table in a schema other than `dbo` (`sales.Order`), first create that schema in the
**child** and give the runtime identity the same schema-level grant there, as an administrator:

```sql
CREATE SCHEMA [sales];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[sales] TO [smarttablesync-runtime];
```

Auto mode would create a missing schema itself, but the runtime identity can't use its tables
until that grant exists.

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
A table seen for the first time isn't re-scanned: it gets its first copy instead.

## Hosting the engine yourself

The Azure Function in this repository is one example host. The engine is a library: reference
the `SmartTableSync.Core` project (NuGet package `SmartTableSync`, once published), register it
with `AddSmartTableSync()`, and call `ISyncOrchestrator.RunAsync` on whatever schedule you want,
from whatever scheduler you already use. Your host reads the same settings as the Function
(`ParentDbConnectionString`, `ChildDbConnectionString`, the `SmartTableSync:*` settings), from
`appsettings.json`, environment variables or any other configuration source.

Four things hold for every host:

- **`SmartTableSync:TableList` is required.** `AddSmartTableSync()` checks it when the host
  starts; an empty or duplicated list stops the host from starting.
- **Overlapping runs are safe.** If a run starts while the previous one is still working, it
  does nothing. This also holds across several machines or instances, through a lock in the child
  database.
- **Register the engine once** with `AddSmartTableSync()`. The orchestrator is a singleton and
  remembers when each table last ran its delete check.
- **A failing table doesn't stop the run.** It is logged and retried next run. Only a failure
  before any table starts, such as an unreachable child database, throws from `RunAsync`, so catch
  and log it in a loop you own.

All examples need these namespaces:

```csharp
using SmartTableSync.Abstractions;   // ISyncOrchestrator
using SmartTableSync.Hosting;        // AddSmartTableSync
```

**A background service** in any .NET host (ASP.NET Core, a Windows service, a container), with
no extra packages:

```csharp
builder.Services.AddSmartTableSync();
builder.Services.AddHostedService<SmartTableSyncWorker>();

public sealed class SmartTableSyncWorker(ISyncOrchestrator orchestrator, ILogger<SmartTableSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await orchestrator.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SmartTableSync run failed; trying again next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

**Hangfire**, as a recurring job, in an app where Hangfire is already set up (`AddHangfire` with
a storage, and `AddHangfireServer`). Register the job after the app is built, when Hangfire's
storage is ready; Hangfire passes its own cancellation token in place of `CancellationToken.None`:

```csharp
builder.Services.AddSmartTableSync();
var app = builder.Build();

app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ISyncOrchestrator>(
    "smarttablesync", orchestrator => orchestrator.RunAsync(CancellationToken.None), "*/5 * * * *");
```

**Quartz.NET**, as a job, with the `Quartz.Extensions.Hosting` package:

```csharp
builder.Services.AddSmartTableSync();
builder.Services.AddQuartz(q => q.ScheduleJob<SmartTableSyncJob>(
    trigger => trigger.WithCronSchedule("0 0/5 * * * ?")));
builder.Services.AddQuartzHostedService();

[DisallowConcurrentExecution]
public sealed class SmartTableSyncJob(ISyncOrchestrator orchestrator) : IJob
{
    public Task Execute(IJobExecutionContext context) => orchestrator.RunAsync(context.CancellationToken);
}
```

Every 5 minutes is only the example interval. Use whatever suits your data: every minute for
near real-time copies, or once a night for a reporting copy.

## Troubleshooting

| Error or symptom | Cause | Fix |
|---|---|---|
| *Cannot find an authentication provider for 'ActiveDirectoryManagedIdentity'* | Since Microsoft.Data.SqlClient 7.0, Entra sign-in is a separate package | Your host needs `Microsoft.Data.SqlClient.Extensions.Azure` (the bundled Function and scaffold tool have it) |
| *VIEW DATABASE PERFORMANCE STATE permission denied* | Profiling a table without `VIEW DATABASE STATE` | Grant it on the parent to the runtime and introspection identities |
| *The EXECUTE permission was denied on the object '\<Table\>TableType'* | TVP batches without `EXECUTE` on the type | `GRANT EXECUTE ON TYPE::dbo.<Table>TableType TO [smarttablesync-runtime]` |
| *Cannot find the object ... or you do not have permissions* on `SET IDENTITY_INSERT` | A child table that has an `IDENTITY` column (created by an earlier version or by hand) | Recreate it from the scaffold tool's `CREATE TABLE` script (no `IDENTITY`), or `GRANT ALTER ON dbo.<Table> TO [smarttablesync-runtime]` on the child |
| *VIEW CHANGE TRACKING permission denied* | Change Tracking table without the grant | Apply the scaffolded `<Table>_ChangeTrackingSetup.sql`, which includes it |
| *... not currently available* on the first connection | A serverless database resuming from pause | Retried automatically (`SmartTableSync:ConnectionOpenAttempts`) |
| The host doesn't start: *SmartTableSync:TableList must list at least one table name* | The table list is missing or empty | Set `SmartTableSync:TableList` |
| A Timestamp table misses some changes | `LastUpdate` holds local time, not UTC, on a server behind UTC | Set it with `SYSUTCDATETIME()` or `GETUTCDATE()` |
| A changed override or timestamp-column setting has no effect | The strategy is kept until the table's schema changes | Reset the table's plan: [changing a table's strategy later](how-it-works.md#changing-a-tables-strategy-later) |

## The demo as a worked example

[The demo](../demo/README.md) performs this guide end to end against new Azure resources with
`demo/run-demo.ps1`:

| Demo step | Guide step |
|---|---|
| 1/6 Terraform: SQL server, parent and child databases, managed identity `smarttablesync-runtime`, Function App | prerequisites, identity of step 1 |
| 2/6 creates and seeds the parent tables, `demo/sql/02_parent_grants.sql` | step 1, parent rights |
| 3/6 `--change-tracking-setup Product`, applied to the parent | step 2 |
| 4/6 `--infrastructure` and `Product Customer Document`, applied to the child | steps 3 and 4 |
| 4/6 `demo/sql/03_child_grants.sql` | step 5 |
| 5/6 publishes the Function with the settings Terraform wrote | step 6 |
| 6/6 triggers a sync and compares both databases | step 7 |

To keep the demo to one person and one script, it runs the scaffold tool and the SQL as you,
the SQL server's Entra admin, instead of separate introspection and provisioning identities.
The Function itself uses only the runtime identity, as in a real setup.
