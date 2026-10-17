namespace SmartTableSync.Orchestration;

/// <summary>
/// The one ITableSyncer implementation, for every table: all per-table behavior comes from
/// the table's TableProfile and SyncPlan, through the runners TableSyncerFactory builds.
/// Picks the initial-copy runner until the table's first full copy has completed (lastSyncDate
/// is still the SyncConstants.InitialDate sentinel), the incremental runner after that.
/// </summary>
internal sealed class ConfigurableTableSyncer : ITableSyncer
{
    private readonly string _tableName;
    private readonly IReadOnlyDictionary<SyncPhase, Lazy<ISyncRunner>> _syncRunners;
    private readonly DeleteReconciler _deleteReconciler;

    public ConfigurableTableSyncer(
        string tableName, IReadOnlyDictionary<SyncPhase, Lazy<ISyncRunner>> syncRunners, DeleteReconciler deleteReconciler)
    {
        _tableName = tableName;
        _syncRunners = syncRunners;
        _deleteReconciler = deleteReconciler;
    }

    public string TableName => _tableName;

    public async Task<SyncResult> SyncAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var isInitial = SyncConstants.IsInitial(lastSyncDate);
        var outcome = await GetSyncRunner(isInitial).RunAsync(parent, child, lastSyncDate, ct);

        return new SyncResult(
            TableName, outcome.Merged, outcome.Errored, outcome.HasRetryableErrors, isInitial, stopwatch.Elapsed, outcome.Deleted);
    }

    public Task<int> ReconcileDeletesAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default) =>
        SyncConstants.IsInitial(lastSyncDate) ? Task.FromResult(0) : _deleteReconciler.ReconcileAsync(parent, child, ct);

    private ISyncRunner GetSyncRunner(bool isInitial) =>
        _syncRunners[isInitial ? SyncPhase.Initial : SyncPhase.Incremental].Value;
}
