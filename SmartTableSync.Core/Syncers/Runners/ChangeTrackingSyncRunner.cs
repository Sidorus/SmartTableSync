namespace SmartTableSync.Syncers.Runners;

/// <summary>Runs the incremental-sync path for a table with SQL Server Change Tracking enabled:
/// reads only the keys changed since the last persisted version (CHANGETABLE(CHANGES ...)),
/// joined back to the table for current values, then MERGEs inserts/updates and deletes
/// deleted keys from the child. Owns its position in CustomSyncState.IncrementalPosition, the
/// same precedent RowVersionSyncRunner set.
///
/// Self-healing: whenever the version chain is broken — no position yet, position older than
/// CHANGE_TRACKING_MIN_VALID_VERSION (retention exceeded, TRUNCATE TABLE, CT re-enabled), or
/// position newer than CHANGE_TRACKING_CURRENT_VERSION (parent restored from a backup) — it
/// falls back to one full re-scan and re-baselines at the version captured BEFORE that re-scan,
/// so no change made during the re-scan is lost.</summary>
public sealed class ChangeTrackingSyncRunner : ISyncRunner
{
    private readonly TableProfile _profile;
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly IBatchMergeStrategy _mergeStrategy;
    private readonly ISyncRunner _fullRescanRunner;
    private readonly ISyncStateRepository _syncState;
    private readonly ChildRowDeleter _rowDeleter;
    private readonly ILogger _logger;

    public ChangeTrackingSyncRunner(
        TableProfile profile, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy,
        ISyncRunner fullRescanRunner, ISyncStateRepository syncState, ILogger logger)
    {
        _profile = profile;
        _commandFactory = commandFactory;
        _mergeStrategy = mergeStrategy;
        _fullRescanRunner = fullRescanRunner;
        _syncState = syncState;
        _rowDeleter = new ChildRowDeleter(profile);
        _logger = logger;
    }

    // lastSyncDate is unused for the query — this strategy's position is
    // CustomSyncState.IncrementalPosition. Passed through only to the full re-scan fallback.
    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var lastVersion = ChangeTrackingVersions.ParsePosition(
            await _syncState.GetIncrementalPositionAsync(child, _profile.TableName, ct));

        var read = await ReadChangesAsync(parent, lastVersion, ct);
        if (read.RescanReason is not null)
            return await RescanAndRebaselineAsync(parent, child, lastSyncDate, read.CurrentVersion, read.RescanReason, ct);

        using var changes = read.Changes!;
        var deleteKeys = SplitOffDeletes(changes);

        int synced = 0, errored = 0;
        var retryable = false;
        if (changes.Rows.Count > 0)
            (synced, errored, retryable) = await _mergeStrategy.MergeBatchAsync(changes, child, ct);

        var (deleted, deleteErrors) = await ApplyDeletesAsync(deleteKeys, child, ct);
        errored += deleteErrors;

        // Same rule as RowVersionSyncRunner: only advance once this window is fully processed —
        // a retryable error (e.g. FK timing race) keeps the old position so those keys are read
        // again next cycle. MERGE is idempotent, so re-processing the rows that did succeed is
        // harmless. A failed delete is not retryable; the hourly DeleteReconciler still catches it.
        if (!retryable)
            await _syncState.UpdateIncrementalPositionAsync(
                child, _profile.TableName, ChangeTrackingVersions.FormatPosition(read.CurrentVersion), ct);

        if (synced + deleted + errored > 0)
            _logger.LogInformation(
                "{Table}: Change Tracking {From} -> {To}: {Upserts} upserted, {Deleted} deleted, {Errors} errors",
                _profile.TableName, lastVersion, read.CurrentVersion, synced, deleted, errored);

