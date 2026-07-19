using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Introspection;

namespace SmartTableSync.IntegrationTests;

/// <summary>
/// One end-to-end proof that the whole pipeline works: container starts, both databases
/// exist, the copied schema scripts ran without error, seed data loads, and - the thing
/// that's been unverified all along - SchemaReader's introspection queries are actually
/// syntactically correct against a real SQL Server, not just unit-tested against synthetic
/// TableProfile records.
/// </summary>
[Collection("SqlServer")]
public class SmokeTests
{
    private readonly MsSqlTestFixture _fixture;

    public SmokeTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SchemaReader_AgainstRealDatabase_ReportsAttributeTableCorrectly()
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "SeedData", "Attribute.json");
        var rows = await SeedDataLoader.LoadAsync(seedPath);

        await using var connection = new SqlConnection(_fixture.ParentConnectionString);
        await connection.OpenAsync();
        await SeedDataLoader.InsertRowsAsync(connection, "Attribute", rows, hasIdentityColumn: true);

        var reader = new SchemaReader();
        var profile = await reader.GetFingerprintInputsAsync(connection, "Attribute", CancellationToken.None);

        Assert.Equal("Attribute", profile.TableName);
        Assert.Equal(["AttributeId"], profile.PrimaryKeyColumns);
        Assert.Equal("AttributeId", profile.IdentityColumn);
        Assert.Contains(profile.Columns, c => c.Name == "Name" && c.SqlTypeName == "nvarchar");

        await using var countCommand = new SqlCommand("SELECT COUNT(*) FROM [dbo].[Attribute]", connection);
        var count = (int)(await countCommand.ExecuteScalarAsync())!;
        Assert.Equal(rows.Count, count);
    }
}
