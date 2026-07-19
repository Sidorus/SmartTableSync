namespace SmartTableSync.Syncers.MergeStrategies;

/// <summary>
/// Strategy for merging one already-loaded batch of parent rows into the child table.
/// Picked once per table by BatchMergeStrategyFactory from SyncPlan.MergeStrategy — Tvp
/// (one stored-procedure call via a table-valued parameter) or RowByRow (one call per row,
/// for FK-timing isolation or oversized-row isolation) — and shared by both
/// InitialSyncRunner (PK-violation fallback) and IncrementalSyncRunner (every batch).
/// </summary>
public interface IBatchMergeStrategy
{
    Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct);
}
