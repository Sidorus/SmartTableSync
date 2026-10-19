# Observability: logs and metrics

SmartTableSync has no monitoring product built in. The engine writes only through two standard
.NET interfaces, and the host decides where that data goes:

| What | .NET interface | Name |
|---|---|---|
| Logs | `ILogger` | categories under `SmartTableSync.` (for example `SmartTableSync.Orchestration.SyncOrchestrator`) |
| Metrics | `System.Diagnostics.Metrics` | the meter `SmartTableSync` (`SyncMetricsReporter.MeterName`) |

The engine emits no traces of its own. Any logging provider and any OpenTelemetry exporter can
collect the rest: Application Insights, Prometheus, Grafana, OTLP, the console. The
[example Function](#application-insights-the-example-function) sends both to Application
Insights; the sections below show the other options and how to turn things off.

## What is measured

| Metric | Type | Tags | What it tells you |
|---|---|---|---|
| `smarttablesync.rows_synced` | counter | `TableName`, `SyncMode` | rows written into the child |
| `smarttablesync.rows_errored` | counter | `TableName`, `SyncMode` | rows that couldn't be written (see [known limitations](../README.md#known-limitations)) |
| `smarttablesync.rows_deleted` | counter | `TableName` | rows removed from the child |
| `smarttablesync.sync_duration_seconds` | histogram | `TableName`, `SyncMode` | how long one table's run took |
| `smarttablesync.sync_lag_minutes` | histogram | `TableName` | time since the table's previous successful run, recorded on each incremental run |
| `smarttablesync.full_rescans` | counter | `TableName`, `Reason` | one-off full re-scans: after a schema change in Auto mode (`schema_changed`), or when Change Tracking's history can't be used (`no_position`, `position_expired`, `position_ahead`). A table on the FullRescan strategy isn't counted on every run |
| `smarttablesync.table_provisioned` | counter | `TableName` | a table's plan was saved: a new table, or a schema change (both modes) |

`SyncMode` is `initial` (the first copy) or `incremental`. `rows_deleted` and `sync_lag_minutes`
are recorded only for incremental runs.

Every run also writes one log line per table at Information level, for example
*"Product: 12 synced, 1 deleted, 0 errors, 0.8s (incremental)"*. Failures are warnings or errors.

## Application Insights (the example Function)

The example Function (`SmartTableSyncFunction/Program.cs`) uses OpenTelemetry with the Azure
Monitor exporter, as Microsoft's isolated worker guide recommends. It exports when Azure sets
`APPLICATIONINSIGHTS_CONNECTION_STRING`, which happens when the Function App is connected to
Application Insights; the demo does that. Logs land in `traces`, metrics in `customMetrics`.
Queries for both are in the setup guide under [logs and metrics](setup.md#logs-and-metrics).

In your own host, the same takes three lines (packages `OpenTelemetry.Extensions.Hosting` and
`Azure.Monitor.OpenTelemetry.Exporter`):

```csharp
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using SmartTableSync.Diagnostics;

builder.Services.AddSmartTableSync();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SyncMetricsReporter.MeterName))
    .UseAzureMonitorExporter();   // logs, traces and the metrics above
```

If your host uses the older Application Insights SDK instead: it keeps only warnings from your
code by default, so remove that rule to get the per-table lines, and it doesn't collect the
`SmartTableSync` meter, so the metrics still need OpenTelemetry. Both send to the same
Application Insights tables.

## Prometheus and Grafana

Pick by how your host runs.

### A long-running host: Prometheus scrapes it

For an ASP.NET Core app, a Windows service with a web endpoint, or a container: expose
`/metrics` and let Prometheus scrape it (package `OpenTelemetry.Exporter.Prometheus.AspNetCore`):

```csharp
using OpenTelemetry.Metrics;
using SmartTableSync.Diagnostics;

builder.Services.AddSmartTableSync();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(SyncMetricsReporter.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();
app.MapPrometheusScrapingEndpoint();   // GET /metrics
```

`OpenTelemetry.Exporter.Prometheus.AspNetCore` has been a pre-release package for a long time;
check its status before you rely on it in production, or use OTLP (below) instead.

Prometheus renames the metrics: dots become underscores, counters get `_total`, and a histogram
becomes `_bucket`, `_sum` and `_count`. So `smarttablesync.rows_synced` is
`smarttablesync_rows_synced_total`, and the tags are labels (`TableName`, `SyncMode`, `Reason`).

### Any host, including an Azure Function: push with OTLP

A timer Function has no permanent endpoint to scrape and can scale to zero, so it pushes its data
instead, with OTLP, the OpenTelemetry protocol (package `OpenTelemetry.Exporter.OpenTelemetryProtocol`):

```csharp
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using SmartTableSync.Diagnostics;

builder.Services.AddSmartTableSync();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(SyncMetricsReporter.MeterName)
        .AddOtlpExporter());
builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter());   // optional: the logs too
```

Set the destination with the standard `OTEL_EXPORTER_OTLP_ENDPOINT` setting (and
`OTEL_EXPORTER_OTLP_HEADERS` when it needs a key). Send to an OpenTelemetry Collector or
Grafana Alloy, which forward metrics to Prometheus or Mimir and logs to Loki, or straight to
Grafana Cloud, which accepts OTLP.

### Keep Application Insights, add Grafana on top

On Azure the easiest way needs no code change: **Azure Managed Grafana**, or *Dashboards with
Grafana* inside the Application Insights resource, reads the data the example Function already
sends. Use the Azure Monitor data source with the queries from
[logs and metrics](setup.md#logs-and-metrics).

### Example queries (PromQL)

```promql
# Rows copied per table, per minute
sum by (TableName) (rate(smarttablesync_rows_synced_total[15m])) * 60

# Tables with rows that failed to write in the last hour: alert when > 0
sum by (TableName) (increase(smarttablesync_rows_errored_total[1h])) > 0

# Average lag per table over the last 30 minutes, in minutes
sum by (TableName) (rate(smarttablesync_sync_lag_minutes_sum[30m]))
  / sum by (TableName) (rate(smarttablesync_sync_lag_minutes_count[30m]))

# Full re-scans by reason today
sum by (TableName, Reason) (increase(smarttablesync_full_rescans_total[1d]))
```

A useful alert besides errors is "no successful run for a while": no increase in
`smarttablesync_sync_duration_seconds_count` for a table over, for example, three times your
schedule interval.

## Less, or nothing at all

| You want | Do this |
|---|---|
| No metrics exported | Register no exporter for the `SmartTableSync` meter. The numbers are still produced but go nowhere, at almost no cost. |
| No metrics produced at all | Replace the reporter after `AddSmartTableSync()`: `services.AddSingleton<ISyncMetricsReporter, NoOpSyncMetricsReporter>();` |
| Your own metrics | Implement `ISyncMetricsReporter` and register it the same way. |
| Fewer logs | Standard .NET log levels, for example `"Logging": { "LogLevel": { "SmartTableSync": "Warning" } }` keeps only warnings and errors. |
| The example Function exporting nothing | Leave the Function App unconnected to Application Insights (no `APPLICATIONINSIGHTS_CONNECTION_STRING`). |

Planned next: a ready-made Grafana dashboard and example alert rules; see the
[roadmap](roadmap.md).
