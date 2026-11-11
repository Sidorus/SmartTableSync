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

    /// <summary>Replaces the whole child table every run: all child rows are deleted and every
    /// parent row is copied again, in one transaction. Needs no key, so it's the strategy for a
    /// table without a PRIMARY KEY, unique index or configured key columns, and it copies
    /// duplicate rows exactly. Chosen automatically only for such a table (with the same size
    /// rule as FullRescan); a table with a key gets it only through an override.</summary>
    WholeTableCopy,
}
