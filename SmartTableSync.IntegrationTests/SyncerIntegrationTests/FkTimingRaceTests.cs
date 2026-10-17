using System.Data;
using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Sql;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// ProductAttributeValue only - the only table with an FK. Simulates Data Sync not having
/// delivered a lookup row to Child yet: the FK check inside sp_SyncProductAttributeValue's
/// MERGE rejects the row with SQL error 547. SyncPlanHeuristic marks 547 retryable only when
/// RowByRow was chosen for FK-timing reasons (SyncPlan.RetryableSqlErrorNumbers), so this also
/// proves the heuristic-derived plan - not just a hardcoded IsExpectedError override - drives
/// the same retry behavior the hand-written engine had.
/// </summary>
[Collection("SqlServer")]
public class FkTimingRaceTests
{
    private readonly MsSqlTestFixture _fixture;

    public FkTimingRaceTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductAttributeValueTable_SkipsRowWithFkNotYetInChildAndMarksRetryable()
    {
        // Arrange - AttributeId 1099 exists in Parent's Attribute table but deliberately NOT
        // in Child's, simulating Data Sync not having delivered that lookup row yet.
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SyncerTestHelpers.InsertAttributeWithExplicitIdAsync(parent, attributeId: 1099, name: "Test-Only, Not In Child");

        var syncPoint = DateTime.UtcNow.AddHours(-8);
        // Spaced 100 apart from IncrementalUpdateTests' explicit ID (1101) - see
        // DeleteReconciliationTests' comment on the same issue.
        await SyncerTestHelpers.InsertProductAttributeValueWithExplicitIdAsync(parent, attributeValueId: 1301, attributeId: 1099, description: "Should Not Sync", slug: "cfg-fk-race-row", lastUpdate: syncPoint.AddSeconds(5));
        var (syncer, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductAttributeValue");

        // Act
        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        // Assert - the FK violation (547) is caught and treated as an expected, retryable
        // error, not an unhandled exception that would crash the rest of the batch
        Assert.Contains(547, plan.RetryableSqlErrorNumbers);
        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsErrored >= 1);
        Assert.True(result.HasRetryableErrors);
        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[ProductAttributeValue] WHERE [AttributeValueId] = @Id", child);
        countCmd.AddParam("@Id", SqlDbType.Int, 1301);
        Assert.Equal(0, (int)(await countCmd.ExecuteScalarAsync())!);
    }
}
