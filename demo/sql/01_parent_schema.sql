-- ============================================================
-- PARENT database: the three demo tables and their seed rows.
-- Each table is shaped so SmartTableSync picks a different incremental strategy for it:
--
--   Product   SQL Server Change Tracking  (enabled by the scaffolded *_ChangeTrackingSetup.sql)
--   Customer  RowVersion                  (has a rowversion column)
--   Document  Timestamp                   (LastUpdate column, the default name)
--
-- Document also has a composite key and a few rows over 1 MB, so it's merged row by row
-- instead of in table-valued-parameter batches.
-- No IDENTITY columns: the runtime identity would need ALTER on the child table to bulk copy
-- with KeepIdentity.
-- Safe to re-run: existing tables are left as they are.
-- ============================================================

IF OBJECT_ID(N'dbo.Product', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Product (
        ProductId   INT            NOT NULL,
        Sku         NVARCHAR(50)   NOT NULL,
        Name        NVARCHAR(200)  NOT NULL,
        Price       DECIMAL(10, 2) NOT NULL,
        Description NVARCHAR(MAX)  NULL,
        CONSTRAINT PK_Product PRIMARY KEY CLUSTERED (ProductId)
    );
END
GO

IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customer (
        CustomerId INT           NOT NULL,
        Name       NVARCHAR(200) NOT NULL,
        Email      NVARCHAR(200) NULL,
        City       NVARCHAR(100) NULL,
        RowVer     ROWVERSION    NOT NULL,
        CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId)
    );

    CREATE NONCLUSTERED INDEX IX_Customer_RowVer ON dbo.Customer (RowVer);
END
GO

IF OBJECT_ID(N'dbo.Document', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Document (
        DocumentId   INT             NOT NULL,
        LanguageCode NVARCHAR(10)    NOT NULL,
        Title        NVARCHAR(250)   NOT NULL,
        Content      VARBINARY(MAX)  NULL,
        -- UTC, like the clock SmartTableSync reads (GETUTCDATE() on the parent)
        LastUpdate   DATETIME        NOT NULL CONSTRAINT DF_Document_LastUpdate DEFAULT (GETUTCDATE()),
        CONSTRAINT PK_Document PRIMARY KEY CLUSTERED (DocumentId, LanguageCode)
    );

    CREATE NONCLUSTERED INDEX IX_Document_LastUpdate ON dbo.Document (LastUpdate);
END
GO

-- Seed rows, only into empty tables. Numbers come from a cross join of catalog views, which
-- works on every Azure SQL compatibility level.
IF NOT EXISTS (SELECT 1 FROM dbo.Product)
BEGIN
    WITH Numbers AS (
        SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
        FROM sys.all_columns a CROSS JOIN sys.all_columns b
    )
    INSERT INTO dbo.Product (ProductId, Sku, Name, Price, Description)
    SELECT N,
           CONCAT(N'SKU-', FORMAT(N, '00000')),
           CONCAT(N'Demo product ', N),
           CAST(10 + (N % 90) + 0.99 AS DECIMAL(10, 2)),
           REPLICATE(CONCAT(N'Description of product ', N, N'. '), 5)
    FROM Numbers;
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Customer)
BEGIN
    WITH Numbers AS (
        SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
        FROM sys.all_columns a CROSS JOIN sys.all_columns b
    )
    INSERT INTO dbo.Customer (CustomerId, Name, Email, City)
    SELECT N,
           CONCAT(N'Customer ', N),
           CONCAT(N'customer', N, N'@example.com'),
           CHOOSE(1 + N % 5, N'Stockholm', N'Gothenburg', N'Malmö', N'Uppsala', N'Linköping')
    FROM Numbers;
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Document)
BEGIN
    WITH Numbers AS (
        SELECT TOP (20) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
        FROM sys.all_columns
    )
    INSERT INTO dbo.Document (DocumentId, LanguageCode, Title, Content)
    SELECT n.N,
           l.LanguageCode,
           CONCAT(N'Document ', n.N, N' (', l.LanguageCode, N')'),
           -- Documents 1-3 are about 1.5 MB, over SmartTableSync's 1 MB large-row threshold;
           -- the rest are about 20 KB.
           CAST(REPLICATE(CAST('0123456789' AS VARCHAR(MAX)),
                          CASE WHEN n.N <= 3 THEN 150000 ELSE 2000 END) AS VARBINARY(MAX))
    FROM Numbers n
    CROSS JOIN (VALUES (N'en'), (N'sv')) AS l (LanguageCode);
END
GO
