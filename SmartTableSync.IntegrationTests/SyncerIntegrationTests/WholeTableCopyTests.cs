using Microsoft.Data.SqlClient;
using SmartTableSync.Hosting;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Planning;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// The WholeTableCopy strategy through the orchestrator, in Auto mode: a table with a key forced
/// to it by an override, and a copy that fails halfway. The least-privilege test for a table
/// without any key is in LeastPrivilegeAutoModeTests.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because the orchestrator writes CustomSyncState rows, which the shared database must not get.
/// </summary>
public class WholeTableCopyTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public WholeTableCopyTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OverrideOnATableWithAKey_ReplacesTheRowsAndKeepsThePrimaryKey()
    {
        // Arrange
        const string table = "WholeTableCopyKeyedTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{table}] ([Id] INT NOT NULL PRIMARY KEY, [Name] NVARCHAR(50) NOT NULL, [Price] DECIMAL(10, 2) NULL);
            INSERT INTO [dbo].[{table}] ([Id], [Name], [Price])
            SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Item', 9.99 FROM sys.all_columns;
            """);
        var settings = Settings(table, overrides: new Dictionary<string, IncrementalStrategy> { [table] = IncrementalStrategy.WholeTableCopy });

        // Act
        var errors = await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings);

        // Assert
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.Equal(await RowsAsync(parent, table), await RowsAsync(child, table));
        Assert.Equal(1, await ScalarAsync(child, $"SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.{table}') AND type = 'PK'"));
        Assert.Equal(1, await ScalarAsync(child, $"SELECT COUNT(*) FROM [dbo].[SyncTableConfig] WHERE [TableName] = '{table}' AND [IncrementalStrategy] = 'WholeTableCopy'"));

        // Act - an update, an insert and a delete
        await ExecuteAsync(parent, $"""
            UPDATE [dbo].[{table}] SET [Price] = NULL, [Name] = N'Changed' WHERE [Id] <= 10;
            INSERT INTO [dbo].[{table}] ([Id], [Name], [Price]) VALUES (5000, N'New', 1.50);
            DELETE FROM [dbo].[{table}] WHERE [Id] BETWEEN 100 AND 119;
            """);
        errors = await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings);

        // Assert
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.Equal(await RowsAsync(parent, table), await RowsAsync(child, table));
        Assert.Equal(181, await ScalarAsync(child, $"SELECT COUNT(*) FROM [dbo].[{table}]"));
    }

    [Fact]
    public async Task FailedCopy_LeavesThePreviousCopyInPlace()
    {
        // Arrange - a keyless table copied once
        const string table = "WholeTableCopyRollbackTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{table}] ([Seq] INT NOT NULL, [Name] NVARCHAR(50) NOT NULL);
            INSERT INTO [dbo].[{table}] ([Seq], [Name])
            SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Item' FROM sys.all_columns;
            """);
        var settings = Settings(table);
        Assert.Empty(await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings));
        var copiedOnce = await RowsAsync(child, table);

        // Act - the child gets a unique index the next copy violates, so the copy fails after
        // all child rows were already deleted inside its transaction
        await ExecuteAsync(child, $"CREATE UNIQUE INDEX [UX_{table}_Seq] ON [dbo].[{table}] ([Seq]);");
        await ExecuteAsync(parent, $"""
            UPDATE [dbo].[{table}] SET [Name] = N'Changed';
            INSERT INTO [dbo].[{table}] ([Seq], [Name]) VALUES (1, N'Duplicate');
            """);
        var errors = await SyncerTestHelpers.RunOrchestratorAsync(ConnectionStrings(), settings);

        // Assert - the table failed, and the child still holds the previous complete copy
        Assert.Contains(errors, e => e.Contains(table, StringComparison.Ordinal));
        Assert.Equal(copiedOnce, await RowsAsync(child, table));
        Assert.Equal(100, copiedOnce.Count);
    }

    private Dictionary<string, string?> ConnectionStrings() => new()
    {
        ["ParentDbConnectionString"] = _fixture.ParentConnectionString,
        ["ChildDbConnectionString"] = _fixture.ChildConnectionString,
        ["ChildProvisioningConnectionString"] = _fixture.ChildConnectionString,
    };

    private static SyncSettings Settings(string table, IReadOnlyDictionary<string, IncrementalStrategy>? overrides = null) => new()
    {
        Tables = [table],
        Mode = SyncMode.Auto,
        IncrementalStrategyOverrides = overrides ?? new Dictionary<string, IncrementalStrategy>(),
    };

    // Every row as text, with how often it appears, in a stable order: compares a parent table with
    // its copy including repeated rows.
    private static async Task<IReadOnlyList<string>> RowsAsync(SqlConnection connection, string table)
    {
        await using var command = new SqlCommand($"""
            SELECT CONCAT((SELECT t.* FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER), '|', COUNT(*))
            FROM   [dbo].[{table}] AS t
            GROUP  BY {await ColumnListAsync(connection, table)}
            ORDER  BY 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));

        return rows;
    }

    private static async Task<string> ColumnListAsync(SqlConnection connection, string table)
    {
        await using var command = new SqlCommand(
            $"SELECT STRING_AGG(QUOTENAME(name), ', ') WITHIN GROUP (ORDER BY column_id) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}')",
            connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
