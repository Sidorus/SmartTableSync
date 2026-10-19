namespace SmartTableSync.Hosting;

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
    public IReadOnlyDictionary<string, string> TimestampColumns { get; set; } =
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

    /// <summary>Per-table key columns, in key order, for a table that has neither a PRIMARY KEY
    /// nor a unique index whose key columns are all NOT NULL. The columns must be NOT NULL and
    /// unique together; uniqueness is checked against the parent's data whenever the table is
    /// (re)provisioned. A table with a PRIMARY KEY can't be given one (it fails, rather than
    /// being ignored); one with only a unique index uses these columns instead of the index's.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeyColumns { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often SqlChangeTracking tables run the full key-set delete reconciliation.
    /// CT already reports deletes every cycle, and ChangeTrackingSyncRunner reconciles right
    /// after any fallback re-scan, so this is only a safety net for a failed child delete or a
    /// manual change in the child — default once a day instead of every hour. Zero disables the
    /// periodic run (reconciliation then happens only after a fallback re-scan). Tables on
    /// every other strategy keep the hourly reconciliation, their only source of deletes.</summary>
    public TimeSpan ChangeTrackingReconcileInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often tables on every strategy except SqlChangeTracking run the full
    /// key-set delete reconciliation — their only source of deletes. Default one hour.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Total attempts (initial + retries) to open a parent/child connection when it
    /// fails with a transient error — Azure SQL failover, throttling, or brief unavailability —
    /// using SqlClient's built-in transient error list and exponential backoff with jitter.
    /// 1 disables retries. Only opening a connection is retried, never a command: a table whose
    /// command fails is retried as a whole on the next run, which is safe because every write
    /// is an idempotent MERGE.</summary>
    public int ConnectionOpenAttempts { get; set; } = 5;
}

public enum SyncMode
{
    Manual,
    Auto
}
