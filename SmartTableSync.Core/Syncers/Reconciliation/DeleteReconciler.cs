namespace SmartTableSync.Syncers.Reconciliation;

/// <summary>Deletes rows from the child that no longer exist in the parent: reads both key
/// sets, diffs them, deletes the orphans. Called by ConfigurableTableSyncer only outside
/// initial sync — a partially-populated child during initial sync would otherwise produce
/// false deletes.</summary>
public sealed class DeleteReconciler
{
    private readonly TableProfile _profile;
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly ILogger _logger;

    public DeleteReconciler(TableProfile profile, TableSyncCommandFactory commandFactory, ILogger logger)
    {
        _profile = profile;
        _commandFactory = commandFactory;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(SqlConnection parent, SqlConnection child, CancellationToken ct)
    {
        var childKeys = await LoadKeysAsync(_commandFactory.BuildChildKeyCommand(child), ct);
        var parentKeys = await LoadKeysAsync(_commandFactory.BuildParentKeyCommand(parent), ct);
        childKeys.ExceptWith(parentKeys);

        if (childKeys.Count == 0) return 0;

        _logger.LogInformation("{Table}: {Count} orphaned rows to delete", _profile.TableName, childKeys.Count);

        int deleted = 0;
        foreach (var key in childKeys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteRowAsync(key, child, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Table} delete key '{Key}': failed", _profile.TableName, key);
            }
        }

        return deleted;
    }

    private async Task DeleteRowAsync(string key, SqlConnection child, CancellationToken ct)
    {
        var keyParts = CompositeKey.Parse(key, _profile.PrimaryKeyColumns);
        var whereClauses = new List<string>();

        await using var cmd = new SqlCommand { Connection = child, CommandTimeout = 60 };
        for (var i = 0; i < _profile.PrimaryKeyColumns.Count; i++)
        {
            var col = _profile.PrimaryKeyColumns[i];
            var colType = ColumnType(col);
            var paramName = $"@Key{i}";
            whereClauses.Add($"[{col}] = {paramName}");
            cmd.Parameters.Add(paramName, SqlTypeMapping.ToSqlDbType(colType)).Value =
                SqlTypeMapping.ParseKeyComponent(keyParts[i], colType);
        }

        cmd.CommandText = $"DELETE FROM [dbo].[{_profile.TableName}] WHERE {string.Join(" AND ", whereClauses)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<HashSet<string>> LoadKeysAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var _ = cmd;
        // Uses the introspected row count as the HashSet's initial capacity hint — a more
        // accurate estimate than the old per-subclass hardcoded guesses, since it comes
        // from sys.dm_db_partition_stats rather than a one-off comment in the code.
        var estimatedRowCount = (int)Math.Clamp(_profile.RowCount, 1024, int.MaxValue);
        var keys = new HashSet<string>(estimatedRowCount, StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    private string ColumnType(string columnName) =>
        _profile.Columns.First(c => c.Name == columnName).SqlTypeName;
}
