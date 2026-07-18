-- ============================================================
-- Three separate identities, matching three separate jobs. The
-- always-on Function should never hold an identity capable of
-- schema changes — only the manually-run scaffold tool needs that.
--
-- | Identity              | Used by            | Runs               |
-- |------------------------|---------------------|--------------------|
-- | Child-provisioning     | Scaffold tool       | manual, on demand  |
-- | Parent-introspection   | Scaffold tool       | manual, on demand  |
-- | Runtime                | the Azure Function  | always-on          |
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
-- Used only by SmartTableSync.Scaffold to create/alter tables, TVP
-- types, and stored procedures. Never used by the Function.
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

GRANT SELECT ON [dbo].[ProductAttributeValue] TO [smarttablesync-parent-introspection];
GRANT SELECT ON [dbo].[ProductCatalogItem]    TO [smarttablesync-parent-introspection];
GRANT SELECT ON [dbo].[DocumentAsset]         TO [smarttablesync-parent-introspection];
-- SELECT is needed for the MAX(DATALENGTH()) row-size sampling in ProfileTableAsync —
-- schema metadata alone isn't enough for that specific check.
GO

-- ============================================================
-- 3. Runtime — used by the always-on Azure Function. No DDL rights
-- at all; SELECT on the parent's synced tables, read/write only on
-- the child's synced tables plus the tracking tables (including
-- SyncTableConfig — writing a config row is DML, not DDL, so it
-- belongs here, not with child-provisioning).
-- ============================================================

-- Run on the PARENT database:

-- CREATE USER [smarttablesync-runtime] FROM EXTERNAL PROVIDER;   -- Managed Identity
-- CREATE USER [smarttablesync_runtime] FOR LOGIN [smarttablesync_runtime]; -- SQL auth

GRANT SELECT ON [dbo].[ProductAttributeValue] TO [smarttablesync-runtime];
GRANT SELECT ON [dbo].[ProductCatalogItem]    TO [smarttablesync-runtime];
GRANT SELECT ON [dbo].[DocumentAsset]         TO [smarttablesync-runtime];
GO

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

-- ============================================================
-- Connection string reference (Managed Identity, Microsoft.Data.SqlClient):
--
--   Server=<server>.database.windows.net;Database=<db>;
--   Authentication=Active Directory Managed Identity;User Id=<managed-identity-client-id>;
--
-- No Azure.Identity API calls are needed in code for this — the connection-string
-- keyword handles the token acquisition. Set ParentDbConnectionString/
-- ChildDbConnectionString (Function) and ParentIntrospectionConnectionString/
-- ChildProvisioningConnectionString (Scaffold tool) to the matching identity's string.
-- ============================================================
