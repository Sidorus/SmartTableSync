namespace SmartTableSync.Syncers;

public sealed class TableSyncerFactory : ITableSyncerFactory
{
    private readonly IBatchLogger _batchLogger;
    private readonly ILoggerFactory _loggerFactory;

    public TableSyncerFactory(IBatchLogger batchLogger, ILoggerFactory loggerFactory)
    {
        _batchLogger = batchLogger;
        _loggerFactory = loggerFactory;
    }

    public ITableSyncer Create(TableProfile profile, SyncPlan plan)
    {
        var logger = _loggerFactory.CreateLogger<ConfigurableTableSyncer>();
        var commandFactory = new TableSyncCommandFactory(profile, plan);
        var mergeStrategy = BatchMergeStrategyFactory.Create(profile, plan, logger);

        // Keyed by SyncPhase so ConfigurableTableSyncer's lookup reads by name, not position.
        // Lazy so whichever phase doesn't apply to a given SyncAsync call is never constructed.
        var syncRunners = new Dictionary<SyncPhase, Lazy<ISyncRunner>>
        {
            [SyncPhase.Initial] = new(() => new InitialSyncRunner(profile, plan, commandFactory, mergeStrategy, _batchLogger, logger)),
            [SyncPhase.Incremental] = new(() => new IncrementalSyncRunner(commandFactory, mergeStrategy)),
        };

        var deleteReconciler = new DeleteReconciler(profile, commandFactory, logger);

        return new ConfigurableTableSyncer(profile.TableName, syncRunners, deleteReconciler);
    }
}
