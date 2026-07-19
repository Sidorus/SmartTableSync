namespace SmartTableSync.Syncers.Sql;

/// <summary>Drains a SqlDataReader into an in-memory DataTable, inferring columns from the
/// reader's first row. Shared by InitialSyncRunner and IncrementalSyncRunner — both read a
/// batch of parent rows the same way, only what happens to the batch afterward differs.</summary>
public static class DataTableLoader
{
    public static async Task<DataTable> LoadAsync(SqlDataReader reader, CancellationToken ct)
    {
        var dt = new DataTable();
        bool first = true;

        while (await reader.ReadAsync(ct))
        {
            if (first)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                    dt.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
                first = false;
            }

            var row = dt.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            dt.Rows.Add(row);
        }

        return dt;
    }
}
