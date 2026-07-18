using Microsoft.ApplicationInsights;
using SmartTableSync.Observability;

namespace SmartTableSync.Functions.Observability;

/// <summary>Application Insights adapter for ISyncMetricsReporter — lives in the Function
/// host, not Core, so Core has no hard dependency on any specific APM package. Preserves
/// the exact metric names/dimensions the original SyncOrchestrator emitted directly.</summary>
public sealed class AppInsightsMetricsReporter : ISyncMetricsReporter
{
    private readonly TelemetryClient _telemetry;

    public AppInsightsMetricsReporter(TelemetryClient telemetry) => _telemetry = telemetry;

    public void RowsSynced(string tableName, string syncMode, int count) =>
        _telemetry.GetMetric("SmartTableSync.RowsSynced", "TableName", "SyncMode")
                  .TrackValue(count, tableName, syncMode);

    public void RowsErrored(string tableName, string syncMode, int count) =>
        _telemetry.GetMetric("SmartTableSync.RowsErrored", "TableName", "SyncMode")
                  .TrackValue(count, tableName, syncMode);

    public void SyncDuration(string tableName, string syncMode, TimeSpan duration) =>
        _telemetry.GetMetric("SmartTableSync.SyncDurationSeconds", "TableName", "SyncMode")
                  .TrackValue(duration.TotalSeconds, tableName, syncMode);

    public void RowsDeleted(string tableName, int count) =>
        _telemetry.GetMetric("SmartTableSync.RowsDeleted", "TableName")
                  .TrackValue(count, tableName);

    public void SyncLag(string tableName, TimeSpan lag) =>
        _telemetry.GetMetric("SmartTableSync.SyncLagMinutes", "TableName")
                  .TrackValue(lag.TotalMinutes, tableName);

    public void TableProvisioned(string tableName) =>
        _telemetry.TrackEvent("SmartTableSync.TableProvisioned", new Dictionary<string, string> { ["TableName"] = tableName });
}
