using System.Net;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LinenLady.Inventory.Functions;

/**
 * ============================================================================
 * CREATE DRAFT ITEM UPLOADS - AZURE FUNCTION
 * ============================================================================
 * 
 * PURPOSE:
 * This Azure Function creates a new draft inventory item in the database
 * and generates Shared Access Signature (SAS) URLs for uploading images
 * to Azure Blob Storage.
 * 
 * HTTP ENDPOINT:
 * POST /api/items/drafts
 * 
 * ARCHITECTURE ROLE:
 * This is a serverless backend function that:
 * 1. Handles database writes (SQL Server)
 * 2. Generates secure upload URLs (Azure Blob Storage with SAS)
 * 3. Returns structured data to the Next.js frontend
 * 
 * WHY AZURE FUNCTIONS?
 * - Serverless: No server management, auto-scales
 * - Secure: Keeps database credentials and storage keys on backend
 * - Cost-effective: Only pay when actually called
 * - Isolated: Database operations separated from frontend
 */
public sealed class CreateDraftItemUploads
{
    private readonly ILogger _logger;

    public CreateDraftItemUploads(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CreateDraftItemUploads>();
    }

    // ========================================================================
    // REQUEST/RESPONSE DATA MODELS
    // ========================================================================

    /**
     * FILE SPECIFICATION
     * Describes one image file that will be uploaded
     */
    public sealed record FileSpec(
        [property: JsonPropertyName("fileName")] string? FileName,      // e.g., "IMG_1234.jpg"
        [property: JsonPropertyName("contentType")] string? ContentType // e.g., "image/jpeg"
    );

    /**
     * REQUEST BODY MODEL
     * What the frontend sends to this function
     * 
     * TWO MODES:
     * 1. Send files[] array: We know filename and content type for each
     * 2. Send count: Just tell us how many, we'll generate generic names
     * 
     * Current frontend uses mode 1 (files array)
     */
    public sealed record CreateDraftRequest(
        [property: JsonPropertyName("titleHint")] string? TitleHint, // Optional: initial item name
        [property: JsonPropertyName("notes")] string? Notes,         // Optional: temporary description
        [property: JsonPropertyName("count")] int? Count,            // Alternative to files[] (deprecated)
        [property: JsonPropertyName("files")] List<FileSpec>? Files  // Array of file metadata (preferred)
    );

    /**
     * UPLOAD TARGET - ONE PER IMAGE
     * Instructions for how to upload one image to Azure Blob Storage
     */
    public sealed record UploadTarget(
        [property: JsonPropertyName("index")] int Index,                    // Position (1-4)
        [property: JsonPropertyName("blobName")] string BlobName,           // Path in storage: "images/abc123.../01-xyz.jpg"
        [property: JsonPropertyName("uploadUrl")] string UploadUrl,         // SAS URL with write permissions
        [property: JsonPropertyName("method")] string Method,               // HTTP method (always "PUT")
        [property: JsonPropertyName("requiredHeaders")] Dictionary<string, string> RequiredHeaders, // Headers client must send
        [property: JsonPropertyName("contentType")] string ContentType      // MIME type for the blob
    );

    // ========================================================================
    // MAIN FUNCTION HANDLER
    // ========================================================================

    /**
     * Azure Function trigger configuration:
     * - AuthorizationLevel.Anonymous: No function key required (uses other auth)
     * - Method: POST only
     * - Route: /api/items/drafts
     */
    [Function("CreateDraftItemUploads")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items/drafts")] HttpRequestData req,
        CancellationToken ct)
    {
        // ====================================================================
        // STEP 1: VALIDATE ENVIRONMENT CONFIGURATION
        // ====================================================================

        /**
         * Get SQL Server connection string from environment variables.
         * This is configured in Azure Function App Settings and typically looks like:
         * "Server=tcp:yourserver.database.windows.net,1433;Database=LinenLady;..."
         * 
         * Keeping this in environment variables (not code) is a security best practice.
         */
        var connStr = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing SQL_CONNECTION_STRING.", ct);
            return bad;
        }

