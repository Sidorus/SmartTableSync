-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Its own table, same reasoning as IncrementalStrategyOverrideTimestampTestTable.sql - proves
-- SmartTableSync:ChangeTrackingColumns naming a wrong-typed column throws at provisioning time,
-- without colliding with another test's cached SyncTableConfig row for a shared table.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[IncrementalStrategyOverrideBadColumnTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
 CONSTRAINT [PK_IncrementalStrategyOverrideBadColumnTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
