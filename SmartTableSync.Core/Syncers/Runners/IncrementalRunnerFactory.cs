namespace SmartTableSync.Syncers.Runners;

/// <summary>Picks the ISyncRunner for a table's incremental phase from
/// SyncPlan.IncrementalStrategy — mirrors BatchMergeStrategyFactory's shape exactly.</summary>
public static class IncrementalRunnerFactory
{
    public static ISyncRunner Create(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy, ILogger logger) =>
        plan.IncrementalStrategy switch
        {
            IncrementalStrategy.Timestamp => new IncrementalSyncRunner(commandFactory, mergeStrategy),
            IncrementalStrategy.FullRescan => new FullRescanSyncRunner(profile, plan, commandFactory, mergeStrategy, logger),
            _ => throw new NotSupportedException($"Unhandled incremental strategy: {plan.IncrementalStrategy}"),
        };
}
