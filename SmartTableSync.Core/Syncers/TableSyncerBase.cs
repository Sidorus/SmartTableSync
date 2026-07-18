namespace SmartTableSync.Syncers;

/// <summary>
/// Handles the batch loop, resume logic, and batch logging for initial full syncs.
/// Subclasses supply the table-specific SQL commands, key extraction, and MERGE call.
///
/// Initial sync uses SqlBulkCopy (one network operation per batch — no SP calls).
/// Incremental sync uses MergeBatchAsync (SP-based MERGE, handles UPDATE + INSERT).
/// </summary>
public abstract class TableSyncerBase : ITableSyncer
{
    protected TableSyncerBase(IBatchLogger batchLogger, ILogger logger)
    {
        BatchLogger = batchLogger;
        Logger = logger;
    }

    public abstract string TableName { get; }
    protected abstract int BatchSize { get; }

    /// <summary>Override to hint the expected row count so LoadKeysAsync pre-sizes its HashSet.</summary>
    protected virtual int EstimatedRowCount => 1_024;

    /// <summary>SqlBulkCopyOptions for initial sync. Override with KeepIdentity for IDENTITY PKs.</summary>
    protected virtual SqlBulkCopyOptions BulkCopyOptions => SqlBulkCopyOptions.Default;

    /// <summary>Timeout in seconds for each bulk copy operation. Override for large-row tables.</summary>
    protected virtual int BulkCopyTimeout => 120;

    protected IBatchLogger BatchLogger { get; }
    protected ILogger Logger { get; }

    public async Task<SyncResult> SyncAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        int synced, errored;
        bool hasRetryable = false;
        if (IsInitial(lastSyncDate))
            (synced, errored) = await RunInitialSyncAsync(parent, child, ct);
        else
            (synced, errored, hasRetryable) = await RunIncrementalSyncAsync(parent, child, lastSyncDate, ct);

