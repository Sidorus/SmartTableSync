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
}
