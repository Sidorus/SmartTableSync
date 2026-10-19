using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using SmartTableSync.Diagnostics;
using SmartTableSync.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Registers the sync engine, including its default ISyncMetricsReporter
        // (SmartTableSync.Diagnostics.SyncMetricsReporter), which emits through
        // System.Diagnostics.Metrics under the "SmartTableSync" meter, so any OpenTelemetry
        // exporter can collect it. Add your own ISyncMetricsReporter registration after this
        // call to replace it.
        services.AddSmartTableSync();

        // Logs: no logging provider is added here, so the worker sends its logs to the Functions
        // host, which writes them to Application Insights with their level and the function's
        // "Executing"/"Executed" lines, and shows them in the portal's Log stream. host.json sets
        // the level (Information, so each table's "N synced, N deleted" line is included).
        //
        // Metrics: the SmartTableSync meter goes to Application Insights through OpenTelemetry.
        // Azure sets APPLICATIONINSIGHTS_CONNECTION_STRING when the Function App is connected to
        // Application Insights; without it (for example when running locally) nothing is exported.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
        {
            services.AddOpenTelemetry().WithMetrics(metrics => metrics
                .AddMeter(SyncMetricsReporter.MeterName)
                .AddAzureMonitorMetricExporter());
        }
    })
    .Build();

await host.RunAsync();
