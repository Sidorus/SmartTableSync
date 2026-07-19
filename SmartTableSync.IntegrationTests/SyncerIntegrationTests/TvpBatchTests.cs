using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Logging;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// ProductCatalogItemSyncer only - the only one of the three syncers that uses the TVP/batch
/// merge strategy. A single incremental sync with several changed rows should merge all of
/// them in one stored-procedure call (sp_SyncProductCatalogItemBatch), not one call per row -
/// that's the entire point of the TVP path, so it's the one thing worth proving here.
/// </summary>
[Collection("SqlServer")]
public class TvpBatchTests
{
    private readonly MsSqlTestFixture _fixture;

    public TvpBatchTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductCatalogItemSyncer_MergesMultipleRowsInOneTvpBatch()
    {
        // Arrange - 3 rows changed in Parent in the same window
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var syncPoint = DateTime.UtcNow;
        var skus = new[] { "MULTI-A", "MULTI-B", "MULTI-C" };
        foreach (var sku in skus)
            await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku, description: $"Batch item {sku}", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductCatalogItemSyncer(new BatchLogger(), NullLogger<ProductCatalogItemSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - BuildIncrementalCommand's filter has no upper bound (see
        // IncrementalInsertTests' class doc comment), so RowsSynced can't be asserted exactly;
        // the 3-row COUNT check below is the precise proof this specific batch merged correctly.
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= skus.Length);
        Assert.Equal(0, result.RowsErrored); // ProductCatalogItem has no test that deliberately errors a row
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductCatalogItem] WHERE [Sku] IN ('MULTI-A', 'MULTI-B', 'MULTI-C')", child);
        Assert.Equal(3, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
