using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.Introspection;
using SmartTableSync.State;

namespace SmartTableSync.IntegrationTests;

/// <summary>
/// Confirms the child database setup produced the right objects: the business tables
/// mirror the parent's shape, the system tracking tables exist and are seeded correctly,
/// the TVP type exists, and the sync stored procedures exist. Whether those objects
/// actually work is covered separately by SyncerIntegrationTests.
/// </summary>
[Collection("SqlServer")]
public class ChildSchemaTests
{
    private readonly MsSqlTestFixture _fixture;

    public ChildSchemaTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("ProductAttributeValue")]
    [InlineData("ProductCatalogItem")]
    [InlineData("DocumentAsset")]
    public async Task ChildDatabase_BusinessTablesMatchParentSchema(string tableName)
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        var reader = new SchemaReader();

        // Act
        var parentProfile = await reader.GetFingerprintInputsAsync(parent, tableName, CancellationToken.None);
        var childProfile = await reader.GetFingerprintInputsAsync(child, tableName, CancellationToken.None);

        // Assert
        Assert.Equal(parentProfile.PrimaryKeyColumns, childProfile.PrimaryKeyColumns);
        Assert.Equal(parentProfile.IdentityColumn, childProfile.IdentityColumn);
        Assert.Equal(
            parentProfile.Columns.Select(c => c.Name).OrderBy(n => n),
            childProfile.Columns.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task ChildDatabase_HasSystemTrackingTables()
    {
        // Arrange
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        var reader = new SchemaReader();

        // Act
        var hasCustomSyncState = await reader.TableExistsAsync(child, "CustomSyncState", CancellationToken.None);
        var hasSyncBatchLog = await reader.TableExistsAsync(child, "SyncBatchLog", CancellationToken.None);
        var hasSyncTableConfig = await reader.TableExistsAsync(child, "SyncTableConfig", CancellationToken.None);

        // Assert
        Assert.True(hasCustomSyncState);
        Assert.True(hasSyncBatchLog);
        Assert.True(hasSyncTableConfig);
    }

    [Fact]
    public async Task ChildDatabase_CustomSyncStateStartsEmpty_NoTableNamesHardcoded()
    {
        // GenerateInfrastructureScript() is table-agnostic - it can't know a consumer's table
        // names ahead of time, so CustomSyncState is intentionally seeded with zero rows.
        // SyncStateRepository.GetLastSyncDateAsync already defaults any table with no row to
        // SyncConstants.InitialDate, so explicit per-table seeding was never actually required
        // (the earlier hand-authored script's seed INSERTs for the 3 example tables were
        // convenience, not a functional dependency).
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[CustomSyncState]", child);

        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("ProductAttributeValue")]
    [InlineData("ProductCatalogItem")]
    [InlineData("DocumentAsset")]
    public async Task GetLastSyncDateAsync_NoRowYet_DefaultsToInitialDate(string tableName)
    {
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        var stateRepository = new SyncStateRepository();

        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, tableName, CancellationToken.None);

        Assert.Equal(SyncConstants.InitialDate, lastSyncDate);
    }

    [Fact]
    public async Task ChildDatabase_HasProductCatalogItemTvpType()
    {
        // Arrange
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();

        // Act
        // OBJECT_ID doesn't resolve table types (they live in sys.types, not sys.objects),
        // so this needs its own query rather than reusing SchemaReader.TableExistsAsync.
        await using var cmd = new SqlCommand("SELECT TYPE_ID(N'dbo.ProductCatalogItemTableType')", child);
        var typeId = await cmd.ExecuteScalarAsync();

        // Assert
        Assert.NotNull(typeId);
        Assert.IsNotType<DBNull>(typeId);
    }

    [Theory]
    [InlineData("sp_SyncProductAttributeValue")]
    [InlineData("sp_SyncProductCatalogItemBatch")]
    [InlineData("sp_SyncDocumentAsset")]
    public async Task ChildDatabase_HasSyncStoredProcedures(string procedureName)
    {
        // Arrange
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        var reader = new SchemaReader();

        // Act
        // OBJECT_ID resolves any object in sys.objects, not just tables - reusing
        // TableExistsAsync here checks stored procedure existence too.
        var exists = await reader.TableExistsAsync(child, procedureName, CancellationToken.None);

        // Assert
        Assert.True(exists);
    }
}
