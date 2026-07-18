/****** Object:  Table [dbo].[DocumentAsset] ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[DocumentAsset](
	[DocumentId] [int] NOT NULL,
	[LanguageCode] [nvarchar](50) NOT NULL,
	[MediaLibraryId] [int] NULL,
	[Title] [varchar](250) NULL,
	[FileType] [nvarchar](50) NOT NULL,
	[MediaUrl] [nvarchar](200) NULL,
	[IsPublished] [bit] NULL,
	[PublishedFrom] [datetime] NULL,
	[PublishedTo] [datetime] NULL,
	[SortOrder] [int] NULL,
	[FileContent] [varbinary](max) NULL,
	[OriginalFileName] [nvarchar](200) NULL,
	[Notes] [nvarchar](250) NULL,
	[LastUpdate] [datetime] NULL,
	[CreatedBy] [nvarchar](50) NULL,
	[PreviewImage] [varbinary](max) NULL,
	[PdfRendition] [varbinary](max) NULL,
	[PreviewSource] [nvarchar](20) NULL,
	[PreviewUpdatedAt] [datetime] NULL,
	[PdfSource] [nvarchar](20) NULL,
	[PdfUpdatedAt] [datetime] NULL,
PRIMARY KEY CLUSTERED
(
	[DocumentId] ASC,
	[LanguageCode] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
