-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Separate table (not IncrementalStrategyOverrideTestTable) so this scenario's
-- SyncTableProvisioner.EnsureProvisionedAsync call doesn't hit the fingerprint-match fast
-- path against a config another test in the same run already persisted for a different
-- override setting - SyncTableConfig caches by schema fingerprint only, not by settings, so
-- two tests against the same table name with different settings but identical schema would
-- silently see whichever one ran first.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[IncrementalStrategyOverrideTimestampTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NOT NULL,
	[RowVer] [rowversion] NOT NULL,
 CONSTRAINT [PK_IncrementalStrategyOverrideTimestampTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
