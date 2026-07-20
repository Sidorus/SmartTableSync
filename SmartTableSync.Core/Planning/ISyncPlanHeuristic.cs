namespace SmartTableSync.Planning;

/// <summary>Pure function: TableProfile -> SyncPlan. No I/O — fully unit-testable without
/// a database. All the actual "how do we sync this table" judgment calls live here,
/// derived from data rather than hand-declared per table.</summary>
public interface ISyncPlanHeuristic
{
    /// <summary>changeTrackingColumn/allowFullRescan are already-resolved inputs (config
    /// lookup + column-existence check happens in SyncTableProvisioner) — this stays a pure
    /// function with no config/I/O of its own. changeTrackingColumn null means the configured
    /// (or default "LastUpdate") column doesn't exist on this table.</summary>
    SyncPlan DerivePlan(
        TableProfile profile,
        PlanHeuristicOptions options,
        string? changeTrackingColumn = null,
        bool allowFullRescan = false);
}
