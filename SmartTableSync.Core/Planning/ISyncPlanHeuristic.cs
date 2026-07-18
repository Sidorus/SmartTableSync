namespace SmartTableSync.Planning;

/// <summary>Pure function: TableProfile -> SyncPlan. No I/O — fully unit-testable without
/// a database. All the actual "how do we sync this table" judgment calls live here,
/// derived from data rather than hand-declared per table.</summary>
public interface ISyncPlanHeuristic
{
    SyncPlan DerivePlan(TableProfile profile, PlanHeuristicOptions options);
}
