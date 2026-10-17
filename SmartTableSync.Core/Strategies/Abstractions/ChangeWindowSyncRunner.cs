namespace SmartTableSync.Strategies.Abstractions;

/// <summary>The range of changes one incremental pass reads. From is where reading starts;
/// To is the position recorded once the whole window is applied (captured before reading, so
/// nothing that commits while the window is being read is skipped). FullRescanRequired is set
/// when the window can't be read incrementally at all (SqlChangeTracking only).</summary>
internal sealed record ChangeWindow<TPosition>(TPosition From, TPosition To, FullRescanReason? FullRescanRequired = null);

/// <summary>Why an incremental strategy had to fall back to a full re-scan. Category is a fixed
/// value safe to use as a metric tag; Description is for logs.</summary>
internal sealed record FullRescanReason(string Category, string Description);

/// <summary>Position type for strategies that keep no position of their own.</summary>
internal readonly record struct NoPosition;

/// <summary>
/// Template Method for every incremental strategy. The fixed algorithm is:
/// <list type="number">
/// <item>OpenWindowAsync — strategy-specific: where to start and the position to record afterwards.</item>
/// <item>ReadPageAsync, repeatedly — strategy-specific: the next page of changed rows in primary-key
/// order, so even a large backlog after an outage is applied in bounded batches.</item>
/// <item>Apply each page — shared: delete rows flagged as deleted, then MERGE the rest.</item>
/// <item>CommitWindowAsync — strategy-specific: record the new position, but only when no
/// retryable error happened, so failed rows stay inside the next window.</item>
/// </list>
/// A subclass only describes what is different about its strategy; paging, applying and the
/// position rule exist once, here.
/// </summary>
internal abstract class ChangeWindowSyncRunner<TPosition> : ISyncRunner
{
    private readonly IBatchMergeStrategy _mergeStrategy;
    private readonly ChildRowDeleter _rowDeleter;

    protected ChangeWindowSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy, ILogger logger)
    {
        Profile = profile;
        Plan = plan;
        Commands = commands;
        Logger = logger;
        _mergeStrategy = mergeStrategy;
        _rowDeleter = new ChildRowDeleter(profile);
    }

    protected TableProfile Profile { get; }

    protected SyncPlan Plan { get; }

    protected TableSyncCommandFactory Commands { get; }

    protected ILogger Logger { get; }

    public async Task<SyncOutcome> RunAsync(SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var window = await OpenWindowAsync(parent, child, lastSyncDate, ct);
        if (window.FullRescanRequired is not null)
        {
            return await RunFullRescanAsync(parent, child, lastSyncDate, window, ct);
        }

        var outcome = SyncOutcome.None;
        string? afterKey = null;

        while (true)
        {
            using var page = await ReadPageAsync(parent, window, afterKey, Plan.BatchSize, ct);
            var rowsRead = page.Rows.Count;
            if (rowsRead == 0)
            {
                break;
            }

            afterKey = CompositeKey.Extract(page.Rows[rowsRead - 1], Profile.PrimaryKeyColumns);
            outcome += await ApplyPageAsync(page, child, ct);

            if (rowsRead < Plan.BatchSize)
            {
                break;
            }
        }

        // A retryable failure (e.g. an FK timing race) keeps the old position, so the failed
        // rows are read again next run. Rows that did succeed are simply merged again.
        if (!outcome.HasRetryableErrors)
        {
            await CommitWindowAsync(child, window, ct);
        }

        return outcome;
    }

    /// <summary>Decides where this pass starts and which position to record when it finishes.</summary>
    protected abstract Task<ChangeWindow<TPosition>> OpenWindowAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct);

    /// <summary>Reads the next page of changed rows, in primary-key order, after afterKey (null
    /// for the first page). An empty page ends the window.</summary>
    protected abstract Task<DataTable> ReadPageAsync(
        SqlConnection parent, ChangeWindow<TPosition> window, string? afterKey, int pageSize, CancellationToken ct);

    /// <summary>Records window.To as the next run's starting point. Default: nothing to record
    /// (the strategy's position is kept elsewhere or doesn't exist).</summary>
    protected virtual Task CommitWindowAsync(SqlConnection child, ChangeWindow<TPosition> window, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Handles a window that can't be read incrementally. Only strategies whose
    /// OpenWindowAsync can set FullRescanRequired override this.</summary>
    protected virtual Task<SyncOutcome> RunFullRescanAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, ChangeWindow<TPosition> window, CancellationToken ct) =>
        throw new NotSupportedException($"{GetType().Name} never requests a full re-scan.");

    /// <summary>Runs a page query and loads its rows; disposes the command.</summary>
    protected static async Task<DataTable> LoadPageAsync(SqlCommand pageCommand, CancellationToken ct)
    {
        await using (pageCommand)
        {
            await using var reader = await pageCommand.ExecuteReaderAsync(ct);
            return await DataTableLoader.LoadAsync(reader, ct);
        }
    }

    // Deletes first: a primary-key update arrives as "delete old key" + "insert new key", and
    // inserting first could collide with the old row on another unique index in the child.
    private async Task<SyncOutcome> ApplyPageAsync(DataTable page, SqlConnection child, CancellationToken ct)
    {
        var outcome = await ApplyDeletesAsync(TakeDeletedKeys(page), child, ct);

        if (page.Rows.Count > 0)
        {
            outcome += await _mergeStrategy.MergeBatchAsync(page, child, ct);
        }

        return outcome;
    }

    /// <summary>Removes rows flagged in RowDeletedColumn from the page and returns their keys,
    /// then drops the flag column so the page has exactly the table's own columns again.</summary>
    private List<string> TakeDeletedKeys(DataTable page)
    {
        const string flagColumn = TableSyncCommandFactory.RowDeletedColumn;
        var deletedKeys = new List<string>();

        if (!page.Columns.Contains(flagColumn))
        {
            return deletedKeys;
        }

        for (var i = page.Rows.Count - 1; i >= 0; i--)
        {
            if (page.Rows[i][flagColumn] is true)
            {
                deletedKeys.Add(CompositeKey.Extract(page.Rows[i], Profile.PrimaryKeyColumns));
                page.Rows.RemoveAt(i);
            }
        }

        page.Columns.Remove(flagColumn);
        return deletedKeys;
    }

    // A failed delete is logged and counted, never retried: the periodic delete reconciliation
    // removes the row later.
    private async Task<SyncOutcome> ApplyDeletesAsync(List<string> keys, SqlConnection child, CancellationToken ct)
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
                Logger.LogError(ex, "{Table} delete key '{Key}': failed", Profile.TableName, key);
                errored++;
            }
        }

        return new SyncOutcome(Merged: 0, deleted, errored, HasRetryableErrors: false);
    }
}
