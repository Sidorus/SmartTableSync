namespace SmartTableSync.Orchestration;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    // Prevents concurrent invocations (e.g. manual trigger while timer run is in progress).
    // Timer triggers are already singleton by default via blob lease, but this guards
    // any manual/HTTP-triggered re-entry within the same app instance.
    private static readonly SemaphoreSlim _runLock = new(1, 1);
    private static readonly TimeSpan _reconcileInterval = TimeSpan.FromHours(1);
    private static DateTime _lastReconcileUtc = DateTime.MinValue;

    private readonly IReadOnlyList<string> _tableNames;
    private readonly SyncMode _mode;
    private readonly ISyncTableProvisioner _provisioner;
    private readonly ITableSyncerFactory _syncerFactory;
    private readonly IChildDdlProvisioner _childDdlProvisioner;
    private readonly IDbConnectionFactory _connFactory;
    private readonly ISyncStateRepository _syncState;
    private readonly ISyncMetricsReporter _metrics;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        ISyncTableProvisioner provisioner,
        ITableSyncerFactory syncerFactory,
        IChildDdlProvisioner childDdlProvisioner,
        IDbConnectionFactory connFactory,
        ISyncStateRepository syncState,
        ISyncMetricsReporter metrics,
        IOptions<SyncSettings> settings,
        ILogger<SyncOrchestrator> logger)
    {
        // Table names are now the only configuration surface for which tables get synced —
        // there's no fixed set of hand-written syncers left to fall back to, so an empty list
        // is a misconfiguration, not a valid "sync everything registered" default.
        _tableNames = settings.Value.Tables.Count > 0
            ? settings.Value.Tables
            : throw new InvalidOperationException(
                "SmartTableSync:TableList must list at least one table name.");

        _mode = settings.Value.Mode;
        _provisioner = provisioner;
        _syncerFactory = syncerFactory;
        _childDdlProvisioner = childDdlProvisioner;
        _connFactory = connFactory;
        _syncState = syncState;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0))
        {
            _logger.LogWarning("SmartTableSync: previous run still in progress — skipping this invocation");
            return;
        }

        try
        {
            _logger.LogInformation("SmartTableSync run started at {Time}", DateTimeOffset.UtcNow);

            var shouldReconcile = DateTime.UtcNow - _lastReconcileUtc >= _reconcileInterval;

            foreach (var tableName in _tableNames)
            {
                ct.ThrowIfCancellationRequested();
                await SyncTableAsync(tableName, shouldReconcile, ct);
            }

            if (shouldReconcile)
                _lastReconcileUtc = DateTime.UtcNow;

            _logger.LogInformation("SmartTableSync run completed at {Time}", DateTimeOffset.UtcNow);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task SyncTableAsync(string tableName, bool shouldReconcile, CancellationToken ct)
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

            // Cheap on every run unless the parent schema actually changed - see
            // SyncTableProvisioner. Building the syncer here (not at DI composition time)
            // is what lets one generic ConfigurableTableSyncer replace a fixed set of
            // hand-written classes: the table's shape and plan are only known once these
            // connections are open.
            var (profile, plan, wasFreshlyProvisioned) = await _provisioner.EnsureProvisionedAsync(parent, child, tableName, ct);

            if (_mode == SyncMode.Auto && wasFreshlyProvisioned)
            {
                await using var childProvisioning = _connFactory.CreateChildProvisioningConnection();
                await childProvisioning.OpenAsync(ct);
                await _childDdlProvisioner.EnsureChildObjectsExistAsync(childProvisioning, profile, plan, ct);
            }

            var syncer = _syncerFactory.Create(profile, plan);

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
            _logger.LogError(ex, "{Table}: sync failed — will retry on next run", tableName);
        }
    }

    private static async Task<DateTime> GetDbUtcNowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT GETUTCDATE()", connection);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
