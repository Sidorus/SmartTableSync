namespace SmartTableSync.Syncers;

public sealed class ProductAttributeValueSyncer : TableSyncerBase
{
    public ProductAttributeValueSyncer(IBatchLogger batchLogger, ILogger<ProductAttributeValueSyncer> logger)
        : base(batchLogger, logger) { }

    public override string TableName => "ProductAttributeValue";
    protected override int BatchSize => 500;
    protected override int EstimatedRowCount => 131_072; // next power of 2 above ~97K

    // AttributeValueId is an IDENTITY column — KeepIdentity preserves the parent's IDs
    // during bulk copy, equivalent to SET IDENTITY_INSERT ON at the SP level.
    protected override SqlBulkCopyOptions BulkCopyOptions => SqlBulkCopyOptions.KeepIdentity;

    protected override SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey)
    {
        var cmd = new SqlCommand("""
            SELECT TOP (@Size) * FROM [dbo].[ProductAttributeValue]
            WHERE  [AttributeValueId] > @LastId
            ORDER  BY [AttributeValueId]
            """, parent) { CommandTimeout = 120 };

        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = BatchSize;
        cmd.Parameters.Add("@LastId", SqlDbType.Int).Value = resumeKey != null ? int.Parse(resumeKey) : 0;
        return cmd;
    }

    protected override SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync)
    {
        var cmd = new SqlCommand("""
            SELECT * FROM [dbo].[ProductAttributeValue]
            WHERE  [LastUpdate] >= DATEADD(minute, -1, @LastSync)
            ORDER  BY [AttributeValueId]
            """, parent) { CommandTimeout = 120 };

        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    protected override string ExtractKey(DataRow row) =>
        row["AttributeValueId"].ToString()!;

    // FK violations (547) are expected when dbo.Attribute hasn't arrived yet in the child.
    // Per-row error isolation is why ProductAttributeValue uses row-by-row merge inside
    // MergeBatchAsync rather than a TVP call — a TVP MERGE would fail the entire batch on one bad FK.
    protected override bool IsExpectedError(Exception ex) =>
        ex is SqlException { Number: 547 };

    protected override async Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct)
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
                // FK timing race: Attribute not yet in child. Flag retryable so LastSyncDate
                // is not advanced — the row will be re-queried and retried on the next run.
                Logger.LogWarning("{Table} key '{Key}': skipped (FK not ready) — {Msg}", TableName, key, ex.Message);
                errored++;
                retryable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "{Table} key '{Key}': merge failed", TableName, key);
                errored++;
            }
        }

        return (synced, errored, retryable);
    }

    protected override SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        new("SELECT CAST([AttributeValueId] AS NVARCHAR(20)) FROM [dbo].[ProductAttributeValue]", parent)
        { CommandTimeout = 120 };

    protected override SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        new("SELECT CAST([AttributeValueId] AS NVARCHAR(20)) FROM [dbo].[ProductAttributeValue]", child)
        { CommandTimeout = 120 };

    protected override async Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id", child)
        { CommandTimeout = 60 };
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = int.Parse(key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MergeSingleRowAsync(DataRow row, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand("[dbo].[sp_SyncProductAttributeValue]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };

        sp.AddParam("@AttributeValueId", SqlDbType.Int, row["AttributeValueId"]);
        sp.AddParam("@AttributeId", SqlDbType.Int, row["AttributeId"]);
        sp.AddParam("@Description", SqlDbType.NVarChar, row["Description"], -1);
        sp.AddParam("@LastUpdate", SqlDbType.DateTime, row["LastUpdate"]);
        sp.AddParam("@ImageId", SqlDbType.Int, row["ImageId"]);
        sp.AddParam("@SortOrder", SqlDbType.Int, row["SortOrder"]);
        sp.AddParam("@ActiveToDate", SqlDbType.DateTime, row["ActiveToDate"]);
        sp.AddParam("@ActiveFromDate", SqlDbType.DateTime, row["ActiveFromDate"]);
        sp.AddParam("@Slug", SqlDbType.NVarChar, row["Slug"], 200);

        await sp.ExecuteNonQueryAsync(ct);
    }
}
