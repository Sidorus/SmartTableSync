namespace SmartTableSync.Syncers.MergeStrategies;

/// <summary>Merges a whole batch in one stored-procedure call via a table-valued parameter —
/// efficient, but one bad row fails the whole batch, so this is only chosen when the table
/// has no FK-timing risk and rows are small enough that per-row isolation isn't needed.</summary>
public sealed class TvpBatchMergeStrategy : IBatchMergeStrategy
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;

    public TvpBatchMergeStrategy(TableProfile profile, SyncPlan plan)
    {
        _profile = profile;
        _plan = plan;
    }

    public async Task<(int synced, int errored, bool retryable)> MergeBatchAsync(
        DataTable batch, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand($"[dbo].[sp_Sync{_profile.TableName}Batch]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _plan.BulkCopyTimeoutSeconds
        };

        sp.Parameters.Add(new SqlParameter("@Rows", SqlDbType.Structured)
        {
            TypeName = $"dbo.{_plan.TvpTypeName}",
            Value = TvpShapedRows(batch)
        });

        await sp.ExecuteNonQueryAsync(ct);
        return (batch.Rows.Count, 0, false);
    }

    // A TVP column typed rowversion/timestamp rejects an explicit value exactly like a real
    // table column does - the TVP type (SqlArtifactGenerator.GenerateTvpUdtScript) excludes
    // the table's rowversion column entirely, so the DataTable passed as @Rows must match
    // that shape. Projects rather than mutates batch — callers (e.g. InitialSyncRunner's
    // PK-violation fallback) still read the original DataTable afterward.
    private DataTable TvpShapedRows(DataTable batch)
    {
        if (_profile.RowVersionColumn is null) return batch;

        var columnNames = batch.Columns.Cast<DataColumn>()
            .Where(c => c.ColumnName != _profile.RowVersionColumn)
            .Select(c => c.ColumnName)
            .ToArray();

        return batch.DefaultView.ToTable(distinct: false, columnNames);
    }
}
