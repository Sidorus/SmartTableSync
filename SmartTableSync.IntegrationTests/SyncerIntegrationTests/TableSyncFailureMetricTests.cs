using Microsoft.Data.SqlClient;
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// A table whose run fails is reported through ISyncMetricsReporter.TableSyncFailed, the one
/// metric a failed table produces, while the other tables keep syncing.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because the orchestrator writes CustomSyncState rows, which the shared database must not get.
/// </summary>
public class TableSyncFailureMetricTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public TableSyncFailureMetricTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ParentColumnTypeChange_ReportsTheFailedTableAndKeepsSyncingTheOthers()
    {
        // Arrange - two tables synced once in Auto mode
        const string failing = "FailureMetricChangedTable";
        const string healthy = "FailureMetricHealthyTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{failing}] ([Id] INT NOT NULL PRIMARY KEY, [Name] NVARCHAR(50) NOT NULL);
            CREATE TABLE [dbo].[{healthy}] ([Id] INT NOT NULL PRIMARY KEY, [Name] NVARCHAR(50) NOT NULL);
            INSERT INTO [dbo].[{failing}] VALUES (1, N'One'), (2, N'Two');
            INSERT INTO [dbo].[{healthy}] VALUES (1, N'One'), (2, N'Two');
            """);
        var settings = new SyncSettings { Tables = [failing, healthy], Mode = SyncMode.Auto };
        var firstRun = new FailureRecorder();
        var errors = await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings, firstRun);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.Empty(firstRun.FailedTables);

        // Act - a change Auto mode doesn't apply by itself, and a new row in the other table
        await ExecuteAsync(parent, $"""
            ALTER TABLE [dbo].[{failing}] ALTER COLUMN [Name] NVARCHAR(200) NOT NULL;
            INSERT INTO [dbo].[{healthy}] VALUES (3, N'Three');
            """);
        var secondRun = new FailureRecorder();
        errors = await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings, secondRun);

        // Assert - only the changed table is reported, and the other one still synced
        Assert.Contains(errors, e => e.Contains(failing, StringComparison.Ordinal));
        Assert.Equal([failing], secondRun.FailedTables);
        Assert.Equal(3, await ScalarAsync(child, $"SELECT COUNT(*) FROM [dbo].[{healthy}]"));
    }

    private Dictionary<string, string?> ConnectionStrings() => new()
    {
        ["ParentDbConnectionString"] = _fixture.ParentConnectionString,
        ["ChildDbConnectionString"] = _fixture.ChildConnectionString,
        ["ChildProvisioningConnectionString"] = _fixture.ChildConnectionString,
    };

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FailureRecorder : ISyncMetricsReporter
    {
        public List<string> FailedTables { get; } = [];

        public void RowsSynced(string tableName, string syncMode, int count) { }
        public void RowsErrored(string tableName, string syncMode, int count) { }
        public void SyncDuration(string tableName, string syncMode, TimeSpan duration) { }
        public void RowsDeleted(string tableName, int count) { }
        public void SyncLag(string tableName, TimeSpan lag) { }
        public void TableProvisioned(string tableName) { }
        public void TableSyncFailed(string tableName) => FailedTables.Add(tableName);
    }
}
