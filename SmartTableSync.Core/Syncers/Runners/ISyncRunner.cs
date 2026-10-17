namespace SmartTableSync.Syncers.Runners;

/// <summary>One sync pass over one table: the initial copy (InitialSyncRunner) or an
/// incremental pass (one ChangeWindowSyncRunner subclass per IncrementalStrategy).
/// ConfigurableTableSyncer picks which one runs; TableSyncerFactory builds them.</summary>
internal interface ISyncRunner
{
    /// <param name="lastSyncDate">The orchestrator's last successful sync time for the table.
    /// Only the Timestamp strategy filters by it; strategies with their own position ignore it.</param>
    Task<SyncOutcome> RunAsync(SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct);
}
