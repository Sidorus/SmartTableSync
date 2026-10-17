namespace SmartTableSync.Syncers.Runners;

/// <summary>
/// SqlChangeTracking strategy: reads only the keys changed since the stored version, from
/// CHANGETABLE(CHANGES ...), with deletes reported directly. The position lives in
/// CustomSyncState.IncrementalPosition as "CT:&lt;version&gt;".
///
/// When the version chain is broken — no position yet, a position older than
/// CHANGE_TRACKING_MIN_VALID_VERSION (retention exceeded, TRUNCATE TABLE, Change Tracking
/// re-enabled) or newer than CHANGE_TRACKING_CURRENT_VERSION (parent restored from a backup) —
/// it runs one full re-scan plus a delete reconciliation, then resumes from the version
/// captured before that re-scan.
/// </summary>
internal sealed class ChangeTrackingSyncRunner : ChangeWindowSyncRunner<long>
{
    private readonly ISyncRunner _fullRescanRunner;
    private readonly DeleteReconciler _deleteReconciler;
    private readonly ISyncStateRepository _syncState;
    private readonly ISyncMetricsReporter _metrics;

    // Read once per run in OpenWindowAsync; decides how every page of that run is read.
    private bool _snapshotIsolationAllowed;

    public ChangeTrackingSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy,
        ISyncRunner fullRescanRunner, DeleteReconciler deleteReconciler, ISyncStateRepository syncState,
        ISyncMetricsReporter metrics, ILogger logger)
        : base(profile, plan, commands, mergeStrategy, logger)
    {
        _fullRescanRunner = fullRescanRunner;
        _deleteReconciler = deleteReconciler;
        _syncState = syncState;
        _metrics = metrics;
    }

    protected override async Task<ChangeWindow<long>> OpenWindowAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        _snapshotIsolationAllowed = await ChangeTrackingVersions.IsSnapshotIsolationAllowedAsync(parent, ct);

        var from = ChangeTrackingVersions.ParsePosition(
            await _syncState.GetIncrementalPositionAsync(child, Profile.TableName, ct));
        var (minValid, current) = await ChangeTrackingVersions.GetBoundsAsync(parent, null, Profile.TableName, ct);

        var rescanReason = from switch
        {
            null => new FullRescanReason("no_position", "no Change Tracking position yet"),
            var position when position < minValid => new FullRescanReason("position_expired",
                $"position {position} is older than CHANGE_TRACKING_MIN_VALID_VERSION ({minValid}) — " +
                "retention exceeded, table truncated, or Change Tracking re-enabled"),
            var position when position > current => new FullRescanReason("position_ahead",
                $"position {position} is ahead of CHANGE_TRACKING_CURRENT_VERSION ({current}) — " +
                "was the parent database restored from a backup?"),
            _ => null,
        };

        return new ChangeWindow<long>(From: from ?? current, To: current, rescanReason);
    }

    /// <summary>Each page is read in its own transaction: under SNAPSHOT isolation (the default
    /// in Azure SQL Database) the version check and the page see the same point in time and
    /// parent writers aren't blocked. If tracking data older than the window was cleaned up
    /// meanwhile, the page throws: the position isn't advanced, and the next run sees the
    /// expired position and re-scans.</summary>
    protected override async Task<DataTable> ReadPageAsync(
        SqlConnection parent, ChangeWindow<long> window, string? afterKey, int pageSize, CancellationToken ct)
    {
        try
        {
            await using var transaction = (SqlTransaction)await parent.BeginTransactionAsync(
                _snapshotIsolationAllowed ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, ct);

            await EnsureWindowStillValidAsync(parent, transaction, window, ct);
            var page = await LoadPageAsync(
                Commands.BuildChangeTrackingPageCommand(parent, transaction, window.From, afterKey, pageSize), ct);

            // Without a snapshot, cleanup could have run between the check and the read.
            if (!_snapshotIsolationAllowed)
            {
                await EnsureWindowStillValidAsync(parent, transaction, window, ct);
            }

            await transaction.CommitAsync(ct);
            return page;
        }
        finally
        {
            if (_snapshotIsolationAllowed)
            {
                await ResetIsolationLevelAsync(parent, ct);
            }
        }
    }

    protected override Task CommitWindowAsync(SqlConnection child, ChangeWindow<long> window, CancellationToken ct) =>
        _syncState.UpdateIncrementalPositionAsync(child, Profile.TableName, ChangeTrackingVersions.FormatPosition(window.To), ct);

    protected override async Task<SyncOutcome> RunFullRescanAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, ChangeWindow<long> window, CancellationToken ct)
    {
        var reason = window.FullRescanRequired!;
        Logger.LogWarning(
            "{Table}: {Reason} — running a full re-scan, then resuming Change Tracking from version {Version}",
            Profile.TableName, reason.Description, window.To);
        _metrics.FullRescan(Profile.TableName, reason.Category);

        var outcome = await _fullRescanRunner.RunAsync(parent, child, lastSyncDate, ct);

        // A re-scan only inserts and updates. Deletes made while the chain was broken are exactly
        // what Change Tracking can no longer report, so compare the key sets now.
        outcome += SyncOutcome.None with { Deleted = await _deleteReconciler.ReconcileAsync(parent, child, ct) };

        // Resume Change Tracking only after a clean re-scan: a row that failed to merge must be
        // read again, and only another re-scan guarantees that. window.To was captured before the
        // re-scan, so changes made during it are read next run.
        if (outcome.Errored == 0 && !outcome.HasRetryableErrors)
        {
            await CommitWindowAsync(child, window, ct);
        }
        else
        {
            Logger.LogWarning(
                "{Table}: full re-scan had {Errors} errors — Change Tracking position not set, the re-scan repeats next run",
                Profile.TableName, outcome.Errored);
        }

        return outcome;
    }

    private async Task EnsureWindowStillValidAsync(
        SqlConnection parent, SqlTransaction transaction, ChangeWindow<long> window, CancellationToken ct)
    {
        var (minValid, _) = await ChangeTrackingVersions.GetBoundsAsync(parent, transaction, Profile.TableName, ct);
        if (window.From < minValid)
        {
            throw new InvalidOperationException(
                $"{Profile.TableName}: Change Tracking data for version {window.From} was cleaned up while it was being " +
                $"read (min valid version is now {minValid}). The position was not advanced; the next run re-scans the table.");
        }
    }

    // SET TRANSACTION ISOLATION LEVEL outlives the transaction and would leak to the pooled
    // connection. Called from a finally block, so failures are logged, not thrown — throwing
    // would hide the exception already propagating.
    private async Task ResetIsolationLevelAsync(SqlConnection parent, CancellationToken ct)
    {
        if (parent.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var reset = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", parent);
            await reset.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is SqlException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Table}: could not reset the parent connection's isolation level", Profile.TableName);
        }
    }
}
