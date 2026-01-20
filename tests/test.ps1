<# 
End-to-end workflow test (PowerShell)

Prereqs:
- Azure Functions running locally (default): http://localhost:7071
- Two image files exist: .\test1.jpg and .\test2.jpg
- PowerShell 7+ recommended (Invoke-RestMethod + Invoke-WebRequest work fine)

This script:
1) POST /api/items/drafts  -> gets inventoryId + uploads[]
2) PUT each file to its SAS uploadUrl (Blob)
3) POST /api/items/{id}/images -> stores blobName(s) in inv.InventoryImage.ImagePath
4) POST /api/items/{id}/ai-prefill -> updates Name/Description/UnitPriceCents
5) PATCH /api/items/{id} -> user edits fields
6) POST /api/items/{id}/publish -> publish
7) POST /api/items/{id}/unpublish -> unpublish
#>

$ErrorActionPreference = "Stop"

# -----------------------------
# Config
# -----------------------------
$BaseUrl = "http://localhost:7071"
$test = "test9"
$Img1 = Join-Path $PSScriptRoot "/test-images/$test.png"

if (!(Test-Path $Img1)) { throw "Missing file: $Img1" }


# -----------------------------
# Helpers
# -----------------------------
function Assert-True([bool]$cond, [string]$message) {
  if (-not $cond) { throw "ASSERT FAILED: $message" }
}

function Put-Blob([string]$sasUrl, [string]$filePath, [string]$contentType) {
  $bytes = [System.IO.File]::ReadAllBytes($filePath)
  $headers = @{
    "x-ms-blob-type" = "BlockBlob"
    "Content-Type"   = $contentType
  }

  $resp = Invoke-WebRequest -Method Put -Uri $sasUrl -Headers $headers -Body $bytes
  Assert-True ($resp.StatusCode -in 200,201) "Blob PUT failed. Status=$($resp.StatusCode)"
}

# -----------------------------
# 1) Create draft + get SAS PUT URLs
# -----------------------------
Write-Host "1) Creating draft..." -ForegroundColor Cyan

$draftReq = @{
  titleHint = "Draft"
  notes     = "front table"
  files     = @(
    @{ fileName = "./test-images/$test.jpg"; contentType = "image/jpeg" }
  )
} | ConvertTo-Json -Depth 6

$draft = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/items/drafts" -ContentType "application/json" -Body $draftReq

Assert-True ($null -ne $draft.inventoryId -and $draft.inventoryId -gt 0) "draft.inventoryId missing/invalid"
Assert-True (![string]::IsNullOrWhiteSpace($draft.publicId)) "draft.publicId missing"
Assert-True ($draft.uploads.Count -ge 1) "draft.uploads missing/empty"

$InventoryId = [int]$draft.inventoryId
$PublicIdN   = [string]$draft.publicId

Write-Host "   inventoryId=$InventoryId publicId(N)=$PublicIdN uploads=$($draft.uploads.Count)" -ForegroundColor DarkGray

# -----------------------------
# 2) Upload files to Blob using SAS URLs
# -----------------------------
Write-Host "2) Uploading blobs (PUT to SAS URLs)..." -ForegroundColor Cyan

# Map local files to upload entries by index (1..n)
$localFiles = @($Img1, $Img2)

for ($i = 0; $i -lt $draft.uploads.Count; $i++) {
  $u = $draft.uploads[$i]
  $filePath = $localFiles[$i]
  $ct = $u.contentType
  Write-Host "   PUT index=$($u.index) blobName=$($u.blobName)" -ForegroundColor DarkGray
  Put-Blob -sasUrl $u.uploadUrl -filePath $filePath -contentType $ct
}

# -----------------------------
# 3) Attach images (store blobName into InventoryImage.ImagePath)
# -----------------------------
Write-Host "3) Attaching images (DB insert)..." -ForegroundColor Cyan

$imgBody = @{
  images = @(
    @{ imagePath = $draft.uploads[0].blobName; isPrimary = $true;  sortOrder = 1 }

  )
} | ConvertTo-Json -Depth 6

$attached = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/items/$InventoryId/images" -ContentType "application/json" -Body $imgBody

Assert-True ($attached.inventoryId -eq $InventoryId) "attach returned wrong inventoryId"
Assert-True ($attached.images.Count -ge 1) "attach returned no images"

Write-Host "   attached images=$($attached.images.Count)" -ForegroundColor DarkGray

# -----------------------------
# 4) AI prefill (Name/Description/UnitPriceCents only)
# -----------------------------
Write-Host "4) AI prefill..." -ForegroundColor Cyan

$aiReq = @{ overwrite = $true; maxImages = 2 } | ConvertTo-Json
$ai = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/items/$InventoryId/ai-prefill" -ContentType "application/json" -Body $aiReq

# Not all models will always return all fields, but UnitPriceCents should typically be set
Assert-True (![string]::IsNullOrWhiteSpace($ai.name)) "ai returned empty name"
Assert-True ($null -ne $ai.unitPriceCents) "ai returned null unitPriceCents"

Write-Host "   ai.name='$($ai.name)' ai.unitPriceCents=$($ai.unitPriceCents)" -ForegroundColor DarkGray

# -----------------------------
# 5) User edits listing (PATCH) - no isActive allowed here
# -----------------------------
<# Write-Host "5) Update item (user edits)..." -ForegroundColor Cyan

$updateReq = @{
  name = "Edited title"
  unitPriceCents = 2500
  description = "User-edited description."
} | ConvertTo-Json

$updated = Invoke-RestMethod -Method Patch -Uri "$BaseUrl/api/items/$InventoryId" -ContentType "application/json" -Body $updateReq

Assert-True ($updated.inventoryId -eq $InventoryId) "update returned wrong inventoryId"
Assert-True ($updated.name -eq "Edited title") "update did not set name"
Assert-True ($updated.unitPriceCents -eq 2500) "update did not set unitPriceCents"

Write-Host "   updated name='$($updated.name)' price=$($updated.unitPriceCents)" -ForegroundColor DarkGray #>

# -----------------------------
# 6) Publish
# -----------------------------
Write-Host "6) Publish..." -ForegroundColor Cyan

$resp = Invoke-WebRequest -Method Post `
  -Uri "$BaseUrl/api/items/$InventoryId/publish" `
  -ContentType "application/json" `
  -Body "{}" `
  -SkipHttpErrorCheck

Assert-True ($resp.StatusCode -eq 200) "publish status code was $($resp.StatusCode). Body: $($resp.Content)"

# Parse JSON to an object
$published = $resp.Content | ConvertFrom-Json

# Debug: show the parsed values
Write-Host "   publish parsed: isActive=$($published.isActive) isDraft=$($published.isDraft)" -ForegroundColor DarkGray

Assert-True ($published.isActive -eq $true)  "publish did not set isActive=true"
Assert-True ($published.isDraft  -eq $false) "publish did not set isDraft=false"


# -----------------------------
# 7) Unpublish
# -----------------------------
<# Write-Host "7) Unpublish..." -ForegroundColor Cyan

$unpublished = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/items/$InventoryId/unpublish" -ContentType "application/json" -Body "{}"

Assert-True ($unpublished.isActive -eq $false) "unpublish did not set isActive=false"
Assert-True ($unpublished.isDraft -eq $true) "unpublish did not set isDraft=true"

Write-Host "   unpublish isActive=$($unpublished.isActive) isDraft=$($unpublished.isDraft)" -ForegroundColor DarkGray

Write-Host "`nDONE. inventoryId=$InventoryId publicId(N)=$PublicIdN" -ForegroundColor Green #>
