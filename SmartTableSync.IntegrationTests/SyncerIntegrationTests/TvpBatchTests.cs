using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Planning;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// ProductCatalogItem only - the only table the heuristic derives a Tvp merge strategy for.
/// A single incremental sync with several changed rows should merge all of them in one
/// stored-procedure call (sp_SyncProductCatalogItemBatch) via ConfigurableTableSyncer's
/// generic MergeBatchTvpAsync, proving the TVP column ordering built from TableProfile.Columns
/// lines up with the real dbo.ProductCatalogItemTableType definition - not just for one row.
/// </summary>
[Collection("SqlServer")]
public class TvpBatchTests
{
    private readonly MsSqlTestFixture _fixture;

    public TvpBatchTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductCatalogItemTable_MergesMultipleRowsInOneTvpBatch()
    {
        // Arrange - 3 rows changed in Parent in the same window
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var syncPoint = DateTime.UtcNow.AddHours(-8);
        var skus = new[] { "CFG-MULTI-A", "CFG-MULTI-B", "CFG-MULTI-C" };
        foreach (var sku in skus)
            await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku, description: $"Batch item {sku}", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - BuildIncrementalCommand's filter has no upper bound (see
        // IncrementalInsertTests' class doc comment), so RowsSynced can't be asserted exactly;
        // the 3-row COUNT check below is the precise proof this specific batch merged correctly.
        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= skus.Length);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductCatalogItem] WHERE [Sku] IN ('CFG-MULTI-A', 'CFG-MULTI-B', 'CFG-MULTI-C')", child);
        Assert.Equal(3, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
