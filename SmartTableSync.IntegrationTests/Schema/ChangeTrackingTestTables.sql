-- ============================================================
-- Test-only support tables for SmartTableSync.IntegrationTests.
-- Not SmartTableSync's managed tables - they exist only to prove the
-- SqlChangeTracking incremental strategy (ChangeTrackingStrategyTests).
-- Created identically on parent and child; MsSqlTestFixture enables
-- Change Tracking on the PARENT copies only (a DBA action in real life).
-- One table per test scenario, so each scenario owns its own
-- CustomSyncState position. LastUpdate exists so CT can be shown to win
-- over the Timestamp strategy, and so an override can pin Timestamp.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[ChangeTrackingTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_ChangeTrackingTestTable] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO

CREATE TABLE [dbo].[ChangeTrackingInFlightTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_ChangeTrackingInFlightTestTable] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO

CREATE TABLE [dbo].[ChangeTrackingRescanTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_ChangeTrackingRescanTestTable] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO

CREATE TABLE [dbo].[ChangeTrackingBaselineTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_ChangeTrackingBaselineTestTable] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO

CREATE TABLE [dbo].[ChangeTrackingOverrideTestTable](
	[Id] [int] NOT NULL,
	[Value] [nvarchar](200) NULL,
	[LastUpdate] [datetime] NULL,
 CONSTRAINT [PK_ChangeTrackingOverrideTestTable] PRIMARY KEY CLUSTERED ([Id] ASC)
)
GO
