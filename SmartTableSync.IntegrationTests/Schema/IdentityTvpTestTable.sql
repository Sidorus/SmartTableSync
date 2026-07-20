-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to prove Item 8a's fix:
-- an identity PK + no incoming FKs + small rows resolves to the Tvp merge strategy (a
-- real, reachable heuristic outcome none of the 3 example tables happen to hit), and the
-- generated TVP MERGE procedure must wrap identity-column inserts in
-- SET IDENTITY_INSERT ON/OFF the same way the RowByRow generator already did.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[IdentityTvpTestTable](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_IdentityTvpTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
