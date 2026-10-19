namespace SmartTableSync.Orchestration;

/// <summary>One sync run: takes the run lock, then for each configured table provisions it
/// (checks the parent schema, derives or reuses its plan), runs its syncer, reconciles deletes
/// when due, records LastSyncDate and reports metrics. A failure in one table is logged and
/// retried next run without stopping the others.</summary>
internal sealed class SyncOrchestrator : ISyncOrchestrator
{
    // Per instance, not static: two orchestrators in one process (tests, or several child
    // databases) must not share run state. This lock only skips overlapping runs inside this
    // process cheaply; ISyncRunLock guarantees a single run across processes and machines.
    private readonly SemaphoreSlim _inProcessRunLock = new(1, 1);

    private readonly IReadOnlyList<string> _tableNames;
    private readonly SyncMode _mode;
    private readonly TimeSpan _reconcileInterval;
    private readonly TimeSpan _changeTrackingReconcileInterval;
    private readonly ISyncTableProvisioner _provisioner;
    private readonly ITableSyncerFactory _syncerFactory;
    private readonly IChildDdlProvisioner _childDdlProvisioner;
    private readonly IDbConnectionFactory _connFactory;
    private readonly ISyncRunLock _runLock;
    private readonly ISyncStateRepository _syncState;
    private readonly ISyncMetricsReporter _metrics;
    private readonly ILogger<SyncOrchestrator> _logger;

    // Per table, because tables on different incremental strategies reconcile on different
    // intervals. A table missing here hasn't reconciled in this process yet, so it's due.
    private readonly Dictionary<string, DateTime> _lastReconcileUtc = new(StringComparer.OrdinalIgnoreCase);

    public SyncOrchestrator(
        ISyncTableProvisioner provisioner,
        ITableSyncerFactory syncerFactory,
        IChildDdlProvisioner childDdlProvisioner,
        IDbConnectionFactory connFactory,
        ISyncRunLock runLock,
        ISyncStateRepository syncState,
        ISyncMetricsReporter metrics,
        IOptions<SyncSettings> settings,
        ILogger<SyncOrchestrator> logger)
    {
        // AddSmartTableSync already validates this at startup; kept for hosts that construct
        // the orchestrator without it.
        _tableNames = settings.Value.Tables.Count > 0
            ? settings.Value.Tables
            : throw new InvalidOperationException(
                "SmartTableSync:TableList must list at least one table name.");

        _mode = settings.Value.Mode;
        _reconcileInterval = settings.Value.ReconcileInterval;
        _changeTrackingReconcileInterval = settings.Value.ChangeTrackingReconcileInterval;
        _provisioner = provisioner;
        _syncerFactory = syncerFactory;
        _childDdlProvisioner = childDdlProvisioner;
        _connFactory = connFactory;
        _runLock = runLock;
        _syncState = syncState;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await _inProcessRunLock.WaitAsync(0, ct))
        {
            _logger.LogWarning("SmartTableSync: previous run still in progress - skipping this invocation");
            return;
        }

