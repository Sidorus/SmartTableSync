namespace SmartTableSync.Merge;

/// <summary>Bulk-copies a batch into the child table — one network write per batch, no
/// per-row overhead. Only used by InitialSyncRunner; incremental sync always goes through
/// the batch's IBatchMergeStrategy instead, since the child already has rows to merge
/// against by then.</summary>
internal static class BulkCopyWriter
{
    public static async Task WriteAsync(
        DataTable batch, SqlConnection child, string tableName,
        SqlBulkCopyOptions options, int timeoutSeconds, string? excludeColumn, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(child, options, externalTransaction: null)
        {
            DestinationTableName = SqlIdentifier.Table(tableName),
            BulkCopyTimeout = timeoutSeconds
        };

        // Map by column name, not position — safe with SELECT * regardless of column order.
        // excludeColumn skips the table's rowversion column, if it has one — SQL Server
        // rejects any explicit value written into a rowversion column, so the child's own
        // engine-generated value must be left alone rather than mirroring the parent's.
        foreach (DataColumn col in batch.Columns)
        {
            if (col.ColumnName == excludeColumn) continue;
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }

        await bulk.WriteToServerAsync(batch, ct);
    }
}
