-- ============================================================
-- Three separate identities, matching three separate jobs. The
-- always-on Function should never hold an identity capable of
-- schema changes — only the manually-run scaffold tool needs that.
--
-- | Identity              | Used by                              | Runs                          |
-- |------------------------|----------------------------------------|---------------------------------|
-- | Child-provisioning     | Scaffold tool; the Function (opt-in)  | manual, on demand; or on cold start/schema drift when SmartTableSync:Mode is Auto |
-- | Parent-introspection   | Scaffold tool; the Function           | manual, on demand; or on cold start/schema drift |
-- | Runtime                | the Azure Function                    | always-on                     |
--
-- Table names below (ProductAttributeValue, ProductCatalogItem,
-- DocumentAsset) match this repo's example schema — substitute your
-- actual managed table list.
--
-- Two auth options are shown per identity: Azure AD / Managed Identity
-- (recommended — no secret to manage) and SQL authentication (if you
-- need it, e.g. local dev against a non-Azure SQL Server). Pick one
-- per identity, not both.
-- ============================================================

-- ============================================================
-- 1. Child-provisioning — run on the CHILD database.
-- Used by SmartTableSync.Scaffold to create/alter tables, TVP types,
-- and stored procedures for a human to review and apply by hand
-- (SmartTableSync:Mode = Manual, the default). Also used, opt-in, by
-- the Function itself via ChildDdlProvisioner/ChildProvisioningConnectionString
-- when SmartTableSync:Mode = Auto — and even then, only briefly, on
-- the same infrequent occasions provisioning already re-runs (a new
-- table, or parent schema drift), never on the routine sync path. The
-- runtime identity below never gains these rights either way.
-- ============================================================

-- Option A — Azure AD / Managed Identity (recommended):
-- CREATE USER [smarttablesync-child-provisioning] FROM EXTERNAL PROVIDER;

-- Option B — SQL authentication (requires a server-level login created first):
-- CREATE USER [smarttablesync_child_provisioning] FOR LOGIN [smarttablesync_child_provisioning];

ALTER ROLE [db_ddladmin] ADD MEMBER [smarttablesync-child-provisioning];
-- db_ddladmin grants CREATE/ALTER/DROP on tables, procedures, and types — exactly what
-- the scaffold tool's generated CREATE TABLE / CREATE TYPE / CREATE OR ALTER PROCEDURE
-- statements need, without full db_owner.
GO

-- ============================================================
-- 2. Parent-introspection — run on the PARENT database.
-- Used only by SmartTableSync.Scaffold (schema generation) and by the
-- Function's SyncTableProvisioner (schema-drift checks on cold start).
-- Read-only: schema metadata + the data needed for row/size sampling.
-- No DDL rights at all.
-- ============================================================

-- Option A — Azure AD / Managed Identity (recommended):
-- CREATE USER [smarttablesync-parent-introspection] FROM EXTERNAL PROVIDER;

-- Option B — SQL authentication:
-- CREATE USER [smarttablesync_parent_introspection] FOR LOGIN [smarttablesync_parent_introspection];

GRANT VIEW DEFINITION TO [smarttablesync-parent-introspection];
-- VIEW DEFINITION is what lets SchemaReader query sys.columns / sys.indexes /
-- sys.key_constraints / sys.identity_columns / sys.foreign_keys / sys.dm_db_partition_stats
-- for tables this principal can otherwise SELECT from.
-- Database-wide is the simplest grant. To limit it to the synced tables only, grant it per
-- table instead (repeat for each managed table):
-- GRANT VIEW DEFINITION ON OBJECT::[dbo].[ProductCatalogItem] TO [smarttablesync-parent-introspection];

GRANT SELECT ON [dbo].[ProductAttributeValue] TO [smarttablesync-parent-introspection];
GRANT SELECT ON [dbo].[ProductCatalogItem]    TO [smarttablesync-parent-introspection];
GRANT SELECT ON [dbo].[DocumentAsset]         TO [smarttablesync-parent-introspection];
-- SELECT is needed for the MAX(DATALENGTH()) row-size sampling in ProfileTableAsync —
-- schema metadata alone isn't enough for that specific check.

GRANT VIEW DATABASE STATE TO [smarttablesync-parent-introspection];
-- Row count and average row size come from sys.dm_db_partition_stats. Without this grant the
-- scaffold tool's per-table generation and --rowversion-setup fail with
-- "VIEW DATABASE PERFORMANCE STATE permission denied". Read-only server metadata.
GO

-- ============================================================
-- 3. Runtime — used by the always-on Azure Function. No DDL rights,
-- with one known exception (ALTER on child tables that have an
-- IDENTITY column, see the end of this section); SELECT on the
-- parent's synced tables, read/write only on the child's synced
-- tables plus the tracking tables (including SyncTableConfig —
-- writing a config row is DML, not DDL, so it belongs here, not
-- with child-provisioning).
-- ============================================================

