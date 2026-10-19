namespace SmartTableSync.Introspection;

/// <summary>Where a table's sync key (TableProfile.PrimaryKeyColumns) comes from. The key drives
/// MERGE matching, keyset paging, resume keys and delete reconciliation, and becomes the child
/// table's PRIMARY KEY whatever its source.</summary>
internal enum SyncKeySource
{
    /// <summary>The table's PRIMARY KEY constraint. The only source Change Tracking accepts.</summary>
    PrimaryKey,

    /// <summary>No PRIMARY KEY: a unique index whose key columns are all NOT NULL, not filtered
    /// and not disabled (fewest columns, then lowest index_id).</summary>
    UniqueIndex,

    /// <summary>No PRIMARY KEY: the columns set in SmartTableSync:KeyColumns:&lt;Table&gt;,
    /// checked for duplicates once at provisioning.</summary>
    Configured,
}