        return new SyncResult(TableName, synced, errored, hasRetryable, IsInitial(lastSyncDate), sw.Elapsed);
    }

    public async Task<int> ReconcileDeletesAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default)
    {
        // Skip during initial sync — the child is partially populated so any missing key
        // looks like a deleted but is really just "not yet synced".
        if (IsInitial(lastSyncDate)) return 0;

        // Load child keys first so any rows inserted into parent (and synced to child) between
        // the two reads are in parentKeys but not childKeys — they will not be treated as orphans.
        var childKeys = await LoadKeysAsync(BuildChildKeyCommand(child), ct);
        var parentKeys = await LoadKeysAsync(BuildParentKeyCommand(parent), ct);
        childKeys.ExceptWith(parentKeys); // childKeys now contains only orphans

        if (childKeys.Count == 0) return 0;

        Logger.LogInformation("{Table}: {Count} orphaned rows to delete", TableName, childKeys.Count);

        int deleted = 0;
        foreach (var key in childKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteRowAsync(key, child, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "{Table} delete key '{Key}': failed", TableName, key);
            }
        }

        return deleted;
    }

    /// <summary>SELECT TOP BatchSize ... WHERE key > resumeKey ORDER BY key</summary>
    protected abstract SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey);

    /// <summary>SELECT ... WHERE LastUpdate >= lastSync - 1 min</summary>
    protected abstract SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync);

    /// <summary>Returns the string key from a DataRow (used as resume cursor).</summary>
    protected abstract string ExtractKey(DataRow row);

    /// <summary>
    /// Merges changed rows into the child DB. Used for incremental sync only.
    /// Returns (rowsSynced, rowsErrored, hasRetryableErrors). Handle per-row errors internally;
    /// only throw for fatal batch-level failures (base class will stop and retry next run).
    /// Set retryable=true when errors are transient FK timing races — this prevents LastSyncDate
    /// from advancing so the affected rows are retried on the next run.
    /// </summary>
    protected abstract Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct);

    /// <summary>Return true for exceptions that should be logged as Warning, not Error.</summary>
    protected virtual bool IsExpectedError(Exception ex) => false;

    /// <summary>SELECT pk-as-single-string FROM parent table (all rows, no filter).</summary>
    protected abstract SqlCommand BuildParentKeyCommand(SqlConnection parent);

    /// <summary>SELECT pk-as-single-string FROM child table (all rows, no filter).</summary>
    protected abstract SqlCommand BuildChildKeyCommand(SqlConnection child);

    /// <summary>DELETE the row identified by key from the child table.</summary>
    protected abstract Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct);

    private async Task<(int synced, int errored)> RunInitialSyncAsync(
        SqlConnection parent, SqlConnection child, CancellationToken ct)
    {
        var resumeKey = await BatchLogger.GetResumeKeyAsync(child, TableName, ct);
        var batchNum = await BatchLogger.GetNextBatchNumberAsync(child, TableName, ct);
        var totalPrior = await BatchLogger.GetTotalSyncedAsync(child, TableName, ct);
        int thisRunSynced = 0;
        int totalErr = 0;

        Logger.LogInformation(
            "{Table} initial sync: batch {N}, resuming from key '{Key}', {Total} rows done so far",
            TableName, batchNum, resumeKey ?? "start", totalPrior);

        while (true)
        {
            await using var cmd = BuildInitialBatchCommand(parent, resumeKey);
            var logId = await BatchLogger.LogBatchStartAsync(child, TableName, batchNum, resumeKey, ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var batch = await LoadBatchAsync(reader, ct);
            int rowsRead = batch.Rows.Count;

            if (rowsRead == 0)
            {
                await BatchLogger.LogBatchCompleteAsync(child, logId, 0, 0, resumeKey ?? string.Empty, CancellationToken.None);
                break;
            }

            var currentKey = ExtractKey(batch.Rows[rowsRead - 1]);
            var sw = Stopwatch.StartNew();

            ct.ThrowIfCancellationRequested();
            bool batchOk = false;
            try
            {
                await BulkInsertBatchAsync(batch, child, ct);
                await BatchLogger.LogBatchCompleteAsync(child, logId, rowsRead, 0, currentKey, CancellationToken.None);
                thisRunSynced += rowsRead;
                resumeKey = currentKey;
                batchOk = true;

                Logger.LogInformation(
                    "{Table} batch {N}: {Count} rows bulk-inserted, key '{Key}', {Ms} ms — {Total} cumulative",
                    TableName, batchNum, rowsRead, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
            }
            catch (SqlException ex) when (ex.Number == 2627) // PK violation — child already has these rows
            {
                // Child table has pre-existing rows (from Data Sync or a prior partial run).
                // Fall back to MERGE for this batch so existing rows are updated and new ones inserted.
                Logger.LogWarning(
                    "{Table} batch {N}: PK violation in bulk copy (row {Key} exists) — falling back to MERGE",
                    TableName, batchNum, currentKey);
                try
                {
                    var (s, e, _) = await MergeBatchAsync(batch, child, ct);
                    await BatchLogger.LogBatchCompleteAsync(child, logId, s, e, currentKey, CancellationToken.None);
                    thisRunSynced += s;
                    totalErr += e;
                    resumeKey = currentKey;
                    batchOk = true;

                    Logger.LogInformation(
                        "{Table} batch {N}: {Count} rows merged (fallback), key '{Key}', {Ms} ms — {Total} cumulative",
                        TableName, batchNum, s, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
                }
                catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
                {
                    Logger.LogError(mergeEx,
                        "{Table} batch {N}: MERGE fallback also failed — pausing, will retry on next run",
                        TableName, batchNum);
                    totalErr += rowsRead;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex,
                    "{Table} batch {N}: bulk insert of {Count} rows failed — pausing, will retry on next run",
                    TableName, batchNum, rowsRead);
                totalErr += rowsRead;
            }

            if (!batchOk) break;

            if (rowsRead < BatchSize) break;
            batchNum++;
        }

        return (thisRunSynced, totalErr);
    }

    private async Task<(int synced, int errored, bool retryable)> RunIncrementalSyncAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSync, CancellationToken ct)
    {
        await using var cmd = BuildIncrementalCommand(parent, lastSync);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        using var batch = await LoadBatchAsync(reader, ct);

        if (batch.Rows.Count == 0) return (0, 0, false);

        return await MergeBatchAsync(batch, child, ct);
    }

    private async Task BulkInsertBatchAsync(DataTable batch, SqlConnection child, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(child, BulkCopyOptions, externalTransaction: null)
        {
            DestinationTableName = $"[dbo].[{TableName}]",
            BulkCopyTimeout = BulkCopyTimeout
        };

        // Map by column name, not position — safe with SELECT * regardless of column order.
        foreach (DataColumn col in batch.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(batch, ct);
    }

    private static async Task<DataTable> LoadBatchAsync(SqlDataReader reader, CancellationToken ct)
    {
        var dt = new DataTable();
        bool first = true;

        while (await reader.ReadAsync(ct))
        {
            if (first)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                    dt.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
                first = false;
            }

            var row = dt.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            dt.Rows.Add(row);
        }

        return dt;
    }

    private async Task<HashSet<string>> LoadKeysAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var _ = cmd;
        var keys = new HashSet<string>(EstimatedRowCount, StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    private bool IsInitial(DateTime lastSyncDate) => lastSyncDate == SyncConstants.InitialDate;
}
