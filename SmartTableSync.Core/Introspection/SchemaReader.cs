namespace SmartTableSync.Introspection;

/// <summary>
/// Read-only schema introspection against SQL Server system catalog views. Never touches
/// table data except for the row-count/size sampling in ProfileTableAsync (MAX(DATALENGTH())
/// per LOB column, and sys.dm_db_partition_stats for row count/avg size — both read-only,
/// no locks beyond what SQL Server itself takes for a SELECT).
/// </summary>
internal sealed class SchemaReader : ISchemaReader
{
    /// <summary>A profile without configured key columns: for a child table, or a parent table
    /// that has a PRIMARY KEY or a usable unique index.</summary>
    public Task<TableProfile> GetFingerprintInputsAsync(SqlConnection connection, string tableName, CancellationToken ct) =>
        GetFingerprintInputsAsync(connection, tableName, configuredKeyColumns: null, ct);

    public async Task<TableProfile> GetFingerprintInputsAsync(
        SqlConnection parent, string tableName, IReadOnlyList<string>? configuredKeyColumns, CancellationToken ct)
    {
        var columns = await GetColumnsAsync(parent, tableName, ct);
        var primaryKeyColumns = await GetPrimaryKeyColumnsAsync(parent, tableName, ct);

        // The unique-index query runs only for a table that has neither a PRIMARY KEY nor a
        // configured key, so a table with a PRIMARY KEY costs no extra query.
        var uniqueIndexColumns = primaryKeyColumns.Count == 0 && configuredKeyColumns is not { Count: > 0 }
            ? await GetUniqueIndexKeyColumnsAsync(parent, tableName, ct)
            : [];
        var (keyColumns, keySource) = SyncKeyResolver.Resolve(
            tableName, columns, primaryKeyColumns, configuredKeyColumns, uniqueIndexColumns);

        var identityColumn = await GetIdentityColumnAsync(parent, tableName, ct);
        var isChangeTrackingEnabled = await GetIsChangeTrackingEnabledAsync(parent, tableName, ct);

        return new TableProfile(
            TableName: tableName,
            Columns: columns,
            PrimaryKeyColumns: keyColumns,
            IdentityColumn: identityColumn,
            IndexedLobColumns: [],
            ReferencesOtherTables: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>())
        {
            IsChangeTrackingEnabled = isChangeTrackingEnabled,
            KeySource = keySource,
        };
    }

    /// <summary>A full profile without configured key columns.</summary>
    public Task<TableProfile> ProfileTableAsync(SqlConnection parent, string tableName, CancellationToken ct) =>
        ProfileTableAsync(parent, tableName, configuredKeyColumns: null, ct);

    public async Task<TableProfile> ProfileTableAsync(
        SqlConnection parent, string tableName, IReadOnlyList<string>? configuredKeyColumns, CancellationToken ct)
    {
        var cheap = await GetFingerprintInputsAsync(parent, tableName, configuredKeyColumns, ct);

        // A PRIMARY KEY or unique index guarantees unique values; configured columns are only a
        // claim, checked against the data whenever the table is (re)provisioned.
        if (cheap.KeySource == SyncKeySource.Configured)
            await EnsureKeyIsUniqueAsync(parent, tableName, cheap.PrimaryKeyColumns, ct);

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
            SELECT DISTINCT SCHEMA_NAME(t.schema_id) AS SchemaName, t.name AS TableName
            FROM   sys.tables t
            JOIN   sys.indexes i        ON i.object_id = t.object_id AND i.index_id > 0
            JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  c.max_length = -1
            ORDER  BY SchemaName, TableName
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Same spelling as SmartTableSync:TableList: unqualified for dbo, "schema.Table" otherwise.
        var tables = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            tables.Add(schema == SyncTableName.DefaultSchema ? table : $"{schema}.{table}");
        }

