// SetPrimaryImage.cs
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

public sealed class SetPrimaryImage
{
    private readonly ILogger _logger;

    public SetPrimaryImage(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SetPrimaryImage>();
    }

    [Function("SetPrimaryImage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "items/{id:int}/images/{imageId:int}/set-primary")]
        HttpRequestData req,
        int id,
        int imageId,
        CancellationToken ct)
    {
        if (id <= 0 || imageId <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid id or imageId.", ct);
            return bad;
        }

        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        const string sql = @"
SET XACT_ABORT ON;
BEGIN TRAN;

-- Validate item exists and is not deleted
IF NOT EXISTS (
    SELECT 1
    FROM inv.Inventory
    WHERE InventoryId = @InventoryId AND IsDeleted = 0
)
BEGIN
    ROLLBACK TRAN;
    SELECT CAST(0 AS bit) AS Ok, CAST(1 AS bit) AS ItemNotFound, CAST(0 AS bit) AS ImageNotFound;
    RETURN;
END

-- Validate image exists and belongs to item
IF NOT EXISTS (
    SELECT 1
    FROM inv.InventoryImage
    WHERE ImageId = @ImageId AND InventoryId = @InventoryId
)
BEGIN
    ROLLBACK TRAN;
    SELECT CAST(0 AS bit) AS Ok, CAST(0 AS bit) AS ItemNotFound, CAST(1 AS bit) AS ImageNotFound;
    RETURN;
END

-- Transaction steps:
UPDATE inv.InventoryImage
SET IsPrimary = 0
WHERE InventoryId = @InventoryId;

UPDATE inv.InventoryImage
SET IsPrimary = 1
WHERE ImageId = @ImageId AND InventoryId = @InventoryId;

COMMIT TRAN;

-- Return updated images for the item
SELECT
    ImageId, InventoryId, ImagePath, IsPrimary, SortOrder, CreatedAt
FROM inv.InventoryImage
WHERE InventoryId = @InventoryId
ORDER BY SortOrder ASC, ImageId ASC;
";

        try
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@InventoryId", id);
            cmd.Parameters.AddWithValue("@ImageId", imageId);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            // First result could be the "flags row" when failing (Ok=0...)
            if (!await reader.ReadAsync(ct))
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Unexpected database response.", ct);
                return err;
            }

            // Detect the failure-flags shape: (Ok, ItemNotFound, ImageNotFound)
            // vs the success shape: (ImageId, InventoryId, ImagePath, IsPrimary, SortOrder, CreatedAt)
            if (reader.FieldCount == 3
                && string.Equals(reader.GetName(0), "Ok", StringComparison.OrdinalIgnoreCase))
            {
                var ok = reader.GetBoolean(0);
                var itemNotFound = reader.GetBoolean(1);
                var imageNotFound = reader.GetBoolean(2);

                if (!ok)
                {
                    if (itemNotFound)
                    {
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteStringAsync("Item not found.", ct);
                        return nf;
                    }

                    // Image missing or doesn't belong to item => treat as not found
                    if (imageNotFound)
                    {
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteStringAsync("Image not found for this item.", ct);
                        return nf;
                    }

                    var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await err.WriteStringAsync("Failed to set primary image.", ct);
                    return err;
                }
            }

            // Success path: we are currently positioned on the first image row.
            var images = new List<object>();

            do
            {
                images.Add(new
                {
                    imageId = reader.GetInt32(0),
                    inventoryId = reader.GetInt32(1),
                    imagePath = reader.GetString(2),
                    isPrimary = reader.GetBoolean(3),
                    sortOrder = reader.GetInt32(4),
                    createdAt = reader.GetDateTime(5),
                });
            } while (await reader.ReadAsync(ct));

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(new
            {
                inventoryId = id,
                primaryImageId = imageId,
                images
            }, ct);

            return resp;
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL error in SetPrimaryImage.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in SetPrimaryImage.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }
}