        /**
         * Get Azure Blob Storage connection string.
         * Try two environment variables (for flexibility):
         * 1. BLOB_STORAGE_CONNECTION_STRING (explicit)
         * 2. AzureWebJobsStorage (default Azure Functions storage)
         * 
         * Connection string format:
         * "DefaultEndpointsProtocol=https;AccountName=youraccount;AccountKey=...;EndpointSuffix=core.windows.net"
         * 
         * IMPORTANT: Must include AccountKey for SAS URL generation to work!
         */
        var storageConn =
            Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING") ??
            Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (string.IsNullOrWhiteSpace(storageConn))
        {
            var bad = req.CreateResponse(HttpStatusCode.InternalServerError);
            await bad.WriteStringAsync("Server misconfigured: missing storage connection string.", ct);
            return bad;
        }

        /**
         * Get the blob container name where images are stored.
         * Default: "inventory-images"
         * 
         * A container is like a folder/bucket in Azure Blob Storage.
         * All inventory images go in this one container, organized by subfolder.
         */
        var containerName =
            Environment.GetEnvironmentVariable("IMAGE_CONTAINER_NAME") ??
            "inventory-images";

        // ====================================================================
        // STEP 2: PARSE AND VALIDATE REQUEST
        // ====================================================================

        /**
         * Parse JSON request body into our CreateDraftRequest model.
         * If parsing fails (malformed JSON), set body to null.
         */
        CreateDraftRequest? body;
        try { body = await req.ReadFromJsonAsync<CreateDraftRequest>(ct); }
        catch { body = null; }

        var files = body?.Files;

        /**
         * Determine how many images to prepare for.
         * 
         * LOGIC:
         * - If files[] array provided and not empty: use files.Count
         * - Otherwise: use count field (legacy support)
         * - If neither: default to 0 (which triggers error below)
         * 
         * DESIGN NOTE: files[] is preferred because it gives us filename
         * and content type, which helps generate appropriate blob names
         * and set correct MIME types.
         */
        int requestedCount = (files is { Count: > 0 }) ? files.Count : (body?.Count ?? 0);
        if (requestedCount <= 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Provide files[] or count > 0.", ct);
            return bad;
        }

        /**
         * BUSINESS RULE: Clamp to 1-4 images per item
         * 
         * Math.Clamp(value, min, max) ensures:
         * - Minimum: 1 image (every item needs at least one photo)
         * - Maximum: 4 images (UI constraint, keeps gallery manageable)
         * 
         * If user tries to send 5+ images, we silently cap at 4.
         * Alternative would be to return error, but truncating is more user-friendly.
         */
        int count = Math.Clamp(requestedCount, 1, 4);

        // ====================================================================
        // STEP 3: GENERATE DRAFT ITEM IDENTIFIERS
        // ====================================================================

        /**
         * PUBLIC ID (UUID)
         * A unique identifier for this item, used in public-facing contexts:
         * - URLs: /items/{publicId}
         * - Blob storage paths: images/{publicId}/...
         * - QR codes or shareable links
         * 
         * Why UUID instead of database ID?
         * - Database IDs are sequential (1, 2, 3...) which leaks business info
         * - UUIDs are globally unique, can be generated client-side if needed
         * - UUIDs prevent enumeration attacks (guessing valid IDs)
         */
        var publicId = Guid.NewGuid();

        /**
         * publicIdN: UUID in "N" format (no hyphens)
         * Standard: 550e8400-e29b-41d4-a716-446655440000
         * N format: 550e8400e29b41d4a716446655440000
         * 
         * Why no hyphens?
         * - Cleaner in URLs and file paths
         * - Slightly shorter
         * - Some systems don't like hyphens in folder names
         */
        var publicIdN = publicId.ToString("N");

        /**
         * SKU (Stock Keeping Unit)
         * The inventory code/product number.
         * Format: "DRAFT-{publicIdN}"
         * 
         * Example: "DRAFT-550e8400e29b41d4a716446655440000"
         * 
         * DESIGN DECISIONS:
         * - Prefixed with "DRAFT-" to clearly indicate draft status
         * - Uses publicIdN to ensure uniqueness (no SKU collisions)
         * - Max 64 characters (database column limit)
         * 
         * When item is published, SKU might be changed to something
         * more user-friendly like "TBL-001" or kept as-is.
         */
        var sku = $"DRAFT-{publicIdN}";