        return tables;
    }

    public async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@TableName) IS NULL THEN 0 ELSE 1 END", connection)
        { CommandTimeout = 30 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int intResult && intResult == 1;
    }

    public async Task<string?> GetIdentityColumnAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name
            FROM   sys.identity_columns c
            WHERE  c.object_id = OBJECT_ID(@TableName)
            """;

        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<IReadOnlyList<ColumnProfile>> GetTableTypeColumnsAsync(
        SqlConnection connection, string tableName, string typeName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name AS ColumnName, t.name AS SqlTypeName, c.max_length AS MaxLength, c.is_nullable AS IsNullable,
                   c.precision AS [Precision], c.scale AS Scale
            FROM   sys.table_types tt
            JOIN   sys.columns c ON c.object_id = tt.type_table_object_id
            JOIN   sys.types t   ON c.user_type_id = t.user_type_id
            WHERE  tt.name = @TypeName AND tt.schema_id = SCHEMA_ID(@SchemaName)
            ORDER  BY c.column_id
            """;

        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        cmd.AddParam("@TypeName", SqlDbType.NVarChar, typeName, 128);
        cmd.AddParam("@SchemaName", SqlDbType.NVarChar, SyncTableName.Schema(tableName), 128);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<ColumnProfile>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnProfile(
                Name: reader.GetString(0),
                SqlTypeName: reader.GetString(1),
                MaxLength: reader.GetInt16(2),
                IsNullable: reader.GetBoolean(3),
                Precision: reader.GetByte(4),
                Scale: reader.GetByte(5)));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<ColumnProfile>> GetColumnsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name AS ColumnName, t.name AS SqlTypeName, c.max_length AS MaxLength, c.is_nullable AS IsNullable,
                   c.precision AS [Precision], c.scale AS Scale
            FROM   sys.columns c
            JOIN   sys.types t ON c.user_type_id = t.user_type_id
            WHERE  c.object_id = OBJECT_ID(@TableName)
            ORDER  BY c.column_id
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<ColumnProfile>();
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnProfile(
                Name: reader.GetString(0),
                SqlTypeName: reader.GetString(1),
                MaxLength: reader.GetInt16(2),
                IsNullable: reader.GetBoolean(3),
                Precision: reader.GetByte(4),
                Scale: reader.GetByte(5)));
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
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var keyColumns = new List<string>();
        while (await reader.ReadAsync(ct))
            keyColumns.Add(reader.GetString(0));

        return keyColumns;
    }

    /// <summary>The key columns of the unique index that stands in for a missing PRIMARY KEY:
    /// enabled, unfiltered, every key column NOT NULL and not rowversion (its values can't be
    /// copied), fewest key columns first, then lowest index_id so the choice is stable. Included
    /// columns (key_ordinal 0) don't count. Empty when no index qualifies.</summary>
    private static async Task<IReadOnlyList<string>> GetUniqueIndexKeyColumnsAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            WITH Candidate AS (
                SELECT TOP (1) i.object_id, i.index_id
                FROM   sys.indexes i
                JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
                JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE  i.object_id = OBJECT_ID(@TableName)
                  AND  i.is_unique = 1
                  AND  i.has_filter = 0
                  AND  i.is_disabled = 0
                  AND  i.is_hypothetical = 0
                GROUP  BY i.object_id, i.index_id
                HAVING MAX(CASE WHEN c.is_nullable = 1 OR c.system_type_id = 189 OR c.max_length = -1 THEN 1 ELSE 0 END) = 0
                ORDER  BY COUNT(*), i.index_id
            )
            SELECT c.name AS ColumnName
            FROM   Candidate k
            JOIN   sys.index_columns ic ON ic.object_id = k.object_id AND ic.index_id = k.index_id AND ic.key_ordinal > 0
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            ORDER  BY ic.key_ordinal
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var keyColumns = new List<string>();
        while (await reader.ReadAsync(ct))
            keyColumns.Add(reader.GetString(0));

        return keyColumns;
    }

    /// <summary>Fails when any combination of the configured key columns' values occurs more
    /// than once. One grouped scan of the table; no values are read back or logged.</summary>
    private static async Task EnsureKeyIsUniqueAsync(
        SqlConnection parent, string tableName, IReadOnlyList<string> keyColumns, CancellationToken ct)
    {
        var groupBy = string.Join(", ", keyColumns.Select(SqlIdentifier.Quote));
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- only identifiers, all quoted by SqlIdentifier
        var sql = $"SELECT TOP (1) 1 FROM {SqlIdentifier.Table(tableName)} GROUP BY {groupBy} HAVING COUNT(*) > 1";

        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- see above
        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 300 };
        if (await cmd.ExecuteScalarAsync(ct) is not null)
        {
            throw new InvalidOperationException(
                $"{tableName}: the configured key columns ({string.Join(", ", keyColumns)}) don't identify each row — " +
                "at least one combination of their values occurs more than once in the parent table. " +
                "Choose columns that are unique (SmartTableSync:KeyColumns), or add a PRIMARY KEY or unique index to the table.");
        }
    }

    private static async Task<bool> GetIsChangeTrackingEnabledAsync(SqlConnection parent, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(@TableName)
            ) THEN 1 ELSE 0 END AS BIT)
            """;

        await using var cmd = new SqlCommand(sql, parent) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
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
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
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
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
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
        cmd.AddParam("@TableName", SqlDbType.NVarChar, SqlIdentifier.Table(tableName), 256);
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
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- only identifiers, all quoted by SqlIdentifier
        var sql = $"SELECT {selectList} FROM {SqlIdentifier.Table(tableName)}";

        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- see above
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
