using System.Data;
using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Sql;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// ConfigurableTableSyncer.ReconcileDeletesAsync: a row present only in Child (simulating one
/// deleted from Parent since the last sync ran) should be removed. No time-based filter is
/// involved here - it diffs the full parent/child key sets via KeysetPredicateBuilder - so
/// this is safe to share the [Collection("SqlServer")] database with every other test class
/// regardless of timestamps.
/// </summary>
[Collection("SqlServer")]
public class DeleteReconciliationTests
{
    private readonly MsSqlTestFixture _fixture;

    public DeleteReconciliationTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueTable_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        // Spaced 100 apart from IncrementalUpdateTests' explicit ID (1101) so this can never
        // collide with IncrementalInsertTests' auto-increment insert, which lands at
        // whatever value the identity seed advances to after any explicit IDENTITY_INSERT.
        const int orphanedId = 1201;
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(child, orphanedId, attributeId: 1, description: "Orphan", slug: "cfg-orphan-row", lastUpdate: DateTime.UtcNow);
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductAttributeValue");

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount); // each orphan-creating test cleans up its own orphan within one serialized run, so no cross-test accumulation
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id", child);
        countCmd.AddParam("@Id", SqlDbType.Int, orphanedId);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProductCatalogItemTable_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const string orphanedSku = "CFG-ORPHAN-SKU";
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(child, orphanedSku, description: "Orphan", lastUpdate: DateTime.UtcNow);
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount); // each orphan-creating test cleans up its own orphan within one serialized run, so no cross-test accumulation
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductCatalogItem] WHERE [Sku] = @Sku", child);
        countCmd.AddParam("@Sku", SqlDbType.NVarChar, orphanedSku, 50);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DocumentAssetTable_RemovesRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        // Ensures DocumentAsset's persisted SyncPlan reflects a real large-row table (see
        // InitialSyncTests' equivalent comment), so this test - if it's the first in the
        // shared collection to provision DocumentAsset - doesn't lock in the wrong merge
        // strategy for every test that runs after it.
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);

        const int orphanedDocumentId = 1305;
        const string orphanedLanguageCode = "en";
        await SyncerTestHelpers.InsertNewDocumentAssetRowAsync(child, orphanedDocumentId, orphanedLanguageCode, title: "Orphan", lastUpdate: DateTime.UtcNow);
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "DocumentAsset");

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount); // each orphan-creating test cleans up its own orphan within one serialized run, so no cross-test accumulation
        await using var countCmd = new SqlCommand("""
            SELECT COUNT(*) FROM [dbo].[DocumentAsset]
            WHERE [DocumentId] = @DocumentId AND [LanguageCode] = @LanguageCode
            """, child);
        countCmd.AddParam("@DocumentId", SqlDbType.Int, orphanedDocumentId);
        countCmd.AddParam("@LanguageCode", SqlDbType.NVarChar, orphanedLanguageCode, 50);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
