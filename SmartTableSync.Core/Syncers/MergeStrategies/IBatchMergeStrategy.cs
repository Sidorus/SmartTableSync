namespace SmartTableSync.Syncers.MergeStrategies;

/// <summary>Merges one loaded batch of parent rows into the child table (insert or update).
/// Tvp sends the whole batch in one stored-procedure call; RowByRow calls the procedure once
/// per row so one bad row can't fail the rest. Chosen per table by BatchMergeStrategyFactory
/// from SyncPlan.MergeStrategy.</summary>
public interface IBatchMergeStrategy
{
    /// <summary>Throws only for a failure of the whole batch; per-row failures are counted in
    /// the returned outcome.</summary>
    Task<SyncOutcome> MergeBatchAsync(DataTable batch, SqlConnection child, CancellationToken ct);
}
