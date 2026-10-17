namespace SmartTableSync.Planning;

internal sealed class SyncTableProvisioner : ISyncTableProvisioner
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

        if (existing is null)
        {
            _logger.LogInformation("{Table}: not yet configured — introspecting and deriving a sync plan", tableName);
        }
        else
        {
            // Routine path: a cheap, catalog-metadata-only drift check. The expensive row/size
            // sampling in ProfileTableAsync only runs when the table must be (re)provisioned.
            var cheapProfile = await _schemaReader.GetFingerprintInputsAsync(parent, tableName, ct);
            var cheapFingerprint = SchemaFingerprint.Compute(cheapProfile);

            if (existing.SchemaFingerprint == cheapFingerprint)
                return (cheapProfile, existing.ToSyncPlan(), false);

            _logger.LogWarning(
                "{Table}: parent schema changed (fingerprint {Old} -> {New}) — re-provisioning",
                tableName, existing.SchemaFingerprint, cheapFingerprint);
        }

        // The full profile includes everything the fingerprint reads, so it hashes the same as
        // the cheap one; a first-seen table needs only this one read.
        var fullProfile = await _schemaReader.ProfileTableAsync(parent, tableName, ct);
        var currentFingerprint = SchemaFingerprint.Compute(fullProfile);

        var timestampColumn = TimestampColumnResolver.Resolve(fullProfile, _settings.Value.TimestampColumns);
        var allowFullRescan = _settings.Value.AllowFullRescanFor.Contains(tableName, StringComparer.OrdinalIgnoreCase);
        var strategyOverride = _settings.Value.IncrementalStrategyOverrides.TryGetValue(tableName, out var overriddenStrategy)
            ? overriddenStrategy : (IncrementalStrategy?)null;

        var plan = _heuristic.DerivePlan(
            fullProfile, new PlanHeuristicOptions(), timestampColumn, allowFullRescan, fullProfile.RowVersionColumn, strategyOverride);

        var retryableSqlErrorsOverride = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            existing?.MergeStrategyOverride, existing?.RetryableSqlErrorsOverride, fullProfile.ReferencesOtherTables);

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
