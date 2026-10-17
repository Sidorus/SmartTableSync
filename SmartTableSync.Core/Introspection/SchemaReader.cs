namespace SmartTableSync.Introspection;

/// <summary>
/// Read-only schema introspection against SQL Server system catalog views. Never touches
/// table data except for the row-count/size sampling in ProfileTableAsync (MAX(DATALENGTH())
/// per LOB column, and sys.dm_db_partition_stats for row count/avg size — both read-only,
/// no locks beyond what SQL Server itself takes for a SELECT).
/// </summary>
public sealed class SchemaReader : ISchemaReader
{
    public async Task<TableProfile> GetFingerprintInputsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        var columns = await GetColumnsAsync(parent, tableName, ct);
        var primaryKeyColumns = await GetPrimaryKeyColumnsAsync(parent, tableName, ct);
        var identityColumn = await GetIdentityColumnAsync(parent, tableName, ct);
        var isChangeTrackingEnabled = await GetIsChangeTrackingEnabledAsync(parent, tableName, ct);

        return new TableProfile(
            TableName: tableName,
            Columns: columns,
            PrimaryKeyColumns: primaryKeyColumns,
            IdentityColumn: identityColumn,
            IndexedLobColumns: [],
            ReferencesOtherTables: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>())
        {
            IsChangeTrackingEnabled = isChangeTrackingEnabled,
        };
    }

    public async Task<TableProfile> ProfileTableAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        var cheap = await GetFingerprintInputsAsync(parent, tableName, ct);

        var indexedLobColumns = await GetIndexedLobColumnsAsync(parent, tableName, ct);
        var referencesOtherTables = await GetReferencesOtherTablesAsync(parent, tableName, ct);
        var (rowCount, avgRowSizeBytes) = await GetRowCountAndAvgSizeAsync(parent, tableName, ct);

        var lobColumnNames = cheap.Columns.Where(c => c.IsLob).Select(c => c.Name).ToList();
        var maxLobColumnSizeBytes = lobColumnNames.Count == 0
            ? new Dictionary<string, long>()
            : await GetMaxLobColumnSizesAsync(parent, tableName, lobColumnNames, ct);

        return cheap with
        {
            IndexedLobColumns = indexedLobColumns,
            ReferencesOtherTables = referencesOtherTables,
            RowCount = rowCount,
            AvgRowSizeBytes = avgRowSizeBytes,
            MaxLobColumnSizeBytes = maxLobColumnSizeBytes,
        };
    }

    public async Task<IReadOnlyList<string>> FindDataSyncIncompatibleTablesAsync(SqlConnection parent, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT t.name AS TableName
            FROM   sys.tables t
            JOIN   sys.indexes i        ON i.object_id = t.object_id AND i.index_id > 0
            JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  c.max_length = -1
            ORDER  BY t.name
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var tables = new List<string>();
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));

        return tables;
    }

    public async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@TableName) IS NULL THEN 0 ELSE 1 END", connection)
        { CommandTimeout = 30 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int intResult && intResult == 1;
    }

    private static async Task<IReadOnlyList<ColumnProfile>> GetColumnsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name AS ColumnName, t.name AS SqlTypeName, c.max_length AS MaxLength, c.is_nullable AS IsNullable
            FROM   sys.columns c
            JOIN   sys.types t ON c.user_type_id = t.user_type_id
            WHERE  c.object_id = OBJECT_ID(@TableName)
            ORDER  BY c.column_id
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<ColumnProfile>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnProfile(
                Name: reader.GetString(0),
                SqlTypeName: reader.GetString(1),
                MaxLength: reader.GetInt16(2),
                IsNullable: reader.GetBoolean(3)));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Table '{tableName}' not found (or caller lacks VIEW DEFINITION) in the connected database.");

        return columns;
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name AS ColumnName
            FROM   sys.key_constraints kc
            JOIN   sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  kc.parent_object_id = OBJECT_ID(@TableName)
              AND  kc.type = 'PK'
            ORDER  BY ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var keyColumns = new List<string>();
        while (await reader.ReadAsync(ct))
            keyColumns.Add(reader.GetString(0));

        return keyColumns;
    }

    private static async Task<string?> GetIdentityColumnAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name
            FROM   sys.identity_columns c
            WHERE  c.object_id = OBJECT_ID(@TableName)
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task<bool> GetIsChangeTrackingEnabledAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(@TableName)
            ) THEN 1 ELSE 0 END AS BIT)
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool boolResult && boolResult;
    }

    private static async Task<IReadOnlyList<string>> GetIndexedLobColumnsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT c.name AS ColumnName
            FROM   sys.indexes i
            JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  i.object_id = OBJECT_ID(@TableName)
              AND  i.index_id > 0
              AND  c.max_length = -1
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<string>();
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));

        return columns;
    }

    private static async Task<bool> GetReferencesOtherTablesAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(@TableName)
            ) THEN 1 ELSE 0 END AS BIT)
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool boolResult && boolResult;
    }

    private static async Task<(long RowCount, long AvgRowSizeBytes)> GetRowCountAndAvgSizeAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT
                SUM(ps.row_count) AS [RowCount],
                CASE WHEN SUM(ps.row_count) > 0
                     THEN CAST((SUM(ps.used_page_count) * 8192.0) / SUM(ps.row_count) AS BIGINT)
                     ELSE 0 END AS [AvgRowSizeBytes]
            FROM   sys.dm_db_partition_stats ps
            WHERE  ps.object_id = OBJECT_ID(@TableName)
              AND  ps.index_id IN (0, 1)
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct) || await reader.IsDBNullAsync(0, ct))
            return (0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Builds one dynamic SELECT that computes MAX(DATALENGTH(col)) for every LOB
    /// column in a single table scan, rather than one query per column. Column names come
    /// from GetColumnsAsync (i.e. sys.columns — real, existing column names), not external
    /// input, but are still bracket-quoted defensively before being concatenated into SQL text.</summary>
    private static async Task<IReadOnlyDictionary<string, long>> GetMaxLobColumnSizesAsync(
        SqlConnection parent, string tableName, IReadOnlyList<string> lobColumnNames, CancellationToken ct)
    {
        var selectList = string.Join(", ", lobColumnNames.Select(col => $"MAX(DATALENGTH({SqlIdentifier.Quote(col)})) AS {SqlIdentifier.Quote(col)}"));
        var sql = $"SELECT {selectList} FROM {SqlIdentifier.Quote(tableName)}";

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var sizes = new Dictionary<string, long>();
        if (await reader.ReadAsync(ct))
        {
            foreach (var col in lobColumnNames)
            {
                var ordinal = reader.GetOrdinal(col);
                sizes[col] = await reader.IsDBNullAsync(ordinal, ct) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));
            }
        }

        return sizes;
    }
}
