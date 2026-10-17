namespace SmartTableSync.Syncers.MergeStrategies;

/// <summary>Merges a batch one row at a time, each via its own stored-procedure call —
/// chosen when a single bad row shouldn't fail the rest of the batch: incoming foreign keys
/// that can race against Data Sync's own delivery of the referenced table, or rows large
/// enough that per-row failure isolation matters more than round-trip efficiency.</summary>
internal sealed class RowByRowBatchMergeStrategy : IBatchMergeStrategy
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly ILogger _logger;

    public RowByRowBatchMergeStrategy(TableProfile profile, SyncPlan plan, ILogger logger)
    {
        _profile = profile;
        _plan = plan;
        _logger = logger;
    }

    public async Task<SyncOutcome> MergeBatchAsync(DataTable batch, SqlConnection child, CancellationToken ct)
    {
        int merged = 0, errored = 0;
        var retryable = false;

        foreach (DataRow row in batch.Rows)
        {
            var key = CompositeKey.Extract(row, _profile.PrimaryKeyColumns);
            try
            {
                await MergeSingleRowAsync(row, child, ct);
                merged++;
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                _logger.LogWarning("{Table} key '{Key}': skipped (expected error) — {Msg}", _profile.TableName, key, ex.Message);
                errored++;
                retryable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Table} key '{Key}': merge failed", _profile.TableName, key);
                errored++;
            }
        }

        return new SyncOutcome(merged, Deleted: 0, errored, retryable);
    }

    private bool IsExpectedError(Exception ex) =>
        ex is SqlException sqlEx && _plan.RetryableSqlErrorNumbers.Contains(sqlEx.Number);

    private async Task MergeSingleRowAsync(DataRow row, SqlConnection child, CancellationToken ct)
    {
        await using var sp = new SqlCommand($"[dbo].[sp_Sync{_profile.TableName}]", child)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _plan.BulkCopyTimeoutSeconds
        };

        foreach (var col in _profile.Columns)
        {
            // The generated procedure has no parameter for this column — SQL Server rejects
            // any explicit value written into a rowversion column, so it's never part of the
            // parameter list on either side (see SqlArtifactGenerator.InsertableColumns).
            if (col.Name == _profile.RowVersionColumn) continue;

            var size = SqlTypeMapping.GetParameterSize(col);
            sp.AddParam($"@{col.Name}", SqlTypeMapping.ToSqlDbType(col.SqlTypeName), row[col.Name], size);
        }

        await sp.ExecuteNonQueryAsync(ct);
    }
}
