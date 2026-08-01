-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to prove that
-- SmartTableSync:IncrementalStrategyOverrides can force Timestamp even when a rowversion
-- column (which otherwise always wins) is also present, and that the override actually
-- flows end-to-end through SyncTableProvisioner into a working sync, not just through the
-- pure SyncPlanHeuristic function.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[IncrementalStrategyOverrideTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NOT NULL,
	[RowVer] [rowversion] NOT NULL,
 CONSTRAINT [PK_IncrementalStrategyOverrideTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
