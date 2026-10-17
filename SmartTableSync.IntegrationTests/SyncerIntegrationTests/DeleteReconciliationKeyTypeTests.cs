using System.Data;
using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Infrastructure;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// ConfigurableTableSyncer.ReconcileDeletesAsync for primary keys that are neither integers
/// nor strings. The key-set diff reads every key as one string, so the conversion must be
/// lossless (two keys never compare equal) and must parse back to the exact value for the
/// DELETE to match. Each test owns its table (GuidKeyTestTable / DateTime2KeyTestTable) and
/// resets it first, so it's safe in the shared [Collection("SqlServer")] database.
/// </summary>
[Collection("SqlServer")]
public class DeleteReconciliationKeyTypeTests
{
    private readonly MsSqlTestFixture _fixture;

    public DeleteReconciliationKeyTypeTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GuidKeyTable_RemovesOnlyRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        // The first two share their first 20 characters, so a truncated key string can't tell them apart.
        var kept1 = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var deleted = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc96400");
        var kept2 = Guid.Parse("0e984725-c51c-4bf4-9960-e1c80e27aba0");
        foreach (var connection in new[] { parent, child })
        {
            await ResetAsync(connection, "GuidKeyTestTable");
            foreach (var id in new[] { kept1, deleted, kept2 })
                await InsertAsync(connection, "GuidKeyTestTable", SqlDbType.UniqueIdentifier, id);
        }

        await DeleteAsync(parent, "GuidKeyTestTable", SqlDbType.UniqueIdentifier, deleted);
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "GuidKeyTestTable");

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount);
        var remaining = await ReadKeysAsync(child, "GuidKeyTestTable");
        Assert.Equal(new object[] { kept1, kept2 }.OrderBy(k => k), remaining.OrderBy(k => k));
    }

    [Fact]
    public async Task DateTime2KeyTable_RemovesOnlyRowNoLongerInParent()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        // The first two differ only in fractional seconds, the third only in its seconds.
        var kept1 = new DateTime(2026, 1, 15, 10, 30, 15).AddTicks(1234567);
        var deleted = new DateTime(2026, 1, 15, 10, 30, 15).AddTicks(7654321);
        var kept2 = new DateTime(2026, 1, 15, 10, 30, 16);
        foreach (var connection in new[] { parent, child })
        {
            await ResetAsync(connection, "DateTime2KeyTestTable");
            foreach (var id in new[] { kept1, deleted, kept2 })
                await InsertAsync(connection, "DateTime2KeyTestTable", SqlDbType.DateTime2, id);
        }

        await DeleteAsync(parent, "DateTime2KeyTestTable", SqlDbType.DateTime2, deleted);
        var (syncer, _) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "DateTime2KeyTestTable");

        // Act
        var deletedCount = await syncer.ReconcileDeletesAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(1, deletedCount);
        var remaining = await ReadKeysAsync(child, "DateTime2KeyTestTable");
        Assert.Equal(new object[] { kept1, kept2 }, remaining.OrderBy(k => k));
    }

    [Fact]
    public async Task KeySelectExpression_EverySupportedKeyType_ParsesBackToTheExactStoredValue()
    {
        // Arrange - one column per key type, each holding a value a lossy conversion would
        // change (full fractional seconds, a non-zero offset, more than 20 characters).
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();

        var columns = new (string Name, string SqlType, string Literal)[]
        {
            ("IntKey", "int", "-2147483648"),
            ("BigIntKey", "bigint", "-9223372036854775808"),
            ("SmallIntKey", "smallint", "-32768"),
            ("TinyIntKey", "tinyint", "255"),
            ("DecimalKey", "decimal(28, 10)", "-123456789012345678.0123456789"),
            ("NumericKey", "numeric(10, 4)", "12.5000"),
            ("GuidKey", "uniqueidentifier", "'6F9619FF-8B86-D011-B42D-00C04FC964FF'"),
            ("DateTimeKey", "datetime", "'2026-01-15T10:30:15.997'"),
            ("DateTime2Key", "datetime2(7)", "'2026-01-15T10:30:15.1234567'"),
            ("DateTime2WholeSecondKey", "datetime2(7)", "'2026-01-15T10:30:15'"),
            ("SmallDateTimeKey", "smalldatetime", "'2026-01-15T10:30:00'"),
            ("DateKey", "date", "'2026-01-15'"),
            ("DateTimeOffsetKey", "datetimeoffset(7)", "'2026-01-15T10:30:15.1234567+05:30'"),
            ("TimeKey", "time(7)", "'10:30:15.1234567'"),
            ("StringKey", "nvarchar(50)", "N'en'"),
        };
        var columnNames = columns.Select(c => c.Name).ToList();
        var baseType = columns.ToDictionary(c => c.Name, c => c.SqlType.Split('(')[0]);

        await using (var create = new SqlCommand(
            $"CREATE TABLE #KeyTypes ({string.Join(", ", columns.Select(c => $"{SqlIdentifier.Quote(c.Name)} {c.SqlType} NOT NULL"))}); " +
            $"INSERT INTO #KeyTypes VALUES ({string.Join(", ", columns.Select(c => c.Literal))});", child))
        {
            await create.ExecuteNonQueryAsync();
        }

        // Act
        var keyExpression = SmartTableSync.Syncers.Keys.KeysetPredicateBuilder.BuildKeySelectExpression(columnNames, c => baseType[c]);
        await using var select = new SqlCommand(
            $"SELECT {keyExpression}, {string.Join(", ", columnNames.Select(SqlIdentifier.Quote))} FROM #KeyTypes", child);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var key = reader.GetString(0);

        // Assert
        var parts = SmartTableSync.Syncers.Keys.CompositeKey.Parse(key, columnNames);
        for (var i = 0; i < columnNames.Count; i++)
        {
            var stored = reader.GetValue(i + 1);
            var parsed = SmartTableSync.Syncers.Sql.SqlTypeMapping.ParseKeyComponent(parts[i], baseType[columnNames[i]]);
            Assert.True(Equals(stored, parsed), $"{columnNames[i]}: stored '{stored}', key component '{parts[i]}' parsed to '{parsed}'");
            if (stored is DateTimeOffset storedOffset)
                Assert.Equal(storedOffset.Offset, ((DateTimeOffset)parsed).Offset);
        }
    }

    private static async Task ResetAsync(SqlConnection connection, string table)
    {
        await using var cmd = new SqlCommand($"DELETE FROM [dbo].{SqlIdentifier.Quote(table)}", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertAsync(SqlConnection connection, string table, SqlDbType keyType, object id)
    {
        await using var cmd = new SqlCommand(
            $"INSERT INTO [dbo].{SqlIdentifier.Quote(table)} ([Id], [Value]) VALUES (@Id, @Value)", connection);
        cmd.AddParam("@Id", keyType, id);
        cmd.AddParam("@Value", SqlDbType.NVarChar, $"row {id}", 200);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DeleteAsync(SqlConnection connection, string table, SqlDbType keyType, object id)
    {
        await using var cmd = new SqlCommand($"DELETE FROM [dbo].{SqlIdentifier.Quote(table)} WHERE [Id] = @Id", connection);
        cmd.AddParam("@Id", keyType, id);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    private static async Task<List<object>> ReadKeysAsync(SqlConnection connection, string table)
    {
        await using var cmd = new SqlCommand($"SELECT [Id] FROM [dbo].{SqlIdentifier.Quote(table)}", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var keys = new List<object>();
        while (await reader.ReadAsync())
            keys.Add(reader.GetValue(0));
        return keys;
    }
}
