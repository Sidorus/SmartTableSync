-- ============================================================
-- Test-only support table for SmartTableSync.IntegrationTests.
-- Not one of SmartTableSync's managed tables - it exists only to
-- satisfy ProductAttributeValue's real FK (REFERENCES [dbo].[Attribute],
-- see Schema/ProductAttributeValue.sql), enabling the FK-timing-race
-- retry scenario (SQL error 547) to be tested against a real database.
-- ============================================================
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[Attribute](
	[AttributeId] [int] IDENTITY(1,1) NOT NULL,
	[Name] [nvarchar](100) NOT NULL,
 CONSTRAINT [PK_Attribute] PRIMARY KEY CLUSTERED
(
	[AttributeId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