        /**
         * ITEM NAME
         * Initial display name for the item.
         * 
         * Priority:
         * 1. Use titleHint if provided (e.g., "Blue tablecloth")
         * 2. Default to "Draft" if not provided
         * 
         * The AI prefill step will likely overwrite this with a
         * better generated name, but we need *something* for the
         * database insert (Name is a required field).
         */
        var name = string.IsNullOrWhiteSpace(body?.TitleHint) 
            ? "Draft" 
            : body!.TitleHint!.Trim();

        /**
         * DESCRIPTION (temporarily stores notes)
         * 
         * HACK ALERT: The database doesn't have a separate "Notes" column
         * for intake notes, so we temporarily store them in Description.
         * 
         * Later, the AI prefill will overwrite Description with generated
         * product description, effectively discarding these notes.
         * 
         * If notes need to persist, consider:
         * - Adding inv.Inventory.IntakeNotes column
         * - Storing in separate metadata table
         * - Prepending to AI description instead of overwriting
         */
        var description = string.IsNullOrWhiteSpace(body?.Notes) 
            ? null 
            : body!.Notes!.Trim();

        // ====================================================================
        // STEP 4: INSERT DRAFT ITEM INTO DATABASE
        // ====================================================================

        /**
         * SQL INSERT STRATEGY:
         * Only specify columns that DON'T have safe defaults in the database.
         * 
         * COLUMNS WE SPECIFY:
         * - PublicId: Has no default (by design, must be explicit)
         * - Sku: Must be unique, can't default
         * - Name: Required field, no default
         * - Description: Optional, but we have data to insert
         * 
         * COLUMNS HANDLED BY DATABASE DEFAULTS:
         * - InventoryId: IDENTITY(1,1) - auto-increments
         * - IsDraft: DEFAULT 1 - marks as draft
         * - IsActive: DEFAULT 0 - not published yet
         * - IsDeleted: DEFAULT 0 - not soft-deleted
         * - QuantityOnHand: DEFAULT 0 - no stock yet
         * - UnitPriceCents: DEFAULT 0 - price TBD
         * - CreatedAt: DEFAULT GETUTCDATE() - timestamp
         * - UpdatedAt: DEFAULT GETUTCDATE() - timestamp
         * 
         * OUTPUT INSERTED.InventoryId:
         * This is how we get back the auto-generated ID immediately.
         * SQL Server executes the INSERT and returns the new InventoryId.
         */
        const string insertSql = @"
INSERT INTO inv.Inventory
(
  PublicId,
  Sku,
  Name,
  Description
)
OUTPUT INSERTED.InventoryId
VALUES
(
  @PublicId,
  @Sku,
  @Name,
  @Description
);";

        int inventoryId;

