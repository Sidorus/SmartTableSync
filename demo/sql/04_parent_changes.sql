-- ============================================================
-- PARENT database: inserts, updates and deletes for the next sync run to pick up.
-- Safe to re-run: new keys continue after the current highest key.
--
-- When the deletes reach the child depends on the strategy:
--   Product  (Change Tracking)  on the next run, like inserts and updates
--   Customer (RowVersion) and Document (Timestamp)  at the next delete reconciliation,
--            which runs on a fresh Function instance's first sync and then every hour
-- ============================================================

DECLARE @nextProduct INT = (SELECT ISNULL(MAX(ProductId), 0) FROM dbo.Product);

INSERT INTO dbo.Product (ProductId, Sku, Name, Price, Description)
VALUES (@nextProduct + 1, CONCAT(N'SKU-NEW-', @nextProduct + 1), N'New product A', 19.99, N'Added by 04_parent_changes.sql'),
       (@nextProduct + 2, CONCAT(N'SKU-NEW-', @nextProduct + 2), N'New product B', 29.99, N'Added by 04_parent_changes.sql');

UPDATE dbo.Product SET Price = Price + 1 WHERE ProductId BETWEEN 1 AND 10;

DELETE FROM dbo.Product WHERE ProductId IN (SELECT TOP (2) ProductId FROM dbo.Product WHERE ProductId > 100 ORDER BY ProductId);
GO

DECLARE @nextCustomer INT = (SELECT ISNULL(MAX(CustomerId), 0) FROM dbo.Customer);

INSERT INTO dbo.Customer (CustomerId, Name, Email, City)
VALUES (@nextCustomer + 1, N'New customer', N'new.customer@example.com', N'Umeå');

-- No LastUpdate column needed: the rowversion column changes on every update by itself
UPDATE dbo.Customer SET Email = CONCAT(N'changed', CustomerId, N'@example.com') WHERE CustomerId BETWEEN 1 AND 5;

DELETE FROM dbo.Customer WHERE CustomerId IN (SELECT TOP (1) CustomerId FROM dbo.Customer WHERE CustomerId > 100 ORDER BY CustomerId);
GO

DECLARE @nextDocument INT = (SELECT ISNULL(MAX(DocumentId), 0) FROM dbo.Document);

INSERT INTO dbo.Document (DocumentId, LanguageCode, Title, Content)
VALUES (@nextDocument + 1, N'en', N'New document', CAST(N'New content' AS VARBINARY(MAX)));

-- The Timestamp strategy only sees updates that also move LastUpdate
UPDATE dbo.Document SET Title = CONCAT(Title, N' (edited)'), LastUpdate = GETUTCDATE()
WHERE DocumentId BETWEEN 4 AND 5 AND LanguageCode = N'en';

DELETE FROM dbo.Document WHERE DocumentId = 10 AND LanguageCode = N'sv';
GO
