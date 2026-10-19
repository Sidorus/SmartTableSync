namespace SmartTableSync.Merge;

/// <summary>Picks the IBatchMergeStrategy for a table, once, from SyncPlan.MergeStrategy -
/// the strategy doesn't change mid-run, so ConfigurableTableSyncer resolves it once at
/// construction and shares the same instance between InitialSyncRunner and
/// IncrementalSyncRunner rather than re-branching on the enum per batch.</summary>
internal static class BatchMergeStrategyFactory
{
    public static IBatchMergeStrategy Create(TableProfile profile, SyncPlan plan, ILogger logger) =>
        plan.MergeStrategy switch
        {
            MergeStrategy.Tvp => new TvpBatchMergeStrategy(profile, plan),
            MergeStrategy.RowByRow => new RowByRowBatchMergeStrategy(profile, plan, logger),
            _ => throw new NotSupportedException($"Unhandled merge strategy: {plan.MergeStrategy}"),
        };
}
