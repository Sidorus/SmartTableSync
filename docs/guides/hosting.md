# Run it in your own app: background service, Hangfire, Quartz.NET

The Azure Function in this repository is only one example host. The engine is a library: register
it with `AddSmartTableSync()` and call `ISyncOrchestrator.RunAsync` on whatever schedule you want,
from whatever scheduler you already use. The host decides how often it runs: every minute for a
near real-time copy, once a night for a reporting copy.

The databases are prepared the same way whatever the host:
[Manual mode](manual-mode.md) (steps 1-7) or [Auto mode](auto-mode.md) (steps 1-3). This page
covers the host itself.

## What every host needs

1. **The engine.** Reference the `SmartTableSync.Core` project (the NuGet package `SmartTableSync`
   once it's published):

   ```bash
   dotnet add reference ../SmartTableSync/SmartTableSync.Core/SmartTableSync.Core.csproj
   ```

2. **For Microsoft Entra sign-in** (`Authentication=Active Directory ...` in a connection string,
   including managed identities), the package that has those sign-in methods, in the same version
   as `Microsoft.Data.SqlClient`. Not needed for SQL logins or Windows authentication.

   ```bash
   dotnet add package Microsoft.Data.SqlClient.Extensions.Azure
   ```

3. **The settings**, in `appsettings.json`, environment variables or any configuration source.
   The connection strings are top-level settings, not in a `ConnectionStrings` section:

   ```json
   {
     "ParentDbConnectionString": "Server=sql01.corp.local;Database=ParentDb;Integrated Security=True;Encrypt=True",
     "ChildDbConnectionString": "Server=sql01.corp.local;Database=ChildDb;Integrated Security=True;Encrypt=True",
     "SmartTableSync": {
       "TableList": "Product,Customer,sales.Order",
       "Mode": "Manual"
     }
   }
   ```

   Auto mode also needs `ChildProvisioningConnectionString`. Every setting is in the
   [configuration reference](../configuration.md).

4. **These namespaces:**

   ```csharp
   using SmartTableSync.Abstractions;   // ISyncOrchestrator
   using SmartTableSync.Hosting;        // AddSmartTableSync
   ```

Four things hold for every host:

- **`SmartTableSync:TableList` is required.** It's checked when the host starts; an empty or
  duplicated list stops the host from starting.
- **Register the engine once** with `AddSmartTableSync()`. The orchestrator is a singleton and
  remembers when each table last ran its delete check.
- **Overlapping runs are safe.** A run that starts while the previous one is still working does
  nothing. This holds across several machines or instances too, through a lock in the child
  database.
- **A failing table doesn't stop the run.** It's logged and retried next run. Only a failure
  before any table starts, such as an unreachable child database, throws from `RunAsync`, so
  catch and log it in a loop you own.

## Background service

Any .NET host (a worker service, ASP.NET Core, a Windows service, a container), no extra
packages. A new worker project:

```bash
dotnet new worker -n SmartTableSyncWorker
cd SmartTableSyncWorker
dotnet add reference ../SmartTableSync/SmartTableSync.Core/SmartTableSync.Core.csproj
dotnet add package Microsoft.Data.SqlClient.Extensions.Azure
```

`Program.cs`:

```csharp
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSmartTableSync();
builder.Services.AddHostedService<SmartTableSyncWorker>();
builder.Build().Run();

public sealed class SmartTableSyncWorker(ISyncOrchestrator orchestrator, ILogger<SmartTableSyncWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
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

Put the settings in `appsettings.json` and run it with `dotnet run`. It syncs right away, then
every 5 minutes. In ASP.NET Core, the same two `builder.Services` lines go into your existing
`Program.cs`.

## Hangfire

As a recurring job, in an app where Hangfire is already set up (`AddHangfire` with a storage, and
`AddHangfireServer`). With the SQL Server storage:

```bash
dotnet add package Hangfire.AspNetCore
dotnet add package Hangfire.SqlServer
```

```csharp
using Hangfire;
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHangfire(config => config.UseSqlServerStorage(builder.Configuration.GetConnectionString("Hangfire")));
builder.Services.AddHangfireServer();
builder.Services.AddSmartTableSync();

var app = builder.Build();

// Registered after the app is built, when Hangfire's storage is ready. Hangfire passes its own
// cancellation token in place of CancellationToken.None when it runs the job.
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ISyncOrchestrator>(
    "smarttablesync", orchestrator => orchestrator.RunAsync(CancellationToken.None), "*/5 * * * *");

app.Run();
```

The job shows up in the Hangfire dashboard as `smarttablesync`, where you can also start it by
hand. The cron expression `*/5 * * * *` is every 5 minutes.

## Quartz.NET

As a job, with the hosting package:

```bash
dotnet add package Quartz.Extensions.Hosting
```

```csharp
using Quartz;
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSmartTableSync();
builder.Services.AddQuartz(q => q.ScheduleJob<SmartTableSyncJob>(
    trigger => trigger.WithCronSchedule("0 0/5 * * * ?")));
builder.Services.AddQuartzHostedService();
builder.Build().Run();

// One run at a time per scheduler; the engine's own lock covers other instances
[DisallowConcurrentExecution]
public sealed class SmartTableSyncJob(ISyncOrchestrator orchestrator) : IJob
{
    public Task Execute(IJobExecutionContext context) => orchestrator.RunAsync(context.CancellationToken);
}
```

Quartz cron has a seconds field: `0 0/5 * * * ?` is every 5 minutes.

## Logs and metrics in your host

The engine writes through `ILogger` and `System.Diagnostics.Metrics` only, so logs go wherever
your host sends them. For the metrics, register the `SmartTableSync` meter with your exporter, for
example OpenTelemetry with Prometheus or Azure Monitor:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter(SmartTableSync.Diagnostics.SyncMetricsReporter.MeterName)
    .AddPrometheusExporter());
```

Every metric, and Application Insights, Prometheus, Grafana and OTLP setups, are in
[observability](../observability.md).

## Replacing parts of the engine

Register your own implementation after `AddSmartTableSync()` to replace:

| Service | Default | Replace it to |
|---|---|---|
| `ISyncRunLock` | an application lock in the child database | use a lock you already have (Redis, a blob lease) |
| `IDbConnectionFactory` | `SqlConnection` from the connection-string settings | get connections or tokens your own way |
| `ISyncMetricsReporter` | the `SmartTableSync` meter | report to something that isn't `System.Diagnostics.Metrics` |
