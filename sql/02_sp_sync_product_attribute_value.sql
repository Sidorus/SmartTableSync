-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: MERGE a single ProductAttributeValue row from parent.
--
-- Requires IDENTITY_INSERT because AttributeValueId is IDENTITY
-- in the parent and we must preserve the same IDs in the child
-- (otherwise FK references from other synced tables would break).
-- ============================================================

CREATE OR ALTER PROCEDURE [dbo].[sp_SyncProductAttributeValue]
    @AttributeValueId  INT,
    @AttributeId        INT,
    @Description        NVARCHAR(MAX),
    @LastUpdate         DATETIME,
    @ImageId            INT,
    @SortOrder          INT,
    @ActiveToDate       DATETIME,
    @ActiveFromDate     DATETIME,
    @Slug               NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    SET IDENTITY_INSERT [dbo].[ProductAttributeValue] ON;

    MERGE [dbo].[ProductAttributeValue] WITH (HOLDLOCK) AS Target
    USING (SELECT
        @AttributeValueId AS [AttributeValueId],
        @AttributeId      AS [AttributeId],
        @Description      AS [Description],
        @LastUpdate       AS [LastUpdate],
        @ImageId          AS [ImageId],
        @SortOrder        AS [SortOrder],
        @ActiveToDate     AS [ActiveToDate],
        @ActiveFromDate   AS [ActiveFromDate],
        @Slug             AS [Slug]
    ) AS Source
    ON Target.[AttributeValueId] = Source.[AttributeValueId]
    WHEN MATCHED THEN
        UPDATE SET
            [AttributeId]    = Source.[AttributeId],
            [Description]    = Source.[Description],
            [LastUpdate]     = Source.[LastUpdate],
            [ImageId]        = Source.[ImageId],
            [SortOrder]      = Source.[SortOrder],
            [ActiveToDate]   = Source.[ActiveToDate],
            [ActiveFromDate] = Source.[ActiveFromDate],
            [Slug]           = Source.[Slug]
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (
            [AttributeValueId], [AttributeId], [Description], [LastUpdate],
            [ImageId], [SortOrder], [ActiveToDate], [ActiveFromDate], [Slug]
        ) VALUES (
            Source.[AttributeValueId], Source.[AttributeId], Source.[Description], Source.[LastUpdate],
            Source.[ImageId], Source.[SortOrder], Source.[ActiveToDate], Source.[ActiveFromDate], Source.[Slug]
        );

    SET IDENTITY_INSERT [dbo].[ProductAttributeValue] OFF;
END;
GO
