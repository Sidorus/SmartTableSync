using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Logging;
using SmartTableSync.State;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Initial sync: the SqlBulkCopy path taken when a table's CustomSyncState still has its
/// seeded InitialDate sentinel. Proves the bulk copy works and that SyncBatchLog gets a
/// 'completed' row logged for it, against each of the three hand-written syncers.
/// </summary>
[Collection("SqlServer")]
public class InitialSyncTests
{
    private readonly MsSqlTestFixture _fixture;

    public InitialSyncTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueSyncer_BulkCopiesSeededRowsAndLogsBatch()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductAttributeValue.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductAttributeValue", rows, hasIdentityColumn: true);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "ProductAttributeValue", CancellationToken.None);
        var syncer = new ProductAttributeValueSyncer(new BatchLogger(), NullLogger<ProductAttributeValueSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - Parent is shared with other test classes in this collection, so RowsSynced
        // can be more than rows.Count if another test already added rows there; check our
        // specific seeded keys landed rather than asserting an exact total.
        Assert.True(result.IsInitialSync);
        Assert.True(result.RowsSynced >= rows.Count);
        Assert.Equal(0, result.RowsErrored);
        await using (var seededCountCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] IN (101, 102, 103)", child))
            Assert.Equal(3, (int)(await seededCountCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "ProductAttributeValue");
    }

    [Fact]
    public async Task ProductCatalogItemSyncer_BulkCopiesSeededRowsAndLogsBatch()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductCatalogItem.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem", rows);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "ProductCatalogItem", CancellationToken.None);
        var syncer = new ProductCatalogItemSyncer(new BatchLogger(), NullLogger<ProductCatalogItemSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - Parent is shared with other test classes in this collection, so RowsSynced
        // can be more than rows.Count if another test already added rows there; check our
        // specific seeded keys landed rather than asserting an exact total.
        Assert.True(result.IsInitialSync);
        Assert.True(result.RowsSynced >= rows.Count);
        Assert.Equal(0, result.RowsErrored);
        await using (var seededCountCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductCatalogItem] WHERE [Sku] IN ('BOLT-M6', 'NUT-M6', 'WASHER-M6')", child))
            Assert.Equal(3, (int)(await seededCountCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "ProductCatalogItem");
    }

    [Fact]
    public async Task DocumentAssetSyncer_BulkCopiesSeededRowsAndLogsBatch()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "DocumentAsset.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "DocumentAsset", rows);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "DocumentAsset", CancellationToken.None);
        var syncer = new DocumentAssetSyncer(new BatchLogger(), NullLogger<DocumentAssetSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - Parent is shared with other test classes in this collection, so RowsSynced
        // can be more than rows.Count if another test already added rows there; check our
        // specific seeded keys landed rather than asserting an exact total.
        Assert.True(result.IsInitialSync);
        Assert.True(result.RowsSynced >= rows.Count);
        Assert.Equal(0, result.RowsErrored);
        await using (var seededCountCmd = new SqlCommand("""
            SELECT COUNT(*) FROM [dbo].[DocumentAsset]
            WHERE ([DocumentId] = 201 AND [LanguageCode] IN ('en', 'sv')) OR ([DocumentId] = 202 AND [LanguageCode] = 'en')
            """, child))
            Assert.Equal(3, (int)(await seededCountCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "DocumentAsset");
    }
}
