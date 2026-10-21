using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;

namespace SmartTableSync.IntegrationTests;

/// <summary>What SQL Server sees as the client application for SmartTableSync's connections:
/// APP_NAME() and sys.dm_exec_sessions.program_name, the columns a DBA looks at.</summary>
[Collection("SqlServer")]
public class ApplicationNameTests
{
    private readonly MsSqlTestFixture _fixture;

    public ApplicationNameTests(MsSqlTestFixture fixture) => _fixture = fixture;

    private static async Task<(string AppName, string ProgramName)> ReadApplicationNameAsync(SqlConnection connection)
    {
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT APP_NAME(), program_name FROM sys.dm_exec_sessions WHERE session_id = @@SPID;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    [Fact]
    public async Task EngineConnections_ReportSmartTableSync()
    {
        var factory = SyncerTestHelpers.CreateConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ParentDbConnectionString"] = _fixture.ParentConnectionString,
            ["ChildDbConnectionString"] = _fixture.ChildConnectionString,
        }).Build());

        await using var parent = factory.CreateParentConnection();
        await using var child = factory.CreateChildConnection();

        Assert.Equal(("SmartTableSync", "SmartTableSync"), await ReadApplicationNameAsync(parent));
        Assert.Equal(("SmartTableSync", "SmartTableSync"), await ReadApplicationNameAsync(child));
    }

    [Fact]
    public async Task EngineConnections_KeepTheApplicationNameFromTheConnectionString()
    {
        var factory = SyncerTestHelpers.CreateConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ParentDbConnectionString"] = _fixture.ParentConnectionString + ";Application Name=OrdersSync",
            ["ChildDbConnectionString"] = _fixture.ChildConnectionString,
        }).Build());

        await using var parent = factory.CreateParentConnection();

        Assert.Equal(("OrdersSync", "OrdersSync"), await ReadApplicationNameAsync(parent));
    }
}
