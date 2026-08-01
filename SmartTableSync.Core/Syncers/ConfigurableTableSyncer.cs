namespace SmartTableSync.Syncers;

/// <summary>
/// A single, config-driven ITableSyncer implementation that replaces one hand-written
/// subclass per table. Behavior that used to come from abstract-method overrides
/// (ProductAttributeValueSyncer, ProductCatalogItemSyncer, DocumentAssetSyncer, all
/// extending TableSyncerBase) is derived here from a TableProfile (schema shape) and a
/// SyncPlan (merge strategy/batch size/etc.), both produced upstream by introspection and
/// heuristic derivation rather than hand-declared.
///
/// Pure orchestration: holds the table's ISyncRunner strategies as a lazily-populated
/// dictionary keyed by SyncPhase (built by TableSyncerFactory) and looks up the right one
/// by lastSyncDate — each entry is only constructed the first time the phase it represents
/// is actually needed. Builds nothing itself; TableSyncerFactory is the composition root
/// that wires the concrete strategies (batch-merge, initial, incremental) together and
/// hands this class the finished dictionary.
/// </summary>
public sealed class ConfigurableTableSyncer : ITableSyncer
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
        var runner = GetSyncRunner(isInitial);
        var (synced, errored, hasRetryable) = await runner.RunAsync(parent, child, lastSyncDate, ct);

        return new SyncResult(TableName, synced, errored, hasRetryable, isInitial, stopwatch.Elapsed);
    }

    public Task<int> ReconcileDeletesAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default) =>
        SyncConstants.IsInitial(lastSyncDate) ? Task.FromResult(0) : _deleteReconciler.ReconcileAsync(parent, child, ct);

    private ISyncRunner GetSyncRunner(bool isInitial) =>
        _syncRunners[isInitial ? SyncPhase.Initial : SyncPhase.Incremental].Value;
}
