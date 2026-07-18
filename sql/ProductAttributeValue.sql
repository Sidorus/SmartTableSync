/****** Object:  Table [dbo].[ProductAttributeValue] ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[ProductAttributeValue](
	[AttributeValueId] [int] IDENTITY(1,1) NOT NULL,
	[AttributeId] [int] NOT NULL,
	[Description] [nvarchar](max) NOT NULL,
	[LastUpdate] [datetime] NOT NULL,
	[ImageId] [int] NULL,
	[SortOrder] [int] NULL,
	[ActiveToDate] [datetime] NULL,
	[ActiveFromDate] [datetime] NULL,
	[Slug] [nvarchar](200) NULL,
 CONSTRAINT [PK_ProductAttributeValue] PRIMARY KEY CLUSTERED
(
	[AttributeValueId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

ALTER TABLE [dbo].[ProductAttributeValue] ADD  CONSTRAINT [DF_ProductAttributeValue_LastUpdate]  DEFAULT (getdate()) FOR [LastUpdate]
GO

ALTER TABLE [dbo].[ProductAttributeValue]  WITH CHECK ADD  CONSTRAINT [FK_ProductAttributeValue_Attribute] FOREIGN KEY([AttributeId])
REFERENCES [dbo].[Attribute] ([AttributeId])
ON DELETE CASCADE
GO

ALTER TABLE [dbo].[ProductAttributeValue] CHECK CONSTRAINT [FK_ProductAttributeValue_Attribute]
GO
