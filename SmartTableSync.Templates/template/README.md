# SmartTableSyncFunction

An Azure Function that keeps tables in a child Azure SQL database up to date with a parent
database, using [SmartTableSync](https://github.com/Sidorus/SmartTableSync). Created with
`dotnet new smarttablesync-azure`.

```text
SmartTableSyncFunction/        the Function: a timer that runs the sync every 5 minutes
quickstart/deploy.ps1          deploys it to Azure and starts the first sync
quickstart/terraform/          the Azure resources: Function App, storage, Application Insights
.config/dotnet-tools.json      the scaffold tool (SmartTableSync.Scaffold), used in Manual mode
Directory.Build.props          .NET 10, nullable, implicit usings
```

The Function uses the `SmartTableSync` NuGet package; the scaffold tool is the
`SmartTableSync.Scaffold` NuGet tool. Both have the version of the template you created this
project with.

## Deploy it to Azure

You need PowerShell 7.2+, the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli),
[Terraform](https://developer.hashicorp.com/terraform/install) 1.5+,
[go-sqlcmd](https://learn.microsoft.com/sql/tools/sqlcmd/go-sqlcmd-utility) and
[Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local).
The parent and the child database must already exist; the child may be empty.

**First, Change Tracking, if you use it.** SQL Server Change Tracking is the best way to find
changed rows, and it must be turned on in the parent for each table you want it for (the table
needs a primary key), before the setup. Run this on the parent, or add `-EnableChangeTracking`
to the command below and the script does it (the parent sign-in then needs `ALTER` rights):

```sql
ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
ALTER TABLE dbo.Product ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
```

Tables without it still sync, with a `rowversion` column, a timestamp column or a full re-scan.

```powershell
az login
az account set --subscription "<subscription name or id>"
```

**With your own connection strings.** They're saved as the Function's app settings exactly as
given. No identities, users or grants are created.

```powershell
./quickstart/deploy.ps1 -Suffix acme1 `
    -ParentConnectionString $env:PARENT_DB `
    -ChildConnectionString $env:CHILD_DB `
    -Tables "Product,Customer"
```

**Or with server and database names.** The script creates managed identities for the Function and
their database users, with exactly the rights they need. No password anywhere.

```powershell
./quickstart/deploy.ps1 -Suffix acme1 `
    -ParentServer myserver.database.windows.net -ParentDatabase SalesDb `
    -ChildDatabase SalesCopy `
    -Tables "Product,Customer"
```

Add `-DryRun` first to see what it would do without changing anything. Afterwards:

```powershell
./quickstart/deploy.ps1 -Suffix acme1 -SyncNow     # sync now and compare row counts
./quickstart/deploy.ps1 -Suffix acme1 -Destroy     # remove the Azure resources
```

Every parameter, the rights each database needs, and troubleshooting:
[the quick start guide](https://github.com/Sidorus/SmartTableSync/blob/main/quickstart/README.md).

## Change the code

`SmartTableSyncFunction/Functions/SmartTableSyncTimerFunction.cs` holds the schedule
(`0 */5 * * * *`, every 5 minutes). Every setting the sync reads is in the
[configuration reference](https://github.com/Sidorus/SmartTableSync/blob/main/docs/configuration.md).
After a change, run the setup command again, or publish only the code:

```powershell
cd SmartTableSyncFunction
func azure functionapp publish <function app name>
```

## Update to a new version

```powershell
dotnet add SmartTableSyncFunction package SmartTableSync
dotnet tool update SmartTableSync.Scaffold
```

Read the version's *Upgrading* part in the
[changelog](https://github.com/Sidorus/SmartTableSync/blob/main/CHANGELOG.md) first.
