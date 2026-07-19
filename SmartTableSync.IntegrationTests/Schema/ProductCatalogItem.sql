-- ============================================================
-- SNAPSHOT COPY for SmartTableSync.IntegrationTests only.
-- Source of truth is /sql/ProductCatalogItem.sql in the repo root - this is NOT
-- auto-synced. If that script changes, re-copy it here by hand.
-- ============================================================

/****** Object:  Table [dbo].[ProductCatalogItem] ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[ProductCatalogItem](
	[Sku] [nvarchar](50) NOT NULL,
	[ProductId] [int] NULL,
	[Description] [nvarchar](max) NULL,
	[ShortDescription] [nvarchar](max) NULL,
	[NetPrice] [real] NULL,
	[GrossPrice] [real] NULL,
	[CurrencyCode] [nvarchar](50) NULL,
	[StockBalance] [real] NULL,
	[IsAvailable] [bit] NULL,
	[IsPublished] [bit] NULL,
	[Status] [nvarchar](1) NULL,
	[LastUpdate] [datetime] NULL,
	[CategoryCode] [nvarchar](100) NULL,
	[SupplierCode] [nvarchar](100) NULL,
	[Barcode] [nvarchar](50) NULL,
	[Weight] [real] NULL,
	[CountryOfOrigin] [nvarchar](100) NULL,
	[SearchIndexKey] [nvarchar](max) NULL,
 CONSTRAINT [PK_ProductCatalogItem] PRIMARY KEY CLUSTERED
(
	[Sku] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
