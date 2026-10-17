namespace SmartTableSync.Options;

/// <summary>Which tables SyncOrchestrator should sync. Bound from a single delimited
/// setting rather than IConfiguration's native indexed array binding, since a single
/// comma-separated value is far easier to enter as one Azure App Setting / env var.</summary>
public sealed class SyncSettings
{
    public IReadOnlyList<string> Tables { get; set; } = [];

    /// <summary>Manual (default): the child database's tables/TVP types/stored procedures
    /// are assumed to already exist, applied by hand from SmartTableSync.Scaffold's output -
    /// today's only behavior. Auto: the Function runs that same generation logic itself and
    /// executes it against the child database the first time a table is seen or its parent
    /// schema drifts, via the separate, opt-in ChildProvisioningConnectionString.</summary>
    public SyncMode Mode { get; set; } = SyncMode.Manual;

    /// <summary>Per-table override of the incremental change-tracking column name, keyed by
    /// table name. A table not listed here defaults to "LastUpdate" — today's hardcoded
    /// behavior, reproduced automatically with zero config needed.</summary>
    public IReadOnlyDictionary<string, string> ChangeTrackingColumns { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tables explicitly allowed to fall back to a full-table re-scan every
    /// incremental cycle when their change-tracking column doesn't exist, even if the table
    /// is larger than PlanHeuristicOptions.FullRescanRowCountThreshold. Without this,
    /// provisioning a large table with no usable column throws instead of silently
    /// re-scanning it every cycle.</summary>
    public IReadOnlyList<string> AllowFullRescanFor { get; set; } = [];

    /// <summary>Per-table override of the auto-derived IncrementalStrategy, keyed by table
    /// name. Without this, a detected rowversion column always wins over a configured/default
    /// datetime column — a correct default (the engine guarantees it's current), but one an
    /// operator has no way to override otherwise. SyncPlanHeuristic still validates the
    /// override is actually satisfiable (e.g. forcing RowVersion without a rowversion column
    /// throws) rather than silently accepting an impossible choice.</summary>
    public IReadOnlyDictionary<string, IncrementalStrategy> IncrementalStrategyOverrides { get; set; } =
        new Dictionary<string, IncrementalStrategy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often SqlChangeTracking tables run the full key-set delete reconciliation.
    /// CT already reports deletes every cycle, and ChangeTrackingSyncRunner reconciles right
    /// after any fallback re-scan, so this is only a safety net for a failed child delete or a
    /// manual change in the child — default once a day instead of every hour. Zero disables the
    /// periodic run (reconciliation then happens only after a fallback re-scan). Tables on
    /// every other strategy keep the hourly reconciliation, their only source of deletes.</summary>
    public TimeSpan ChangeTrackingReconcileInterval { get; set; } = TimeSpan.FromHours(24);
}

public enum SyncMode
{
    Manual,
    Auto
}
