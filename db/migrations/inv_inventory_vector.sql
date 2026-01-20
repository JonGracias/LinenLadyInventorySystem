SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [inv].[InventoryVector](
	[VectorId] [int] IDENTITY(1,1) NOT NULL,
	[InventoryId] [int] NOT NULL,
	[VectorPurpose] [nvarchar](50) NOT NULL,
	[Model] [nvarchar](100) NOT NULL,
	[Dimensions] [int] NOT NULL,
	[ContentHash] [binary](32) NOT NULL,
	[VectorJson] [nvarchar](max) NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[UpdatedAt] [datetime2](7) NOT NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
ALTER TABLE [inv].[InventoryVector] ADD  CONSTRAINT [PK_InventoryVector] PRIMARY KEY CLUSTERED 
(
	[VectorId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_InventoryVector_InventoryId] ON [inv].[InventoryVector]
(
	[InventoryId] ASC
)
INCLUDE([VectorId],[VectorPurpose],[Model],[Dimensions],[ContentHash],[CreatedAt],[UpdatedAt]) WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
CREATE NONCLUSTERED INDEX [IX_InventoryVector_ItemModelHash] ON [inv].[InventoryVector]
(
	[InventoryId] ASC,
	[Model] ASC,
	[ContentHash] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
CREATE NONCLUSTERED INDEX [IX_InventoryVector_Purpose] ON [inv].[InventoryVector]
(
	[InventoryId] ASC,
	[VectorPurpose] ASC,
	[UpdatedAt] DESC
)
INCLUDE([Model],[Dimensions],[ContentHash]) WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_InventoryVector_Dedup] ON [inv].[InventoryVector]
(
	[InventoryId] ASC,
	[VectorPurpose] ASC,
	[Model] ASC,
	[Dimensions] ASC,
	[ContentHash] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_InventoryVector_ItemPurposeModel] ON [inv].[InventoryVector]
(
	[InventoryId] ASC,
	[VectorPurpose] ASC,
	[Model] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
ALTER TABLE [inv].[InventoryVector] ADD  CONSTRAINT [DF_InventoryVector_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [inv].[InventoryVector] ADD  CONSTRAINT [DF_InventoryVector_UpdatedAt]  DEFAULT (sysutcdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [inv].[InventoryVector]  WITH CHECK ADD  CONSTRAINT [FK_InventoryVector_Inventory] FOREIGN KEY([InventoryId])
REFERENCES [inv].[Inventory] ([InventoryId])
GO
ALTER TABLE [inv].[InventoryVector] CHECK CONSTRAINT [FK_InventoryVector_Inventory]
GO
ALTER TABLE [inv].[InventoryVector]  WITH CHECK ADD  CONSTRAINT [CK_InventoryVector_Dimensions_Positive] CHECK  (([Dimensions]>(0)))
GO
ALTER TABLE [inv].[InventoryVector] CHECK CONSTRAINT [CK_InventoryVector_Dimensions_Positive]
GO
