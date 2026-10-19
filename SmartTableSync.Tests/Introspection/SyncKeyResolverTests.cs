using SmartTableSync.Introspection;
using Xunit;

namespace SmartTableSync.Tests.Introspection;

public class SyncKeyResolverTests
{
    private static readonly IReadOnlyList<ColumnProfile> Columns =
    [
        new("Id", "int", 4, false),
        new("Code", "nvarchar", 40, false),
        new("Region", "char", 2, false),
        new("Note", "nvarchar", 200, true),
        new("Body", "nvarchar", -1, false),
        new("RowVer", "timestamp", 8, false),
    ];

    private static (IReadOnlyList<string> Columns, SyncKeySource Source) Resolve(
        IReadOnlyList<string>? primaryKey = null,
        IReadOnlyList<string>? configured = null,
        IReadOnlyList<string>? uniqueIndex = null) =>
        SyncKeyResolver.Resolve("Orders", Columns, primaryKey ?? [], configured, uniqueIndex ?? []);

    [Fact]
    public void Resolve_PrimaryKey_WinsOverUniqueIndex()
    {
        var (columns, source) = Resolve(primaryKey: ["Id"], uniqueIndex: ["Code"]);

        Assert.Equal(["Id"], columns);
        Assert.Equal(SyncKeySource.PrimaryKey, source);
    }

    [Fact]
    public void Resolve_PrimaryKeyAndConfiguredKey_Throws()
    {
        // A configured key for a table with a PRIMARY KEY is a mistake an operator should hear
        // about, not a setting that silently does nothing.
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(primaryKey: ["Id"], configured: ["Code"]));

        Assert.Contains("Orders", ex.Message);
        Assert.Contains("PRIMARY KEY (Id)", ex.Message);
        Assert.Contains("SmartTableSync:KeyColumns", ex.Message);
    }

    [Fact]
    public void Resolve_NoPrimaryKey_UsesUniqueIndex()
    {
        var (columns, source) = Resolve(uniqueIndex: ["Region", "Code"]);

        Assert.Equal(["Region", "Code"], columns);
        Assert.Equal(SyncKeySource.UniqueIndex, source);
    }

    [Fact]
    public void Resolve_ConfiguredKey_WinsOverUniqueIndex_InConfiguredOrderAndTheTablesSpelling()
    {
        var (columns, source) = Resolve(configured: ["region", "CODE"], uniqueIndex: ["Id"]);

        Assert.Equal(["Region", "Code"], columns);
        Assert.Equal(SyncKeySource.Configured, source);
    }

    [Fact]
    public void Resolve_NoKeyAtAll_ReturnsEmptyKeyWithTheSourceSuchATableAlwaysHad()
    {
        // Empty key + PrimaryKey source = the profile a keyless table had before the fallbacks,
        // so its fingerprint doesn't change; SyncPlanHeuristic refuses it.
        var (columns, source) = Resolve();

        Assert.Empty(columns);
        Assert.Equal(SyncKeySource.PrimaryKey, source);
    }

    [Fact]
    public void Resolve_EmptyConfiguredList_IsIgnored()
    {
        var (columns, source) = Resolve(configured: [], uniqueIndex: ["Code"]);

        Assert.Equal(["Code"], columns);
        Assert.Equal(SyncKeySource.UniqueIndex, source);
    }

    [Theory]
    [InlineData("Missing", "column Missing doesn't exist")]
    [InlineData("Note", "column Note allows NULL")]
    [InlineData("Body", "column Body is a (max) type")]
    [InlineData("RowVer", "column RowVer is a rowversion column")]
    public void Resolve_InvalidConfiguredColumn_Throws(string column, string expectedProblem)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(configured: ["Id", column]));

        Assert.Contains("Orders", ex.Message);
        Assert.Contains(expectedProblem, ex.Message);
    }

    [Fact]
    public void Resolve_ConfiguredColumnListedTwice_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(configured: ["Code", "code"]));

        Assert.Contains("column Code is listed more than once", ex.Message);
    }

    [Fact]
    public void Resolve_SeveralInvalidConfiguredColumns_ReportsEveryProblem()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(configured: ["Missing", "Note"]));

        Assert.Contains("Missing", ex.Message);
        Assert.Contains("Note", ex.Message);
    }
}
