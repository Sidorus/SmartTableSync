-- ============================================================
-- Test-only support tables for SmartTableSync.IntegrationTests.
-- Not SmartTableSync's managed tables - they exist only to prove delete reconciliation
-- works for primary keys that are neither integers nor strings: the parent/child key-set
-- diff must turn every key into a lossless string and parse it back exactly for the DELETE.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[GuidKeyTestTable](
	[Id] [uniqueidentifier] NOT NULL,
	[Value] [nvarchar](200) NULL,
 CONSTRAINT [PK_GuidKeyTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

CREATE TABLE [dbo].[DateTime2KeyTestTable](
	[Id] [datetime2](7) NOT NULL,
	[Value] [nvarchar](200) NULL,
 CONSTRAINT [PK_DateTime2KeyTestTable] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
