using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartTableSync.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Wires the Function's own logs/traces/requests into Application Insights — the
        // Functions host's built-in diagnostics pipeline, unrelated to the sync engine's own
        // metrics below.
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Registers the sync engine, including its default ISyncMetricsReporter
        // (SmartTableSync.Observability.SyncMetricsReporter) — emits via
        // System.Diagnostics.Metrics under the "SmartTableSync" meter name, so it's
        // collectible by whatever OpenTelemetry exporter this host wants (Azure Monitor's
        // OpenTelemetry distro, OTLP, Prometheus, ...), not tied to any one vendor. Wire up
        // an exporter here if you want these metrics collected; add your own
        // ISyncMetricsReporter registration after this call to replace them entirely.
        services.AddSmartTableSync();
    })
    .Build();

await host.RunAsync();
