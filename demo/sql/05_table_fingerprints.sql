-- ============================================================
-- Run against BOTH the parent and the child database. When the sync has caught up, both
-- return the same row counts and checksums for every table.
-- The checksum covers the key and the business columns; Customer.RowVer is left out because
-- the parent generates its own value.
-- ============================================================

SET NOCOUNT ON;

SELECT N'Product' AS TableName,
       COUNT(*) AS [Rows],
       CHECKSUM_AGG(BINARY_CHECKSUM(ProductId, Sku, Name, Price, Description)) AS [Checksum]
FROM dbo.Product
UNION ALL
SELECT N'Customer',
       COUNT(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(CustomerId, Name, Email, City))
FROM dbo.Customer
UNION ALL
SELECT N'Document',
       COUNT(*),
       CHECKSUM_AGG(BINARY_CHECKSUM(DocumentId, LanguageCode, Title, DATALENGTH(Content), LastUpdate))
FROM dbo.Document
ORDER BY TableName;
