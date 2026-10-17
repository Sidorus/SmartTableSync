namespace SmartTableSync.Merge;

/// <summary>Drains a SqlDataReader into an in-memory DataTable, inferring columns from the
/// reader's first row. Shared by InitialSyncRunner and IncrementalSyncRunner — both read a
/// batch of parent rows the same way, only what happens to the batch afterward differs.</summary>
internal static class DataTableLoader
{
    public static async Task<DataTable> LoadAsync(SqlDataReader reader, CancellationToken ct)
    {
        var dataTable = new DataTable();
        bool first = true;

        while (await reader.ReadAsync(ct))
        {
            if (first)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                    dataTable.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
                first = false;
            }

            var row = dataTable.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            dataTable.Rows.Add(row);
        }

        return dataTable;
    }
}
