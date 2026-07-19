using System.Data;
using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Incremental sync via ConfigurableTableSyncer: a brand-new row changed in Parent gets
/// INSERTed into Child via the real stored procedure MERGE, proving the generic engine -
/// built via SchemaReader -> SyncTableProvisioner -> TableSyncerFactory, exactly how
/// SyncOrchestrator builds it - calls the SP correctly for each of the 3 real tables.
///
/// Shares the [Collection("SqlServer")] database with every other test class in this
/// namespace (safe - unlike initial sync, incremental sync never touches SyncBatchLog).
/// BuildIncrementalCommand's filter (LastUpdate >= cutoff) has no upper bound, so a test
/// using an earlier cutoff than a sibling test on the same table would also sweep up that
/// sibling's row once it exists - each test below uses its own synthetic hours-in-the-past
/// timestamp and asserts RowsSynced >= 1 (not ==) for that reason; the precise proof is the
/// specific row's value, checked via a targeted SELECT.
/// </summary>
[Collection("SqlServer")]
public class IncrementalInsertTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalInsertTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueTable_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewProductAttributeValueRowAsync(parent, attributeId: 2, slug: "cfg-extra-large", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductAttributeValue");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - no RowsErrored check: this table has an FK, and FkTimingRaceTests may
        // leave a deliberately-bad row that this call also sweeps up.
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Extra Large", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductAttributeValue] WHERE [Slug] = @Slug",
            ("@Slug", SqlDbType.NVarChar, "cfg-extra-large", 200)));
    }

    [Fact]
    public async Task ProductCatalogItemTable_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, sku: "CFG-SCREW-M8", description: "M8 Hex Screw", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // ProductCatalogItem has no test that deliberately errors a row
        Assert.Equal("M8 Hex Screw", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Description] FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku",
            ("@Sku", SqlDbType.NVarChar, "CFG-SCREW-M8", 50)));
    }

    [Fact]
    public async Task DocumentAssetTable_MergesNewRowViaRealStoredProcedure()
    {
        // Arrange - composite key (1303, "en"), a value no seed file or other test uses
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        // Ensures DocumentAsset's persisted SyncPlan reflects a real large-row table (see
        // InitialSyncTests' equivalent comment) regardless of which test in this shared
        // collection is the first to provision it.
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);

        var syncPoint = DateTime.UtcNow.AddHours(-10);
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(parent, documentId: 1303, languageCode: "en", title: "Return Policy", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "DocumentAsset");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal(0, result.RowsErrored); // DocumentAsset has no test that deliberately errors a row
        Assert.Equal("Return Policy", await SyncerTestHelpers.ReadScalarStringAsync(child, """
            SELECT [Title] FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """,
            ("@DocumentId", SqlDbType.Int, 1303, 0),
            ("@LanguageCode", SqlDbType.NVarChar, "en", 50)));
    }
}
