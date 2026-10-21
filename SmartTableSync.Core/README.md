# SmartTableSync

A self-configuring .NET engine that keeps tables synchronized from one SQL database to another
(parent to child). Give it table names: it inspects each table's schema, picks how to detect
changes and how to merge them, and keeps the child up to date on the schedule your host chooses.

Built for Azure SQL Database, where it runs in production, and works with Azure SQL Managed
Instance and SQL Server (on-premises or in a VM) in any combination.

## What it does

- Syncs the tables you list, one way: inserts, updates and deletes.
- Chooses per table how to find changed rows: SQL Server Change Tracking, a `rowversion` column,
  a timestamp column, or a full re-scan for small tables.
- Copies a new table in full once, then only what changed, and resumes where it stopped.
- Needs no DDL rights at run time and signs in with managed identities or any SQL Server sign-in.
- Notices schema changes in the parent; in Auto mode it adds new columns to the child by itself.

## Install

```bash
dotnet add package SmartTableSync
```

That's the only package you need: it includes every sign-in method, from SQL logins and Windows
authentication to managed identities and other Microsoft Entra methods
(`Authentication=Active Directory ...` in the connection string).

## Use it

Register the engine and run it on your own schedule, here every 5 minutes in a background service:

```csharp
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSmartTableSync();
builder.Services.AddHostedService<SyncWorker>();
builder.Build().Run();

public sealed class SyncWorker(ISyncOrchestrator orchestrator) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            await orchestrator.RunAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

`appsettings.json`:

```json
{
  "ParentDbConnectionString": "Server=tcp:myserver.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True",
  "ChildDbConnectionString": "Server=tcp:myserver.database.windows.net,1433;Database=ChildDb;Authentication=Active Directory Managed Identity;User Id=<client id>;Encrypt=True",
  "SmartTableSync": {
    "TableList": "Product,Customer,sales.Order",
    "Mode": "Manual"
  }
}
```

The child database needs a table, a table type and a merge procedure for every synced table.
In Manual mode (the default) you create them from the scripts of the
[SmartTableSync.Scaffold](https://www.nuget.org/packages/SmartTableSync.Scaffold) tool; in Auto
mode the engine creates them itself with a separate provisioning connection
(`ChildProvisioningConnectionString`).

Hangfire, Quartz.NET and other hosts work the same way: call `ISyncOrchestrator.RunAsync` from
your scheduler. Overlapping runs are safe, also across several instances.

## How changes are found

| Strategy | Used when | Deletes reach the child |
|---|---|---|
| Change Tracking | SQL Server Change Tracking is on for the table | on every run |
| RowVersion | the table has a `rowversion` column | at the delete check, hourly by default |
| Timestamp | the table has a UTC datetime column updated on every change (default `LastUpdate`) | at the delete check |
| Full re-scan | none of the above, and the table has up to 50,000 rows | at the delete check |

## Documentation

- [Overview and guides](https://github.com/Sidorus/SmartTableSync#readme)
- [Manual mode, step by step](https://github.com/Sidorus/SmartTableSync/blob/main/docs/guides/manual-mode.md)
- [Auto mode, step by step](https://github.com/Sidorus/SmartTableSync/blob/main/docs/guides/auto-mode.md)
- [Run it in your own app: background service, Hangfire, Quartz.NET](https://github.com/Sidorus/SmartTableSync/blob/main/docs/guides/hosting.md)
- [Every setting, with examples](https://github.com/Sidorus/SmartTableSync/blob/main/docs/configuration.md)
- [Identities, rights and troubleshooting](https://github.com/Sidorus/SmartTableSync/blob/main/docs/setup.md)
- [How each strategy finds changes](https://github.com/Sidorus/SmartTableSync/blob/main/docs/sync-strategies.md)
- [Known limitations](https://github.com/Sidorus/SmartTableSync/blob/main/docs/limitations.md)
- [Changelog](https://github.com/Sidorus/SmartTableSync/blob/main/CHANGELOG.md)

## Feedback

Tried it? Tell me what worked and what didn't:
[report a bug or ask for a feature](https://github.com/Sidorus/SmartTableSync/issues/new/choose),
[ask a question](https://github.com/Sidorus/SmartTableSync/discussions/categories/q-a), or
[report a security problem privately](https://github.com/Sidorus/SmartTableSync/security/advisories/new).

## License

Apache License 2.0. See [LICENSE](https://github.com/Sidorus/SmartTableSync/blob/main/LICENSE) and
[NOTICE](https://github.com/Sidorus/SmartTableSync/blob/main/NOTICE).
