-- ============================================================
-- SNAPSHOT COPY for SmartTableSync.IntegrationTests only.
-- Source of truth is /sql/04_sp_sync_document_asset.sql in the repo root - this is NOT
-- auto-synced. If that script changes, re-copy it here by hand.
-- ============================================================

-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: MERGE a single DocumentAsset row from parent.
--
-- Composite PK: DocumentId + LanguageCode.
-- Contains large varbinary(max) columns (FileContent, PreviewImage,
-- PdfRendition) — rows can exceed 50 MB.  The Azure Function sends
-- one row at a time with CommandTimeout = 300 seconds.
-- ============================================================

CREATE OR ALTER PROCEDURE [dbo].[sp_SyncDocumentAsset]
    @DocumentId          INT,
    @LanguageCode        NVARCHAR(50),
    @MediaLibraryId      INT,
    @Title               VARCHAR(250),
    @FileType            NVARCHAR(50),
    @MediaUrl            NVARCHAR(200),
    @IsPublished         BIT,
    @PublishedFrom       DATETIME,
    @PublishedTo         DATETIME,
    @SortOrder           INT,
    @FileContent         VARBINARY(MAX),
    @OriginalFileName    NVARCHAR(200),
    @Notes               NVARCHAR(250),
    @LastUpdate          DATETIME,
    @CreatedBy           NVARCHAR(50),
    @PreviewImage        VARBINARY(MAX),
    @PdfRendition        VARBINARY(MAX),
    @PreviewSource       NVARCHAR(20),
    @PreviewUpdatedAt    DATETIME,
    @PdfSource           NVARCHAR(20),
    @PdfUpdatedAt        DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    MERGE [dbo].[DocumentAsset] WITH (HOLDLOCK) AS Target
    USING (SELECT @DocumentId AS [DocumentId], @LanguageCode AS [LanguageCode]) AS Source
    ON Target.[DocumentId] = Source.[DocumentId] AND Target.[LanguageCode] = Source.[LanguageCode]
    WHEN MATCHED THEN
        UPDATE SET
            [MediaLibraryId]   = @MediaLibraryId,
            [Title]            = @Title,
            [FileType]         = @FileType,
            [MediaUrl]         = @MediaUrl,
            [IsPublished]      = @IsPublished,
            [PublishedFrom]    = @PublishedFrom,
            [PublishedTo]      = @PublishedTo,
            [SortOrder]        = @SortOrder,
            [FileContent]      = @FileContent,
            [OriginalFileName] = @OriginalFileName,
            [Notes]            = @Notes,
            [LastUpdate]       = @LastUpdate,
            [CreatedBy]        = @CreatedBy,
            [PreviewImage]     = @PreviewImage,
            [PdfRendition]     = @PdfRendition,
            [PreviewSource]    = @PreviewSource,
            [PreviewUpdatedAt] = @PreviewUpdatedAt,
            [PdfSource]        = @PdfSource,
            [PdfUpdatedAt]     = @PdfUpdatedAt
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (
            [DocumentId], [LanguageCode], [MediaLibraryId], [Title], [FileType], [MediaUrl],
            [IsPublished], [PublishedFrom], [PublishedTo], [SortOrder], [FileContent],
            [OriginalFileName], [Notes], [LastUpdate], [CreatedBy], [PreviewImage],
            [PdfRendition], [PreviewSource], [PreviewUpdatedAt], [PdfSource], [PdfUpdatedAt]
        ) VALUES (
            @DocumentId, @LanguageCode, @MediaLibraryId, @Title, @FileType, @MediaUrl,
            @IsPublished, @PublishedFrom, @PublishedTo, @SortOrder, @FileContent,
            @OriginalFileName, @Notes, @LastUpdate, @CreatedBy, @PreviewImage,
            @PdfRendition, @PreviewSource, @PreviewUpdatedAt, @PdfSource, @PdfUpdatedAt
        );
END;
GO
