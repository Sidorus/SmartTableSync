-- ============================================================
-- SNAPSHOT COPY for SmartTableSync.IntegrationTests only.
-- Source of truth is /sql/06_sync_table_config.sql in the repo root - this is NOT
-- auto-synced. If that script changes, re-copy it here by hand.
-- ============================================================

-- ============================================================
-- Run on: ChildDb  (CHILD database)
-- Purpose: Persisted, self-healing per-table sync configuration.
--
-- One row per managed table, written automatically by
-- ISyncTableProvisioner the first time a table is synced (or
-- whenever its parent schema changes — detected via SchemaFingerprint).
-- Replaces hand-declared per-table constants with introspected +
-- heuristically-derived values that survive container/Function
-- redeploys, since they live here rather than in a local file.
--
-- The *Override columns are the escape hatch for the rare case the
-- heuristic gets wrong: set one by hand and re-provisioning will
-- preserve it rather than overwriting it with a fresh heuristic value.
-- ============================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SyncTableConfig' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[SyncTableConfig] (
        [TableName]                      NVARCHAR(100) NOT NULL,
        [PrimaryKeyColumns]              NVARCHAR(400) NOT NULL,  -- comma-delimited, ordered
        [IdentityColumn]                 NVARCHAR(100) NULL,
        [MergeStrategy]                  NVARCHAR(20)  NOT NULL,  -- 'Tvp' | 'RowByRow'
        [BatchSize]                      INT           NOT NULL,
        [BulkCopyTimeoutSeconds]         INT           NOT NULL,
        [RetryableSqlErrors]             NVARCHAR(200) NULL,      -- comma-delimited ints
        [TvpTypeName]                    NVARCHAR(200) NULL,
        [SchemaFingerprint]              NVARCHAR(100) NOT NULL,
        [ConfiguredAtUtc]                DATETIME      NOT NULL,

        -- Hand-set overrides — non-null wins over the heuristic's own derived value.
        [MergeStrategyOverride]          NVARCHAR(20)  NULL,
        [BatchSizeOverride]              INT           NULL,
        [BulkCopyTimeoutSecondsOverride] INT           NULL,
        [RetryableSqlErrorsOverride]     NVARCHAR(200) NULL,

        CONSTRAINT [PK_SyncTableConfig] PRIMARY KEY ([TableName])
    );

    PRINT 'Created dbo.SyncTableConfig';
END
ELSE
    PRINT 'dbo.SyncTableConfig already exists';
GO
