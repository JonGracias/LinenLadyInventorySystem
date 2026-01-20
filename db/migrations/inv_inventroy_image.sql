SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [inv].[InventoryImage](
	[ImageId] [int] IDENTITY(1,1) NOT NULL,
	[InventoryId] [int] NOT NULL,
	[ImagePath] [nvarchar](1024) NOT NULL,
	[IsPrimary] [bit] NOT NULL,
	[SortOrder] [int] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL
) ON [PRIMARY]
GO
ALTER TABLE [inv].[InventoryImage] ADD  CONSTRAINT [PK_InventoryImage] PRIMARY KEY CLUSTERED 
(
	[ImageId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_InventoryImage_InventoryId_SortOrder] ON [inv].[InventoryImage]
(
	[InventoryId] ASC,
	[SortOrder] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
ALTER TABLE [inv].[InventoryImage] ADD  CONSTRAINT [DF_InventoryImage_IsPrimary]  DEFAULT ((0)) FOR [IsPrimary]
GO
ALTER TABLE [inv].[InventoryImage] ADD  CONSTRAINT [DF_InventoryImage_SortOrder]  DEFAULT ((1)) FOR [SortOrder]
GO
ALTER TABLE [inv].[InventoryImage] ADD  CONSTRAINT [DF_InventoryImage_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [inv].[InventoryImage]  WITH CHECK ADD  CONSTRAINT [FK_InventoryImage_Inventory] FOREIGN KEY([InventoryId])
REFERENCES [inv].[Inventory] ([InventoryId])
GO
ALTER TABLE [inv].[InventoryImage] CHECK CONSTRAINT [FK_InventoryImage_Inventory]
GO
