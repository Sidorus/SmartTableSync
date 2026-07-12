namespace SmartTableSync.Syncers;

public sealed class DocumentAssetSyncer : TableSyncerBase
{
    public DocumentAssetSyncer(IBatchLogger batchLogger, ILogger<DocumentAssetSyncer> logger)
        : base(batchLogger, logger) { }

    public override string TableName => "DocumentAsset";
    protected override int BatchSize => 5; // rows can reach 52 MB each

    // Allow up to 5 min for a bulk copy of 5 rows that may total 260 MB.
    protected override int BulkCopyTimeout => 300;

    protected override SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey)
    {
        var (docId, lang) = ParseKey(resumeKey);

        // Composite keyset pagination: (DocumentId, LanguageCode) > (lastDocId, lastLang)
        var cmd = new SqlCommand("""
            SELECT TOP (@Size) * FROM [dbo].[DocumentAsset]
            WHERE  [DocumentId] > @LastDocId
                OR ([DocumentId] = @LastDocId AND [LanguageCode] > @LastLang)
            ORDER  BY [DocumentId], [LanguageCode]
            """, parent) { CommandTimeout = 300 };

        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = BatchSize;
        cmd.Parameters.Add("@LastDocId", SqlDbType.Int).Value = docId;
        cmd.Parameters.Add("@LastLang", SqlDbType.NVarChar, 50).Value = lang;
        return cmd;
    }

    protected override SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync)
    {
        var cmd = new SqlCommand("""
            SELECT * FROM [dbo].[DocumentAsset]
            WHERE  [LastUpdate] >= DATEADD(minute, -1, @LastSync)
            """, parent) { CommandTimeout = 300 };

        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    protected override string ExtractKey(DataRow row) =>
        $"{row["DocumentId"]}|{row["LanguageCode"]}";

    protected override SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        new("""
            SELECT CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]
            FROM   [dbo].[DocumentAsset]
            """, parent) { CommandTimeout = 300 };

    protected override SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        new("""
            SELECT CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]
            FROM   [dbo].[DocumentAsset]
            """, child) { CommandTimeout = 300 };

    protected override async Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct)
    {
        var (docId, lang) = ParseKey(key);
        await using var cmd = new SqlCommand("""
            DELETE FROM [dbo].[DocumentAsset]
            WHERE  [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """, child) { CommandTimeout = 300 };
        cmd.Parameters.Add("@DocumentId", SqlDbType.Int).Value = docId;
        cmd.Parameters.Add("@LanguageCode", SqlDbType.NVarChar, 50).Value = lang;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Row-by-row merge: rows can be up to 52 MB each (varbinary(max) columns).
    // All 5 rows are loaded into the DataTable before any SP is called (by the base class).
    // Row-by-row ensures that a failure on one SP call does not prevent the remaining rows
    // in the batch from being attempted — isolation is at the SP level, not the memory level.
    protected override async Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct)
    {
        int synced = 0, errored = 0;

        foreach (DataRow row in batch.Rows)
        {
            var key = ExtractKey(row);
            try
            {
                await MergeSingleRowAsync(row, child, ct);
                synced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "{Table} key '{Key}': merge failed", TableName, key);
                errored++;
            }
        }

        return (synced, errored, false);
    }

    private async Task MergeSingleRowAsync(DataRow row, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand("[dbo].[sp_SyncDocumentAsset]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 300
        };

        sp.AddParam("@DocumentId", SqlDbType.Int, row["DocumentId"]);
        sp.AddParam("@LanguageCode", SqlDbType.NVarChar, row["LanguageCode"], 50);
        sp.AddParam("@MediaLibraryId", SqlDbType.Int, row["MediaLibraryId"]);
        sp.AddParam("@Title", SqlDbType.VarChar, row["Title"], 250);
        sp.AddParam("@FileType", SqlDbType.NVarChar, row["FileType"], 50);
        sp.AddParam("@MediaUrl", SqlDbType.NVarChar, row["MediaUrl"], 200);
        sp.AddParam("@IsPublished", SqlDbType.Bit, row["IsPublished"]);
        sp.AddParam("@PublishedFrom", SqlDbType.DateTime, row["PublishedFrom"]);
        sp.AddParam("@PublishedTo", SqlDbType.DateTime, row["PublishedTo"]);
        sp.AddParam("@SortOrder", SqlDbType.Int, row["SortOrder"]);
        sp.AddParam("@FileContent", SqlDbType.VarBinary, row["FileContent"], -1);
        sp.AddParam("@OriginalFileName", SqlDbType.NVarChar, row["OriginalFileName"], 200);
        sp.AddParam("@Notes", SqlDbType.NVarChar, row["Notes"], 250);
        sp.AddParam("@LastUpdate", SqlDbType.DateTime, row["LastUpdate"]);
        sp.AddParam("@CreatedBy", SqlDbType.NVarChar, row["CreatedBy"], 50);
        sp.AddParam("@PreviewImage", SqlDbType.VarBinary, row["PreviewImage"], -1);
        sp.AddParam("@PdfRendition", SqlDbType.VarBinary, row["PdfRendition"], -1);
        sp.AddParam("@PreviewSource", SqlDbType.NVarChar, row["PreviewSource"], 20);
        sp.AddParam("@PreviewUpdatedAt", SqlDbType.DateTime, row["PreviewUpdatedAt"]);
        sp.AddParam("@PdfSource", SqlDbType.NVarChar, row["PdfSource"], 20);
        sp.AddParam("@PdfUpdatedAt", SqlDbType.DateTime, row["PdfUpdatedAt"]);

        await sp.ExecuteNonQueryAsync(ct);
    }

    private static (int docId, string lang) ParseKey(string? key)
    {
        if (key is null) return (0, string.Empty);
        var parts = key.Split('|', 2);
        if (parts.Length != 2)
            throw new FormatException($"DocumentAsset key has unexpected format: '{key}'");
        return (int.Parse(parts[0]), parts[1]);
    }
}
