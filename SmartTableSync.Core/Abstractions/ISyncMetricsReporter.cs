namespace SmartTableSync.Abstractions;

/// <summary>Metrics/events emitted during a sync run. Kept vendor-neutral so Core has no
/// hard dependency on any specific APM package — the Function host supplies an
/// App-Insights-backed implementation; Core itself only needs NoOpSyncMetricsReporter.</summary>
public interface ISyncMetricsReporter
{
    void RowsSynced(string tableName, string syncMode, int count);
    void RowsErrored(string tableName, string syncMode, int count);
    void SyncDuration(string tableName, string syncMode, TimeSpan duration);

    /// <summary>Incremental-mode only — meaningless during initial sync, where lastSync is
    /// the sentinel date.</summary>
    void RowsDeleted(string tableName, int count);

    /// <summary>Incremental-mode only, same reason as RowsDeleted.</summary>
    void SyncLag(string tableName, TimeSpan lag);

    /// <summary>Fired the first time a table is auto-provisioned or re-provisioned after a
    /// schema change (Planning/ISyncTableProvisioner) — keeps otherwise-silent
    /// automatic provisioning observable.</summary>
    void TableProvisioned(string tableName);

    /// <summary>A table ran a full re-scan instead of its incremental pass: a SqlChangeTracking
    /// table whose version chain was broken, or, in Auto mode, any table after its parent schema
    /// changed. reason is a fixed category ("no_position", "position_expired", "position_ahead",
    /// "cleaned_up_during_read", "schema_changed"), never free text, so it is safe as a metric
    /// tag. Has a default no-op body so existing implementations keep compiling.</summary>
    void FullRescan(string tableName, string reason)
    {
    }
}