        return (synced + deleted, errored, retryable);
    }

    /// <summary>Reads the version bounds and the changed rows in ONE transaction. Under
    /// SNAPSHOT isolation (the default in Azure SQL Database) all three see the same point in
    /// time: the change set matches the captured version exactly, auto-cleanup can't remove
    /// tracking rows between the validity check and the read, and parent writers aren't blocked.
    /// Without snapshot isolation the version is still captured before the read (a row changed
    /// after it is simply read again next cycle), and the validity check is repeated after the
    /// read to catch cleanup that ran in between.</summary>
    private async Task<ChangeRead> ReadChangesAsync(SqlConnection parent, long? lastVersion, CancellationToken ct)
    {
        var snapshotAllowed = await ChangeTrackingVersions.IsSnapshotIsolationAllowedAsync(parent, ct);

        try
        {
            await using var transaction = (SqlTransaction)await parent.BeginTransactionAsync(
                snapshotAllowed ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, ct);

            var (minValid, current) = await ChangeTrackingVersions.GetBoundsAsync(parent, transaction, _profile.TableName, ct);

            var rescanReason = GetRescanReason(lastVersion, minValid, current);
            if (rescanReason is not null)
            {
                await transaction.CommitAsync(ct);
                return new ChangeRead(null, current, rescanReason);
            }

            DataTable changes;
            await using (var cmd = _commandFactory.BuildChangeTrackingCommand(parent, transaction, lastVersion!.Value))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                changes = await DataTableLoader.LoadAsync(reader, ct);

            if (!snapshotAllowed)
            {
                var (minValidAfterRead, _) = await ChangeTrackingVersions.GetBoundsAsync(parent, transaction, _profile.TableName, ct);
                if (lastVersion < minValidAfterRead)
                {
                    changes.Dispose();
                    await transaction.CommitAsync(ct);
                    return new ChangeRead(null, current,
                        $"tracking data was cleaned up during the read (min valid version moved to {minValidAfterRead})");
                }
            }

            await transaction.CommitAsync(ct);
            return new ChangeRead(changes, current, null);
        }
        finally
        {
            // SET TRANSACTION ISOLATION LEVEL is session-scoped and outlives the transaction —
            // reset it so later commands on this (pooled) connection run under the default.
            if (snapshotAllowed && parent.State == ConnectionState.Open)
            {
                await using var reset = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", parent);
                await reset.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private static string? GetRescanReason(long? lastVersion, long minValidVersion, long currentVersion) =>
        lastVersion switch
        {
            null => "no Change Tracking position yet",
            var v when v < minValidVersion =>
                $"position {v} is older than CHANGE_TRACKING_MIN_VALID_VERSION ({minValidVersion}) — " +
                "retention exceeded, table truncated, or Change Tracking re-enabled",
            var v when v > currentVersion =>
                $"position {v} is ahead of CHANGE_TRACKING_CURRENT_VERSION ({currentVersion}) — " +
                "was the parent database restored from a backup?",
            _ => null,
        };

    private async Task<(int synced, int errored, bool retryable)> RescanAndRebaselineAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, long versionBeforeRescan, string reason, CancellationToken ct)
    {
        _logger.LogWarning(
            "{Table}: {Reason} — running a full re-scan, then resuming Change Tracking from version {Version}",
            _profile.TableName, reason, versionBeforeRescan);

        var (synced, errored, retryable) = await _fullRescanRunner.RunAsync(parent, child, lastSyncDate, ct);

        // Baseline only after a clean re-scan: any row that failed to merge must be re-read,
        // and only another full re-scan can guarantee that (CT won't report it again unless it
        // changes). The version was captured BEFORE the re-scan, so changes made during it are
        // picked up by the next CT read.
        if (errored == 0 && !retryable)
            await _syncState.UpdateIncrementalPositionAsync(
                child, _profile.TableName, ChangeTrackingVersions.FormatPosition(versionBeforeRescan), ct);
        else
            _logger.LogWarning(
                "{Table}: full re-scan had {Errors} errors — Change Tracking position not set, the re-scan repeats next cycle",
                _profile.TableName, errored);

        return (synced, errored, retryable);
    }

    /// <summary>Removes rows whose key no longer exists in the parent (deletes) from the change
    /// set and returns their keys, then drops the helper column so what's left has exactly the
    /// table's own columns — the shape the merge strategies expect.</summary>
    private List<string> SplitOffDeletes(DataTable changes)
    {
        const string rowMissingColumn = TableSyncCommandFactory.ChangeTrackingRowMissingColumn;
        var deleteKeys = new List<string>();

        // An empty result has no columns at all (DataTableLoader infers them from the first row).
        if (!changes.Columns.Contains(rowMissingColumn))
            return deleteKeys;

        for (var i = changes.Rows.Count - 1; i >= 0; i--)
        {
            var row = changes.Rows[i];
            if (row[rowMissingColumn] is true)
            {
                deleteKeys.Add(CompositeKey.Extract(row, _profile.PrimaryKeyColumns));
                changes.Rows.RemoveAt(i);
            }
        }

        changes.Columns.Remove(rowMissingColumn);
        return deleteKeys;
    }

    private async Task<(int deleted, int errored)> ApplyDeletesAsync(List<string> keys, SqlConnection child, CancellationToken ct)
    {
        int deleted = 0, errored = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _rowDeleter.DeleteAsync(key, child, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Table} delete key '{Key}': failed", _profile.TableName, key);
                errored++;
            }
        }

        return (deleted, errored);
    }

    private sealed record ChangeRead(DataTable? Changes, long CurrentVersion, string? RescanReason);
}
