-- ============================================================
-- CHILD database: the runtime identity can read and write the synced copies and the
-- tracking tables, and run the scaffolded merge procedures. No DDL rights.
-- Run as the SQL server's Microsoft Entra admin, after the scaffolded SQL is applied
-- (the procedures and table types must exist). Follows the runtime section of
-- sql/07_permissions.sql. Safe to re-run.
-- ============================================================

IF DATABASE_PRINCIPAL_ID(N'smarttablesync-runtime') IS NULL
    CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Product  TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Customer TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Document TO [smarttablesync-runtime];

GRANT SELECT, INSERT, UPDATE ON dbo.CustomSyncState TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE ON dbo.SyncBatchLog    TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE ON dbo.SyncTableConfig TO [smarttablesync-runtime];
GO

-- Which procedures and table types exist depends on the merge strategy SmartTableSync chose
-- per table (sp_Sync<Table>Batch + <Table>TableType for TVP, sp_Sync<Table> for row by row),
-- so grant on whatever the scaffold step created. Passing a table-valued parameter also needs
-- EXECUTE on its type.
DECLARE @grants NVARCHAR(MAX) = N'';

SELECT @grants += N'GRANT EXECUTE ON dbo.' + QUOTENAME(name) + N' TO [smarttablesync-runtime];' + CHAR(10)
FROM sys.procedures
WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'sp[_]Sync%';

SELECT @grants += N'GRANT EXECUTE ON TYPE::dbo.' + QUOTENAME(name) + N' TO [smarttablesync-runtime];' + CHAR(10)
FROM sys.table_types
WHERE schema_id = SCHEMA_ID(N'dbo');

PRINT @grants;
EXEC sys.sp_executesql @grants;
GO
