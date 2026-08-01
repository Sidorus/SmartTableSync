-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to prove Item 2's rowversion
-- incremental strategy: a rowversion/timestamp column is detected as a fact (no config
-- needed) and takes priority over any datetime column. No identity column, deliberately -
-- this table isn't testing Item 8a's identity+Tvp fix, just the rowversion exclusion from
-- generated INSERT/UPDATE/bulk-copy column lists.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[RowVersionTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[RowVer] [rowversion] NOT NULL,
 CONSTRAINT [PK_RowVersionTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
