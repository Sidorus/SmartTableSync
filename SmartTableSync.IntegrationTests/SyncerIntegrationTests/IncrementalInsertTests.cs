using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Logging;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Incremental sync: a brand-new row changed in Parent gets picked up and INSERTed into Child
/// via the real stored procedure MERGE - proves the SP works, not just that it exists.
///
/// BuildIncrementalCommand's filter (LastUpdate >= cutoff) has no upper bound, so a test using
/// an earlier cutoff than a sibling test on the same table would also sweep up that sibling's
/// row once it exists. Each test below uses its own synthetic hours-in-the-past timestamp and
/// asserts RowsSynced >= 1 (not ==) for that reason - the precise proof is the specific row's
/// value, checked via a targeted SELECT.
/// </summary>
[Collection("SqlServer")]
public class IncrementalInsertTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalInsertTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueSyncer_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange - a row with a fresh LastUpdate, as if it just changed in the parent
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewProductAttributeValueRowAsync(parent, attributeId: 2, slug: "extra-large", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductAttributeValueSyncer(new BatchLogger(), NullLogger<ProductAttributeValueSyncer>.Instance);

        // Act - lastSyncDate != SyncConstants.InitialDate, so this calls sp_SyncProductAttributeValue
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Extra Large", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductAttributeValue] WHERE [Slug] = @Slug",
            ("@Slug", SqlDbType.NVarChar, "extra-large", 200)));
    }

    [Fact]
    public async Task ProductCatalogItemSyncer_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku: "SCREW-M8", description: "M8 Hex Screw", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductCatalogItemSyncer(new BatchLogger(), NullLogger<ProductCatalogItemSyncer>.Instance);

        // Act - lastSyncDate != SyncConstants.InitialDate, so this calls sp_SyncProductCatalogItemBatch (TVP)
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // ProductCatalogItem has no test that deliberately errors a row
        Assert.Equal("M8 Hex Screw", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku",
            ("@Sku", SqlDbType.NVarChar, "SCREW-M8", 50)));
    }

    [Fact]
    public async Task DocumentAssetSyncer_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange - composite key (203, "en"), a value no seed file or other test uses
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(parent, documentId: 203, languageCode: "en", title: "Return Policy", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new DocumentAssetSyncer(new BatchLogger(), NullLogger<DocumentAssetSyncer>.Instance);

        // Act - lastSyncDate != SyncConstants.InitialDate, so this calls sp_SyncDocumentAsset
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // DocumentAsset has no test that deliberately errors a row
        Assert.Equal("Return Policy", await SyncerTestHelpers.ReadScalarStringAsync(child, """
            SELECT [Title] FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """,
            ("@DocumentId", SqlDbType.Int, 203, 0),
            ("@LanguageCode", SqlDbType.NVarChar, "en", 50)));
    }
}
