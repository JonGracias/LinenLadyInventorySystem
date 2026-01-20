CREATE UNIQUE INDEX UX_InventoryImage_InventoryId_Primary
ON inv.InventoryImage (InventoryId)
WHERE IsPrimary = 1;
