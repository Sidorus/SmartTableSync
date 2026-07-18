using SmartTableSync.Syncers;
using Xunit;

namespace SmartTableSync.Tests.Syncers;

public class KeysetPredicateBuilderTests
{
    [Fact]
    public void Build_SingleColumn_ProducesSimpleGreaterThan()
    {
        // ProductAttributeValue / ProductCatalogItem shape — one PK column.
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
        // Original: "SELECT [Sku] FROM [dbo].[ProductCatalogItem]" — no CAST at all.
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["Sku"], _ => true);

        Assert.Equal("[Sku]", expr);
    }

    [Fact]
    public void BuildKeySelectExpression_SingleIntColumn_MatchesProductAttributeValuesOriginal()
    {
        // Original: "SELECT CAST([AttributeValueId] AS NVARCHAR(20)) FROM ..."
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(["AttributeValueId"], _ => false);

        Assert.Equal("CAST([AttributeValueId] AS NVARCHAR(20))", expr);
    }

    [Fact]
    public void BuildKeySelectExpression_IntThenString_MatchesDocumentAssetsOriginal()
    {
        // Original: "CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]"
        var expr = KeysetPredicateBuilder.BuildKeySelectExpression(
            ["DocumentId", "LanguageCode"],
            column => column == "LanguageCode");

        Assert.Equal("CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]", expr);
    }
}
