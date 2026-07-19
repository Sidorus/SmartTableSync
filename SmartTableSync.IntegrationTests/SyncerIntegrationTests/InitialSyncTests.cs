using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Planning;
using SmartTableSync.State;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Initial sync via ConfigurableTableSyncer, built through the real production construction
/// path (SchemaReader -> SyncTableProvisioner -> TableSyncerFactory) - the same construction
/// SyncOrchestrator itself uses.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because initial sync reads/writes SyncBatchLog's resume cursor keyed only by
/// (TableName, Phase) - this is the only place in the test suite that exercises the
/// initial-sync path for these 3 real tables, so keeping it isolated means the seed data and
/// row-count assertions can stay exact instead of needing the >= tolerance the shared-database
/// tests elsewhere in this namespace require.
/// </summary>
public class InitialSyncTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public InitialSyncTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueTable_BulkCopiesSeededRowsAndLogsBatch()
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
        var (syncer, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductAttributeValue");

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - has a real incoming FK (Attribute) -> heuristic must pick RowByRow, same
        // as the hand-written engine's actual (hardcoded) choice for this table.
        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);
        Assert.True(result.IsInitialSync);
        Assert.Equal(rows.Count, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductAttributeValue]", child);
        Assert.Equal(rows.Count, (int)(await countCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "ProductAttributeValue");
    }

    [Fact]
    public async Task ProductCatalogItemTable_BulkCopiesSeededRowsAndLogsBatch()
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
        var (syncer, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - no FK, small rows -> heuristic must pick Tvp, same as the hand-written engine
        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);
        Assert.True(result.IsInitialSync);
        Assert.Equal(rows.Count, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductCatalogItem]", child);
        Assert.Equal(rows.Count, (int)(await countCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "ProductCatalogItem");
    }

    [Fact]
    public async Task DocumentAssetTable_BulkCopiesSeededRowsAndLogsBatch()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "DocumentAsset.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "DocumentAsset", rows);

        // The JSON seed rows all have NULL LOB columns - real introspection would correctly
        // (if misleadingly, for this test) see no large rows and pick Tvp. Production's real
        // DocumentAsset table has rows up to ~50 MB (and only a row-by-row SP deployed, no
        // TVP type), so this test needs at least one genuinely large row for the heuristic to
        // observe the same reality the hand-written engine was built around.
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);
        var totalRowCount = rows.Count + 1;

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "DocumentAsset", CancellationToken.None);
        var (syncer, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "DocumentAsset");

        // Act
        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert - no FK, but large LOB rows -> heuristic must pick RowByRow, same as the
        // hand-written engine
        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);
        Assert.True(result.IsInitialSync);
        Assert.Equal(totalRowCount, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[DocumentAsset]", child);
        Assert.Equal(totalRowCount, (int)(await countCmd.ExecuteScalarAsync())!);
        await SyncerTestHelpers.AssertBatchLogCompletedAsync(child, "DocumentAsset");
    }
}
