-- ============================================================
-- PARENT database: the runtime identity (the Function's managed identity) can read the
-- demo tables and nothing else. Run as the SQL server's Microsoft Entra admin.
-- Same as the runtime section of sql/07_permissions.sql, for the demo tables.
-- The VIEW CHANGE TRACKING grant for Product comes from the scaffolded
-- Product_ChangeTrackingSetup.sql, which run-demo.ps1 applies after this script.
-- Safe to re-run.
-- ============================================================

IF DATABASE_PRINCIPAL_ID(N'smarttablesync-runtime') IS NULL
    CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;
GO

GRANT SELECT ON dbo.Product  TO [smarttablesync-runtime];
GRANT SELECT ON dbo.Customer TO [smarttablesync-runtime];
GRANT SELECT ON dbo.Document TO [smarttablesync-runtime];
GO

-- On first sight of a table, the Function profiles it with this identity: row count and
-- average row size come from sys.dm_db_partition_stats, which fails with "VIEW DATABASE
-- PERFORMANCE STATE permission denied" under SELECT alone.
GRANT VIEW DEFINITION ON dbo.Product  TO [smarttablesync-runtime];
GRANT VIEW DEFINITION ON dbo.Customer TO [smarttablesync-runtime];
GRANT VIEW DEFINITION ON dbo.Document TO [smarttablesync-runtime];
GRANT VIEW DATABASE STATE TO [smarttablesync-runtime];
GO
