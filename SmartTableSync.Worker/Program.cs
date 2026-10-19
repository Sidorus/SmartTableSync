using OpenTelemetry.Metrics;
using SmartTableSync.Abstractions;
using SmartTableSync.Diagnostics;
using SmartTableSync.Hosting;
using SmartTableSync.Worker;

// Settings come from environment variables (SmartTableSync__TableList, ParentDbConnectionString,
// ...), appsettings.json or any other configuration source; see docs/guides/containers.md.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSmartTableSync();
builder.Services.Configure<WorkerSettings>(builder.Configuration.GetSection(WorkerSettings.SectionName));

// Logs go to the console (stdout), where every container platform collects them. Metrics go out
// over OTLP only when a collector is configured with the standard OTEL_EXPORTER_OTLP_ENDPOINT.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
        .AddMeter(SyncMetricsReporter.MeterName)
        .AddOtlpExporter());
}

var runOnce = args.Contains("--once")
    || builder.Configuration.GetSection(WorkerSettings.SectionName).GetValue<bool>(nameof(WorkerSettings.RunOnce));

if (!runOnce)
{
    builder.Services.AddHostedService<SyncWorker>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Exit code 1, with the reason in the log, when the host can't start: for example a missing
// SmartTableSync:TableList, or (run once) a run that can't begin because a database is unreachable.
try
{
    if (!runOnce)
    {
        await host.RunAsync();
        return 0;
    }

    // Run once, for a scheduler (Kubernetes CronJob, Azure Container Apps job). Starting the host
    // starts the metrics exporter; stopping it flushes the metrics before the process ends. A table
    // that fails is logged and doesn't fail the run.
    await host.StartAsync();
    await host.Services.GetRequiredService<ISyncOrchestrator>()
        .RunAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
    await host.StopAsync();
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "SmartTableSync stopped: {Reason}", ex.Message);
    return 1;
}
