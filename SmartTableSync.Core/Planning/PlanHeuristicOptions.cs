namespace SmartTableSync.Planning;

/// <summary>Tunable inputs for ISyncPlanHeuristic. Defaults are calibrated against this
/// repo's own known-good hand-tuned values (e.g. a ~50 MB row landing near the existing
/// 300s DocumentAsset timeout) rather than picked arbitrarily — but a heuristic is
/// necessarily approximate; exact per-table tuning is what SyncTableConfig's override
/// columns (Phase 8) are for.</summary>
internal sealed record PlanHeuristicOptions(
    long TargetBatchPayloadBytes = 6_000_000,
    int MinBatchSize = 1,
    int MaxBatchSize = 1000,
    int BaseTimeoutSeconds = 120,
    double SecondsPerMb = 3.6,
    long LargeRowThresholdBytes = 1_000_000,
    long FullRescanRowCountThreshold = 50_000);
