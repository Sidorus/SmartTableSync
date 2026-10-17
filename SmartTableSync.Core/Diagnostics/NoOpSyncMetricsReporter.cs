namespace SmartTableSync.Diagnostics;

/// <summary>Default when no APM adapter is registered — e.g. a NuGet consumer that hasn't
/// wired up their own ISyncMetricsReporter yet. Deliberately does nothing.</summary>
public sealed class NoOpSyncMetricsReporter : ISyncMetricsReporter
{
    public void RowsSynced(string tableName, string syncMode, int count) { }
    public void RowsErrored(string tableName, string syncMode, int count) { }
    public void SyncDuration(string tableName, string syncMode, TimeSpan duration) { }
    public void RowsDeleted(string tableName, int count) { }
    public void SyncLag(string tableName, TimeSpan lag) { }
    public void TableProvisioned(string tableName) { }
}
