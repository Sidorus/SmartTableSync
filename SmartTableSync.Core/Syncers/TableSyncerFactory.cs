namespace SmartTableSync.Syncers;

public sealed class TableSyncerFactory : ITableSyncerFactory
{
    private readonly IBatchLogger _batchLogger;
    private readonly ISyncStateRepository _syncState;
    private readonly ISyncMetricsReporter _metrics;
    private readonly ILoggerFactory _loggerFactory;

    public TableSyncerFactory(
        IBatchLogger batchLogger, ISyncStateRepository syncState, ISyncMetricsReporter metrics, ILoggerFactory loggerFactory)
    {
        _batchLogger = batchLogger;
        _syncState = syncState;
        _metrics = metrics;
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
            [SyncPhase.Initial] = new(() => CreateInitialRunner(profile, plan, commandFactory, mergeStrategy, logger)),
            [SyncPhase.Incremental] = new(() => IncrementalRunnerFactory.Create(profile, plan, commandFactory, mergeStrategy, _syncState, _metrics, logger)),
        };

        var deleteReconciler = new DeleteReconciler(profile, commandFactory, logger);

        return new ConfigurableTableSyncer(profile.TableName, syncRunners, deleteReconciler);
    }

    // A SqlChangeTracking table records its CT baseline before the initial copy starts, so the
    // first incremental cycle reads only what changed since then (see ChangeTrackingBaselineSyncRunner).
    private ISyncRunner CreateInitialRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy, ILogger logger)
    {
        var initialSyncRunner = new InitialSyncRunner(profile, plan, commandFactory, mergeStrategy, _batchLogger, logger);

        return plan.IncrementalStrategy == IncrementalStrategy.SqlChangeTracking
            ? new ChangeTrackingBaselineSyncRunner(initialSyncRunner, profile.TableName, _syncState, logger)
            : initialSyncRunner;
    }
}
