namespace SmartTableSync.Syncers;

public interface ITableSyncer
{
    string TableName { get; }

    Task<SyncResult> SyncAsync(
        SqlConnection parent,
        SqlConnection child,
        DateTime lastSyncDate,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes rows from the child that no longer exist in the parent.
    /// Skipped during initial sync to avoid false deletes from a partially-populated child.
    /// </summary>
    Task<int> ReconcileDeletesAsync(
        SqlConnection parent,
        SqlConnection child,
        DateTime lastSyncDate,
        CancellationToken ct = default);
}
