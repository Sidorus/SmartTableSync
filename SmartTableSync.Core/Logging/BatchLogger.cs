namespace SmartTableSync.Logging;

public sealed class BatchLogger : IBatchLogger
{
    public async Task<string?> GetResumeKeyAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            SELECT TOP 1 [LastKeyValue]
            FROM   [dbo].[SyncBatchLog]
            WHERE  [TableName] = @T AND [Phase] = 'initial' AND [Status] = 'completed'
            ORDER  BY [LogId] DESC
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : (string)result;
    }

    public async Task<int> GetNextBatchNumberAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            SELECT ISNULL(MAX([BatchNumber]), 0) + 1
            FROM   [dbo].[SyncBatchLog]
            WHERE  [TableName] = @T AND [Phase] = 'initial'
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> GetTotalSyncedAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            SELECT ISNULL(SUM([RowsSynced]), 0)
            FROM   [dbo].[SyncBatchLog]
            WHERE  [TableName] = @T AND [Phase] = 'initial' AND [Status] = 'completed'
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<int> LogBatchStartAsync(
        SqlConnection conn, string tableName, int batchNumber, string? startKey, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[SyncBatchLog]
                ([TableName], [Phase], [BatchNumber], [StartKeyValue], [StartedAt], [Status])
            VALUES (@T, 'initial', @Batch, @Key, GETUTCDATE(), 'started');
            SELECT SCOPE_IDENTITY();
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;
        cmd.Parameters.Add("@Batch", SqlDbType.Int).Value = batchNumber;
        cmd.Parameters.Add("@Key", SqlDbType.NVarChar, 400).Value = (object?)startKey ?? DBNull.Value;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task LogBatchCompleteAsync(
        SqlConnection conn, int logId, int synced, int errored, string lastKey, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            UPDATE [dbo].[SyncBatchLog]
            SET    [RowsSynced]   = @Synced,
                   [RowsErrored]  = @Errored,
                   [LastKeyValue] = @Key,
                   [CompletedAt]  = GETUTCDATE(),
                   [Status]       = 'completed'
            WHERE  [LogId] = @Id
            """, conn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = logId;
        cmd.Parameters.Add("@Synced", SqlDbType.Int).Value = synced;
        cmd.Parameters.Add("@Errored", SqlDbType.Int).Value = errored;
        cmd.Parameters.Add("@Key", SqlDbType.NVarChar, 400).Value = lastKey;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
