namespace SmartTableSync.Syncers.Runners;

/// <summary>Picks the ISyncRunner for a table's incremental phase from
/// SyncPlan.IncrementalStrategy — mirrors BatchMergeStrategyFactory's shape exactly.</summary>
public static class IncrementalRunnerFactory
{
    public static ISyncRunner Create(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy,
        ISyncStateRepository syncState, ILogger logger) =>
        plan.IncrementalStrategy switch
        {
            IncrementalStrategy.Timestamp => new IncrementalSyncRunner(commandFactory, mergeStrategy),
            IncrementalStrategy.FullRescan => new FullRescanSyncRunner(profile, plan, commandFactory, mergeStrategy, logger),
            IncrementalStrategy.RowVersion => new RowVersionSyncRunner(profile, commandFactory, mergeStrategy, syncState, logger),
            IncrementalStrategy.SqlChangeTracking => new ChangeTrackingSyncRunner(
                profile, commandFactory, mergeStrategy,
                new FullRescanSyncRunner(profile, plan, commandFactory, mergeStrategy, logger),
                syncState, logger),
            _ => throw new NotSupportedException($"Unhandled incremental strategy: {plan.IncrementalStrategy}"),
        };
}
