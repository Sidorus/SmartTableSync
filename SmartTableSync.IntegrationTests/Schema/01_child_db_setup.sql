-- ============================================================
-- SNAPSHOT COPY for SmartTableSync.IntegrationTests only.
-- Source of truth is /sql/01_child_db_setup.sql in the repo root - this is NOT
-- auto-synced. If that script changes, re-copy it here by hand.
-- ============================================================

-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: Create the sync state tracker only.
--
-- dbo.ProductAttributeValue, dbo.ProductCatalogItem, dbo.DocumentAsset
-- already exist in the child database with their indexes.
-- ============================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CustomSyncState' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[CustomSyncState] (
        [TableName]    NVARCHAR(100) NOT NULL,
        [LastSyncDate] DATETIME      NOT NULL,
        CONSTRAINT [PK_CustomSyncState] PRIMARY KEY ([TableName])
    );

    -- 2000-01-01 signals "initial full sync not yet done"
    INSERT INTO [dbo].[CustomSyncState] ([TableName], [LastSyncDate]) VALUES
        ('ProductAttributeValue', '2000-01-01 00:00:00'),
        ('ProductCatalogItem',    '2000-01-01 00:00:00'),
        ('DocumentAsset',         '2000-01-01 00:00:00');

    PRINT 'Created dbo.CustomSyncState';
END
ELSE
    PRINT 'dbo.CustomSyncState already exists';
GO

-- ============================================================
-- SyncBatchLog — tracks initial full-sync progress per batch.
-- Allows the Azure Function to resume after a crash or timeout
-- without restarting from row 1.
--
-- LastKeyValue stores the last PK processed per batch:
--   ProductAttributeValue  → string of int             e.g. "45231"
--   ProductCatalogItem     → Sku                        e.g. "BOLT-M6"
--   DocumentAsset          → "DocumentId|LanguageCode"  e.g. "206|en"
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncBatchLog' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[SyncBatchLog] (
        [LogId]          INT            IDENTITY(1,1) NOT NULL,
        [TableName]      NVARCHAR(100)  NOT NULL,
        [Phase]          NVARCHAR(20)   NOT NULL DEFAULT 'initial',
        [BatchNumber]    INT            NOT NULL,
        [StartKeyValue]  NVARCHAR(400)  NULL,   -- first key in this batch (for readability)
        [LastKeyValue]   NVARCHAR(400)  NULL,   -- last key in this batch  (used for resume)
        [RowsSynced]     INT            NOT NULL DEFAULT 0,
        [RowsErrored]    INT            NOT NULL DEFAULT 0,
        [StartedAt]      DATETIME       NOT NULL DEFAULT GETUTCDATE(),
        [CompletedAt]    DATETIME       NULL,
        [Status]         NVARCHAR(20)   NOT NULL DEFAULT 'started',  -- 'started' | 'completed'
        CONSTRAINT [PK_SyncBatchLog] PRIMARY KEY ([LogId])
    );

    -- Used by resume query: last completed batch for a table
    CREATE NONCLUSTERED INDEX [IX_SyncBatchLog_Resume]
        ON [dbo].[SyncBatchLog] ([TableName], [Phase], [Status], [LogId] DESC);

    PRINT 'Created dbo.SyncBatchLog';
END
ELSE
    PRINT 'dbo.SyncBatchLog already exists';
GO
