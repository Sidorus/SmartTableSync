namespace SmartTableSync.Syncers;

public sealed class ProductCatalogItemSyncer : TableSyncerBase
{
    public ProductCatalogItemSyncer(IBatchLogger batchLogger, ILogger<ProductCatalogItemSyncer> logger)
        : base(batchLogger, logger) { }

    public override string TableName => "ProductCatalogItem";
    protected override int BatchSize => 200;
    protected override int EstimatedRowCount => 65_536; // next power of 2 above ~48K

    protected override SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey)
    {
        var cmd = new SqlCommand("""
            SELECT TOP (@Size) * FROM [dbo].[ProductCatalogItem]
            WHERE  [Sku] > @LastSku
            ORDER  BY [Sku]
            """, parent) { CommandTimeout = 120 };

        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = BatchSize;
        cmd.Parameters.Add("@LastSku", SqlDbType.NVarChar, 50).Value = resumeKey ?? string.Empty;
        return cmd;
    }

    protected override SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync)
    {
        var cmd = new SqlCommand("""
            SELECT * FROM [dbo].[ProductCatalogItem]
            WHERE  [LastUpdate] >= DATEADD(minute, -1, @LastSync)
            ORDER  BY [Sku]
            """, parent) { CommandTimeout = 120 };

        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    protected override string ExtractKey(DataRow row) =>
        (string)row["Sku"];

    protected override SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        new("SELECT [Sku] FROM [dbo].[ProductCatalogItem]", parent) { CommandTimeout = 120 };

    protected override SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        new("SELECT [Sku] FROM [dbo].[ProductCatalogItem]", child) { CommandTimeout = 120 };

    protected override async Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku", child)
        { CommandTimeout = 60 };
        cmd.Parameters.Add("@Sku", SqlDbType.NVarChar, 50).Value = key;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Passes the full batch to sp_SyncProductCatalogItemBatch via TVP in a single round trip.
    // If the SP throws (transient error), the exception propagates to the base class,
    // which stops the batch loop without advancing the resume key so the batch is retried
    // on the next 5-minute run.
    protected override async Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand("[dbo].[sp_SyncProductCatalogItemBatch]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        sp.Parameters.Add(new SqlParameter("@Rows", SqlDbType.Structured)
        {
            TypeName = "dbo.ProductCatalogItemTableType",
            Value = batch
        });

        await sp.ExecuteNonQueryAsync(ct);
        return (batch.Rows.Count, 0, false);
    }
}
