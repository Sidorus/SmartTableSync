using System.Data;
using System.Globalization;
using SmartTableSync.Syncers.Keys;
using SmartTableSync.Syncers.Sql;
using Xunit;

namespace SmartTableSync.Tests.Syncers;

public class CompositeKeyTests
{
    private static DataRow CreateRow(params (string Column, object Value)[] values)
    {
        var table = new DataTable();
        foreach (var (column, value) in values)
            table.Columns.Add(column, value.GetType());

        var row = table.NewRow();
        foreach (var (column, value) in values)
            row[column] = value;
        table.Rows.Add(row);
        return row;
    }

    [Fact]
    public void Extract_SingleColumn_ReturnsPlainValue()
    {
        var row = CreateRow(("AttributeValueId", 45231));

        Assert.Equal("45231", CompositeKey.Extract(row, ["AttributeValueId"]));
    }

    [Fact]
    public void Extract_TwoColumns_MatchesDocumentAssetsOriginalFormat()
    {
        // Must reproduce DocumentAsset's original ExtractKey: $"{row["DocumentId"]}|{row["Language"]}"
        var row = CreateRow(("DocumentId", 206), ("LanguageCode", "en"));

        Assert.Equal("206|en", CompositeKey.Extract(row, ["DocumentId", "LanguageCode"]));
    }

    [Fact]
    public void Parse_TwoColumns_SplitsBackIntoOriginalComponents()
    {
        var parts = CompositeKey.Parse("206|en", ["DocumentId", "LanguageCode"]);

        Assert.Equal(["206", "en"], parts);
    }

    [Fact]
    public void Parse_ValueContainingDelimiterInLastComponent_StopsSplittingAtExpectedColumnCount()
    {
        // Split with a count limit means a delimiter character inside the LAST column's
        // value doesn't over-split — matches DocumentAsset's original Split('|', 2) behavior.
        var parts = CompositeKey.Parse("206|en|extra", ["DocumentId", "LanguageCode"]);

        Assert.Equal(["206", "en|extra"], parts);
    }

    [Fact]
    public void Parse_WrongComponentCount_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => CompositeKey.Parse("206", ["DocumentId", "LanguageCode"]));
    }

    [Fact]
    public void Extract_DateTimeColumn_RoundTripsViaParseKeyComponentRegardlessOfCurrentCulture()
    {
        // Regression test for the confirmed bug: Extract used to call row[c]!.ToString() with
        // no culture specified (CurrentCulture), while SqlTypeMapping.ParseKeyComponent always
        // parses with InvariantCulture - on a host whose culture isn't invariant/en-US-
        // compatible, a DateTime-typed key could format one way and fail to parse (or parse
        // to the wrong value) the other way. de-DE formats dates day-first, distinct from
        // invariant's month-first "MM/dd/yyyy" default ToString() would otherwise have used.
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var value = new DateTime(2026, 3, 5, 14, 30, 0);
            var row = CreateRow(("ModifiedAt", value));

            var extracted = CompositeKey.Extract(row, ["ModifiedAt"]);
            var parsed = SqlTypeMapping.ParseKeyComponent(extracted, "datetime2");

            Assert.Equal(value, parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
