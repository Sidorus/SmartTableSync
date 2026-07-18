using Microsoft.Data.SqlClient;
using SmartTableSync.Introspection;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using Xunit;

namespace SmartTableSync.Tests.Scaffolding;

public class SqlArtifactGeneratorTests
{
    private readonly SqlArtifactGenerator _generator = new();

    // --- ProductAttributeValue shape: single IDENTITY int PK, RowByRow, no TVP ---

    private static TableProfile ProductAttributeValueProfile() => new(
        TableName: "ProductAttributeValue",
        Columns:
        [
            new ColumnProfile("AttributeValueId", "int", 4, false),
            new ColumnProfile("AttributeId", "int", 4, false),
            new ColumnProfile("Description", "nvarchar", -1, false),
            new ColumnProfile("Slug", "nvarchar", 400, true),
        ],
        PrimaryKeyColumns: ["AttributeValueId"],
        IdentityColumn: "AttributeValueId",
        IndexedLobColumns: ["Description"],
        HasIncomingForeignKeys: true,
        RowCount: 100,
        AvgRowSizeBytes: 500,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static SyncPlan ProductAttributeValuePlan() => new(
        "ProductAttributeValue", MergeStrategy.RowByRow, 500, SqlBulkCopyOptions.KeepIdentity, 60, [547], null);

    [Fact]
    public void GenerateCreateTableScript_IdentityColumn_IncludesIdentityClause()
    {
        var script = _generator.GenerateCreateTableScript(ProductAttributeValueProfile());

        Assert.Contains("[AttributeValueId] [int] IDENTITY(1,1) NOT NULL", script);
    }

    [Fact]
    public void GenerateCreateTableScript_MaxLengthColumn_UsesMaxKeyword()
    {
        var script = _generator.GenerateCreateTableScript(ProductAttributeValueProfile());

        Assert.Contains("[Description] [nvarchar](max) NOT NULL", script);
    }

    [Fact]
    public void GenerateCreateTableScript_SizedNvarchar_HalvesByteLengthToCharacterLength()
    {
        // sys.columns reports 400 bytes for nvarchar(200) - script must show (200), not (400).
        var script = _generator.GenerateCreateTableScript(ProductAttributeValueProfile());

        Assert.Contains("[Slug] [nvarchar](200) NULL", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_RowByRowWithIdentity_WrapsInIdentityInsert()
    {
        var script = _generator.GenerateMergeProcedureScript(ProductAttributeValueProfile(), ProductAttributeValuePlan());

        Assert.Contains("SET IDENTITY_INSERT [dbo].[ProductAttributeValue] ON;", script);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[ProductAttributeValue] OFF;", script);
        Assert.Contains("CREATE OR ALTER PROCEDURE [dbo].[sp_SyncProductAttributeValue]", script);
        Assert.Contains("@AttributeValueId [int]", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_RowByRow_ExcludesPrimaryKeyFromUpdateSet()
    {
        var script = _generator.GenerateMergeProcedureScript(ProductAttributeValueProfile(), ProductAttributeValuePlan());

        // The PK column must appear in the join and INSERT list, but not in UPDATE SET.
        var updateSetIndex = script.IndexOf("UPDATE SET", StringComparison.Ordinal);
        var insertIndex = script.IndexOf("INSERT (", StringComparison.Ordinal);
        var updateSetBlock = script[updateSetIndex..insertIndex];

        Assert.DoesNotContain("[AttributeValueId] = Source.[AttributeValueId]", updateSetBlock);
        Assert.Contains("[Description] = Source.[Description]", updateSetBlock);
    }

    // --- ProductCatalogItem shape: single string PK, Tvp, no identity ---

    private static TableProfile ProductCatalogItemProfile() => new(
        TableName: "ProductCatalogItem",
        Columns:
        [
            new ColumnProfile("Sku", "nvarchar", 200, false), // sys.columns reports bytes: nvarchar(100) = 200
            new ColumnProfile("Description", "nvarchar", -1, true),
        ],
        PrimaryKeyColumns: ["Sku"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 1000,
        AvgRowSizeBytes: 2000,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static SyncPlan ProductCatalogItemPlan() => new(
        "ProductCatalogItem", MergeStrategy.Tvp, 200, SqlBulkCopyOptions.Default, 120, [], "ProductCatalogItemTableType");

    [Fact]
    public void GenerateCreateTableScript_StringPrimaryKey_NoIdentityClause()
    {
        var script = _generator.GenerateCreateTableScript(ProductCatalogItemProfile());

        Assert.DoesNotContain("IDENTITY", script);
        Assert.Contains("[Sku] [nvarchar](100) NOT NULL", script);
    }

    [Fact]
    public void GenerateTvpUdtScript_MatchesTypeNameAndColumns()
    {
        var script = _generator.GenerateTvpUdtScript(ProductCatalogItemProfile(), "ProductCatalogItemTableType");

        Assert.Contains("CREATE TYPE [dbo].[ProductCatalogItemTableType] AS TABLE", script);
        Assert.Contains("[Sku]", script);
        // Case/bracket style is a cosmetic generator convention, not something that needs to
        // match the original scripts exactly — just confirm the type and length made it in.
        Assert.Contains("nvarchar", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(100)", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_Tvp_UsesTableValuedParameter()
    {
        var script = _generator.GenerateMergeProcedureScript(ProductCatalogItemProfile(), ProductCatalogItemPlan());

        Assert.Contains("CREATE OR ALTER PROCEDURE [dbo].[sp_SyncProductCatalogItemBatch]", script);
        Assert.Contains("@Rows [dbo].[ProductCatalogItemTableType] READONLY", script);
        Assert.Contains("USING @Rows AS Source", script);
        Assert.DoesNotContain("IDENTITY_INSERT", script);
    }

    // --- DocumentAsset shape: composite PK, RowByRow, no identity ---

    private static TableProfile DocumentAssetProfile() => new(
        TableName: "DocumentAsset",
        Columns:
        [
            new ColumnProfile("DocumentId", "int", 4, false),
            new ColumnProfile("LanguageCode", "nvarchar", 100, false),
            new ColumnProfile("FileContent", "varbinary", -1, true),
        ],
        PrimaryKeyColumns: ["DocumentId", "LanguageCode"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 500,
        AvgRowSizeBytes: 1000,
        MaxLobColumnSizeBytes: new Dictionary<string, long> { ["FileContent"] = 52_000_000 });

    private static SyncPlan DocumentAssetPlan() => new(
        "DocumentAsset", MergeStrategy.RowByRow, 5, SqlBulkCopyOptions.Default, 300, [], null);

    [Fact]
    public void GenerateCreateTableScript_CompositePrimaryKey_ListsBothColumnsInConstraint()
    {
        var script = _generator.GenerateCreateTableScript(DocumentAssetProfile());

        Assert.Contains("[DocumentId] ASC", script);
        Assert.Contains("[LanguageCode] ASC", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_CompositePrimaryKey_JoinsOnBothColumns()
    {
        var script = _generator.GenerateMergeProcedureScript(DocumentAssetProfile(), DocumentAssetPlan());

        Assert.Contains("Target.[DocumentId] = Source.[DocumentId] AND Target.[LanguageCode] = Source.[LanguageCode]", script);
        Assert.DoesNotContain("IDENTITY_INSERT", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_VarbinaryMax_UsesMaxKeyword()
    {
        var script = _generator.GenerateMergeProcedureScript(DocumentAssetProfile(), DocumentAssetPlan());

        Assert.Contains("@FileContent [varbinary](max)", script);
    }
}
