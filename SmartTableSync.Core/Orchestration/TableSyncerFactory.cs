namespace SmartTableSync.Orchestration;

/// <summary>Composition root for one table's syncer: wires the plan's merge strategy and
/// incremental strategy (from the IncrementalStrategies registry) into a ConfigurableTableSyncer.</summary>
internal sealed class TableSyncerFactory : ITableSyncerFactory
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
        var commands = new TableSyncCommandFactory(profile, plan);
        var mergeStrategy = BatchMergeStrategyFactory.Create(profile, plan, logger);
        var strategy = IncrementalStrategies.For(plan.IncrementalStrategy);

        // Lazy: only the phase a given run needs is ever built.
        var syncRunners = new Dictionary<SyncPhase, Lazy<ISyncRunner>>
        {
            [SyncPhase.Initial] = new(() => CreateInitialRunner(profile, plan, commands, mergeStrategy, strategy.Baseline, logger)),
            [SyncPhase.Incremental] = new(() => strategy.CreateRunner(
                new RunnerServices(profile, plan, commands, mergeStrategy, _syncState, _metrics, logger))),
        };

        return new ConfigurableTableSyncer(profile.TableName, syncRunners, new DeleteReconciler(profile, commands, logger));
    }

    private ISyncRunner CreateInitialRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy,
        IncrementalBaseline? baseline, ILogger logger)
    {
        var initialSyncRunner = new InitialSyncRunner(profile, plan, commands, mergeStrategy, _batchLogger, logger);

        return baseline is null
            ? initialSyncRunner
            : new BaselineCapturingSyncRunner(initialSyncRunner, baseline, profile.TableName, _syncState, logger);
    }
}
