namespace SmartTableSync.Planning;

/// <summary>How a table finds what changed since the last run. Chosen per table automatically
/// (see IncrementalStrategies.All for the order) or forced with
/// SmartTableSync:IncrementalStrategyOverrides:&lt;Table&gt;.</summary>
public enum IncrementalStrategy
{
    /// <summary>Rows whose datetime column (SmartTableSync:TimestampColumns, default
    /// "LastUpdate") changed since the last run. Relies on the application updating that
    /// column on every write.</summary>
    Timestamp,

    /// <summary>Re-reads and re-merges the whole table every run. For small tables, or tables
    /// listed in SmartTableSync:AllowFullRescanFor.</summary>
    FullRescan,

    /// <summary>Rows whose rowversion column is at or after the stored position. SQL Server
    /// updates the column on every write, so no change can be missed.</summary>
    RowVersion,

    /// <summary>SQL Server Change Tracking, enabled on the table by a DBA (SmartTableSync never
    /// enables it): only changed keys are read and deletes are reported directly. Named
    /// "SqlChangeTracking" to keep it distinct from the Timestamp strategy's column.</summary>
    SqlChangeTracking,
}
