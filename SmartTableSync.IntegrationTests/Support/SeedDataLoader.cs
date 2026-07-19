using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SmartTableSync.IntegrationTests.Support;

/// <summary>
/// Generic JSON -> DB row loader used by every seed data file (SeedData/*.json). One
/// generic loader instead of a POCO class per table, matching how the rest of the engine
/// already treats rows as name/value pairs rather than typed entities.
/// </summary>
public static class SeedDataLoader
{
    public static async Task<List<Dictionary<string, object?>>> LoadAsync(string jsonPath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var rowElement in document.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, object?>();
            foreach (var property in rowElement.EnumerateObject())
                row[property.Name] = ConvertJsonValue(property.Value);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Seeds a table only if it's currently empty. Several test classes share the
    /// same database via the collection fixture and some seed the same lookup table (e.g.
    /// Attribute) - since xUnit doesn't guarantee which class runs first, whichever runs
    /// first seeds the rows and the rest detect they're already there and skip.</summary>
    public static async Task EnsureSeededAsync(
        SqlConnection connection, string tableName, string jsonFileName,
        bool hasIdentityColumn = false, CancellationToken ct = default)
    {
        await using var countCommand = new SqlCommand($"SELECT COUNT(*) FROM [dbo].[{tableName}]", connection);
        var existing = (int)(await countCommand.ExecuteScalarAsync(ct))!;
        if (existing > 0) return;

        var seedPath = Path.Combine(AppContext.BaseDirectory, "SeedData", jsonFileName);
        var rows = await LoadAsync(seedPath, ct);
        await InsertRowsAsync(connection, tableName, rows, hasIdentityColumn, ct);
    }

    /// <summary>Inserts every row into the given table via a parameterized INSERT built
    /// from each row's keys/values. Set hasIdentityColumn=true for tables where the seed
    /// data specifies an IDENTITY column's value explicitly (e.g. to keep FK references
    /// between seed files predictable) - wraps the inserts in SET IDENTITY_INSERT ON/OFF,
    /// which SQL Server rejects for tables that don't have an identity column at all.</summary>
    public static async Task InsertRowsAsync(
        SqlConnection connection, string tableName, IReadOnlyList<Dictionary<string, object?>> rows,
        bool hasIdentityColumn = false, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        if (hasIdentityColumn)
        {
            await using var identityOn = new SqlCommand($"SET IDENTITY_INSERT [dbo].[{tableName}] ON", connection);
            await identityOn.ExecuteNonQueryAsync(ct);
        }

        try
        {
            foreach (var row in rows)
            {
                var columns = string.Join(", ", row.Keys.Select(c => $"[{c}]"));

                // Null values are emitted as the literal NULL rather than a parameter -
                // AddWithValue can't infer a SqlDbType from DBNull.Value alone and defaults
                // to NVarChar, which SQL Server then refuses to implicitly convert against a
                // varbinary(max) target column (DocumentAsset's FileContent/PreviewImage/
                // PdfRendition all hit this).
                var valuePlaceholders = string.Join(", ", row.Select(kv => kv.Value is null ? "NULL" : $"@{kv.Key}"));

                await using var command = new SqlCommand(
                    $"INSERT INTO [dbo].[{tableName}] ({columns}) VALUES ({valuePlaceholders})", connection);

                // AddWithValue is normally avoided in this codebase (see CLAUDE.md) because
                // implicit type inference can produce bad query plans on hot paths - but this
                // loader has no ColumnProfile/SqlDbType metadata to build typed parameters
                // from, and it only ever seeds a handful of static test rows once per run, so
                // the tradeoff that convention exists to avoid doesn't apply here.
                foreach (var (column, value) in row)
                    if (value is not null)
                        command.Parameters.AddWithValue($"@{column}", value);

                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (hasIdentityColumn)
            {
                await using var identityOff = new SqlCommand($"SET IDENTITY_INSERT [dbo].[{tableName}] OFF", connection);
                await identityOff.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
        JsonValueKind.String => ConvertJsonString(element.GetString()!),
        _ => throw new NotSupportedException($"Unsupported JSON value kind in seed data: {element.ValueKind}"),
    };

    private static object ConvertJsonString(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime) && LooksLikeIsoDate(value)
            ? dateTime
            : value;

    // A plain string like "en" or "SEK" can accidentally parse as a date under some
    // cultures - only treat it as a date if it actually looks like one (starts with a
    // 4-digit year), so language/currency codes stay strings.
    private static bool LooksLikeIsoDate(string value) =>
        value.Length >= 10 && value[4] == '-' && value.Take(4).All(char.IsDigit);
}
