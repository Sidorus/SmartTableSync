using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SmartTableSync.Configuration;
using SmartTableSync.Introspection;
using SmartTableSync.Options;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;

// Manual, on-demand tool — never run by the timer-triggered Function. Reads schema
// (read-only) and writes reviewable .sql files; never executes anything against a
// database itself. See Plan.md Phase 9/10 for why this uses its own connection strings
// instead of the Function's runtime ones.

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        SmartTableSync.Scaffold — generates reviewable SQL for one or more tables.

        Usage:
          dotnet run -- <TableName> [<TableName> ...] [--output <dir>]
          dotnet run -- --infrastructure [--output <dir>]
          dotnet run -- --rowversion-setup <TableName> [<TableName> ...] [--column <Name>] [--output <dir>]

        Environment variables required (per-table generation — --infrastructure needs none):
          ParentIntrospectionConnectionString   (read-only: VIEW DEFINITION + SELECT on parent)
          ChildProvisioningConnectionString      (used only to check whether the child table
                                                   already exists — never executes DDL)
                                                   Not needed for --rowversion-setup (parent-only).

        Per-table generation also reads the same SmartTableSync:* settings the runtime does
        (SmartTableSync__ChangeTrackingColumns__<Table>, SmartTableSync__AllowFullRescanFor,
        SmartTableSync__IncrementalStrategyOverrides__<Table>) from environment variables, so
        generated artifacts match what SyncTableProvisioner will actually derive at runtime for
        the same settings — set them the same way you would for the Function host.

        --infrastructure writes the table-agnostic tracking schema every managed table
        depends on (CustomSyncState, SyncBatchLog, SyncTableConfig) to one reviewable file.
        Apply it once, before syncing any table for the first time.

        --rowversion-setup writes a script that adds a rowversion column (default name
        "RowVer", override with --column) plus its required index to the named table(s) —
        for the operator/DBA to apply BY HAND against the PARENT database, opting that table
        into the RowVersion incremental strategy. SmartTableSync never has, and never will
        have, DDL access to the parent database in either Manual or Auto mode — this script
        is generated for manual review only, same trust model as sql/07_permissions.sql.

        Output defaults to sql/generated/ (relative to the current directory).
        """);
    return 0;
}

var outputIndex = Array.IndexOf(args, "--output");
var outputDir = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : "sql/generated";

if (args.Contains("--infrastructure"))
{
    Directory.CreateDirectory(outputDir);
    var infrastructurePath = Path.Combine(outputDir, "00_infrastructure.sql");
    await File.WriteAllTextAsync(infrastructurePath, new SqlArtifactGenerator().GenerateInfrastructureScript());
    Console.WriteLine($"wrote {infrastructurePath}");
    return 0;
}

if (args.Contains("--rowversion-setup"))
{
    var columnIndex = Array.IndexOf(args, "--column");
    var columnName = columnIndex >= 0 && columnIndex + 1 < args.Length ? args[columnIndex + 1] : "RowVer";

    var rowVersionTableNames = args.Where((a, i) =>
        a != "--rowversion-setup" && a != "--output" && a != "--column" &&
        (outputIndex < 0 || i != outputIndex + 1) &&
        (columnIndex < 0 || i != columnIndex + 1)).ToList();

    var rowVersionParentConnString = Environment.GetEnvironmentVariable("ParentIntrospectionConnectionString");
    if (string.IsNullOrWhiteSpace(rowVersionParentConnString))
    {
        Console.Error.WriteLine("ParentIntrospectionConnectionString must be set.");
        return 1;
    }

    Directory.CreateDirectory(outputDir);
    var rowVersionSchemaReader = new SchemaReader();
    var rowVersionGenerator = new SqlArtifactGenerator();

    await using var rowVersionParent = new SqlConnection(rowVersionParentConnString);
    await rowVersionParent.OpenAsync();

    var failedRowVersionTables = new List<string>();

    foreach (var tableName in rowVersionTableNames)
    {
        try
        {
            var profile = await rowVersionSchemaReader.ProfileTableAsync(rowVersionParent, tableName, CancellationToken.None);
            var path = Path.Combine(outputDir, $"{tableName}_RowVersionSetup.sql");
            await File.WriteAllTextAsync(path, rowVersionGenerator.GenerateRowVersionSetupScript(profile, columnName));
            Console.WriteLine($"wrote {path} — apply manually against the PARENT database");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same aggregate-and-continue behavior as the main per-table loop below — one bad
            // table (e.g. a typo'd name that fails introspection) shouldn't abort the rest.
            Console.Error.WriteLine($"{tableName}: FAILED: {ex.Message}");
            failedRowVersionTables.Add(tableName);
        }
    }

    if (failedRowVersionTables.Count > 0)
    {
        Console.Error.WriteLine($"{failedRowVersionTables.Count} table(s) failed: {string.Join(", ", failedRowVersionTables)} — fix and re-run.");
        return 1;
    }

    return 0;
}

var tableNames = args.Where((a, i) => a != "--output" && (outputIndex < 0 || i != outputIndex + 1)).ToList();

var parentConnString = Environment.GetEnvironmentVariable("ParentIntrospectionConnectionString");
var childConnString = Environment.GetEnvironmentVariable("ChildProvisioningConnectionString");

if (string.IsNullOrWhiteSpace(parentConnString) || string.IsNullOrWhiteSpace(childConnString))
{
    Console.Error.WriteLine("Both ParentIntrospectionConnectionString and ChildProvisioningConnectionString must be set.");
    return 1;
}

Directory.CreateDirectory(outputDir);

var schemaReader = new SchemaReader();
var heuristic = new SyncPlanHeuristic();
var generator = new SqlArtifactGenerator();

// Binds the same SyncSettings the runtime does (ChangeTrackingColumns/AllowFullRescanFor/
// IncrementalStrategyOverrides), from an IConfiguration over environment variables, via the
// shared SyncSettingsServiceCollectionExtensions.Bind - so a table's generated artifacts
// match what SyncTableProvisioner will actually derive for the same settings, instead of
// Scaffold silently diverging with its own partial approximation.
var syncSettings = new SyncSettings();
SyncSettingsServiceCollectionExtensions.Bind(syncSettings, new ConfigurationBuilder().AddEnvironmentVariables().Build());

await using var parent = new SqlConnection(parentConnString);
await using var child = new SqlConnection(childConnString);
await parent.OpenAsync();
await child.OpenAsync();

var failedTables = new List<string>();

foreach (var tableName in tableNames)
{
    Console.WriteLine($"=== {tableName} ===");

    try
    {
        var profile = await schemaReader.ProfileTableAsync(parent, tableName, CancellationToken.None);

        var changeTrackingColumn = ChangeTrackingColumnResolver.Resolve(profile, syncSettings.ChangeTrackingColumns);
        var allowFullRescan = syncSettings.AllowFullRescanFor.Contains(tableName, StringComparer.OrdinalIgnoreCase);
        var strategyOverride = syncSettings.IncrementalStrategyOverrides.TryGetValue(tableName, out var overriddenStrategy)
            ? overriddenStrategy : (IncrementalStrategy?)null;

        var plan = heuristic.DerivePlan(profile, new PlanHeuristicOptions(), changeTrackingColumn, allowFullRescan, profile.RowVersionColumn, strategyOverride);
        var childExists = await schemaReader.TableExistsAsync(child, tableName, CancellationToken.None);

        Console.WriteLine($"  MergeStrategy={plan.MergeStrategy}, BatchSize={plan.BatchSize}, " +
            $"BulkCopyTimeoutSeconds={plan.BulkCopyTimeoutSeconds}, IncrementalStrategy={plan.IncrementalStrategy}");

        if (!childExists)
        {
            var createTablePath = Path.Combine(outputDir, $"{tableName}.sql");
            await File.WriteAllTextAsync(createTablePath, generator.GenerateCreateTableScript(profile));
            Console.WriteLine($"  wrote {createTablePath} (child table does not exist yet)");
        }
        else
        {
            Console.WriteLine("  child table already exists — skipping CREATE TABLE");
        }

        if (plan.MergeStrategy == MergeStrategy.Tvp)
        {
            var udtPath = Path.Combine(outputDir, $"{plan.TvpTypeName}.sql");
            await File.WriteAllTextAsync(udtPath, generator.GenerateTvpUdtScript(profile, plan.TvpTypeName!));
            Console.WriteLine($"  wrote {udtPath}");
        }

        var spName = plan.MergeStrategy == MergeStrategy.Tvp ? $"sp_Sync{tableName}Batch" : $"sp_Sync{tableName}";
        var spPath = Path.Combine(outputDir, $"{spName}.sql");
        await File.WriteAllTextAsync(spPath, generator.GenerateMergeProcedureScript(profile, plan));
        Console.WriteLine($"  wrote {spPath}");
    }
    catch (InvalidOperationException ex)
    {
        // Same errors SyncTableProvisioner would throw for this table at runtime (bad
        // ChangeTrackingColumns type, unsatisfiable IncrementalStrategyOverrides, no usable
        // column on a large table with no AllowFullRescanFor opt-in) - surfaced here instead
        // of silently generating artifacts for a configuration the runtime would refuse.
        Console.Error.WriteLine($"  FAILED: {ex.Message}");
        failedTables.Add(tableName);
    }
}

Console.WriteLine();

if (failedTables.Count > 0)
{
    Console.Error.WriteLine($"{failedTables.Count} table(s) failed: {string.Join(", ", failedTables)} — fix the settings above and re-run.");
    return 1;
}

Console.WriteLine($"Done. Review the generated files under {outputDir}/ before folding them into the numbered sql/ deployment sequence.");
return 0;
