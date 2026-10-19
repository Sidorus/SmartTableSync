namespace SmartTableSync.Sql;

/// <summary>Builds the parent/child SqlCommands the sync and delete-reconciliation paths need,
/// from a TableProfile + SyncPlan. Every read of parent rows is one page of a keyset-paginated
/// query: "SELECT TOP (@Size) ... WHERE [filter] AND [key after the last page] ORDER BY [key]".
/// The strategies differ only in the filter (and, for Change Tracking, the source), so they
/// share BuildPageCommand. The page size is a parameter rather than taken from the plan because
/// InitialSyncRunner can shrink it at runtime after a timeout.</summary>
internal sealed class TableSyncCommandFactory
{
    /// <summary>Extra bit column a page may carry: 1 when the row's key no longer exists in the
    /// parent table (a delete), 0 otherwise. Only the Change Tracking page sets it;
    /// ChangeWindowSyncRunner deletes flagged rows and removes the column before merging.</summary>
    public const string RowDeletedColumn = "__SmartTableSync_RowDeleted";

    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly string _table;

    public TableSyncCommandFactory(TableProfile profile, SyncPlan plan)
    {
        _profile = profile;
        _plan = plan;
        _table = SqlIdentifier.Table(profile.TableName);
    }

    /// <summary>Every row, page by page - the initial copy and the FullRescan strategy.</summary>
    public SqlCommand BuildFullTablePageCommand(SqlConnection parent, string? afterKey, int pageSize) =>
        BuildPageCommand(parent, null, "*", _table, filter: null, keyAlias: null, afterKey, pageSize);

    /// <summary>Timestamp strategy. The one-minute overlap absorbs clock skew and transactions
    /// that committed slightly after they stamped the column; re-reading a row is harmless
    /// because every write is an idempotent MERGE.</summary>
    public SqlCommand BuildTimestampPageCommand(SqlConnection parent, DateTime lastSync, string? afterKey, int pageSize)
    {
        var cmd = BuildPageCommand(parent, null, "*", _table,
            $"{SqlIdentifier.Quote(_plan.IncrementalColumn!)} >= DATEADD(minute, -1, @LastSync)", keyAlias: null, afterKey, pageSize);
        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    /// <summary>RowVersion strategy. Inclusive (&gt;=) on purpose: the position is captured with
    /// MIN_ACTIVE_ROWVERSION(), which is the value the next write receives when nothing is in
    /// flight, so the first write after the capture can land exactly on it.</summary>
    public SqlCommand BuildRowVersionPageCommand(SqlConnection parent, byte[] fromRowVersion, string? afterKey, int pageSize)
    {
        var cmd = BuildPageCommand(parent, null, "*", _table,
            $"{SqlIdentifier.Quote(_plan.IncrementalColumn!)} >= @FromRowVersion", keyAlias: null, afterKey, pageSize);
        cmd.Parameters.Add("@FromRowVersion", SqlDbType.Timestamp).Value = fromRowVersion;
        return cmd;
    }

    /// <summary>SqlChangeTracking strategy: keys changed since fromVersion (CHANGETABLE already
    /// returns one row per key, however often it changed), LEFT JOINed to the table for current
    /// values. Columns come back with the same names and order as SELECT *, so the page feeds
    /// the merge strategies unchanged. Key values come from CHANGETABLE because the table side is
    /// NULL for a deleted row; that NULL is what sets RowDeletedColumn. Runs on the caller's
    /// transaction so it sees the same snapshot as the version check before it.</summary>
    public SqlCommand BuildChangeTrackingPageCommand(
        SqlConnection parent, SqlTransaction transaction, long fromVersion, string? afterKey, int pageSize)
    {
        var keyColumns = _profile.PrimaryKeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = _profile.Columns.Select(c => $"{(keyColumns.Contains(c.Name) ? "CT" : "T")}.{SqlIdentifier.Quote(c.Name)}");
        var firstKey = SqlIdentifier.Quote(_profile.PrimaryKeyColumns[0]);
        var selectList = string.Join(", ", columns) +
            $", CAST(CASE WHEN T.{firstKey} IS NULL THEN 1 ELSE 0 END AS BIT) AS {SqlIdentifier.Quote(RowDeletedColumn)}";

        var joinOnKeys = string.Join(" AND ", _profile.PrimaryKeyColumns.Select(c =>
            $"T.{SqlIdentifier.Quote(c)} = CT.{SqlIdentifier.Quote(c)}"));
        var source = $"CHANGETABLE(CHANGES {_table}, @FromVersion) AS CT LEFT JOIN {_table} AS T ON {joinOnKeys}";

        var cmd = BuildPageCommand(parent, transaction, selectList, source, filter: null, keyAlias: "CT", afterKey, pageSize);
        cmd.Parameters.Add("@FromVersion", SqlDbType.BigInt).Value = fromVersion;
        return cmd;
    }

    public SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- only identifiers, all quoted by SqlIdentifier
        new($"SELECT {BuildKeySelectExpression()} FROM {_table}", parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    public SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- only identifiers, all quoted by SqlIdentifier
        new($"SELECT {BuildKeySelectExpression()} FROM {_table}", child) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    /// <summary>afterKey null means the first page: no key condition at all. (A "before every
    /// value" placeholder such as 0 or '' would skip rows whose key is 0, negative or empty, and
    /// doesn't exist at all for date or GUID keys.)</summary>
    private SqlCommand BuildPageCommand(
        SqlConnection parent, SqlTransaction? transaction, string selectList, string source,
        string? filter, string? keyAlias, string? afterKey, int pageSize)
    {
        var conditions = new List<string>(2);
        if (filter is not null)
        {
            conditions.Add(filter);
        }

        var keyParameters = _profile.PrimaryKeyColumns.Select((_, i) => $"@Key{i}").ToList();
        if (afterKey is not null)
        {
            conditions.Add($"({KeysetPredicateBuilder.Build(_profile.PrimaryKeyColumns, keyParameters, keyAlias)})");
        }

        var where = conditions.Count > 0 ? $"WHERE  {string.Join("\n  AND  ", conditions)}" : string.Empty;

        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- identifiers quoted by SqlIdentifier; page size, keys and versions are parameters
        var cmd = new SqlCommand($"""
            SELECT TOP (@Size) {selectList}
            FROM   {source}
            {where}
            ORDER  BY {KeysetPredicateBuilder.BuildOrderBy(_profile.PrimaryKeyColumns, keyAlias)}
            """, parent, transaction) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = pageSize;

        if (afterKey is not null)
        {
            var keyParts = CompositeKey.Parse(afterKey, _profile.PrimaryKeyColumns);
            for (var i = 0; i < _profile.PrimaryKeyColumns.Count; i++)
            {
                var columnType = ColumnType(_profile.PrimaryKeyColumns[i]);
                cmd.Parameters.Add(keyParameters[i], SqlTypeMapping.ToSqlDbType(columnType)).Value =
                    SqlTypeMapping.ParseKeyComponent(keyParts[i], columnType);
            }
        }

        return cmd;
    }

    private string BuildKeySelectExpression() =>
        KeysetPredicateBuilder.BuildKeySelectExpression(_profile.PrimaryKeyColumns, ColumnType);

    private string ColumnType(string columnName) =>
        _profile.Columns.First(c => c.Name == columnName).SqlTypeName;
}
