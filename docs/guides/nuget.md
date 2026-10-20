# Use it from NuGet: install, set up, run and test

SmartTableSync is published on nuget.org as two packages:

| Package | What it is | Install |
|---|---|---|
| [SmartTableSync](https://www.nuget.org/packages/SmartTableSync) | the sync engine, a library for your .NET app | `dotnet add package SmartTableSync` |
| [SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold) | a command-line tool that writes the SQL the child database needs | `dotnet tool install --global SmartTableSync.Scaffold` |

While only pre-release versions exist (for example `0.5.0-preview.1`), add `--prerelease` to
these commands. What changed in each version is in the [changelog](../../CHANGELOG.md).

This page takes you from nothing to a running sync: first a **complete test on your own machine**
with SQL Server in Docker, then **your real databases**.

## What you need

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- for the local test: [Docker](https://docs.docker.com/get-docker/).

## 1. Create the app

A worker service is the simplest host. Any .NET app works the same way; see
[run it in your own app](hosting.md) for ASP.NET Core, Hangfire and Quartz.NET.

```bash
dotnet new worker -n MySync
cd MySync
dotnet add package SmartTableSync --prerelease
```

That's all for packages: `SmartTableSync` includes every sign-in method, including managed
identities and other Microsoft Entra methods for Azure SQL. (Version `0.5.0-preview.1` is the
exception: with it, also run `dotnet add package Microsoft.Data.SqlClient.Extensions.Azure --version 7.0.2`.)

Replace `Program.cs` with:

```csharp
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSmartTableSync();
builder.Services.AddHostedService<SyncWorker>();
builder.Build().Run();

public sealed class SyncWorker(ISyncOrchestrator orchestrator, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Syncs right away, then every minute; choose any interval
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await orchestrator.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sync failed; trying again next time");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

You can delete the `Worker.cs` file the template created.

## 2. Test it locally with SQL Server in Docker

This gives you a parent and a child database in a few minutes, with nothing installed but Docker.

**Start SQL Server** (choose your own strong password; it's only for this test):

```bash
docker run -d --name sync-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Test-Only-Pa55w0rd" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

**Create the two databases and a table with some rows**, with the `sqlcmd` inside the container
(wait about 20 seconds after starting it). These commands work in bash, zsh, PowerShell and CMD;
in **Git Bash on Windows**, run `export MSYS_NO_PATHCONV=1` first, or Git Bash rewrites the
`/opt/...` path and Docker can't find `sqlcmd`.

```bash
docker exec sync-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Test-Only-Pa55w0rd" -C -Q "
CREATE DATABASE ParentDb; CREATE DATABASE ChildDb;"
```

```bash
docker exec sync-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Test-Only-Pa55w0rd" -C -d ParentDb -Q "
CREATE TABLE dbo.Product (ProductId INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Price DECIMAL(10,2) NOT NULL, LastUpdate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
INSERT dbo.Product (ProductId, Name, Price, LastUpdate) SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CONCAT(N'Product ', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))), 9.99, DATEADD(HOUR, -1, SYSUTCDATETIME()) FROM sys.all_columns;"
```

The rows are dated an hour back, like data that already existed. (The Timestamp strategy re-reads
the last minute on every run, so rows changed seconds before the first sync are copied twice.
That's harmless, but it would make the counts below larger.)

**Point the app at them.** Replace `appsettings.json` with:

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "ParentDbConnectionString": "Server=localhost,1433;Database=ParentDb;User Id=sa;Password=Test-Only-Pa55w0rd;TrustServerCertificate=True",
  "ChildDbConnectionString": "Server=localhost,1433;Database=ChildDb;User Id=sa;Password=Test-Only-Pa55w0rd;TrustServerCertificate=True",
  "ChildProvisioningConnectionString": "Server=localhost,1433;Database=ChildDb;User Id=sa;Password=Test-Only-Pa55w0rd;TrustServerCertificate=True",
  "SmartTableSync": {
    "TableList": "Product",
    "Mode": "Auto"
  }
}
```

`Mode: Auto` lets the engine create the child's table itself, which keeps the test short. With
`sa` everything is allowed; for real databases you give each sign-in only the rights it needs
(step 3).

**Run it:**

```bash
dotnet run
```

The log shows what it does, ending with a line per table:

```text
info: SmartTableSync.Scaffolding.ChildDdlProvisioner[0]
      Product: child table missing - auto-creating
info: SmartTableSync.Orchestration.SyncOrchestrator[0]
      Product: 1000 synced, 0 deleted, 0 errors, 0.2s (initial)
```

**Check the child**, in a second terminal:

```bash
docker exec sync-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Test-Only-Pa55w0rd" -C -d ChildDb -Q "
SELECT COUNT(*) AS Products FROM dbo.Product;
SELECT TableName, IncrementalStrategy FROM dbo.SyncTableConfig;"
```

`1000` products, and the strategy it chose: `Timestamp`, because the table has a `LastUpdate`
column.

**Change data and watch it follow:**

```bash
docker exec sync-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Test-Only-Pa55w0rd" -C -d ParentDb -Q "
UPDATE dbo.Product SET Price = 19.99, LastUpdate = SYSUTCDATETIME() WHERE ProductId <= 10;
INSERT dbo.Product (ProductId, Name, Price) VALUES (5000, N'New product', 1.50);"
```

Within a minute the app logs `Product: 11 synced, ...`, and the child has the new prices and the
new row. Deletes in a Timestamp table arrive at the delete check, which runs once an hour and on
every start of the app; restart it with `Ctrl+C` and `dotnet run` to see one right away.

**Clean up** when you're done:

```bash
docker rm -f sync-sql
```

## 3. Use your real databases

The app stays the same; only the settings and the database preparation change.

1. **Connection strings.** Put yours in `appsettings.json`, environment variables or any other
   configuration source, under the same names. For Azure SQL without a password:
   `Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True`.
   Every setting is in the [configuration reference](../configuration.md).
2. **Rights.** Give the sign-in in `ParentDbConnectionString` read access to the listed tables, and
   the one in `ChildDbConnectionString` write access in the child. The exact grants are in the
   setup guides.
3. **Choose a mode:**
   - **Manual** (the default, for production): create the child's tables, table types and
     procedures with the scaffold tool, review the scripts, and apply them. The sync then needs no
     right to change any schema. Step by step: [Manual mode](manual-mode.md).

     ```bash
     dotnet tool install --global SmartTableSync.Scaffold --prerelease
     smarttablesync-scaffold --infrastructure --output sql/child
     smarttablesync-scaffold Product Customer --output sql/child
     ```

   - **Auto**: as in the local test, with a separate `ChildProvisioningConnectionString` that may
     create objects. Step by step: [Auto mode](auto-mode.md).
4. **Run it** wherever your app runs: a server, a container ([guide](containers.md)), Azure
   App Service or Functions.

## Check that it runs

On the child database:

```sql
SELECT TableName, IncrementalStrategy, MergeStrategy FROM dbo.SyncTableConfig;   -- the plan per table
SELECT TableName, LastSyncDate, IncrementalPosition FROM dbo.CustomSyncState;    -- where each table stopped
```

In the log, every run ends with one line per table: rows synced, deleted, errors, and duration.
For metrics and dashboards, see [observability](../observability.md).

## Updating to a new version

```bash
dotnet add package SmartTableSync
dotnet tool update --global SmartTableSync.Scaffold
```

Read the version's *Upgrading* part in the [changelog](../../CHANGELOG.md) first: it says when a
schema script or a new right is needed.
