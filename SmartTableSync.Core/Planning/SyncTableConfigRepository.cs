namespace SmartTableSync.Planning;

/// <summary>Uses the same MERGE ... WITH (HOLDLOCK) upsert idiom as
/// SyncStateRepository.UpdateLastSyncDateAsync — a proven pattern, not a new one.</summary>
internal sealed class SyncTableConfigRepository : ISyncTableConfigRepository
{
    public async Task<SyncTableConfig?> GetAsync(SqlConnection child, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT [TableName], [PrimaryKeyColumns], [IdentityColumn], [MergeStrategy], [BatchSize],
                   [BulkCopyTimeoutSeconds], [RetryableSqlErrors], [TvpTypeName], [SchemaFingerprint], [ConfiguredAtUtc],
                   [MergeStrategyOverride], [BatchSizeOverride], [BulkCopyTimeoutSecondsOverride], [RetryableSqlErrorsOverride],
                   [ChangeTrackingColumn], [IncrementalStrategy]
            FROM   [dbo].[SyncTableConfig]
            WHERE  [TableName] = @TableName
            """;

        await using var cmd = new SqlCommand(sql, child) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 100);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        return new SyncTableConfig(
            TableName: reader.GetString(0),
            PrimaryKeyColumns: SplitCsv(reader.GetString(1)),
            IdentityColumn: reader.IsDBNull(2) ? null : reader.GetString(2),
            MergeStrategy: Enum.Parse<MergeStrategy>(reader.GetString(3)),
            BatchSize: reader.GetInt32(4),
            BulkCopyTimeoutSeconds: reader.GetInt32(5),
            RetryableSqlErrorNumbers: reader.IsDBNull(6) ? [] : SplitCsvInts(reader.GetString(6)),
            TvpTypeName: reader.IsDBNull(7) ? null : reader.GetString(7),
            SchemaFingerprint: reader.GetString(8),
            ConfiguredAtUtc: reader.GetDateTime(9),
            MergeStrategyOverride: reader.IsDBNull(10) ? null : Enum.Parse<MergeStrategy>(reader.GetString(10)),
            BatchSizeOverride: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            BulkCopyTimeoutSecondsOverride: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            RetryableSqlErrorsOverride: reader.IsDBNull(13) ? null : SplitCsvInts(reader.GetString(13)),
            IncrementalColumn: reader.IsDBNull(14) ? null : reader.GetString(14),
            IncrementalStrategy: Enum.Parse<IncrementalStrategy>(reader.GetString(15)));
    }

    public async Task UpsertAsync(SqlConnection child, SyncTableConfig config, CancellationToken ct)
    {
        const string sql = """
            MERGE [dbo].[SyncTableConfig] WITH (HOLDLOCK) AS Target
            USING (SELECT @TableName AS [TableName]) AS Source
            ON Target.[TableName] = Source.[TableName]
            WHEN MATCHED THEN
                UPDATE SET
                    [PrimaryKeyColumns] = @PrimaryKeyColumns,
                    [IdentityColumn] = @IdentityColumn,
                    [MergeStrategy] = @MergeStrategy,
                    [BatchSize] = @BatchSize,
                    [BulkCopyTimeoutSeconds] = @BulkCopyTimeoutSeconds,
                    [RetryableSqlErrors] = @RetryableSqlErrors,
                    [TvpTypeName] = @TvpTypeName,
                    [SchemaFingerprint] = @SchemaFingerprint,
                    [ConfiguredAtUtc] = @ConfiguredAtUtc,
                    [MergeStrategyOverride] = @MergeStrategyOverride,
                    [BatchSizeOverride] = @BatchSizeOverride,
                    [BulkCopyTimeoutSecondsOverride] = @BulkCopyTimeoutSecondsOverride,
                    [RetryableSqlErrorsOverride] = @RetryableSqlErrorsOverride,
                    [ChangeTrackingColumn] = @ChangeTrackingColumn,
                    [IncrementalStrategy] = @IncrementalStrategy
            WHEN NOT MATCHED THEN
                INSERT (
                    [TableName], [PrimaryKeyColumns], [IdentityColumn], [MergeStrategy], [BatchSize],
                    [BulkCopyTimeoutSeconds], [RetryableSqlErrors], [TvpTypeName], [SchemaFingerprint], [ConfiguredAtUtc],
                    [MergeStrategyOverride], [BatchSizeOverride], [BulkCopyTimeoutSecondsOverride], [RetryableSqlErrorsOverride],
                    [ChangeTrackingColumn], [IncrementalStrategy]
                ) VALUES (
                    @TableName, @PrimaryKeyColumns, @IdentityColumn, @MergeStrategy, @BatchSize,
                    @BulkCopyTimeoutSeconds, @RetryableSqlErrors, @TvpTypeName, @SchemaFingerprint, @ConfiguredAtUtc,
                    @MergeStrategyOverride, @BatchSizeOverride, @BulkCopyTimeoutSecondsOverride, @RetryableSqlErrorsOverride,
                    @ChangeTrackingColumn, @IncrementalStrategy
                );
            """;

        await using var cmd = new SqlCommand(sql, child) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, config.TableName, 100);
        cmd.AddParam("@PrimaryKeyColumns", SqlDbType.NVarChar, string.Join(",", config.PrimaryKeyColumns), 400);
        cmd.AddParam("@IdentityColumn", SqlDbType.NVarChar, config.IdentityColumn, 100);
        cmd.AddParam("@MergeStrategy", SqlDbType.NVarChar, config.MergeStrategy.ToString(), 20);
        cmd.AddParam("@BatchSize", SqlDbType.Int, config.BatchSize);
        cmd.AddParam("@BulkCopyTimeoutSeconds", SqlDbType.Int, config.BulkCopyTimeoutSeconds);
        cmd.AddParam("@RetryableSqlErrors", SqlDbType.NVarChar,
            config.RetryableSqlErrorNumbers.Count == 0 ? null : string.Join(",", config.RetryableSqlErrorNumbers), 200);
        cmd.AddParam("@TvpTypeName", SqlDbType.NVarChar, config.TvpTypeName, 200);
        cmd.AddParam("@SchemaFingerprint", SqlDbType.NVarChar, config.SchemaFingerprint, 100);
        cmd.AddParam("@ConfiguredAtUtc", SqlDbType.DateTime, config.ConfiguredAtUtc);
        cmd.AddParam("@MergeStrategyOverride", SqlDbType.NVarChar, config.MergeStrategyOverride?.ToString(), 20);
        cmd.AddParam("@BatchSizeOverride", SqlDbType.Int, config.BatchSizeOverride);
        cmd.AddParam("@BulkCopyTimeoutSecondsOverride", SqlDbType.Int, config.BulkCopyTimeoutSecondsOverride);
        cmd.AddParam("@RetryableSqlErrorsOverride", SqlDbType.NVarChar,
            config.RetryableSqlErrorsOverride is null ? null : string.Join(",", config.RetryableSqlErrorsOverride), 200);
        // The column keeps its original name, ChangeTrackingColumn, so existing databases need no
        // migration; in code it is IncrementalColumn, to avoid confusion with SQL Server Change Tracking.
        cmd.AddParam("@ChangeTrackingColumn", SqlDbType.NVarChar, config.IncrementalColumn, 100);
        cmd.AddParam("@IncrementalStrategy", SqlDbType.NVarChar, config.IncrementalStrategy.ToString(), 20);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IReadOnlyList<string> SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<int> SplitCsvInts(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}