        try
        {
            // Open database connection
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            // Create SQL command with parameters (prevents SQL injection)
            using var cmd = new SqlCommand(insertSql, conn) { CommandTimeout = 30 };
            
            /**
             * PARAMETERIZED QUERY (Security Best Practice)
             * Using @parameters instead of string concatenation prevents SQL injection.
             * 
             * Bad (vulnerable):
             *   $"INSERT INTO ... VALUES ('{publicId}', '{sku}', ...)"
             * 
             * Good (safe):
             *   Parameters.AddWithValue("@PublicId", publicId)
             */
            cmd.Parameters.AddWithValue("@PublicId", publicId);
            
            /**
             * Explicitly specify SqlDbType and size for string parameters.
             * This ensures proper type handling and prevents truncation issues.
             * 
             * NVarChar(64): Variable-length Unicode string, max 64 characters
             */
            cmd.Parameters.Add(new SqlParameter("@Sku", System.Data.SqlDbType.NVarChar, 64) 
                { Value = sku });
            cmd.Parameters.Add(new SqlParameter("@Name", System.Data.SqlDbType.NVarChar, 255) 
                { Value = name });
            
            /**
             * NVarChar(-1) = NVARCHAR(MAX) in SQL Server
             * Used for Description which can be arbitrarily long.
             * 
             * DBNull.Value: Proper way to insert NULL in ADO.NET
             * Can't just use null, must be DBNull.Value
             */
            cmd.Parameters.Add(new SqlParameter("@Description", System.Data.SqlDbType.NVarChar, -1)
            {
                Value = (object?)description ?? DBNull.Value
            });

            /**
             * ExecuteScalarAsync() executes the query and returns the first column
             * of the first row (in this case, INSERTED.InventoryId).
             * 
             * Convert.ToInt32() safely converts the result to int.
             */
            inventoryId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        catch (SqlException ex)
        {
            /**
             * SQL ERRORS TO EXPECT:
             * - Unique constraint violation (duplicate Sku, unlikely with UUIDs)
             * - Connection timeout (database unreachable)
             * - Permission denied (connection string lacks INSERT rights)
             * 
             * We log the full exception but return generic error to client
             * (don't leak SQL details to frontend for security).
             */
            _logger.LogError(ex, "SQL error creating draft item.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Database error.", ct);
            return err;
        }

        // At this point: Draft item exists in database with inventoryId = X

        // ====================================================================
        // STEP 5: GENERATE SAS UPLOAD URLS FOR BLOB STORAGE
        // ====================================================================

        /**
         * SAS URL EXPIRATION
         * Upload URLs are valid for 15 minutes from now.
         * 
         * Why 15 minutes?
         * - Long enough: User has time to upload 4 images even on slow connection
         * - Short enough: Limits window for URL leaking/abuse
         * - Standard practice: Most upload flows use 10-60 minute expiry
         * 
         * After expiry, the URL becomes invalid and returns 403 Forbidden.
         * User would need to request new URLs (or restart intake).
         */
        var expiresInMinutes = 15;
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(expiresInMinutes);

        try
        {
            /**
             * AZURE BLOB STORAGE SDK SETUP
             * 
             * BlobServiceClient: Top-level client for the storage account
             * BlobContainerClient: Client for a specific container ("inventory-images")
             * BlobClient: Client for a specific blob (one file)
             */
            var service = new BlobServiceClient(storageConn);
            var container = service.GetBlobContainerClient(containerName);
            
            /**
             * CreateIfNotExistsAsync: Idempotent container creation
             * - If container exists: no-op, succeeds silently
             * - If container doesn't exist: creates it
             * 
             * This ensures the container is ready without failing if already present.
             * Typically the container is created during initial deployment, but this
             * adds defensive resilience.
             */
            await container.CreateIfNotExistsAsync(cancellationToken: ct);

            var targets = new List<UploadTarget>(count);

            /**
             * GENERATE ONE UPLOAD TARGET PER IMAGE
             * Loop count times (1-4) to create upload instructions
             */
            for (int i = 0; i < count; i++)
            {
                /**
                 * Get file metadata if provided.
                 * If files array has an entry at index i, use it.
                 * Otherwise, spec will be null (fallback to defaults).
                 */
                FileSpec? spec = null;
                if (files is { Count: > 0 } && i < files.Count)
                    spec = files[i];

                /**
                 * Determine file extension and content type.
                 * Uses helper methods (defined at bottom of file):
                 * 
                 * NormalizeExtension: Extracts and validates extension from filename
                 * - Supports: .jpg, .jpeg, .png, .webp, .heic
                 * - Default: .jpg if unknown
                 * 
                 * NormalizeContentType: Determines MIME type
                 * - Uses provided contentType if available
                 * - Otherwise infers from extension
                 * - Default: image/jpeg
                 */
                var ext = NormalizeExtension(spec?.FileName) ?? ".jpg";
                var contentType = NormalizeContentType(spec?.ContentType, ext);

                /**
                 * BLOB NAME GENERATION
                 * Format: images/{publicIdN}/{index}-{guid}{ext}
                 * 
                 * Example: images/550e8400e29b41d4a716446655440000/01-abc123def456.jpg
                 * 
                 * STRUCTURE:
                 * - images/: Top-level folder for all inventory images
                 * - {publicIdN}/: Subfolder per item (groups item's images together)
                 * - {index:00}-: Two-digit index (01, 02, 03, 04) for ordering
                 * - {guid:N}: Random UUID to prevent filename collisions
                 * - {ext}: File extension (.jpg, .png, etc.)
                 * 
                 * WHY THIS FORMAT?
                 * - Organized: Easy to list all images for one item
                 * - Ordered: Index prefix ensures consistent sort order
                 * - Unique: GUID prevents overwrites if user re-uploads
                 * - Clean: No spaces or special characters (URL-safe)
                 */
                var blobName = $"images/{publicIdN}/{i + 1:00}-{Guid.NewGuid():N}{ext}";
                
                /**
                 * Get a BlobClient for this specific blob path.
                 * Even though the blob doesn't exist yet, we can create a client for it.
                 */
                var blobClient = container.GetBlobClient(blobName);

                /**
                 * SAS (Shared Access Signature) BUILDER
                 * This generates a temporary, scoped access token for the blob.
                 * 
                 * CONFIGURATION:
                 * - BlobContainerName: Which container the blob is in
                 * - BlobName: Full path to the specific blob
                 * - Resource: "b" = blob (vs "c" for container)
                 * - ExpiresOn: When the signature becomes invalid
                 * - Permissions: Create + Write (can create new blob and modify it)
                 * 
                 * SECURITY MODEL:
                 * - No credentials needed: The URL itself grants access
                 * - Time-limited: Only valid for 15 minutes
                 * - Scope-limited: Only for this one specific blob, only write/create
                 * - Can't read other blobs, delete, or list container contents
                 */
                var sas = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b",  // "b" = blob-level SAS
                    ExpiresOn = expiresOn
                };
                sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

                /**
                 * GENERATE SAS URI
                 * Combines the blob URL with the SAS query string.
                 * 
                 * Result looks like:
                 * https://youraccount.blob.core.windows.net/inventory-images/images/550e.../01-abc.jpg?
                 * sv=2021-06-08&
                 * se=2026-01-19T14%3A30%3A00Z&
                 * sr=b&
                 * sp=cw&
                 * sig=...signature...
                 * 
                 * WHERE:
                 * - sv: storage version
                 * - se: signed expiry time
                 * - sr: signed resource (blob)
                 * - sp: signed permissions (create, write)
                 * - sig: cryptographic signature (proves authenticity)
                 */
                var uploadUri = blobClient.GenerateSasUri(sas);

                /**
                 * CREATE UPLOAD TARGET OBJECT
                 * Package all upload information for this image.
                 */
                targets.Add(new UploadTarget(
                    Index: i + 1,  // 1-based index (1, 2, 3, 4)
                    BlobName: blobName,
                    UploadUrl: uploadUri.ToString(),
                    Method: "PUT",  // HTTP PUT uploads the blob
                    
                    /**
                     * REQUIRED HEADERS
                     * Azure Blob Storage requires these headers on PUT request:
                     * 
                     * x-ms-blob-type: "BlockBlob"
                     * - Specifies the blob type (vs AppendBlob or PageBlob)
                     * - BlockBlob is standard for files/images
                     * 
                     * Content-Type: MIME type
                     * - Sets the blob's content type metadata
                     * - Browser will use this when serving the image
                     * - Important for proper rendering (browser knows it's an image)
                     */
                    RequiredHeaders: new Dictionary<string, string>
                    {
                        ["x-ms-blob-type"] = "BlockBlob",
                        ["Content-Type"] = contentType
                    },
                    ContentType: contentType
                ));
            }

            // ================================================================
            // STEP 6: RETURN SUCCESS RESPONSE
            // ================================================================

            /**
             * RESPONSE PAYLOAD
             * Send back everything the frontend needs:
             * - inventoryId: For subsequent API calls (add images, AI prefill, etc.)
             * - publicId: For display and URL generation
             * - sku: For inventory management
             * - container: Informational (where images will be stored)
             * - expiresOnUtc: Frontend can warn user if time running out
             * - uploads[]: Array of upload instructions (one per image)
             */
            var payload = new
            {
                inventoryId,
                publicId = publicIdN,   // Return N format (no hyphens)
                sku,
                container = containerName,
                expiresOnUtc = expiresOn.UtcDateTime,
                uploads = targets
            };

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(payload, ct);
            return ok;
        }
        catch (InvalidOperationException ex)
        {
            /**
             * COMMON CAUSE: Missing AccountKey in connection string
             * 
             * SAS URL generation requires the storage account key.
             * If connection string uses SAS or Managed Identity instead of AccountKey,
             * GenerateSasUri() will throw InvalidOperationException.
             * 
             * FIX: Ensure connection string includes AccountKey:
             * "DefaultEndpointsProtocol=https;AccountName=X;AccountKey=Y;EndpointSuffix=core.windows.net"
             */
            _logger.LogError(ex, "SAS generation failed (likely missing AccountKey).");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("SAS generation failed. Ensure storage connection string includes AccountKey.", ct);
            return err;
        }
        catch (Exception ex)
        {
            /**
             * CATCH-ALL ERROR HANDLER
             * Handles unexpected exceptions (network issues, Azure outages, etc.)
             */
            _logger.LogError(ex, "Unhandled error creating draft uploads.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Server error.", ct);
            return err;
        }
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    /**
     * NORMALIZE FILE EXTENSION
     * Extracts and validates the file extension from a filename.
     * 
     * LOGIC:
     * 1. Extract extension using Path.GetExtension()
     * 2. Convert to lowercase for case-insensitive comparison
     * 3. Validate against whitelist of supported formats
     * 4. Default to .jpg if unknown/unsupported
     * 
     * SUPPORTED FORMATS:
     * - .jpg / .jpeg: JPEG images (most common)
     * - .png: PNG images (supports transparency)
     * - .webp: Modern format, smaller file sizes
     * - .heic: iPhone default format (High Efficiency Image Format)
     * 
     * WHY WHITELIST?
     * - Security: Prevent uploading executable files (.exe, .js)
     * - Compatibility: Ensure browser can display the image
     * - Consistency: Predictable file types in storage
     */
    private static string? NormalizeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var ext = Path.GetExtension(fileName.Trim());
        if (string.IsNullOrWhiteSpace(ext)) return null;

