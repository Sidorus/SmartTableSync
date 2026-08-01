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
}
