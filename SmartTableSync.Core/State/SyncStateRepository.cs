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
        cmd.Parameters.Add("@D", SqlDbType.DateTime).Value      = syncDate;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