-- Run on the PARENT database:

-- CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;   -- Managed Identity
-- CREATE USER [smarttablesync_runtime] FOR LOGIN [smarttablesync_runtime]; -- SQL auth

GRANT SELECT ON [dbo].[ProductAttributeValue] TO [smarttablesync-runtime];
GRANT SELECT ON [dbo].[ProductCatalogItem]    TO [smarttablesync-runtime];
GRANT SELECT ON [dbo].[DocumentAsset]         TO [smarttablesync-runtime];
GO

-- The Function profiles a table the first time it sees it (and after schema drift), with
-- this identity: row count and average row size come from sys.dm_db_partition_stats, which
-- fails with "VIEW DATABASE PERFORMANCE STATE permission denied" under SELECT alone.
-- Read-only metadata; no table data and no DDL rights.
GRANT VIEW DEFINITION ON [dbo].[ProductAttributeValue] TO [smarttablesync-runtime];
GRANT VIEW DEFINITION ON [dbo].[ProductCatalogItem]    TO [smarttablesync-runtime];
GRANT VIEW DEFINITION ON [dbo].[DocumentAsset]         TO [smarttablesync-runtime];
GRANT VIEW DATABASE STATE TO [smarttablesync-runtime];
GO

-- Only for tables where a DBA has enabled SQL Server Change Tracking (the
-- SqlChangeTracking incremental strategy): CHANGETABLE and
-- CHANGE_TRACKING_MIN_VALID_VERSION also need VIEW CHANGE TRACKING. Without it,
-- the runtime fails that table with a clear error instead of syncing it.
-- `SmartTableSync.Scaffold --change-tracking-setup <Table>` generates this grant
-- together with the CT enable statements. Example:
-- GRANT VIEW CHANGE TRACKING ON [dbo].[ProductCatalogItem] TO [smarttablesync-runtime];

-- Run on the CHILD database:

-- CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;   -- Managed Identity
-- CREATE USER [smarttablesync_runtime] FOR LOGIN [smarttablesync_runtime]; -- SQL auth

GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[ProductAttributeValue] TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[ProductCatalogItem]    TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[DocumentAsset]         TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE         ON [dbo].[CustomSyncState]       TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE         ON [dbo].[SyncBatchLog]          TO [smarttablesync-runtime];
GRANT SELECT, INSERT, UPDATE         ON [dbo].[SyncTableConfig]       TO [smarttablesync-runtime];
GRANT EXECUTE ON [dbo].[sp_SyncProductAttributeValue]      TO [smarttablesync-runtime];
GRANT EXECUTE ON [dbo].[sp_SyncProductCatalogItemBatch]    TO [smarttablesync-runtime];
GRANT EXECUTE ON [dbo].[sp_SyncDocumentAsset]              TO [smarttablesync-runtime];
GO

-- Tables merged in TVP batches (sp_Sync<Table>Batch): passing the table-valued parameter also
-- needs EXECUTE on its table type, or every batch fails with "The EXECUTE permission was
-- denied on the object '<Table>TableType'". One grant per TVP table:
GRANT EXECUTE ON TYPE::[dbo].[ProductCatalogItemTableType] TO [smarttablesync-runtime];
GO

-- Tables with an IDENTITY column (here ProductAttributeValue): the initial bulk copy and the
-- merge procedure keep the parent's identity values with SET IDENTITY_INSERT, which SQL Server
-- only allows with ALTER on the table. Known limitation: until the engine stops needing it,
-- the runtime identity needs ALTER on each such child table (and only on those).
GRANT ALTER ON [dbo].[ProductAttributeValue] TO [smarttablesync-runtime];
GO

-- Auto mode (SmartTableSync:Mode = Auto): the Function creates tables, table types and
-- procedures after these grants were given, so per-object grants can't exist in advance.
-- Instead of the CHILD grants above, grant once on the schema; it covers objects created
-- later and still gives no DDL (CREATE/ALTER/DROP stay denied). Broader than per-object
-- grants: use a child database dedicated to SmartTableSync. The ALTER grant for IDENTITY
-- tables is still needed.
-- GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[dbo] TO [smarttablesync-runtime];

-- ============================================================
-- Connection string reference (Managed Identity, Microsoft.Data.SqlClient):
--
--   Server=<server>.database.windows.net;Database=<db>;
--   Authentication=Active Directory Managed Identity;User Id=<managed-identity-client-id>;
--
-- No Azure.Identity API calls are needed in code for this — the connection-string
-- keyword handles the token acquisition, through the Microsoft.Data.SqlClient.Extensions.Azure
-- package the Function and the scaffold tool reference. Set ParentDbConnectionString/
-- ChildDbConnectionString (Function) and ParentIntrospectionConnectionString/
-- ChildProvisioningConnectionString (Scaffold tool) to the matching identity's string.
-- ============================================================
