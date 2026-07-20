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
}

public enum SyncMode
{
    Manual,
    Auto
}
