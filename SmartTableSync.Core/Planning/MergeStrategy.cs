namespace SmartTableSync.Planning;

internal enum MergeStrategy
{
    /// <summary>Batch MERGE via a table-valued parameter — one SP call per batch.
    /// Chosen when a table has no incoming FK timing risk and rows are small enough
    /// that per-row isolation isn't needed.</summary>
    Tvp,

    /// <summary>One SP call per row. Chosen either because incoming foreign keys can
    /// race against Data Sync's own delivery of the referenced table (a bad row
    /// shouldn't fail the whole batch), or because rows are large enough that
    /// per-row failure isolation matters more than round-trip efficiency.</summary>
    RowByRow,
}
