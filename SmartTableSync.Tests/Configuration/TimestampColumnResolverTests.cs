using SmartTableSync.Strategies.Timestamp;
using SmartTableSync.Introspection;
using Xunit;

namespace SmartTableSync.Tests.Configuration;

public class ChangeTrackingColumnResolverTests
{
    private static TableProfile CreateProfile(params ColumnProfile[] columns) => new(
        TableName: "TestTable",
        Columns: columns,
        PrimaryKeyColumns: ["Id"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        ReferencesOtherTables: false,
        RowCount: 100,
        AvgRowSizeBytes: 100,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static readonly IReadOnlyDictionary<string, string> NoConfig = new Dictionary<string, string>();

    [Fact]
    public void Resolve_DefaultLastUpdateColumnExistsAndIsDateTime_ReturnsIt()
    {
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false), new ColumnProfile("LastUpdate", "datetime", 8, false));

        Assert.Equal("LastUpdate", TimestampColumnResolver.Resolve(profile, NoConfig));
    }

    [Fact]
    public void Resolve_NoMatchingColumnAtAll_ReturnsNull()
    {
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false));

        Assert.Null(TimestampColumnResolver.Resolve(profile, NoConfig));
    }

    [Fact]
    public void Resolve_DefaultLastUpdateColumnExistsButWrongType_ReturnsNullWithoutThrowing()
    {
        // A naming coincidence (nobody configured anything for this table) shouldn't hard-fail
        // provisioning - treated the same as "column doesn't exist" so it falls through to
        // FullRescan/AllowFullRescanFor, not a thrown exception.
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false), new ColumnProfile("LastUpdate", "int", 4, false));

        Assert.Null(TimestampColumnResolver.Resolve(profile, NoConfig));
    }

    [Fact]
    public void Resolve_ExplicitlyConfiguredColumnExistsAndIsDateTime_ReturnsIt()
    {
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false), new ColumnProfile("ModifiedAt", "datetime2", 8, false));
        var config = new Dictionary<string, string> { ["TestTable"] = "ModifiedAt" };

        Assert.Equal("ModifiedAt", TimestampColumnResolver.Resolve(profile, config));
    }

    [Fact]
    public void Resolve_ExplicitlyConfiguredColumnDoesNotExist_ReturnsNull()
    {
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false));
        var config = new Dictionary<string, string> { ["TestTable"] = "ModifiedAt" };

        Assert.Null(TimestampColumnResolver.Resolve(profile, config));
    }

    [Fact]
    public void Resolve_ExplicitlyConfiguredColumnExistsButWrongType_Throws()
    {
        // This IS a real operator mistake (they named this column deliberately) - must fail
        // loudly at provisioning time, not silently fall back and fail every cycle forever.
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false));
        var config = new Dictionary<string, string> { ["TestTable"] = "Id" };

        var ex = Assert.Throws<InvalidOperationException>(() => TimestampColumnResolver.Resolve(profile, config));
        Assert.Contains("Id", ex.Message);
        Assert.Contains("int", ex.Message);
    }

    [Fact]
    public void Resolve_ConfiguredColumnLookupIsCaseInsensitive()
    {
        // Mirrors how SyncSettings.TimestampColumns is actually constructed
        // (StringComparer.OrdinalIgnoreCase) - Resolve relies on the dictionary's own
        // comparer rather than normalizing case itself.
        var profile = CreateProfile(new ColumnProfile("Id", "int", 4, false), new ColumnProfile("ModifiedAt", "datetime", 8, false));
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["testtable"] = "ModifiedAt" };

        Assert.Equal("ModifiedAt", TimestampColumnResolver.Resolve(profile, config));
    }
}
