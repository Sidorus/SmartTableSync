namespace SmartTableSync.Syncers.Sql;

/// <summary>Bulk-copies a batch into the child table — one network write per batch, no
/// per-row overhead. Only used by InitialSyncRunner; incremental sync always goes through
/// the batch's IBatchMergeStrategy instead, since the child already has rows to merge
/// against by then.</summary>
public static class BulkCopyWriter
{
    public static async Task WriteAsync(
        DataTable batch, SqlConnection child, string tableName,
        SqlBulkCopyOptions options, int timeoutSeconds, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(child, options, externalTransaction: null)
        {
            DestinationTableName = $"[dbo].[{tableName}]",
            BulkCopyTimeout = timeoutSeconds
        };

        // Map by column name, not position — safe with SELECT * regardless of column order.
        foreach (DataColumn col in batch.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(batch, ct);
    }
}
