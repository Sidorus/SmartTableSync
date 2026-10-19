using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
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

        // The Function's logs (Information and above, so each table's "N synced, N deleted"
        // line too), its traces, and the SmartTableSync metrics go to Application Insights
        // through OpenTelemetry. Azure sets APPLICATIONINSIGHTS_CONNECTION_STRING when the
        // Function App is connected to Application Insights; without it (for example when
        // running locally) nothing is exported and the Function runs as usual.
        var telemetry = services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
        telemetry.WithMetrics(metrics => metrics.AddMeter(SyncMetricsReporter.MeterName));

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
        {
            telemetry.UseAzureMonitorExporter();
        }
    })
    .Build();

await host.RunAsync();
