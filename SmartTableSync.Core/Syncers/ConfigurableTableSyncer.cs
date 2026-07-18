namespace SmartTableSync.Syncers;

/// <summary>
/// A single, config-driven ITableSyncer implementation that replaces one hand-written
/// subclass per table. Behavior that used to come from abstract-method overrides
/// (ProductAttributeValueSyncer, ProductCatalogItemSyncer, DocumentAssetSyncer, all
/// extending TableSyncerBase) is derived here from a TableProfile (schema shape) and a
/// SyncPlan (merge strategy/batch size/etc.), both produced upstream by introspection and
/// heuristic derivation rather than hand-declared.
///
/// Deliberately NOT built on top of TableSyncerBase: TableSyncerBase and its three
/// subclasses stay completely untouched so they keep working as the verified production
/// path while this class is validated in parallel (see Plan.md's migration-safety
/// approach) — this does mean the batch/resume/bulk-copy loop below duplicates
/// TableSyncerBase's structure rather than reusing it. That duplication is temporary and
/// gets deleted once this class's output is confirmed to match the old syncers row-for-row
/// and they're removed.
/// </summary>
public sealed class ConfigurableTableSyncer : ITableSyncer
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly IBatchLogger _batchLogger;
    private readonly ILogger _logger;

    // Seeded from _plan.BatchSize; can shrink at runtime via the timeout backoff below.
    private int _batchSize;

    public ConfigurableTableSyncer(TableProfile profile, SyncPlan plan, IBatchLogger batchLogger, ILogger logger)
    {
        _profile = profile;
        _plan = plan;
        _batchLogger = batchLogger;
        _logger = logger;
        _batchSize = plan.BatchSize;
    }

    public string TableName => _profile.TableName;

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
        if (IsInitial(lastSyncDate)) return 0;

        var childKeys = await LoadKeysAsync(BuildChildKeyCommand(child), ct);
        var parentKeys = await LoadKeysAsync(BuildParentKeyCommand(parent), ct);
        childKeys.ExceptWith(parentKeys);

        if (childKeys.Count == 0) return 0;

        _logger.LogInformation("{Table}: {Count} orphaned rows to delete", TableName, childKeys.Count);

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
                _logger.LogError(ex, "{Table} delete key '{Key}': failed", TableName, key);
            }
        }

        return deleted;
    }

    private async Task<(int synced, int errored)> RunInitialSyncAsync(SqlConnection parent, SqlConnection child, CancellationToken ct)
    {
        var resumeKey = await _batchLogger.GetResumeKeyAsync(child, TableName, ct);
        var batchNum = await _batchLogger.GetNextBatchNumberAsync(child, TableName, ct);
        var totalPrior = await _batchLogger.GetTotalSyncedAsync(child, TableName, ct);
        int thisRunSynced = 0;
        int totalErr = 0;
        var backedOffAtCurrentPosition = false;

        _logger.LogInformation(
            "{Table} initial sync: batch {N}, resuming from key '{Key}', {Total} rows done so far",
            TableName, batchNum, resumeKey ?? "start", totalPrior);

        while (true)
        {
            await using var cmd = BuildInitialBatchCommand(parent, resumeKey);
            var logId = await _batchLogger.LogBatchStartAsync(child, TableName, batchNum, resumeKey, ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var batch = await LoadBatchAsync(reader, ct);
            int rowsRead = batch.Rows.Count;

            if (rowsRead == 0)
            {
                await _batchLogger.LogBatchCompleteAsync(child, logId, 0, 0, resumeKey ?? string.Empty, CancellationToken.None);
                break;
            }

            var currentKey = ExtractKey(batch.Rows[rowsRead - 1]);
            var sw = Stopwatch.StartNew();

            ct.ThrowIfCancellationRequested();
            bool batchOk = false;
            try
            {
                await BulkInsertBatchAsync(batch, child, ct);
                await _batchLogger.LogBatchCompleteAsync(child, logId, rowsRead, 0, currentKey, CancellationToken.None);
                thisRunSynced += rowsRead;
                resumeKey = currentKey;
                batchOk = true;
                backedOffAtCurrentPosition = false;

                _logger.LogInformation(
                    "{Table} batch {N}: {Count} rows bulk-inserted, key '{Key}', {Ms} ms — {Total} cumulative",
                    TableName, batchNum, rowsRead, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
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
                    TableName, batchNum, _batchSize, newSize);
                _batchSize = newSize;
                backedOffAtCurrentPosition = true;
                continue;
            }
            catch (SqlException ex) when (ex.Number == 2627) // PK violation — child already has these rows
            {
                _logger.LogWarning(
                    "{Table} batch {N}: PK violation in bulk copy (row {Key} exists) — falling back to MERGE",
                    TableName, batchNum, currentKey);
                try
                {
                    var (s, e, _) = await MergeBatchAsync(batch, child, ct);
                    await _batchLogger.LogBatchCompleteAsync(child, logId, s, e, currentKey, CancellationToken.None);
                    thisRunSynced += s;
                    totalErr += e;
                    resumeKey = currentKey;
                    batchOk = true;

                    _logger.LogInformation(
                        "{Table} batch {N}: {Count} rows merged (fallback), key '{Key}', {Ms} ms — {Total} cumulative",
                        TableName, batchNum, s, currentKey, sw.ElapsedMilliseconds, totalPrior + thisRunSynced);
                }
                catch (Exception mergeEx) when (mergeEx is not OperationCanceledException)
                {
                    _logger.LogError(mergeEx,
                        "{Table} batch {N}: MERGE fallback also failed — pausing, will retry on next run",
                        TableName, batchNum);
                    totalErr += rowsRead;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "{Table} batch {N}: bulk insert of {Count} rows failed — pausing, will retry on next run",
                    TableName, batchNum, rowsRead);
                totalErr += rowsRead;
            }

            if (!batchOk) break;
            if (rowsRead < _batchSize) break;
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

    private SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey)
    {
        var paramNames = _profile.PrimaryKeyColumns.Select((_, i) => $"@Key{i}").ToList();
        var predicate = KeysetPredicateBuilder.Build(_profile.PrimaryKeyColumns, paramNames);
        var orderBy = KeysetPredicateBuilder.BuildOrderBy(_profile.PrimaryKeyColumns);

        var sql = $"""
            SELECT TOP (@Size) * FROM [dbo].[{_profile.TableName}]
            WHERE  {predicate}
            ORDER  BY {orderBy}
            """;

        var cmd = new SqlCommand(sql, parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };
        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = _batchSize;

        var keyParts = resumeKey is null
            ? _profile.PrimaryKeyColumns.Select(c => SqlTypeMapping.DefaultKeyComponent(ColumnType(c))).ToArray()
            : CompositeKey.Parse(resumeKey, _profile.PrimaryKeyColumns);

        for (var i = 0; i < _profile.PrimaryKeyColumns.Count; i++)
        {
            var colType = ColumnType(_profile.PrimaryKeyColumns[i]);
            cmd.Parameters.Add(paramNames[i], SqlTypeMapping.ToSqlDbType(colType)).Value =
                SqlTypeMapping.ParseKeyComponent(keyParts[i], colType);
        }

        return cmd;
    }

    private SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync)
    {
        var cmd = new SqlCommand($"""
            SELECT * FROM [dbo].[{_profile.TableName}]
            WHERE  [LastUpdate] >= DATEADD(minute, -1, @LastSync)
            """, parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    private string ExtractKey(DataRow row) => CompositeKey.Extract(row, _profile.PrimaryKeyColumns);

    private SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        new($"SELECT {BuildKeySelectExpression()} FROM [dbo].[{_profile.TableName}]", parent)
        { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    private SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        new($"SELECT {BuildKeySelectExpression()} FROM [dbo].[{_profile.TableName}]", child)
        { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    private string BuildKeySelectExpression() =>
        KeysetPredicateBuilder.BuildKeySelectExpression(
            _profile.PrimaryKeyColumns,
            c => SqlTypeMapping.IsStringType(ColumnType(c)));

    private async Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct)
    {
        var keyParts = CompositeKey.Parse(key, _profile.PrimaryKeyColumns);
        var whereClauses = new List<string>();

        await using var cmd = new SqlCommand { Connection = child, CommandTimeout = 60 };
        for (var i = 0; i < _profile.PrimaryKeyColumns.Count; i++)
        {
            var col = _profile.PrimaryKeyColumns[i];
            var colType = ColumnType(col);
            var paramName = $"@Key{i}";
            whereClauses.Add($"[{col}] = {paramName}");
            cmd.Parameters.Add(paramName, SqlTypeMapping.ToSqlDbType(colType)).Value =
                SqlTypeMapping.ParseKeyComponent(keyParts[i], colType);
        }

        cmd.CommandText = $"DELETE FROM [dbo].[{_profile.TableName}] WHERE {string.Join(" AND ", whereClauses)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private bool IsExpectedError(Exception ex) =>
        ex is SqlException sqlEx && _plan.RetryableSqlErrorNumbers.Contains(sqlEx.Number);

    private async Task<(int synced, int errored, bool retryable)> MergeBatchAsync(DataTable batch, SqlConnection child, CancellationToken ct) =>
        _plan.MergeStrategy == MergeStrategy.Tvp
            ? await MergeBatchTvpAsync(batch, child, ct)
            : await MergeBatchRowByRowAsync(batch, child, ct);

    private async Task<(int synced, int errored, bool retryable)> MergeBatchTvpAsync(DataTable batch, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand($"[dbo].[sp_Sync{_profile.TableName}Batch]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _plan.BulkCopyTimeoutSeconds
        };

        sp.Parameters.Add(new SqlParameter("@Rows", SqlDbType.Structured)
        {
            TypeName = $"dbo.{_plan.TvpTypeName}",
            Value = batch
        });

        await sp.ExecuteNonQueryAsync(ct);
        return (batch.Rows.Count, 0, false);
    }

    private async Task<(int synced, int errored, bool retryable)> MergeBatchRowByRowAsync(DataTable batch, SqlConnection child, CancellationToken ct)
    {
        int synced = 0, errored = 0;
        bool retryable = false;

        foreach (DataRow row in batch.Rows)
        {
            var key = ExtractKey(row);
            try
            {
                await MergeSingleRowAsync(row, child, ct);
                synced++;
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                _logger.LogWarning("{Table} key '{Key}': skipped (expected error) — {Msg}", TableName, key, ex.Message);
                errored++;
                retryable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Table} key '{Key}': merge failed", TableName, key);
                errored++;
            }
        }

        return (synced, errored, retryable);
    }

    private async Task MergeSingleRowAsync(DataRow row, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand($"[dbo].[sp_Sync{_profile.TableName}]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _plan.BulkCopyTimeoutSeconds
        };

        foreach (var col in _profile.Columns)
        {
            var size = SqlTypeMapping.GetParameterSize(col);
            sp.AddParam($"@{col.Name}", SqlTypeMapping.ToSqlDbType(col.SqlTypeName), row[col.Name], size);
        }

        await sp.ExecuteNonQueryAsync(ct);
    }

    private async Task BulkInsertBatchAsync(DataTable batch, SqlConnection child, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(child, _plan.BulkCopyOptions, externalTransaction: null)
        {
            DestinationTableName = $"[dbo].[{TableName}]",
            BulkCopyTimeout = _plan.BulkCopyTimeoutSeconds
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
        // Uses the introspected row count as the HashSet's initial capacity hint — a more
        // accurate estimate than the old per-subclass hardcoded guesses, since it comes
        // from sys.dm_db_partition_stats rather than a one-off comment in the code.
        var estimatedRowCount = (int)Math.Clamp(_profile.RowCount, 1024, int.MaxValue);
        var keys = new HashSet<string>(estimatedRowCount, StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    private string ColumnType(string columnName) =>
        _profile.Columns.First(c => c.Name == columnName).SqlTypeName;

    private static bool IsInitial(DateTime lastSyncDate) => lastSyncDate == SyncConstants.InitialDate;
}