        try
        {
            await using var databaseLock = await _runLock.TryAcquireAsync(ct);
            if (databaseLock is null)
            {
                _logger.LogWarning("SmartTableSync: another instance is already syncing this child database - skipping this invocation");
                return;
            }

            _logger.LogInformation("SmartTableSync run started at {Time}", DateTimeOffset.UtcNow);

            if (_mode == SyncMode.Auto)
            {
                await using var childProvisioning = _connFactory.CreateChildProvisioningConnection();
                await childProvisioning.OpenAsync(ct);
                await _childDdlProvisioner.EnsureInfrastructureExistsAsync(childProvisioning, ct);
            }

            foreach (var tableName in _tableNames)
            {
                ct.ThrowIfCancellationRequested();
                await SyncTableAsync(tableName, ct);
            }

            _logger.LogInformation("SmartTableSync run completed at {Time}", DateTimeOffset.UtcNow);
        }
        finally
        {
            _inProcessRunLock.Release();
        }
    }

    private async Task SyncTableAsync(string tableName, CancellationToken ct)
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

            // Cheap unless the parent schema changed (see SyncTableProvisioner). The syncer is
            // built here, per run, because the table's shape and plan are only known now.
            var provisioning = await _provisioner.PlanAsync(parent, child, tableName, ct);
            var (profile, plan) = (provisioning.Profile, provisioning.Plan);
            var lastSync = await _syncState.GetLastSyncDateAsync(child, tableName, ct);

            // A new plan is saved only after the child matches it: in Auto mode a failed DDL step
            // or re-scan leaves the old plan in place, so the next run tries again.
            if (provisioning.Change != ProvisioningChange.None)
            {
                if (_mode == SyncMode.Auto && !await ApplyChildSchemaAsync(parent, child, provisioning, lastSync, ct))
                {
                    return;
                }

                await _provisioner.SaveAsync(child, provisioning, ct);
            }

            var syncer = _syncerFactory.Create(profile, plan);
            var shouldReconcile = IsReconcileDue(tableName, plan.IncrementalStrategy);

            // Use the parent DB clock so LastSyncDate is comparable to column LastUpdate
            // values that SQL Server stamped - eliminates any app-server clock skew gap.
            var syncStartedAt = await GetDbUtcNowAsync(parent, ct);

            var result = await syncer.SyncAsync(parent, child, lastSync, ct);
            // Deletes applied during the pass itself (SqlChangeTracking) plus any found by the
            // periodic key-set reconciliation.
            var deleted = result.RowsDeleted + (shouldReconcile
                ? await syncer.ReconcileDeletesAsync(parent, child, lastSync, ct)
                : 0);

            // Don't advance the timestamp in two cases:
            // 1. Initial sync had errors - keeps InitialDate so next run re-enters initial sync
            //    mode and resumes from the saved resume key rather than switching to incremental.
            // 2. Incremental sync had retryable errors (e.g. an FK timing race) -
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

            // Only advance once this table's whole pass (sync + reconcile) succeeded - a table
            // whose reconcile threw must not wait a full interval before its next attempt.
            if (shouldReconcile)
            {
                _lastReconcileUtc[tableName] = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Table}: sync failed - will retry on next run", tableName);
        }
    }

    // Auto mode: creates or evolves the child objects for a new plan. After a parent schema change
    // on a table that was already synced, one full re-scan copies every row again, so existing
    // rows get values in added columns; incremental strategies would only copy rows that change.
    // Returns false when the re-scan had errors: the new plan then stays unsaved and the next
    // run evolves (idempotently) and re-scans again.
    private async Task<bool> ApplyChildSchemaAsync(
        SqlConnection parent, SqlConnection child, TableProvisioning provisioning, DateTime lastSync, CancellationToken ct)
    {
        await using (var childProvisioning = _connFactory.CreateChildProvisioningConnection())
        {
            await childProvisioning.OpenAsync(ct);
            await _childDdlProvisioner.EnsureChildObjectsExistAsync(childProvisioning, provisioning.Profile, provisioning.Plan, ct);
        }

        if (provisioning.Change != ProvisioningChange.SchemaChanged || SyncConstants.IsInitial(lastSync))
        {
            return true;
        }

        var tableName = provisioning.Profile.TableName;
        _logger.LogWarning("{Table}: parent schema changed - running one full re-scan so every child row matches the new schema", tableName);
        _metrics.FullRescan(tableName, "schema_changed");

        var rescanPlan = provisioning.Plan with { IncrementalStrategy = IncrementalStrategy.FullRescan, IncrementalColumn = null };
        var rescan = await _syncerFactory.Create(provisioning.Profile, rescanPlan).SyncAsync(parent, child, lastSync, ct);
        if (rescan.RowsErrored == 0 && !rescan.HasRetryableErrors)
        {
            return true;
        }

        _logger.LogWarning("{Table}: the re-scan after the schema change had {Errors} error(s) - it runs again next run",
            tableName, rescan.RowsErrored);
        return false;
    }

    // SqlChangeTracking tables get deletes from CT itself every cycle, so their full key-set
    // comparison is only a rare safety net (SyncSettings.ChangeTrackingReconcileInterval,
    // zero = never). Every other strategy has no other source of deletes and uses
    // SyncSettings.ReconcileInterval (default hourly).
    private bool IsReconcileDue(string tableName, IncrementalStrategy strategy)
    {
        var interval = strategy == IncrementalStrategy.SqlChangeTracking
            ? _changeTrackingReconcileInterval
            : _reconcileInterval;

        if (interval <= TimeSpan.Zero)
        {
            return false;
        }

        var lastReconcileUtc = _lastReconcileUtc.GetValueOrDefault(tableName, DateTime.MinValue);
        return DateTime.UtcNow - lastReconcileUtc >= interval;
    }

    private static async Task<DateTime> GetDbUtcNowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT GETUTCDATE()", connection);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
