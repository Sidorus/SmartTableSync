using SmartTableSync.Sql.Keys;
using Xunit;

namespace SmartTableSync.Tests.Syncers;

public class KeysetPredicateBuilderTests
{
    [Fact]
    public void Build_SingleColumn_ProducesSimpleGreaterThan()
    {
        // ProductAttributeValue / ProductCatalogItem shape - one PK column.
        var predicate = KeysetPredicateBuilder.Build(["AttributeValueId"], ["@Key0"]);

        Assert.Equal("[AttributeValueId] > @Key0", predicate);
    }

    [Fact]
    public void Build_TwoColumns_MatchesDocumentAssetsOriginalHandWrittenClause()
    {
        // Must reproduce DocumentAsset's exact original predicate:
        //   [DocumentId] > @LastDocId
        //   OR ([DocumentId] = @LastDocId AND [LanguageCode] > @LastLang)
        var predicate = KeysetPredicateBuilder.Build(
            ["DocumentId", "LanguageCode"],
            ["@LastDocId", "@LastLang"]);

        Assert.Equal(
            "[DocumentId] > @LastDocId\n    OR ([DocumentId] = @LastDocId AND [LanguageCode] > @LastLang)",
            predicate);
    }

    [Fact]
    public void Build_ThreeColumns_ChainsAllPriorColumnsAsEquality()
    {
        var predicate = KeysetPredicateBuilder.Build(
            ["A", "B", "C"],
            ["@p0", "@p1", "@p2"]);

        Assert.Equal(
            "[A] > @p0\n    OR ([A] = @p0 AND [B] > @p1)\n    OR ([A] = @p0 AND [B] = @p1 AND [C] > @p2)",
            predicate);
    }

    [Fact]
    public void Build_EmptyColumnList_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeysetPredicateBuilder.Build([], []));
    }

    [Fact]
    public void Build_MismatchedColumnAndParameterCounts_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeysetPredicateBuilder.Build(["A", "B"], ["@p0"]));
    }

    [Fact]
    public void BuildOrderBy_SingleColumn()
    {
        Assert.Equal("[Sku]", KeysetPredicateBuilder.BuildOrderBy(["Sku"]));
    }

    [Fact]
    public void BuildOrderBy_TwoColumns_MatchesDocumentAssetsOriginalOrderBy()
    {
        Assert.Equal("[DocumentId], [LanguageCode]", KeysetPredicateBuilder.BuildOrderBy(["DocumentId", "LanguageCode"]));
    }

    [Fact]
    public void BuildKeySelectExpression_SingleStringColumn_MatchesProductCatalogItemsOriginal()
    {
        // Original: "SELECT [Sku] FROM [dbo].[ProductCatalogItem]" - no CAST at all.
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["Sku"], _ => "nvarchar");

        Assert.Equal("[Sku]", expr);
    }

    [Fact]
    public void BuildKeySelectExpression_SingleIntColumn_MatchesProductAttributeValuesOriginal()
    {
        // Original: "SELECT CAST([AttributeValueId] AS NVARCHAR(20)) FROM ..."
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["AttributeValueId"], _ => "int");

        Assert.Equal("CAST([AttributeValueId] AS NVARCHAR(20))", expr);
    }

    [Fact]
    public void BuildKeySelectExpression_IntThenString_MatchesDocumentAssetsOriginal()
    {
        // Original: "CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]"
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(
            ["DocumentId", "LanguageCode"],
            column => column == "LanguageCode" ? "nvarchar" : "int");

        Assert.Equal("CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]", expr);
    }

    [Theory]
    [InlineData("bigint", "CAST([Id] AS NVARCHAR(20))")]
    [InlineData("decimal", "CONVERT(NVARCHAR(40), [Id])")]
    [InlineData("numeric", "CONVERT(NVARCHAR(40), [Id])")]
    [InlineData("uniqueidentifier", "CONVERT(NVARCHAR(36), [Id])")]
    [InlineData("datetime", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("datetime2", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("smalldatetime", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("date", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("datetimeoffset", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("time", "CONVERT(NVARCHAR(40), [Id], 126)")]
    [InlineData("DateTime2", "CONVERT(NVARCHAR(40), [Id], 126)")]
    public void BuildKeySelectExpression_NonIntegerNonStringColumn_UsesLosslessConversion(string sqlTypeName, string expected)
    {
        // Regression test: every non-string column used to be CAST to NVARCHAR(20), which
        // overflows for uniqueidentifier and cuts datetime2's fractional seconds, so different
        // keys compared equal and orphaned child rows were never deleted.
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["Id"], _ => sqlTypeName);

        Assert.Equal(expected, expr);
    }

    [Fact]
    public void BuildKeySelectExpression_GuidThenDateTime2_JoinsLosslessConversionsWithDelimiter()
    {
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(
            ["TenantId", "ValidFrom"],
            column => column == "TenantId" ? "uniqueidentifier" : "datetime2");

        Assert.Equal("CONVERT(NVARCHAR(36), [TenantId]) + '|' + CONVERT(NVARCHAR(40), [ValidFrom], 126)", expr);
    }

    [Fact]
    public void BuildKeySelectExpression_ColumnNameWithClosingBracket_IsQuoted()
    {
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["Odd]Id"], _ => "uniqueidentifier");

        Assert.Equal("CONVERT(NVARCHAR(36), [Odd]]Id])", expr);
    }

    [Theory]
    [InlineData("float")]
    [InlineData("money")]
    [InlineData("varbinary")]
    public void BuildKeySelectExpression_TypeWithoutParseableKeyForm_ThrowsNotSupported(string sqlTypeName)
    {
        Assert.Throws<NotSupportedException>(() => KeysetPredicateBuilder.BuildKeySelectExpression(["Id"], _ => sqlTypeName));
    }
}
