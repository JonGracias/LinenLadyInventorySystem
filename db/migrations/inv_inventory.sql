SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [inv].[Inventory](
	[InventoryId] [int] IDENTITY(1,1) NOT NULL,
	[Sku] [nvarchar](64) NOT NULL,
	[Name] [nvarchar](255) NOT NULL,
	[Description] [nvarchar](max) NULL,
	[QuantityOnHand] [int] NOT NULL,
	[IsActive] [bit] NOT NULL,
	[CreatedAt] [datetime2](7) NOT NULL,
	[UpdatedAt] [datetime2](7) NOT NULL,
	[IsDeleted] [bit] NOT NULL,
	[IsDraft] [bit] NOT NULL,
	[UnitPriceCents] [int] NOT NULL,
	[PublicId] [uniqueidentifier] NOT NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [PK_Inventory] PRIMARY KEY CLUSTERED 
(
	[InventoryId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [UQ_Inventory_PublicId] UNIQUE NONCLUSTERED 
(
	[PublicId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [UQ_Inventory_Sku] UNIQUE NONCLUSTERED 
(
	[Sku] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_Inventory_Drafts] ON [inv].[Inventory]
(
	[IsDraft] ASC,
	[IsDeleted] ASC,
	[UpdatedAt] DESC
)
INCLUDE([InventoryId],[PublicId],[Name],[QuantityOnHand],[UnitPriceCents],[Sku],[IsActive],[CreatedAt]) WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_Inventory_Published] ON [inv].[Inventory]
(
	[IsActive] ASC,
	[IsDeleted] ASC,
	[IsDraft] ASC,
	[UpdatedAt] DESC
)
INCLUDE([InventoryId],[PublicId],[Name],[QuantityOnHand],[UnitPriceCents],[Sku],[CreatedAt]) WITH (STATISTICS_NORECOMPUTE = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Inventory_PublicId] ON [inv].[Inventory]
(
	[PublicId] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Inventory_Sku] ON [inv].[Inventory]
(
	[Sku] ASC
)WITH (STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_QuantityOnHand]  DEFAULT ((1)) FOR [QuantityOnHand]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_IsActive]  DEFAULT ((0)) FOR [IsActive]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_CreatedAt]  DEFAULT (sysutcdatetime()) FOR [CreatedAt]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_UpdatedAt]  DEFAULT (sysutcdatetime()) FOR [UpdatedAt]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_IsDeleted]  DEFAULT ((0)) FOR [IsDeleted]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_IsDraft]  DEFAULT ((1)) FOR [IsDraft]
GO
ALTER TABLE [inv].[Inventory] ADD  CONSTRAINT [DF_Inventory_UnitPriceCents]  DEFAULT ((0)) FOR [UnitPriceCents]
GO
ALTER TABLE [inv].[Inventory]  WITH CHECK ADD  CONSTRAINT [CK_Inventory_UnitPriceCents_NonNegative] CHECK  (([UnitPriceCents]>=(0)))
GO
ALTER TABLE [inv].[Inventory] CHECK CONSTRAINT [CK_Inventory_UnitPriceCents_NonNegative]
GO
