namespace SmartTableSync.Orchestration;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    // Prevents concurrent invocations (e.g. manual trigger while timer run is in progress).
    // Timer triggers are already singleton by default via blob lease, but this guards
    // any manual/HTTP-triggered re-entry within the same app instance.
    private static readonly SemaphoreSlim s_runLock = new(1, 1);
    private static readonly TimeSpan s_reconcileInterval = TimeSpan.FromHours(1);
    private static DateTime s_lastReconcileUtc = DateTime.MinValue;

    private readonly IEnumerable<ITableSyncer> _syncers;
    private readonly IDbConnectionFactory _connFactory;
    private readonly ISyncStateRepository _syncState;
    private readonly ISyncMetricsReporter _metrics;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IEnumerable<ITableSyncer> syncers,
        IDbConnectionFactory connFactory,
        ISyncStateRepository syncState,
        ISyncMetricsReporter metrics,
        IOptions<SyncSettings> settings,
        ILogger<SyncOrchestrator> logger)
    {
        var configuredTables = settings.Value.Tables;

        // Empty list means "not configured yet" — fall back to every registered syncer
        // rather than silently syncing nothing, so this stays backward-compatible with
        // deployments that haven't set SmartTableSync:TableList.
        _syncers = configuredTables.Count == 0
            ? syncers
            : syncers.Where(s => configuredTables.Contains(s.TableName, StringComparer.OrdinalIgnoreCase));

        _connFactory = connFactory;
        _syncState = syncState;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await s_runLock.WaitAsync(0))
        {
            _logger.LogWarning("SmartTableSync: previous run still in progress — skipping this invocation");
            return;
        }

        try
        {
            _logger.LogInformation("SmartTableSync run started at {Time}", DateTimeOffset.UtcNow);

            var shouldReconcile = DateTime.UtcNow - s_lastReconcileUtc >= s_reconcileInterval;

            foreach (var syncer in _syncers)
            {
                ct.ThrowIfCancellationRequested();
                await SyncTableAsync(syncer, shouldReconcile, ct);
            }

            if (shouldReconcile)
                s_lastReconcileUtc = DateTime.UtcNow;

            _logger.LogInformation("SmartTableSync run completed at {Time}", DateTimeOffset.UtcNow);
        }
        finally
        {
            s_runLock.Release();
        }
    }

    private async Task SyncTableAsync(
        ITableSyncer syncer,
        bool shouldReconcile, CancellationToken ct)
    {
        try
        {
            // Short-lived connections per table: Azure SQL drops idle connections
            // after 30 min, so a long initial sync on the first table would leave
            // subsequent tables with a broken shared connection.
            await using var parent = _connFactory.CreateParentConnection();
            await using var child = _connFactory.CreateChildConnection();
            await parent.OpenAsync(ct);
            await child.OpenAsync(ct);

            // Use the parent DB clock so LastSyncDate is comparable to column LastUpdate
            // values that SQL Server stamped — eliminates any app-server clock skew gap.
            var syncStartedAt = await GetDbUtcNowAsync(parent, ct);

            var lastSync = await _syncState.GetLastSyncDateAsync(child, syncer.TableName, ct);
            var result = await syncer.SyncAsync(parent, child, lastSync, ct);
            var deleted = shouldReconcile
                ? await syncer.ReconcileDeletesAsync(parent, child, lastSync, ct)
                : 0;

            // Don't advance the timestamp in two cases:
            // 1. Initial sync had errors — keeps InitialDate so next run re-enters initial sync
            //    mode and resumes from the saved resume key rather than switching to incremental.
            // 2. Incremental sync had retryable errors (e.g. FK timing race on AttributeValue) —
            //    keeps LastSyncDate so the failed rows fall inside the next run's query window
            //    and are retried. MERGE is idempotent so re-processing successful rows is safe.
            var initialSyncFailed = result.IsInitialSync && result.RowsErrored > 0;
            if (!initialSyncFailed && !result.HasRetryableErrors)
                await _syncState.UpdateLastSyncDateAsync(child, syncer.TableName, syncStartedAt, ct);

            var mode = result.IsInitialSync ? "initial" : "incremental";

            _logger.LogInformation(
                "{Table}: {Synced} synced, {Deleted} deleted, {Errors} errors, {Duration:s\\.f}s ({Mode})",
                result.TableName, result.RowsSynced, deleted, result.RowsErrored, result.Duration, mode);

            _metrics.RowsSynced(result.TableName, mode, result.RowsSynced);
            _metrics.RowsErrored(result.TableName, mode, result.RowsErrored);
            _metrics.SyncDuration(result.TableName, mode, result.Duration);

            // Lag and deletes are only meaningful in incremental mode; during initial sync
            // lastSync is 2000-01-01, which would produce misleading metric values.
            if (!result.IsInitialSync)
            {
                _metrics.RowsDeleted(result.TableName, deleted);
                _metrics.SyncLag(result.TableName, syncStartedAt - lastSync);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Table}: sync failed — will retry on next run", syncer.TableName);
        }
    }

    private static async Task<DateTime> GetDbUtcNowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT GETUTCDATE()", connection);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