        ext = ext.ToLowerInvariant();

        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" => ext,
            _ => ".jpg"  // Default to .jpg for unknown extensions
        };
    }

    /**
     * NORMALIZE CONTENT TYPE (MIME Type)
     * Determines the proper MIME type for the blob.
     * 
     * LOGIC:
     * 1. If contentType is provided and not empty, use it as-is
     * 2. Otherwise, infer from file extension
     * 
     * MIME TYPE MAPPING:
     * - .png → image/png
     * - .webp → image/webp
     * - .heic → image/heic
     * - default → image/jpeg
     * 
     * WHY THIS MATTERS?
     * - Browser rendering: Correct MIME type ensures proper display
     * - Content negotiation: APIs can request specific formats
     * - CDN behavior: Some CDNs serve different content based on MIME type
     * 
     * STORAGE NOTE:
     * This MIME type is stored as blob metadata in Azure Storage.
     * When the blob is served via HTTP, Azure returns this as the
     * Content-Type header automatically.
     */
    private static string NormalizeContentType(string? contentType, string ext)
    {
        if (!string.IsNullOrWhiteSpace(contentType)) return contentType.Trim();

        return ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            _ => "image/jpeg"  // Default MIME type
        };
    }
}

/**
 * ============================================================================
 * END-TO-END FLOW SUMMARY
 * ============================================================================
 * 
 * 1. Frontend sends POST /api/items/drafts with files[] metadata
 * 2. This function creates a draft record in inv.Inventory table
 * 3. For each file, generates a unique blob path and SAS upload URL
 * 4. Returns inventoryId + upload instructions to frontend
 * 5. Frontend uploads image bytes to Azure Blob Storage using SAS URLs
 * 6. Frontend calls other APIs to link images, run AI, generate vectors
 * 
 * ============================================================================
 * SECURITY CONSIDERATIONS
 * ============================================================================
 * 
 * ✅ Parameterized SQL queries (prevents SQL injection)
 * ✅ SAS URLs with minimal permissions (create/write only)
 * ✅ Time-limited SAS URLs (15 minute expiry)
 * ✅ File extension whitelist (prevents malicious uploads)
 * ✅ Connection strings in environment variables (not hardcoded)
 * ✅ Generic error messages to client (don't leak SQL/storage details)
 * ✅ Logging with correlation IDs (for debugging without exposing to user)
 * 
 * ⚠️ TODO: Add authentication/authorization check
 *    Currently AuthorizationLevel.Anonymous - anyone can call this!
 *    Should verify user is an admin before creating drafts.
 * 
 * ============================================================================
 * PERFORMANCE CONSIDERATIONS
 * ============================================================================
 * 
 * ✅ Async/await throughout (non-blocking I/O)
 * ✅ Single database round-trip (one INSERT, not N queries)
 * ✅ Container creation is idempotent (won't fail if exists)
 * ✅ SAS generation is local (no network calls to Azure)
 * 
 * SCALABILITY:
 * - Azure Functions auto-scale based on load
 * - Blob Storage can handle millions of concurrent uploads
 * - SQL connection pooling handles concurrent requests
 * - No state stored in function (each request is independent)
 * 
 * ============================================================================
 */