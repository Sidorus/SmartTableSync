namespace SmartTableSync.Planning;

/// <summary>Pure function: TableProfile -> SyncPlan. No I/O, so every "how do we sync this
/// table" decision is unit-testable without a database.</summary>
internal interface ISyncPlanHeuristic
{
    /// <summary>The optional arguments are already resolved by the caller (SyncTableProvisioner
    /// reads configuration and checks columns): timestampColumn is null when the configured
    /// (default "LastUpdate") column doesn't exist, rowVersionColumn is null when there is no
    /// rowversion column. The incremental strategy is the first applicable one in
    /// IncrementalStrategies.All, unless strategyOverride forces one — which must then be
    /// applicable, or this throws.</summary>
    SyncPlan DerivePlan(
        TableProfile profile,
        PlanHeuristicOptions options,
        string? timestampColumn = null,
        bool allowFullRescan = false,
        string? rowVersionColumn = null,
        IncrementalStrategy? strategyOverride = null);
}
