namespace SmartTableSync.State;

public sealed class SyncStateRepository : ISyncStateRepository
{
    public async Task<DateTime> GetLastSyncDateAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand(
            "SELECT [LastSyncDate] FROM [dbo].[CustomSyncState] WHERE [TableName] = @T", conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? dt : SyncConstants.InitialDate;
    }

    public async Task UpdateLastSyncDateAsync(
        SqlConnection conn, string tableName, DateTime syncDate, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand("""
            MERGE [dbo].[CustomSyncState] WITH (HOLDLOCK) AS Target
            USING (SELECT @T AS [TableName]) AS Source
            ON Target.[TableName] = Source.[TableName]
            WHEN MATCHED THEN
                UPDATE SET [LastSyncDate] = @D
            WHEN NOT MATCHED THEN
                INSERT ([TableName], [LastSyncDate]) VALUES (@T, @D);
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;
        cmd.Parameters.Add("@D", SqlDbType.DateTime).Value = syncDate;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetIncrementalPositionAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default)
    {
        await using var cmd = new SqlCommand(
            "SELECT [IncrementalPosition] FROM [dbo].[CustomSyncState] WHERE [TableName] = @T", conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;

        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task UpdateIncrementalPositionAsync(
        SqlConnection conn, string tableName, string position, CancellationToken ct = default)
    {
        // WHEN NOT MATCHED seeds LastSyncDate with the "initial sync not yet done" sentinel -
        // in practice this branch never fires, since RowVersionSyncRunner only ever runs
        // during the incremental phase, which only begins once UpdateLastSyncDateAsync has
        // already inserted a row for this table. Guarded anyway so this can never violate
        // LastSyncDate's NOT NULL constraint regardless of call order.
        await using var cmd = new SqlCommand("""
            MERGE [dbo].[CustomSyncState] WITH (HOLDLOCK) AS Target
            USING (SELECT @T AS [TableName]) AS Source
            ON Target.[TableName] = Source.[TableName]
            WHEN MATCHED THEN
                UPDATE SET [IncrementalPosition] = @P
            WHEN NOT MATCHED THEN
                INSERT ([TableName], [LastSyncDate], [IncrementalPosition]) VALUES (@T, @InitialDate, @P);
            """, conn);
        cmd.Parameters.Add("@T", SqlDbType.NVarChar, 100).Value = tableName;
        cmd.Parameters.Add("@P", SqlDbType.NVarChar, 50).Value = position;
        cmd.Parameters.Add("@InitialDate", SqlDbType.DateTime).Value = SyncConstants.InitialDate;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
