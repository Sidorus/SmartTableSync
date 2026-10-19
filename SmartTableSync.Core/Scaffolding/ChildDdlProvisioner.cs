namespace SmartTableSync.Scaffolding;

internal sealed class ChildDdlProvisioner : IChildDdlProvisioner
{
    private readonly ISchemaReader _schemaReader;
    private readonly ISqlArtifactGenerator _generator;
    private readonly ILogger<ChildDdlProvisioner> _logger;

    public ChildDdlProvisioner(ISchemaReader schemaReader, ISqlArtifactGenerator generator, ILogger<ChildDdlProvisioner> logger)
    {
        _schemaReader = schemaReader;
        _generator = generator;
        _logger = logger;
    }

    public async Task EnsureChildObjectsExistAsync(
        SqlConnection childProvisioning, TableProfile profile, SyncPlan plan, CancellationToken ct)
    {
        if (!await _schemaReader.TableExistsAsync(childProvisioning, profile.TableName, ct))
        {
            _logger.LogInformation("{Table}: child table missing — auto-creating", profile.TableName);
            await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateCreateTableScript(profile), ct);
        }
        else
        {
            await EvolveChildTableAsync(childProvisioning, profile, ct);
        }

        if (plan.MergeStrategy == MergeStrategy.Tvp)
        {
            if (string.IsNullOrEmpty(plan.TvpTypeName))
                throw new InvalidOperationException(
                    $"{profile.TableName}: MergeStrategy is Tvp but TvpTypeName is null/empty — refusing to " +
                    $"generate/execute a CREATE TYPE with no name. This should never happen via " +
                    $"SyncTableConfig.ToSyncPlan()/SyncPlanHeuristic.DerivePlan; check whatever constructed this plan.");

            // A table type can't be altered: one that no longer matches the table is dropped
            // (with its procedure) and created again below.
            var typeColumns = await _schemaReader.GetTableTypeColumnsAsync(childProvisioning, profile.TableName, plan.TvpTypeName, ct);
            if (typeColumns.Count > 0 && !ChildSchemaComparer.TableTypeMatches(profile, typeColumns))
            {
                _logger.LogWarning("{Table}: table type {Type} no longer matches the table — recreating it", profile.TableName, plan.TvpTypeName);
                await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateDropTvpTypeScript(profile.TableName, plan.TvpTypeName), ct);
            }

            // IF NOT EXISTS-guarded: creates the type when it's missing or was just dropped.
            await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateTvpUdtScript(profile, plan.TvpTypeName), ct);
        }

        // CREATE OR ALTER — idempotent by construction, always safe to (re)run.
        await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateMergeProcedureScript(profile, plan), ct);

        _logger.LogInformation("{Table}: child DDL objects verified/created", profile.TableName);
    }

    public async Task EnsureInfrastructureExistsAsync(SqlConnection childProvisioning, CancellationToken ct)
    {
        await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateInfrastructureScript(), ct);
    }

    // Adds new parent columns; anything that could lose or break data is left to a person.
    private async Task EvolveChildTableAsync(SqlConnection childProvisioning, TableProfile profile, CancellationToken ct)
    {
        // The child's key is always its PRIMARY KEY (created from the parent's sync key), so no
        // configured key columns.
        var childProfile = await _schemaReader.GetFingerprintInputsAsync(childProvisioning, profile.TableName, configuredKeyColumns: null, ct);
        var diff = ChildSchemaComparer.Compare(profile, childProfile);

        if (diff.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{profile.TableName}: the parent schema changed in a way Auto mode doesn't apply by itself, so the child " +
                $"table was left unchanged: {string.Join("; ", diff.Problems)}. Apply a reviewed script (SmartTableSync.Scaffold " +
                $"{profile.TableName} writes the new procedure and table type); the table syncs again on the next run after that.");
        }

        foreach (var column in diff.ExtraNullableColumns)
        {
            _logger.LogWarning("{Table}: child column {Column} no longer exists in the parent; it is nullable, so it's left in place", profile.TableName, column);
        }

        if (diff.ColumnsToAdd.Count > 0)
        {
            _logger.LogWarning("{Table}: adding parent column(s) {Columns} to the child table", profile.TableName,
                string.Join(", ", diff.ColumnsToAdd.Select(c => c.Name)));
            await SqlScriptExecutor.ExecuteAsync(childProvisioning, _generator.GenerateAddColumnsScript(profile, diff.ColumnsToAdd), ct);
        }
    }
}
