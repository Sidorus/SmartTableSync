using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SmartTableSync.Scaffolding;
using Testcontainers.MsSql;

namespace SmartTableSync.IntegrationTests.Fixtures;

/// <summary>
/// Starts one disposable SQL Server container (via Testcontainers) shared across every
/// test class tagged [Collection("SqlServer")], creates ParentTestDb/ChildTestDb inside
/// it, runs the copied schema scripts against each, and exposes an IConfiguration with
/// ParentDbConnectionString/ChildDbConnectionString already pointed at them - the same
/// seam SmartTableSync.Core.Infrastructure.DbConnectionFactory already reads from, so no
/// test-only subclass is needed.
/// </summary>
public sealed class MsSqlTestFixture : IAsyncLifetime
{
    private static readonly Regex _goSeparator = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public IConfiguration Configuration { get; private set; } = null!;
    public string ParentConnectionString { get; private set; } = null!;
    public string ChildConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var baseConnectionString = _container.GetConnectionString();

        await using (var connection = new SqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("CREATE DATABASE ParentTestDb; CREATE DATABASE ChildTestDb;", connection);
            await command.ExecuteNonQueryAsync();
        }

        ParentConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "ParentTestDb" }.ConnectionString;
        ChildConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "ChildTestDb" }.ConnectionString;

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "Schema");
        var tableScripts = new[]
        {
            "Attribute.sql", "ProductAttributeValue.sql", "ProductCatalogItem.sql", "DocumentAsset.sql",
            "IncrementalStrategyFullRescanTestTable.sql", "IncrementalStrategyConfiguredColumnTestTable.sql",
            "IdentityTvpTestTable.sql", "RowVersionTestTable.sql", "IncrementalStrategyOverrideTestTable.sql",
            "IncrementalStrategyOverrideTimestampTestTable.sql", "IncrementalStrategyOverrideBadColumnTestTable.sql",
        };

        await RunScriptsAsync(ParentConnectionString, tableScripts.Select(f => Path.Combine(schemaDir, f)));

        var childScripts = tableScripts.Concat(
        [
            "05_udt_types.sql",
            "02_sp_sync_product_attribute_value.sql",
            "03_sp_sync_product_catalog_item.sql",
            "04_sp_sync_document_asset.sql",
        ]).Select(f => Path.Combine(schemaDir, f));

        // CustomSyncState/SyncBatchLog/SyncTableConfig are no longer static files - Scaffold's
        // --infrastructure output and Auto mode's IChildDdlProvisioner.EnsureInfrastructureExistsAsync
        // both come from this same generator, so exercising it here proves that generated
        // content is what test setup actually relies on, not a hand-copied file that could drift.
        await RunGeneratedScriptAsync(ChildConnectionString, new SqlArtifactGenerator().GenerateInfrastructureScript());
        await RunScriptsAsync(ChildConnectionString, childScripts);

        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ParentDbConnectionString"] = ParentConnectionString,
                ["ChildDbConnectionString"] = ChildConnectionString,
            })
            .Build();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static async Task RunScriptsAsync(string connectionString, IEnumerable<string> scriptPaths)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var path in scriptPaths)
            await ExecuteBatchesAsync(connection, await File.ReadAllTextAsync(path), Path.GetFileName(path));
    }

    private static async Task RunGeneratedScriptAsync(string connectionString, string script)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteBatchesAsync(connection, script, "SqlArtifactGenerator.GenerateInfrastructureScript()");
    }

    private static async Task ExecuteBatchesAsync(SqlConnection connection, string script, string scriptLabel)
    {
        foreach (var batch in _goSeparator.Split(script))
        {
            var trimmed = batch.Trim();
            if (trimmed.Length == 0) continue;

            await using var command = new SqlCommand(trimmed, connection) { CommandTimeout = 120 };
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"Schema script '{scriptLabel}' failed: {ex.Message}", ex);
            }
        }
    }
}
