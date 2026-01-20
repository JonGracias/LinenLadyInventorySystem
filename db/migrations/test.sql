ALTER TABLE [inv.InventoryImage]


  InventoryId,
  COUNT(*) AS PrimaryCount
FROM inv.InventoryImages
WHERE IsPrimary = 1
  AND IsDeleted = 0
GROUP BY InventoryId
HAVING COUNT(*) > 1;
GO
