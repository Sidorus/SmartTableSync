using SmartTableSync.Introspection;
using Xunit;

namespace SmartTableSync.Tests.Introspection;

public class TableProfileTests
{
    private static TableProfile CreateProfile(params ColumnProfile[] columns) => new(
        TableName: "TestTable",
        Columns: columns,
        PrimaryKeyColumns: ["Id"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 0,
        AvgRowSizeBytes: 0,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    [Fact]
    public void RowVersionColumn_TableHasTimestampColumn_ReturnsItsName()
    {
        var profile = CreateProfile(
            new ColumnProfile("Id", "int", 4, false),
            new ColumnProfile("RowVer", "timestamp", 8, false));

        Assert.Equal("RowVer", profile.RowVersionColumn);
    }

    [Fact]
    public void RowVersionColumn_NoTimestampColumn_ReturnsNull()
    {
        var profile = CreateProfile(
            new ColumnProfile("Id", "int", 4, false),
            new ColumnProfile("LastUpdate", "datetime", 8, true));

        Assert.Null(profile.RowVersionColumn);
    }

    [Fact]
    public void RowVersionColumn_MatchIsCaseInsensitive()
    {
        // sys.types always reports "timestamp" lowercase in practice, but detection
        // shouldn't silently depend on that casing happening to hold.
        var profile = CreateProfile(new ColumnProfile("RowVer", "TIMESTAMP", 8, false));

        Assert.Equal("RowVer", profile.RowVersionColumn);
    }
}
