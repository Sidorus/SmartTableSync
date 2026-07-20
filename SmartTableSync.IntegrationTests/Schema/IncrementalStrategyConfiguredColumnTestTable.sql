-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to prove Item 1's
-- explicit per-table configuration: its change-tracking column is named "ModifiedAt",
-- not the default "LastUpdate", so it only resolves to IncrementalStrategy.Timestamp
-- when SmartTableSync:ChangeTrackingColumns:IncrementalStrategyConfiguredColumnTestTable
-- is set to "ModifiedAt".
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

-- No IDENTITY column, deliberately - see IncrementalStrategyFullRescanTestTable.sql's
-- comment: sidesteps Item 8a's known (not-yet-fixed) identity+Tvp bug by using a plain,
-- explicitly-supplied Id.
CREATE TABLE [dbo].[IncrementalStrategyConfiguredColumnTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[ModifiedAt] [datetime] NULL,
 CONSTRAINT [PK_IncrementalStrategyConfiguredColumnTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
