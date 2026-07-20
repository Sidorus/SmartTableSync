namespace SmartTableSync.Configuration;

public sealed class SyncTableProvisioner : ISyncTableProvisioner
{
    private readonly ISchemaReader _schemaReader;
    private readonly ISyncPlanHeuristic _heuristic;
    private readonly ISyncTableConfigRepository _configRepository;
    private readonly ISyncMetricsReporter _metrics;
    private readonly IOptions<SyncSettings> _settings;
    private readonly ILogger<SyncTableProvisioner> _logger;

    public SyncTableProvisioner(
        ISchemaReader schemaReader,
        ISyncPlanHeuristic heuristic,
        ISyncTableConfigRepository configRepository,
        ISyncMetricsReporter metrics,
        IOptions<SyncSettings> settings,
        ILogger<SyncTableProvisioner> logger)
    {
        _schemaReader = schemaReader;
        _heuristic = heuristic;
        _configRepository = configRepository;
        _metrics = metrics;
        _settings = settings;
        _logger = logger;
    }

    public async Task<(TableProfile Profile, SyncPlan Plan, bool WasFreshlyProvisioned)> EnsureProvisionedAsync(SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct)
    {
        var existing = await _configRepository.GetAsync(child, tableName, ct);

        // Cheap, catalog-metadata-only check on every call — the expensive row/size
        // sampling in ProfileTableAsync only runs when we're actually about to
        // (re)provision, not just to check whether we need to.
        var cheapProfile = await _schemaReader.GetFingerprintInputsAsync(parent, tableName, ct);
        var currentFingerprint = SchemaFingerprint.Compute(cheapProfile);

        if (existing is not null && existing.SchemaFingerprint == currentFingerprint)
            return (cheapProfile, existing.ToSyncPlan(), false);

        if (existing is null)
            _logger.LogInformation("{Table}: not yet configured — introspecting and deriving a sync plan", tableName);
        else
            _logger.LogWarning(
                "{Table}: parent schema changed (fingerprint {Old} -> {New}) — re-provisioning",
                tableName, existing.SchemaFingerprint, currentFingerprint);

        var fullProfile = await _schemaReader.ProfileTableAsync(parent, tableName, ct);

        var configuredColumn = _settings.Value.ChangeTrackingColumns.TryGetValue(tableName, out var name)
            ? name : "LastUpdate";
        var columnExists = fullProfile.Columns.Any(c =>
            string.Equals(c.Name, configuredColumn, StringComparison.OrdinalIgnoreCase));
        var changeTrackingColumn = columnExists ? configuredColumn : null;
        var allowFullRescan = _settings.Value.AllowFullRescanFor.Contains(tableName, StringComparer.OrdinalIgnoreCase);

        var plan = _heuristic.DerivePlan(fullProfile, new PlanHeuristicOptions(), changeTrackingColumn, allowFullRescan);

        var retryableSqlErrorsOverride = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            existing?.MergeStrategyOverride, existing?.RetryableSqlErrorsOverride, fullProfile.HasIncomingForeignKeys);

        // Preserve any hand-set overrides across re-provisioning — they're reviewable data
        // an operator set deliberately, not something a fresh heuristic run should erase.
        var config = SyncTableConfig.FromPlan(
            fullProfile, plan, currentFingerprint, DateTime.UtcNow,
            mergeStrategyOverride: existing?.MergeStrategyOverride,
            batchSizeOverride: existing?.BatchSizeOverride,
            bulkCopyTimeoutSecondsOverride: existing?.BulkCopyTimeoutSecondsOverride,
            retryableSqlErrorsOverride: retryableSqlErrorsOverride);

        await _configRepository.UpsertAsync(child, config, ct);
        _metrics.TableProvisioned(tableName);

        return (fullProfile, config.ToSyncPlan(), true);
    }
}
