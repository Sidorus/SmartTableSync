-- ============================================================
-- CHILD database: what SmartTableSync decided and recorded.
-- ============================================================

SET NOCOUNT ON;

-- The plan per table: which change-detection strategy and which merge strategy it chose
SELECT TableName, IncrementalStrategy, MergeStrategy, BatchSize, ConfiguredAtUtc
FROM dbo.SyncTableConfig
ORDER BY TableName;

-- Where each table stopped: a Change Tracking version (CT:...), a rowversion (0x...) or,
-- for Timestamp tables, LastSyncDate
SELECT TableName, LastSyncDate, IncrementalPosition
FROM dbo.CustomSyncState
ORDER BY TableName;

-- The most recent batches
SELECT TOP (15) TableName, Phase, BatchNumber, RowsSynced, RowsErrored, Status, StartedAt, CompletedAt
FROM dbo.SyncBatchLog
ORDER BY LogId DESC;
