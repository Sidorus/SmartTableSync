namespace SmartTableSync.Strategies.WholeTableCopy;

/// <summary>
/// WholeTableCopy strategy: every run deletes all child rows and bulk-copies every parent row,
/// in one child transaction. Nothing is matched by key, so it works for a table without one,
/// and duplicate rows are copied exactly. The same runner serves the first run and every later
/// one: each run is already a complete copy.
///
/// It needs no right to change a schema (DELETE and INSERT only), so the runtime identity's
/// rights stay as they are. Until the transaction commits, readers of the child see the
/// previous complete copy under read committed snapshot (the default in Azure SQL Database);
/// without it they wait for the commit. The parent is read in one SNAPSHOT transaction when the
/// database allows it, so the copy is one consistent point in time; otherwise under read
/// committed, where a row changed during the copy arrives with the next run.
/// </summary>
internal sealed class WholeTableCopySyncRunner : ISyncRunner
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly ILogger _logger;

    public WholeTableCopySyncRunner(TableProfile profile, SyncPlan plan, ILogger logger)
    {
        _profile = profile;
        _plan = plan;
        _logger = logger;
    }

    public async Task<SyncOutcome> RunAsync(SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        // The rowversion column is left out: SQL Server rejects an explicit value for it, and
        // the child's own column fills itself.
        var columns = SqlArtifactGenerator.InsertableColumns(_profile).Select(c => c.Name).ToList();
        var table = SqlIdentifier.Table(_profile.TableName);
        var snapshotIsolationAllowed = await ChangeTrackingVersions.IsSnapshotIsolationAllowedAsync(parent, ct);

        try
        {
            await using var parentTransaction = (SqlTransaction)await parent.BeginTransactionAsync(
                snapshotIsolationAllowed ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, ct);
            await using var childTransaction = (SqlTransaction)await child.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- table name quoted by SqlIdentifier, no values
            await using var delete = new SqlCommand($"DELETE FROM {table}", child, childTransaction) { CommandTimeout = 0 };
            var deleted = await delete.ExecuteNonQueryAsync(ct);

            // The reader is closed before either commit: a transaction can't commit while its
            // connection still has an open reader.
            var copied = await CopyAllRowsAsync(parent, parentTransaction, child, childTransaction, table, columns, ct);

            await childTransaction.CommitAsync(ct);
            await parentTransaction.CommitAsync(ct);

            // Every child row was deleted and every parent row copied again; "deleted" reports only
            // the rows that are gone now, so a table that didn't change shows 0.
            var removed = Math.Max(0, deleted - copied);
            _logger.LogInformation("{Table}: whole-table copy replaced {Deleted} child rows with {Copied} parent rows",
                _profile.TableName, deleted, copied);

            return new SyncOutcome(Merged: copied, Deleted: removed, Errored: 0, HasRetryableErrors: false);
        }
        finally
        {
            // A pooled connection keeps its isolation level; the next user expects the default.
            if (snapshotIsolationAllowed)
            {
                await using var reset = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", parent);
                await reset.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    // Streams the parent rows straight into the child: nothing is held in memory, whatever the
    // table's size. No timeout on the copy: its length grows with the table, the run lock keeps a
    // second run from starting meanwhile, and ct still cancels it.
    private async Task<int> CopyAllRowsAsync(
        SqlConnection parent, SqlTransaction parentTransaction, SqlConnection child, SqlTransaction childTransaction,
        string table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- identifiers quoted by SqlIdentifier, no values
        await using var read = new SqlCommand(
            $"SELECT {string.Join(", ", columns.Select(SqlIdentifier.Quote))} FROM {table}", parent, parentTransaction)
        {
            CommandTimeout = 0,
        };
        await using var reader = await read.ExecuteReaderAsync(ct);

        using var bulk = new SqlBulkCopy(child, _plan.BulkCopyOptions, childTransaction)
        {
            DestinationTableName = table,
            BatchSize = _plan.BatchSize,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        await bulk.WriteToServerAsync(reader, ct);
        return (int)Math.Min(int.MaxValue, bulk.RowsCopied64);
    }
}
