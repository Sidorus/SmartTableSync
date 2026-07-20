-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to prove Item 1's
-- FullRescan fallback: no datetime column at all, so with no configured
-- SmartTableSync:ChangeTrackingColumns entry, the default "LastUpdate" lookup fails
-- and SyncPlanHeuristic falls back to IncrementalStrategy.FullRescan (small row count,
-- well under FullRescanRowCountThreshold).
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

-- No IDENTITY column, deliberately - Item 8a's known (not-yet-fixed) bug means the
-- generated TVP merge procedure doesn't wrap identity-column inserts in
-- SET IDENTITY_INSERT ON/OFF the way the RowByRow one does. This table only needs Tvp
-- strategy (no incoming FKs, small rows) to prove Item 1's FullRescan fallback, so it
-- sidesteps that unrelated bug entirely by using a plain, explicitly-supplied Id.
CREATE TABLE [dbo].[IncrementalStrategyFullRescanTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
 CONSTRAINT [PK_IncrementalStrategyFullRescanTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
