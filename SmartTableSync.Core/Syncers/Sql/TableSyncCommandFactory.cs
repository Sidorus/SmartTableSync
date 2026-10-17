namespace SmartTableSync.Syncers.Sql;

/// <summary>Builds the parent/child SqlCommands the sync and delete-reconciliation paths
/// need, from a TableProfile + SyncPlan. Stateless beyond the profile/plan it was built
/// with — batch size is a per-call parameter rather than a constructor-captured field,
/// since InitialSyncRunner mutates it at runtime via timeout backoff and this factory has
/// no reason to know about that.</summary>
public sealed class TableSyncCommandFactory
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;

    public TableSyncCommandFactory(TableProfile profile, SyncPlan plan)
    {
        _profile = profile;
        _plan = plan;
    }

    public SqlCommand BuildInitialBatchCommand(SqlConnection parent, string? resumeKey, int batchSize)
    {
        var paramNames = _profile.PrimaryKeyColumns.Select((_, i) => $"@Key{i}").ToList();
        var predicate = KeysetPredicateBuilder.Build(_profile.PrimaryKeyColumns, paramNames);
        var orderBy = KeysetPredicateBuilder.BuildOrderBy(_profile.PrimaryKeyColumns);

        var sql = $"""
            SELECT TOP (@Size) * FROM [dbo].[{_profile.TableName}]
            WHERE  {predicate}
            ORDER  BY {orderBy}
            """;

        var cmd = new SqlCommand(sql, parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };
        cmd.Parameters.Add("@Size", SqlDbType.Int).Value = batchSize;

        var keyParts = resumeKey is null
            ? _profile.PrimaryKeyColumns.Select(c => SqlTypeMapping.DefaultKeyComponent(ColumnType(c))).ToArray()
            : CompositeKey.Parse(resumeKey, _profile.PrimaryKeyColumns);

        for (var i = 0; i < _profile.PrimaryKeyColumns.Count; i++)
        {
            var colType = ColumnType(_profile.PrimaryKeyColumns[i]);
            cmd.Parameters.Add(paramNames[i], SqlTypeMapping.ToSqlDbType(colType)).Value =
                SqlTypeMapping.ParseKeyComponent(keyParts[i], colType);
        }

        return cmd;
    }

    public SqlCommand BuildIncrementalCommand(SqlConnection parent, DateTime lastSync)
    {
        var cmd = new SqlCommand($"""
            SELECT * FROM [dbo].[{_profile.TableName}]
            WHERE  [{_plan.ChangeTrackingColumn}] >= DATEADD(minute, -1, @LastSync)
            """, parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

        cmd.Parameters.Add("@LastSync", SqlDbType.DateTime).Value = lastSync;
        return cmd;
    }

    /// <summary>Rowversion strategy's incremental query — an exact cutoff, unlike
    /// BuildIncrementalCommand's 1-minute datetime overlap, since rowversion values are
    /// unambiguous and monotonically increasing. RowVersionSyncRunner is responsible for
    /// capturing lastRowVersion correctly (MIN_ACTIVE_ROWVERSION(), never @@DBTS).
    ///
    /// Deliberately &gt;= (inclusive), not &gt; : MIN_ACTIVE_ROWVERSION() returns the value the
    /// *next* write will receive when nothing is currently in flight, so the very first write
    /// committed after a position was captured can land on exactly that value — a strict
    /// &gt; would silently skip it. Never re-matches a row from the previous cycle either: that
    /// row's version was already committed (thus strictly less than) the position captured
    /// before that cycle's read, by the same reasoning.</summary>
    public SqlCommand BuildRowVersionIncrementalCommand(SqlConnection parent, byte[] lastRowVersion)
    {
        var cmd = new SqlCommand($"""
            SELECT * FROM [dbo].[{_profile.TableName}]
            WHERE  [{_plan.ChangeTrackingColumn}] >= @LastRowVersion
            """, parent) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

        cmd.Parameters.Add("@LastRowVersion", SqlDbType.Timestamp).Value = lastRowVersion;
        return cmd;
    }

    /// <summary>Name of the extra bit column BuildChangeTrackingCommand appends: 1 when the
    /// changed key no longer exists in the parent table (a delete), 0 otherwise.
    /// ChangeTrackingSyncRunner removes it before handing the rows to the merge strategy.</summary>
    public const string ChangeTrackingRowMissingColumn = "__SmartTableSync_RowMissing";

    /// <summary>SQL Server Change Tracking strategy's incremental query: every key changed since
    /// lastVersion (one row per key — CHANGETABLE already collapses repeated changes), LEFT
    /// JOINed back to the table for current values. Columns come back in the table's own
    /// column order with the same names as SELECT *, so the result feeds the existing merge
    /// strategies unchanged; key columns are taken from CHANGETABLE because the table side is
    /// NULL for a deleted row. A row is a delete exactly when the join finds nothing — that
    /// covers SYS_CHANGE_OPERATION = 'D' and, outside snapshot isolation, a row deleted after
    /// the change was recorded. Must run on the caller's transaction (snapshot isolation) so
    /// it sees the same point in time as the version bounds read before it.</summary>
    public SqlCommand BuildChangeTrackingCommand(SqlConnection parent, SqlTransaction transaction, long lastVersion)
    {
        var keyColumns = _profile.PrimaryKeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectList = _profile.Columns.Select(c => keyColumns.Contains(c.Name)
            ? $"CT.{QuoteIdentifier(c.Name)}"
            : $"T.{QuoteIdentifier(c.Name)}");
        var joinPredicate = string.Join(" AND ", _profile.PrimaryKeyColumns.Select(c =>
            $"T.{QuoteIdentifier(c)} = CT.{QuoteIdentifier(c)}"));
        var firstKey = QuoteIdentifier(_profile.PrimaryKeyColumns[0]);
        var table = $"[dbo].{QuoteIdentifier(_profile.TableName)}";

        var cmd = new SqlCommand($"""
            SELECT {string.Join(", ", selectList)},
                   CAST(CASE WHEN T.{firstKey} IS NULL THEN 1 ELSE 0 END AS BIT) AS {QuoteIdentifier(ChangeTrackingRowMissingColumn)}
            FROM   CHANGETABLE(CHANGES {table}, @LastVersion) AS CT
            LEFT JOIN {table} AS T ON {joinPredicate}
            """, parent, transaction) { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

        cmd.Parameters.Add("@LastVersion", SqlDbType.BigInt).Value = lastVersion;
        return cmd;
    }

    public SqlCommand BuildParentKeyCommand(SqlConnection parent) =>
        new($"SELECT {BuildKeySelectExpression()} FROM [dbo].[{_profile.TableName}]", parent)
        { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    public SqlCommand BuildChildKeyCommand(SqlConnection child) =>
        new($"SELECT {BuildKeySelectExpression()} FROM [dbo].[{_profile.TableName}]", child)
        { CommandTimeout = _plan.BulkCopyTimeoutSeconds };

    private string BuildKeySelectExpression() =>
        KeysetPredicateBuilder.BuildKeySelectExpression(
            _profile.PrimaryKeyColumns,
            c => SqlTypeMapping.IsStringType(ColumnType(c)));

    private string ColumnType(string columnName) =>
        _profile.Columns.First(c => c.Name == columnName).SqlTypeName;

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
