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

    public ITableSyncer Create(TableProfile profile, SyncPlan plan) =>
        new ConfigurableTableSyncer(profile, plan, _batchLogger, _loggerFactory.CreateLogger<ConfigurableTableSyncer>());
}
