using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Infrastructure;
using SmartTableSync.Logging;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// TableSyncerBase.ReconcileDeletesAsync: a row present only in Child (simulating one deleted
/// from Parent since the last sync ran) should be removed. Reconciliation has no time-based
/// filter - it diffs the full parent/child key sets - so unlike the incremental-sync tests,
/// these don't need synthetic timestamps to stay isolated from sibling tests.
/// </summary>
[Collection("SqlServer")]
public class DeleteReconciliationTests
{
    private readonly MsSqlTestFixture _fixture;

    public DeleteReconciliationTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueSyncer_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        const int orphanedId = 601;
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(child, orphanedId, attributeId: 1, description: "Orphan", slug: "orphan-row", lastUpdate: DateTime.UtcNow);
        var syncer = new ProductAttributeValueSyncer(new BatchLogger(), NullLogger<ProductAttributeValueSyncer>.Instance);

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount);
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id", child);
        countCmd.AddParam("@Id", SqlDbType.Int, orphanedId);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProductCatalogItemSyncer_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const string orphanedSku = "ORPHAN-SKU";
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(child, orphanedSku, description: "Orphan", lastUpdate: DateTime.UtcNow);
        var syncer = new ProductCatalogItemSyncer(new BatchLogger(), NullLogger<ProductCatalogItemSyncer>.Instance);

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount);
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku", child);
        countCmd.AddParam("@Sku", SqlDbType.NVarChar, orphanedSku, 50);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DocumentAssetSyncer_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const int orphanedDocumentId = 901;
        const string orphanedLanguageCode = "en";
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(child, orphanedDocumentId, orphanedLanguageCode, title: "Orphan", lastUpdate: DateTime.UtcNow);
        var syncer = new DocumentAssetSyncer(new BatchLogger(), NullLogger<DocumentAssetSyncer>.Instance);

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount);
        await using var countCmd = new SqlCommand("""
            SELECT COUNT(*) FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """, child);
        countCmd.AddParam("@DocumentId", SqlDbType.Int, orphanedDocumentId);
        countCmd.AddParam("@LanguageCode", SqlDbType.NVarChar, orphanedLanguageCode, 50);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
