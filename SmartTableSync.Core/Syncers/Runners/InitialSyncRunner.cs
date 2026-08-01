namespace SmartTableSync.Syncers.Runners;

/// <summary>
/// Runs the initial-sync batch loop for one table: keyset-paginated SqlBulkCopy batches,
/// a resume position checkpointed after every batch, a runtime batch-size-halving backoff
/// on command timeout, and a fallback to the table's IBatchMergeStrategy on a PK violation
/// (the child already has some of these rows).
///
/// Owns the one piece of mutable state in the whole sync path — the batch size, which can
/// shrink at runtime but never grows back — which is why it's an instance class rather than
/// a stateless static helper like the rest of Syncers/.
/// </summary>
public sealed class InitialSyncRunner : ISyncRunner
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly IBatchMergeStrategy _mergeStrategy;
    private readonly IBatchLogger _batchLogger;
    private readonly ILogger _logger;

    private int _batchSize;

    public InitialSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commandFactory,
        IBatchMergeStrategy mergeStrategy, IBatchLogger batchLogger, ILogger logger)
    {
        _profile = profile;
        _plan = plan;
        _commandFactory = commandFactory;
        _mergeStrategy = mergeStrategy;
        _batchLogger = batchLogger;
        _logger = logger;
        _batchSize = plan.BatchSize;
    }

    // lastSyncDate is unused — initial sync always resumes from the persisted batch-log
    // cursor, never from the caller's timestamp. Present only so this satisfies ISyncRunner,
    // the interface ConfigurableTableSyncer holds both this and IncrementalSyncRunner behind.
    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var tableName = _profile.TableName;
        var resumeKey = await _batchLogger.GetResumeKeyAsync(child, tableName, ct);
        var batchNum = await _batchLogger.GetNextBatchNumberAsync(child, tableName, ct);
        var totalPrior = await _batchLogger.GetTotalSyncedAsync(child, tableName, ct);
        int thisRunSynced = 0;
        int totalErr = 0;
        var backedOffAtCurrentPosition = false;

        _logger.LogInformation(
            "{Table} initial sync: batch {N}, resuming from key '{Key}', {Total} rows done so far",
            tableName, batchNum, resumeKey ?? "start", totalPrior);

        while (true)
        {
            await using var cmd = _commandFactory.BuildInitialBatchCommand(parent, resumeKey, _batchSize);
            var logId = await _batchLogger.LogBatchStartAsync(child, tableName, batchNum, resumeKey, ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var batch = await DataTableLoader.LoadAsync(reader, ct);
            int rowsRead = batch.Rows.Count;

            if (rowsRead == 0)
            {
                await _batchLogger.LogBatchCompleteAsync(child, logId, 0, 0, resumeKey ?? string.Empty, CancellationToken.None);
                break;
            }

            var currentKey = CompositeKey.Extract(batch.Rows[rowsRead - 1], _profile.PrimaryKeyColumns);
            var sw = Stopwatch.StartNew();

            ct.ThrowIfCancellationRequested();
            bool batchOk = false;
            try
            {
                await BulkCopyWriter.WriteAsync(batch, child, tableName, _plan.BulkCopyOptions, _plan.BulkCopyTimeoutSeconds, _profile.RowVersionColumn, ct);
                await _batchLogger.LogBatchCompleteAsync(child, logId, rowsRead, 0, currentKey, CancellationToken.None);
                thisRunSynced += rowsRead;
                resumeKey = currentKey;
                batchOk = true;
                backedOffAtCurrentPosition = false;

                _logger.LogInformation(
                    "{Table} batch {N}: {Count} rows bulk-inserted, key '{Key}', {Ms} ms — {Total} cumulative",
                    tableName, batchNum, rowsRead, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
            }
            catch (SqlException ex) when (ex.Number == -2 && !backedOffAtCurrentPosition && _batchSize > 1)
            {
                // Command timeout (-2). Halve the batch size and retry from the same
                // resume position once, rather than trusting the heuristic's original
                // estimate forever. If the retry at the smaller size also times out,
                // fall through to the fatal-error path below like any other failure.
                var newSize = BatchSizeBackoff.Halve(_batchSize);
                _logger.LogWarning(
                    "{Table} batch {N}: bulk copy timed out at batch size {Old} — retrying from the same position at {New}",
                    tableName, batchNum, _batchSize, newSize);
                _batchSize = newSize;
                backedOffAtCurrentPosition = true;
                continue;
            }
            catch (SqlException ex) when (ex.Number == 2627) // PK violation — child already has these rows
            {
                _logger.LogWarning(
                    "{Table} batch {N}: PK violation in bulk copy (row {Key} exists) — falling back to MERGE",
                    tableName, batchNum, currentKey);
                try
                {
                    var (s, e, _) = await _mergeStrategy.MergeBatchAsync(batch, child, ct);
                    await _batchLogger.LogBatchCompleteAsync(child, logId, s, e, currentKey, CancellationToken.None);
                    thisRunSynced += s;
                    totalErr += e;
                    resumeKey = currentKey;
                    batchOk = true;

                    _logger.LogInformation(
                        "{Table} batch {N}: {Count} rows merged (fallback), key '{Key}', {Ms} ms — {Total} cumulative",
                        tableName, batchNum, s, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
                }
                catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
                {
                    _logger.LogError(mergeEx,
                        "{Table} batch {N}: MERGE fallback also failed — pausing, will retry on next run",
                        tableName, batchNum);
                    totalErr += rowsRead;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "{Table} batch {N}: bulk insert of {Count} rows failed — pausing, will retry on next run",
                    tableName, batchNum, rowsRead);
                totalErr += rowsRead;
            }

            if (!batchOk) break;
            if (rowsRead < _batchSize) break;
            batchNum++;
        }

        return (thisRunSynced, totalErr, false);
    }
}
