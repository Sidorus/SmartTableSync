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

        // The generated script has its own IF NOT EXISTS guard, so this is safe to run every
        // time the strategy is Tvp, whether or not the type already exists.
        if (plan.MergeStrategy == MergeStrategy.Tvp)
        {
            if (string.IsNullOrEmpty(plan.TvpTypeName))
                throw new InvalidOperationException(
                    $"{profile.TableName}: MergeStrategy is Tvp but TvpTypeName is null/empty — refusing to " +
                    $"generate/execute a CREATE TYPE with no name. This should never happen via " +
                    $"SyncTableConfig.ToSyncPlan()/SyncPlanHeuristic.DerivePlan; check whatever constructed this plan.");

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
}
