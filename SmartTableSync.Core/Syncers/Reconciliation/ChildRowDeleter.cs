namespace SmartTableSync.Syncers.Reconciliation;

/// <summary>Deletes one child row by its CompositeKey-formatted key. Shared by DeleteReconciler
/// (orphans found by the hourly key-set diff) and ChangeTrackingSyncRunner (deletes reported
/// directly by SQL Server Change Tracking), so both build the same parameterized DELETE.</summary>
public sealed class ChildRowDeleter
{
    private readonly TableProfile _profile;

    public ChildRowDeleter(TableProfile profile) => _profile = profile;

    public async Task DeleteAsync(string key, SqlConnection child, CancellationToken ct)
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

    private string ColumnType(string columnName) =>
        _profile.Columns.First(c => c.Name == columnName).SqlTypeName;
}
