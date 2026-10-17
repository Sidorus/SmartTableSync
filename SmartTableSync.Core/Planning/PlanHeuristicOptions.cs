namespace SmartTableSync.Planning;

/// <summary>Tunable inputs for ISyncPlanHeuristic. The defaults size batches to roughly 6 MB
/// and give about 300 seconds to a 50 MB row. A heuristic is approximate by nature; exact
/// per-table tuning is what the override columns in dbo.SyncTableConfig are for.</summary>
internal sealed record PlanHeuristicOptions(
    long TargetBatchPayloadBytes = 6_000_000,
    int MinBatchSize = 1,
    int MaxBatchSize = 1000,
    int BaseTimeoutSeconds = 120,
    double SecondsPerMb = 3.6,
    long LargeRowThresholdBytes = 1_000_000,
    long FullRescanRowCountThreshold = 50_000);
