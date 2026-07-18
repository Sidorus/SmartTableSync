-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: MERGE a full batch of ProductCatalogItem rows via TVP.
--
-- Prerequisite: 05_udt_types.sql must be run first.
--
-- The Azure Function passes one DataTable (up to BatchSize rows)
-- as @Rows instead of calling the SP once per row.
-- ============================================================

CREATE OR ALTER PROCEDURE [dbo].[sp_SyncProductCatalogItemBatch]
    @Rows [dbo].[ProductCatalogItemTableType] READONLY
AS
BEGIN
    SET NOCOUNT ON;

    MERGE [dbo].[ProductCatalogItem] WITH (HOLDLOCK) AS Target
    USING @Rows AS Source
    ON Target.[Sku] = Source.[Sku]
    WHEN MATCHED THEN
        UPDATE SET
            [ProductId]        = Source.[ProductId],
            [Description]      = Source.[Description],
            [ShortDescription] = Source.[ShortDescription],
            [NetPrice]         = Source.[NetPrice],
            [GrossPrice]       = Source.[GrossPrice],
            [CurrencyCode]     = Source.[CurrencyCode],
            [StockBalance]     = Source.[StockBalance],
            [IsAvailable]      = Source.[IsAvailable],
            [IsPublished]      = Source.[IsPublished],
            [Status]           = Source.[Status],
            [LastUpdate]       = Source.[LastUpdate],
            [CategoryCode]     = Source.[CategoryCode],
            [SupplierCode]     = Source.[SupplierCode],
            [Barcode]          = Source.[Barcode],
            [Weight]           = Source.[Weight],
            [CountryOfOrigin]  = Source.[CountryOfOrigin],
            [SearchIndexKey]   = Source.[SearchIndexKey]
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (
            [Sku], [ProductId], [Description], [ShortDescription],
            [NetPrice], [GrossPrice], [CurrencyCode], [StockBalance],
            [IsAvailable], [IsPublished], [Status], [LastUpdate],
            [CategoryCode], [SupplierCode], [Barcode], [Weight],
            [CountryOfOrigin], [SearchIndexKey]
        ) VALUES (
            Source.[Sku], Source.[ProductId], Source.[Description], Source.[ShortDescription],
            Source.[NetPrice], Source.[GrossPrice], Source.[CurrencyCode], Source.[StockBalance],
            Source.[IsAvailable], Source.[IsPublished], Source.[Status], Source.[LastUpdate],
            Source.[CategoryCode], Source.[SupplierCode], Source.[Barcode], Source.[Weight],
            Source.[CountryOfOrigin], Source.[SearchIndexKey]
        );
END;
GO
