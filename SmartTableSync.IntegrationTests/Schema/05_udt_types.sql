-- ============================================================
-- SNAPSHOT COPY for SmartTableSync.IntegrationTests only.
-- Source of truth is /sql/05_udt_types.sql in the repo root - this is NOT
-- auto-synced. If that script changes, re-copy it here by hand.
-- ============================================================

-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: User-Defined Table Types for TVP batch MERGE SPs.
--
-- Run BEFORE the corresponding SP files.
-- To update a UDT: drop the SP that uses it, drop the UDT,
-- recreate the UDT, recreate the SP.
-- ============================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ProductCatalogItem TVP type - matches dbo.ProductCatalogItem column set.
-- Used by sp_SyncProductCatalogItemBatch to merge a full batch in one call
-- instead of one stored procedure call per row.
IF NOT EXISTS (SELECT 1 FROM sys.types WHERE name = 'ProductCatalogItemTableType' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TYPE [dbo].[ProductCatalogItemTableType] AS TABLE (
        [Sku]              NVARCHAR(50)  NOT NULL,
        [ProductId]        INT           NULL,
        [Description]      NVARCHAR(MAX) NULL,
        [ShortDescription] NVARCHAR(MAX) NULL,
        [NetPrice]         REAL          NULL,
        [GrossPrice]       REAL          NULL,
        [CurrencyCode]     NVARCHAR(50)  NULL,
        [StockBalance]     REAL          NULL,
        [IsAvailable]      BIT           NULL,
        [IsPublished]      BIT           NULL,
        [Status]           NVARCHAR(1)   NULL,
        [LastUpdate]       DATETIME      NULL,
        [CategoryCode]     NVARCHAR(100) NULL,
        [SupplierCode]     NVARCHAR(100) NULL,
        [Barcode]          NVARCHAR(50)  NULL,
        [Weight]           REAL          NULL,
        [CountryOfOrigin]  NVARCHAR(100) NULL,
        [SearchIndexKey]   NVARCHAR(MAX) NULL
    );

    PRINT 'Created dbo.ProductCatalogItemTableType';
END
ELSE
    PRINT 'dbo.ProductCatalogItemTableType already exists';
GO
