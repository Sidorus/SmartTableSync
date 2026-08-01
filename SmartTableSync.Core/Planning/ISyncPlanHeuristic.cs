namespace SmartTableSync.Planning;

/// <summary>Pure function: TableProfile -> SyncPlan. No I/O — fully unit-testable without
/// a database. All the actual "how do we sync this table" judgment calls live here,
/// derived from data rather than hand-declared per table.</summary>
public interface ISyncPlanHeuristic
{
    /// <summary>changeTrackingColumn/allowFullRescan/rowVersionColumn/strategyOverride are
    /// already-resolved inputs (config lookup + column-existence checks happen in
    /// SyncTableProvisioner) — this stays a pure function with no config/I/O of its own.
    /// changeTrackingColumn null means the configured (or default "LastUpdate") column
    /// doesn't exist on this table. rowVersionColumn non-null takes priority over
    /// changeTrackingColumn — detecting a rowversion-typed column is a fact, not a guess, so
    /// it's preferred whenever present regardless of config. strategyOverride, when set,
    /// wins over both of the above — but is validated against the table's actual columns
    /// (e.g. forcing RowVersion without a rowversion column throws) rather than silently
    /// accepted.</summary>
    SyncPlan DerivePlan(
        TableProfile profile,
        PlanHeuristicOptions options,
        string? changeTrackingColumn = null,
        bool allowFullRescan = false,
        string? rowVersionColumn = null,
        IncrementalStrategy? strategyOverride = null);
}
