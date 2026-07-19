using System.Data;
using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Incremental sync via ConfigurableTableSyncer: a row already present in both Parent and
/// Child gets changed in Parent only, then re-synced - MERGE's UPDATE branch, the counterpart
/// to IncrementalInsertTests' INSERT branch. See that class's doc comment for why this shares
/// the [Collection("SqlServer")] database safely and why RowsSynced is asserted as >= 1, not ==.
/// </summary>
[Collection("SqlServer")]
public class IncrementalUpdateTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalUpdateTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueTable_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        const int attributeValueId = 1101;
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(parent, attributeValueId, attributeId: 1, description: "Original", slug: "cfg-update-me", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(child, attributeValueId, attributeId: 1, description: "Original", slug: "cfg-update-me", lastUpdate: originalLastUpdate);

        var syncPoint = DateTime.UtcNow.AddHours(-9);
        await SyncerTestHelpers.UpdateProductAttributeValueDescriptionAsync(parent, attributeValueId, newDescription: "Updated", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductAttributeValue");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - no RowsErrored check, see IncrementalInsertTests' equivalent test
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Updated", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id",
            ("@Id", SqlDbType.Int, attributeValueId, 0)));
    }

    [Fact]
    public async Task ProductCatalogItemTable_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const string sku = "CFG-UPDATE-ME-SKU";
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku, description: "Original Item", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(child, sku, description: "Original Item", lastUpdate: originalLastUpdate);

        var syncPoint = DateTime.UtcNow.AddHours(-9);
        await SyncerTestHelpers.UpdateProductCatalogItemDescriptionAsync(parent, sku, newDescription: "Updated Item", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored);
        Assert.Equal("Updated Item", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku",
            ("@Sku", SqlDbType.NVarChar, sku, 50)));
    }

    [Fact]
    public async Task DocumentAssetTable_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const int documentId = 1304;
        const string languageCode = "en";
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(parent, documentId, languageCode, title: "Original Title", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(child, documentId, languageCode, title: "Original Title", lastUpdate: originalLastUpdate);

        // Ensures DocumentAsset's persisted SyncPlan reflects a real large-row table (see
        // InitialSyncTests' equivalent comment) regardless of which test in this shared
        // collection is the first to provision it.
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);

        var syncPoint = DateTime.UtcNow.AddHours(-9);
        await SyncerTestHelpers.UpdateDocumentAssetTitleAsync(parent, documentId, languageCode, newTitle: "Updated Title", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "DocumentAsset");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored);
        Assert.Equal("Updated Title", await SyncerTestHelpers.ReadScalarStringAsync(child, """
            SELECT [Title] FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """,
            ("@DocumentId", SqlDbType.Int, documentId, 0),
            ("@LanguageCode", SqlDbType.NVarChar, languageCode, 50)));
    }
}
