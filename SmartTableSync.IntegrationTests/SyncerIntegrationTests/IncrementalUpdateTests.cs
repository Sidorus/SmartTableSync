using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Logging;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Incremental sync: a row already present in both Parent and Child (as if a prior sync ran)
/// gets changed in Parent only, then re-synced via the real stored procedure MERGE - this is
/// MERGE's UPDATE branch, the counterpart to IncrementalInsertTests' INSERT branch.
///
/// See IncrementalInsertTests' class doc comment for why RowsSynced is asserted as >= 1, not ==.
/// </summary>
[Collection("SqlServer")]
public class IncrementalUpdateTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalUpdateTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueSyncer_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        const int attributeValueId = 501;
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(parent, attributeValueId, attributeId: 1, description: "Original", slug: "update-me", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(child, attributeValueId, attributeId: 1, description: "Original", slug: "update-me", lastUpdate: originalLastUpdate);

        var syncPoint = DateTime.UtcNow.AddHours(-9); // distinct hour from this table's incremental-insert test
        await SyncerTestHelpers.UpdateProductAttributeValueDescriptionAsync(parent, attributeValueId, newDescription: "Updated", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductAttributeValueSyncer(new BatchLogger(), NullLogger<ProductAttributeValueSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Updated", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id",
            ("@Id", SqlDbType.Int, attributeValueId, 0)));
    }

    [Fact]
    public async Task ProductCatalogItemSyncer_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const string sku = "UPDATE-ME-SKU";
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku, description: "Original Item", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(child, sku, description: "Original Item", lastUpdate: originalLastUpdate);

        var syncPoint = DateTime.UtcNow.AddHours(-9); // distinct hour from this table's other incremental tests
        await SyncerTestHelpers.UpdateProductCatalogItemDescriptionAsync(parent, sku, newDescription: "Updated Item", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductCatalogItemSyncer(new BatchLogger(), NullLogger<ProductCatalogItemSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // ProductCatalogItem has no test that deliberately errors a row
        Assert.Equal("Updated Item", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku",
            ("@Sku", SqlDbType.NVarChar, sku, 50)));
    }

    [Fact]
    public async Task DocumentAssetSyncer_UpdatesExistingRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const int documentId = 401;
        const string languageCode = "en";
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(parent, documentId, languageCode, title: "Original Title", lastUpdate: originalLastUpdate);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(child, documentId, languageCode, title: "Original Title", lastUpdate: originalLastUpdate);

        var syncPoint = DateTime.UtcNow.AddHours(-9); // distinct hour from this table's other incremental tests
        await SyncerTestHelpers.UpdateDocumentAssetTitleAsync(parent, documentId, languageCode, newTitle: "Updated Title", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new DocumentAssetSyncer(new BatchLogger(), NullLogger<DocumentAssetSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // DocumentAsset has no test that deliberately errors a row
        Assert.Equal("Updated Title", await SyncerTestHelpers.ReadScalarStringAsync(child, """
            SELECT [Title] FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """,
            ("@DocumentId", SqlDbType.Int, documentId, 0),
            ("@LanguageCode", SqlDbType.NVarChar, languageCode, 50)));
    }
}
