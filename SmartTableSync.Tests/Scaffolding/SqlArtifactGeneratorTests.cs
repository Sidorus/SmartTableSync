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

    // --- Rowversion shape: Tvp, with a rowversion/timestamp column — SQL Server rejects any
    // explicit value written into a rowversion column, so it must be excluded from generated
    // INSERT/UPDATE column lists regardless of merge strategy. ---

    private static TableProfile RowVersionTvpProfile() => new(
        TableName: "PricePoint",
        Columns:
        [
            new ColumnProfile("PricePointId", "int", 4, false),
            new ColumnProfile("Amount", "decimal", 9, false),
            new ColumnProfile("RowVer", "timestamp", 8, false),
        ],
        PrimaryKeyColumns: ["PricePointId"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 100,
        AvgRowSizeBytes: 200,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static SyncPlan RowVersionTvpPlan() => new(
        "PricePoint", MergeStrategy.Tvp, 200, SqlBulkCopyOptions.Default, 120, [], "PricePointTableType",
        ChangeTrackingColumn: "RowVer", IncrementalStrategy: IncrementalStrategy.RowVersion);

    [Fact]
    public void GenerateTvpUdtScript_WithRowVersionColumn_ExcludesItFromTvpType()
    {
        // A TVP column typed rowversion/timestamp rejects an explicit value exactly like a
        // real table column does - confirmed against a real database (SQL error 200: "data
        // for table-valued parameter doesn't conform to the table type") before this fix.
        var script = _generator.GenerateTvpUdtScript(RowVersionTvpProfile(), "PricePointTableType");

        Assert.DoesNotContain("RowVer", script);
        Assert.Contains("PricePointId", script);
        Assert.Contains("Amount", script);
    }

    [Fact]
    public void GenerateMergeProcedureScript_TvpWithRowVersionColumn_ExcludesItFromInsertAndUpdate()
    {
        var script = _generator.GenerateMergeProcedureScript(RowVersionTvpProfile(), RowVersionTvpPlan());

        var updateSetIndex = script.IndexOf("UPDATE SET", StringComparison.Ordinal);
        var insertIndex = script.IndexOf("INSERT (", StringComparison.Ordinal);
        var updateSetBlock = script[updateSetIndex..insertIndex];
        var insertBlock = script[insertIndex..];

        Assert.DoesNotContain("[RowVer]", updateSetBlock);
        Assert.DoesNotContain("[RowVer]", insertBlock);
        // Everything else still makes it in.
        Assert.Contains("[Amount] = Source.[Amount]", updateSetBlock);
        Assert.Contains("[PricePointId]", insertBlock);
    }

    [Fact]
    public void GenerateMergeProcedureScript_RowByRowWithRowVersionColumn_ExcludesItFromParametersInsertAndUpdate()
    {
        var plan = RowVersionTvpPlan() with { MergeStrategy = MergeStrategy.RowByRow, TvpTypeName = null };
        var script = _generator.GenerateMergeProcedureScript(RowVersionTvpProfile(), plan);

        Assert.DoesNotContain("@RowVer", script);
        Assert.DoesNotContain("[RowVer]", script);
        Assert.Contains("@Amount", script);
    }

    // --- GenerateRowVersionSetupScript: parent-side, hand-applied-only DDL ---

    private static TableProfile NoRowVersionProfile() => new(
        TableName: "OrderStatus",
        Columns:
        [
            new ColumnProfile("OrderStatusId", "int", 4, false),
            new ColumnProfile("Name", "nvarchar", 200, false),
        ],
        PrimaryKeyColumns: ["OrderStatusId"],
        IdentityColumn: "OrderStatusId",
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 100,
        AvgRowSizeBytes: 100,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    [Fact]
    public void GenerateRowVersionSetupScript_NoExistingRowVersionColumn_IncludesAlterTableAndIndex()
    {
        var script = _generator.GenerateRowVersionSetupScript(NoRowVersionProfile());

        Assert.Contains("ALTER TABLE [dbo].[OrderStatus] ADD [RowVer] ROWVERSION NOT NULL;", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_OrderStatus_RowVer] ON [dbo].[OrderStatus] ([RowVer]);", script);
    }

    [Fact]
    public void GenerateRowVersionSetupScript_CustomColumnName_UsesItThroughout()
    {
        var script = _generator.GenerateRowVersionSetupScript(NoRowVersionProfile(), columnName: "Version");

        Assert.Contains("ADD [Version] ROWVERSION NOT NULL;", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_OrderStatus_Version] ON [dbo].[OrderStatus] ([Version]);", script);
        Assert.DoesNotContain("[RowVer]", script);
    }

    [Fact]
    public void GenerateRowVersionSetupScript_AlreadyHasRowVersionColumn_SkipsAlterTableButStillIndexes()
    {
        // RowVersionTvpProfile already has a "RowVer" timestamp column - the ALTER TABLE
        // would fail outright if generated (SQL Server rejects two rowversion columns per
        // table), so it must be skipped, with only the index generated against that column.
        var script = _generator.GenerateRowVersionSetupScript(RowVersionTvpProfile());

        Assert.DoesNotContain("ALTER TABLE", script);
        Assert.Contains("already has a rowversion column ('RowVer')", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_PricePoint_RowVer] ON [dbo].[PricePoint] ([RowVer]);", script);
    }

    // --- GenerateChangeTrackingSetupScript: parent-side, hand-applied-only DDL ---

    [Fact]
    public void GenerateChangeTrackingSetupScript_EnablesDatabaseAndTableAndGrantsViewChangeTracking()
    {
        var script = _generator.GenerateChangeTrackingSetupScript(NoRowVersionProfile());

        Assert.Contains("ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);", script);
        Assert.Contains("ALTER TABLE [dbo].[OrderStatus] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);", script);
        Assert.Contains("GRANT VIEW CHANGE TRACKING ON [dbo].[OrderStatus] TO [smarttablesync-runtime];", script);
    }

    [Fact]
    public void GenerateChangeTrackingSetupScript_EnableStatementsAreGuardedByIfNotExists()
    {
        var script = _generator.GenerateChangeTrackingSetupScript(NoRowVersionProfile());

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())", script);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.OrderStatus'))", script);
    }

    [Fact]
    public void GenerateChangeTrackingSetupScript_CustomRetentionAndPrincipal_AreUsed()
    {
        var script = _generator.GenerateChangeTrackingSetupScript(NoRowVersionProfile(), retentionDays: 7, runtimePrincipal: "sync_rt");

        Assert.Contains("CHANGE_RETENTION = 7 DAYS", script);
        Assert.Contains("TO [sync_rt];", script);
    }

    [Fact]
    public void GenerateChangeTrackingSetupScript_TableWithoutPrimaryKey_Throws()
    {
        var keyless = NoRowVersionProfile() with { PrimaryKeyColumns = [] };

        Assert.Throws<InvalidOperationException>(() => _generator.GenerateChangeTrackingSetupScript(keyless));
    }

    [Fact]
    public void GenerateRowVersionSetupScript_EveryStatementIsGuardedByIfNotExists()
    {
        var script = _generator.GenerateRowVersionSetupScript(NoRowVersionProfile());

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.OrderStatus') AND name = 'RowVer')", script);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.OrderStatus') AND name = 'IX_OrderStatus_RowVer')", script);
    }

    // --- Identity + Tvp shape: no incoming FKs, small rows -> Tvp, but with an identity PK
    // (a real, reachable heuristic outcome none of the 3 example tables happen to hit) ---

    private static TableProfile IdentityTvpProfile() => new(
        TableName: "LookupCode",
        Columns:
        [
            new ColumnProfile("LookupCodeId", "int", 4, false),
            new ColumnProfile("Description", "nvarchar", 400, true),
        ],
        PrimaryKeyColumns: ["LookupCodeId"],
        IdentityColumn: "LookupCodeId",
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 100,
        AvgRowSizeBytes: 500,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static SyncPlan IdentityTvpPlan() => new(
        "LookupCode", MergeStrategy.Tvp, 200, SqlBulkCopyOptions.KeepIdentity, 120, [], "LookupCodeTableType");

    [Fact]
    public void GenerateMergeProcedureScript_TvpWithIdentity_WrapsInIdentityInsert()
    {
        // Regression test for the confirmed bug: GenerateRowByRowMergeProcedure already
        // wrapped identity-column inserts in SET IDENTITY_INSERT ON/OFF, but
        // GenerateTvpMergeProcedure had no such wrapping at all - any table with both an
        // identity column and the Tvp merge strategy failed every incremental sync with
        // SQL error 544 (explicit identity value with IDENTITY_INSERT OFF).
        var script = _generator.GenerateMergeProcedureScript(IdentityTvpProfile(), IdentityTvpPlan());

        Assert.Contains("SET IDENTITY_INSERT [dbo].[LookupCode] ON;", script);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[LookupCode] OFF;", script);
        Assert.Contains("CREATE OR ALTER PROCEDURE [dbo].[sp_SyncLookupCodeBatch]", script);
        Assert.Contains("@Rows [dbo].[LookupCodeTableType] READONLY", script);

        // IDENTITY_INSERT must wrap the MERGE, not just decorate the procedure body randomly.
        var identityOnIndex = script.IndexOf("SET IDENTITY_INSERT [dbo].[LookupCode] ON;", StringComparison.Ordinal);
        var mergeIndex = script.IndexOf("MERGE [dbo].[LookupCode]", StringComparison.Ordinal);
        var identityOffIndex = script.IndexOf("SET IDENTITY_INSERT [dbo].[LookupCode] OFF;", StringComparison.Ordinal);
        Assert.True(identityOnIndex < mergeIndex);
        Assert.True(mergeIndex < identityOffIndex);
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

    // --- GenerateInfrastructureScript: table-agnostic, no TableProfile involved ---

    [Fact]
    public void GenerateInfrastructureScript_CreatesAllThreeTrackingTables()
    {
        var script = _generator.GenerateInfrastructureScript();

        Assert.Contains("CREATE TABLE [dbo].[CustomSyncState]", script);
        Assert.Contains("CREATE TABLE [dbo].[SyncBatchLog]", script);
        Assert.Contains("CREATE TABLE [dbo].[SyncTableConfig]", script);
    }

    [Fact]
    public void GenerateInfrastructureScript_DoesNotHardcodeAnyTableName()
    {
        // The earlier hand-authored script seeded CustomSyncState with rows for this repo's
        // 3 example tables - a real consumer's tables aren't ProductAttributeValue/
        // ProductCatalogItem/DocumentAsset, so this generator must never bake those in.
        var script = _generator.GenerateInfrastructureScript();

        Assert.DoesNotContain("ProductAttributeValue", script);
        Assert.DoesNotContain("ProductCatalogItem", script);
        Assert.DoesNotContain("DocumentAsset", script);
    }

    [Fact]
    public void GenerateInfrastructureScript_SyncTableConfig_IncludesChangeTrackingColumns()
    {
        var script = _generator.GenerateInfrastructureScript();

        Assert.Contains("[ChangeTrackingColumn]", script);
        Assert.Contains("[IncrementalStrategy]", script);
    }

    [Fact]
    public void GenerateInfrastructureScript_EveryCreateTableIsGuardedByIfNotExists()
    {
        var script = _generator.GenerateInfrastructureScript();

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomSyncState'", script);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncBatchLog'", script);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncTableConfig'", script);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SyncTableConfig') AND name = 'ChangeTrackingColumn'", script);
    }
}
