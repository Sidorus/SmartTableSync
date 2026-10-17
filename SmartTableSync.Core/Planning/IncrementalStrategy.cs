namespace SmartTableSync.Planning;

public enum IncrementalStrategy
{
    /// <summary>Windowed query against a datetime/datetime2 change-tracking column,
    /// maintained by the parent application itself (or its default column, "LastUpdate").
    /// Chosen when that column exists on the table.</summary>
    Timestamp,

    /// <summary>No usable change-tracking column — every incremental cycle re-reads and
    /// re-MERGEs the whole table instead of a windowed delta, relying on MERGE already
    /// being idempotent everywhere in this codebase. Chosen only below
    /// PlanHeuristicOptions.FullRescanRowCountThreshold, or when explicitly allowed via
    /// SyncSettings.AllowFullRescanFor.</summary>
    FullRescan,

    /// <summary>Windowed query against a rowversion/timestamp column, engine-maintained on
    /// every INSERT/UPDATE with no trigger or app-code discipline required — strictly more
    /// reliable than a datetime column, so detected and preferred over one whenever present.
    /// Position tracking (CustomSyncState.IncrementalPosition) is owned entirely by
    /// RowVersionSyncRunner, separate from LastSyncDate.</summary>
    RowVersion,

    /// <summary>SQL Server Change Tracking, enabled on the table by a DBA (SmartTableSync never
    /// enables it). Reads CHANGETABLE(CHANGES ...) since the last version — only changed rows,
    /// deletes included, with commit-time versions (no in-flight-transaction workaround needed).
    /// Auto-detected and preferred over RowVersion whenever CT is enabled. Named
    /// "SqlChangeTracking" rather than "ChangeTracking" because SyncSettings.TimestampColumns /
    /// SyncPlan.IncrementalColumn already use "change tracking" for Timestamp's datetime column.
    /// Position tracking (CustomSyncState.IncrementalPosition) is owned by
    /// ChangeTrackingSyncRunner.</summary>
    SqlChangeTracking,
}
