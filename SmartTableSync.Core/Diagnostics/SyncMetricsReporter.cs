using System.Diagnostics.Metrics;

namespace SmartTableSync.Diagnostics;

/// <summary>Default ISyncMetricsReporter implementation, built on System.Diagnostics.Metrics -
/// the .NET runtime's own vendor-neutral metrics API (built in since .NET 6, no package
/// dependency at all). Any OpenTelemetry exporter (OTLP, Prometheus, console, or Azure
/// Monitor's own OpenTelemetry distro) can collect these by listening on the "SmartTableSync"
/// meter name; which exporter (if any) is wired up is entirely the host's choice, not Core's -
/// this class doesn't know or care whether anything is listening.</summary>
public sealed class SyncMetricsReporter : ISyncMetricsReporter, IDisposable
{
    public const string MeterName = "SmartTableSync";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _rowsSynced;
    private readonly Counter<long> _rowsErrored;
    private readonly Histogram<double> _syncDurationSeconds;
    private readonly Counter<long> _rowsDeleted;
    private readonly Histogram<double> _syncLagMinutes;
    private readonly Counter<long> _tableProvisioned;
    private readonly Counter<long> _fullRescans;

    public SyncMetricsReporter()
    {
        _rowsSynced = _meter.CreateCounter<long>("smarttablesync.rows_synced");
        _rowsErrored = _meter.CreateCounter<long>("smarttablesync.rows_errored");
        _syncDurationSeconds = _meter.CreateHistogram<double>("smarttablesync.sync_duration_seconds");
        _rowsDeleted = _meter.CreateCounter<long>("smarttablesync.rows_deleted");
        _syncLagMinutes = _meter.CreateHistogram<double>("smarttablesync.sync_lag_minutes");
        _tableProvisioned = _meter.CreateCounter<long>("smarttablesync.table_provisioned");
        _fullRescans = _meter.CreateCounter<long>("smarttablesync.full_rescans");
    }

    public void RowsSynced(string tableName, string syncMode, int count) =>
        _rowsSynced.Add(count, new TagList { { "TableName", tableName }, { "SyncMode", syncMode } });

    public void RowsErrored(string tableName, string syncMode, int count) =>
        _rowsErrored.Add(count, new TagList { { "TableName", tableName }, { "SyncMode", syncMode } });

    public void SyncDuration(string tableName, string syncMode, TimeSpan duration) =>
        _syncDurationSeconds.Record(duration.TotalSeconds, new TagList { { "TableName", tableName }, { "SyncMode", syncMode } });

    public void RowsDeleted(string tableName, int count) =>
        _rowsDeleted.Add(count, new TagList { { "TableName", tableName } });

    public void SyncLag(string tableName, TimeSpan lag) =>
        _syncLagMinutes.Record(lag.TotalMinutes, new TagList { { "TableName", tableName } });

    public void TableProvisioned(string tableName) =>
        _tableProvisioned.Add(1, new TagList { { "TableName", tableName } });

    public void FullRescan(string tableName, string reason) =>
        _fullRescans.Add(1, new TagList { { "TableName", tableName }, { "Reason", reason } });

    public void Dispose() => _meter.Dispose();
}
