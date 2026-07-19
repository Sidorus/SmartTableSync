using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.Infrastructure;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Observability;
using SmartTableSync.Planning;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.Support;

/// <summary>
/// Shared row insert/update/read helpers used across SyncerIntegrationTests's test classes -
/// each builds one parameterized SqlCommand for a specific table shape, so the test classes
/// themselves only contain Arrange/Act/Assert, not repeated column-by-column SQL.
/// </summary>
public static class SyncerTestHelpers
{
    /// <summary>Builds a ConfigurableTableSyncer for the given table via the real production
    /// construction path (SchemaReader -> SyncTableProvisioner -> TableSyncerFactory), so
    /// tests exercise the exact same code SyncOrchestrator will use, not a shortcut. Returns
    /// the derived SyncPlan alongside the syncer so tests can assert the chosen MergeStrategy
    /// matches what the hand-written engine encoded per table.</summary>
    public static async Task<(ITableSyncer Syncer, SyncPlan Plan)> BuildConfigurableSyncerAsync(
        SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct = default)
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan) = await provisioner.EnsureProvisionedAsync(parent, child, tableName, ct);

        var factory = new TableSyncerFactory(new BatchLogger(), NullLoggerFactory.Instance);
        return (factory.Create(profile, plan), plan);
    }

    public static async Task InsertNewProductAttributeValueRowAsync(
        SqlConnection parent, int attributeId, string slug, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[ProductAttributeValue]
                ([AttributeId], [Description], [LastUpdate], [SortOrder], [Slug])
            VALUES (@AttributeId, @Description, @LastUpdate, @SortOrder, @Slug)
            """, parent);
        cmd.AddParam("@AttributeId", SqlDbType.Int, attributeId);
        cmd.AddParam("@Description", SqlDbType.NVarChar, "Extra Large", -1);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@SortOrder", SqlDbType.Int, 2);
        cmd.AddParam("@Slug", SqlDbType.NVarChar, slug, 200);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task InsertNewProductCatalogItemRowAsync(
        SqlConnection parent, string sku, string description, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[ProductCatalogItem]
                ([Sku], [ProductId], [Description], [NetPrice], [GrossPrice], [CurrencyCode],
                 [StockBalance], [IsAvailable], [IsPublished], [Status], [LastUpdate])
            VALUES (@Sku, @ProductId, @Description, @NetPrice, @GrossPrice, @CurrencyCode,
                    @StockBalance, @IsAvailable, @IsPublished, @Status, @LastUpdate)
            """, parent);
        cmd.AddParam("@Sku", SqlDbType.NVarChar, sku, 50);
        cmd.AddParam("@ProductId", SqlDbType.Int, 1004);
        cmd.AddParam("@Description", SqlDbType.NVarChar, description, -1);
        cmd.AddParam("@NetPrice", SqlDbType.Real, 0.65f);
        cmd.AddParam("@GrossPrice", SqlDbType.Real, 0.81f);
        cmd.AddParam("@CurrencyCode", SqlDbType.NVarChar, "SEK", 50);
        cmd.AddParam("@StockBalance", SqlDbType.Real, 300.0f);
        cmd.AddParam("@IsAvailable", SqlDbType.Bit, true);
        cmd.AddParam("@IsPublished", SqlDbType.Bit, true);
        cmd.AddParam("@Status", SqlDbType.NVarChar, "A", 1);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task InsertNewDocumentAssetRowAsync(
        SqlConnection parent, int documentId, string languageCode, string title, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[DocumentAsset]
                ([DocumentId], [LanguageCode], [MediaLibraryId], [Title], [FileType], [MediaUrl],
                 [IsPublished], [SortOrder], [LastUpdate], [CreatedBy])
            VALUES (@DocumentId, @LanguageCode, @MediaLibraryId, @Title, @FileType, @MediaUrl,
                    @IsPublished, @SortOrder, @LastUpdate, @CreatedBy)
            """, parent);
        cmd.AddParam("@DocumentId", SqlDbType.Int, documentId);
        cmd.AddParam("@LanguageCode", SqlDbType.NVarChar, languageCode, 50);
        cmd.AddParam("@MediaLibraryId", SqlDbType.Int, 3);
        cmd.AddParam("@Title", SqlDbType.VarChar, title, 250);
        cmd.AddParam("@FileType", SqlDbType.NVarChar, "pdf", 50);
        cmd.AddParam("@MediaUrl", SqlDbType.NVarChar, $"https://example.test/docs/{documentId}-{languageCode}.pdf", 200);
        cmd.AddParam("@IsPublished", SqlDbType.Bit, true);
        cmd.AddParam("@SortOrder", SqlDbType.Int, 3);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@CreatedBy", SqlDbType.NVarChar, "tester", 50);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Same shape as InsertNewDocumentAssetRowAsync, but with a genuinely large
    /// FileContent value. DocumentAsset's real rows can reach ~50 MB - SyncPlanHeuristic only
    /// picks RowByRow for a FK-less table when it observes a row over LargeRowThresholdBytes
    /// (1 MB), so a test relying on real introspection needs at least one row that's actually
    /// large, not just NULL/small LOB columns like the standard JSON seed data.</summary>
    public static async Task InsertLargeDocumentAssetRowAsync(
        SqlConnection parent, int documentId, string languageCode, DateTime lastUpdate, int fileContentBytes = 2_000_000)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO [dbo].[DocumentAsset]
                ([DocumentId], [LanguageCode], [MediaLibraryId], [Title], [FileType], [MediaUrl],
                 [IsPublished], [SortOrder], [FileContent], [LastUpdate], [CreatedBy])
            VALUES (@DocumentId, @LanguageCode, @MediaLibraryId, @Title, @FileType, @MediaUrl,
                    @IsPublished, @SortOrder, @FileContent, @LastUpdate, @CreatedBy)
            """, parent);
        cmd.AddParam("@DocumentId", SqlDbType.Int, documentId);
        cmd.AddParam("@LanguageCode", SqlDbType.NVarChar, languageCode, 50);
        cmd.AddParam("@MediaLibraryId", SqlDbType.Int, 3);
        cmd.AddParam("@Title", SqlDbType.VarChar, "Large Attachment", 250);
        cmd.AddParam("@FileType", SqlDbType.NVarChar, "pdf", 50);
        cmd.AddParam("@MediaUrl", SqlDbType.NVarChar, $"https://example.test/docs/{documentId}-{languageCode}.pdf", 200);
        cmd.AddParam("@IsPublished", SqlDbType.Bit, true);
        cmd.AddParam("@SortOrder", SqlDbType.Int, 9);
        cmd.AddParam("@FileContent", SqlDbType.VarBinary, new byte[fileContentBytes], -1);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@CreatedBy", SqlDbType.NVarChar, "tester", 50);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Idempotent version of InsertLargeDocumentAssetRowAsync (DocumentId 999,
    /// LanguageCode "xx") - several test classes in the shared collection each independently
    /// need DocumentAsset's introspected profile to include a large row before the first one
    /// of them provisions it (SyncTableProvisioner persists the derived plan and every later
    /// caller reuses it), and xUnit doesn't guarantee which one runs first.</summary>
    public static async Task EnsureLargeDocumentAssetRowSeededAsync(SqlConnection parent)
    {
        await using var countCommand = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[DocumentAsset] WHERE [DocumentId] = 999 AND [LanguageCode] = 'xx'", parent);
        var existing = (int)(await countCommand.ExecuteScalarAsync())!;
        if (existing > 0) return;

        await InsertLargeDocumentAssetRowAsync(parent, documentId: 999, languageCode: "xx", lastUpdate: DateTime.UtcNow);
    }

    public static async Task InsertProductAttributeValueWithExplicitIdAsync(
        SqlConnection connection, int attributeValueId, int attributeId, string description, string slug, DateTime lastUpdate)
    {
        await using (var identityOn = new SqlCommand("SET IDENTITY_INSERT [dbo].[ProductAttributeValue] ON", connection))
            await identityOn.ExecuteNonQueryAsync();

        await using (var cmd = new SqlCommand("""
            INSERT INTO [dbo].[ProductAttributeValue]
                ([AttributeValueId], [AttributeId], [Description], [LastUpdate], [SortOrder], [Slug])
            VALUES (@Id, @AttributeId, @Description, @LastUpdate, @SortOrder, @Slug)
            """, connection))
        {
            cmd.AddParam("@Id", SqlDbType.Int, attributeValueId);
            cmd.AddParam("@AttributeId", SqlDbType.Int, attributeId);
            cmd.AddParam("@Description", SqlDbType.NVarChar, description, -1);
            cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
            cmd.AddParam("@SortOrder", SqlDbType.Int, 1);
            cmd.AddParam("@Slug", SqlDbType.NVarChar, slug, 200);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var identityOff = new SqlCommand("SET IDENTITY_INSERT [dbo].[ProductAttributeValue] OFF", connection);
        await identityOff.ExecuteNonQueryAsync();
    }

    public static async Task InsertAttributeWithExplicitIdAsync(SqlConnection connection, int attributeId, string name)
    {
        await using (var identityOn = new SqlCommand("SET IDENTITY_INSERT [dbo].[Attribute] ON", connection))
            await identityOn.ExecuteNonQueryAsync();

        await using (var cmd = new SqlCommand(
            "INSERT INTO [dbo].[Attribute] ([AttributeId], [Name]) VALUES (@Id, @Name)", connection))
        {
            cmd.AddParam("@Id", SqlDbType.Int, attributeId);
            cmd.AddParam("@Name", SqlDbType.NVarChar, name, 100);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var identityOff = new SqlCommand("SET IDENTITY_INSERT [dbo].[Attribute] OFF", connection);
        await identityOff.ExecuteNonQueryAsync();
    }

    public static async Task UpdateProductAttributeValueDescriptionAsync(
        SqlConnection parent, int attributeValueId, string newDescription, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            UPDATE [dbo].[ProductAttributeValue] SET [Description] = @Description, [LastUpdate] = @LastUpdate
            WHERE [AttributeValueId] = @Id
            """, parent);
        cmd.AddParam("@Description", SqlDbType.NVarChar, newDescription, -1);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@Id", SqlDbType.Int, attributeValueId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateProductCatalogItemDescriptionAsync(
        SqlConnection parent, string sku, string newDescription, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            UPDATE [dbo].[ProductCatalogItem] SET [Description] = @Description, [LastUpdate] = @LastUpdate
            WHERE [Sku] = @Sku
            """, parent);
        cmd.AddParam("@Description", SqlDbType.NVarChar, newDescription, -1);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@Sku", SqlDbType.NVarChar, sku, 50);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateDocumentAssetTitleAsync(
        SqlConnection parent, int documentId, string languageCode, string newTitle, DateTime lastUpdate)
    {
        await using var cmd = new SqlCommand("""
            UPDATE [dbo].[DocumentAsset] SET [Title] = @Title, [LastUpdate] = @LastUpdate
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """, parent);
        cmd.AddParam("@Title", SqlDbType.VarChar, newTitle, 250);
        cmd.AddParam("@LastUpdate", SqlDbType.DateTime, lastUpdate);
        cmd.AddParam("@DocumentId", SqlDbType.Int, documentId);
        cmd.AddParam("@LanguageCode", SqlDbType.NVarChar, languageCode, 50);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<string> ReadScalarStringAsync(
        SqlConnection connection, string sql, params (string Name, SqlDbType Type, object Value, int Size)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, connection);
        foreach (var (name, type, value, size) in parameters)
            cmd.AddParam(name, type, value, size);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task AssertBatchLogCompletedAsync(SqlConnection child, string tableName)
    {
        await using var logCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[SyncBatchLog] WHERE [TableName] = @T AND [Status] = 'completed'", child);
        logCmd.AddParam("@T", SqlDbType.NVarChar, tableName, 100);
        Assert.True((int)(await logCmd.ExecuteScalarAsync())! >= 1);
    }
}
