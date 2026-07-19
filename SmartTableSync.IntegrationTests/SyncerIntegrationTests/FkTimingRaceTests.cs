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
/// ProductAttributeValueSyncer only - the only one of the three syncers whose table has an FK.
/// Simulates Data Sync not having delivered a lookup row to Child yet: the FK check inside
/// sp_SyncProductAttributeValue's MERGE rejects the row with SQL error 547, and the row-by-row
/// merge path is expected to catch that as a retryable error rather than fail the whole batch.
/// </summary>
[Collection("SqlServer")]
public class FkTimingRaceTests
{
    private readonly MsSqlTestFixture _fixture;

    public FkTimingRaceTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueSyncer_SkipsRowWithFkNotYetInChildAndMarksRetryable()
    {
        // Arrange - AttributeId 999 exists in Parent's Attribute table but deliberately NOT in
        // Child's, simulating Data Sync not having delivered that lookup row yet.
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SyncerTestHelpers.InsertAttributeWithExplicitIdAsync(parent, attributeId: 999, name: "Test-Only, Not In Child");

        var syncPoint = DateTime.UtcNow;
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(parent, attributeValueId: 701, attributeId: 999, description: "Should Not Sync", slug: "fk-race-row", lastUpdate: syncPoint.AddSeconds(5));
        var syncer = new ProductAttributeValueSyncer(new BatchLogger(), NullLogger<ProductAttributeValueSyncer>.Instance);

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - the FK violation (547) is caught and treated as an expected, retryable
        // error, not an unhandled exception that would crash the rest of the batch
        Assert.False(result.IsInitialSync);
        Assert.Equal(0, result.RowsSynced);
        Assert.True(result.RowsErrored >= 1);
        Assert.True(result.HasRetryableErrors);
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id", child);
        countCmd.AddParam("@Id", SqlDbType.Int, 701);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
