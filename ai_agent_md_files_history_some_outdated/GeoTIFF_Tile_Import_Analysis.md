# GeoTIFF Import Comparison: Single File vs. Tiles (Directory)

## Executive Summary

After analyzing the codebase, significant gaps have been identified in the tile (directory) import functionality compared to single GeoTIFF file import. The single file import path is fully featured with validation, cropping support, elevation recalculation, and proper metadata propagation. The tile import is missing most of these features.

---

## Feature Comparison Matrix

| Feature | Single GeoTIFF File | GeoTIFF Tiles (Directory) |
|---------|:------------------:|:------------------------:|
| **Basic Import** | ? Full | ? Full |
| **WGS84 Bounding Box** | ? Computed with GDAL transformation | ?? Combined from tiles, no validation |
| **Projection Detection** | ? Full with `GetGeoTiffInfoExtended()` | ?? Only from first tile, not validated |
| **Projection WKT Storage** | ? `_geoTiffProjectionWkt` populated | ? Not captured |
| **GeoTransform Storage** | ? `_geoTiffGeoTransform` populated | ?? Only from first tile (incorrect for combined) |
| **Original Dimensions** | ? `_geoTiffOriginalWidth/Height` set | ? Set to first tile dimensions only |
| **Elevation Range** | ? `_geoTiffMinElevation/MaxElevation` | ? Combined across all tiles |
| **GeoTIFF Validation** | ? `ValidateGeoTiff()` called | ? No validation |
| **OSM Availability Check** | ? `_canFetchOsmData` / `_osmBlockedReason` | ? Not checked |
| **Native Pixel Size Display** | ? Shown in UI | ?? From first tile only (misleading) |
| **Real-World Extent Display** | ? Calculated correctly | ? Incorrect (uses first tile data) |
| **CropAnchorSelector** | ? Fully functional | ?? UI appears but data is wrong |
| **Interactive Cropping** | ? Full support | ? Not implemented |
| **Cropped Elevation Recalc** | ? `RecalculateCroppedElevation()` | ? Not supported |
| **Coordinate Transformer** | ? Created for cropped region | ? Not adjusted for combined tiles |
| **OSM Feature Alignment** | ? Correct with cropped bbox | ?? Likely misaligned |
| **Terrain Generation** | ? Handles cropping | ?? No cropping support for tiles |

---

## Detailed Gap Analysis

### 1. ReadGeoTiffMetadata() - Critical Gaps for Tiles

**Location:** `GenerateTerrain.razor.cs` lines 195-335

#### Single File Path (lines 210-280):

```csharp
// FIRST: Validate the GeoTIFF and log diagnostic info
_geoTiffValidationResult = reader.ValidateGeoTiff(_geoTiffPath);
_canFetchOsmData = _geoTiffValidationResult.CanFetchOsmData;
_osmBlockedReason = _geoTiffValidationResult.OsmBlockedReason;

// Read extended info to get WGS84 bounding box
var info = reader.GetGeoTiffInfoExtended(_geoTiffPath);
wgs84BoundingBox = info.Wgs84BoundingBox;
nativeBoundingBox = info.BoundingBox;
projectionName = info.ProjectionName;
projectionWkt = info.Projection;
geoTransform = info.GeoTransform;
originalWidth = info.Width;
originalHeight = info.Height;
```

#### Tile Directory Path (lines 282-365):

```csharp
// Directory with tiles - calculate combined bounding box
var tiffFiles = Directory.GetFiles(...)

foreach (var tiffFile in tiffFiles)
{
    var tileInfo = reader.GetGeoTiffInfoExtended(tiffFile);
    
    // ? MISSING: No ValidateGeoTiff() call
    // ? MISSING: No _canFetchOsmData check
    // ? MISSING: No _osmBlockedReason set
    
    // Capture projection from first tile
    if (firstProjectionName == null)
    {
        firstProjectionName = tileInfo.ProjectionName;
        firstProjectionWkt = tileInfo.Projection;
        firstGeoTransform = tileInfo.GeoTransform;  // ? Wrong for combined!
        firstWidth = tileInfo.Width;                 // ? Wrong for combined!
        firstHeight = tileInfo.Height;               // ? Wrong for combined!
    }
    
    // Bounding box combination OK
    // Elevation combination OK
}

// ? MISSING: Calculate combined dimensions
// ? MISSING: Calculate combined GeoTransform
// ? MISSING: Validate combined bounding box
```

---

### 2. CropAnchorSelector - Incorrect Data for Tiles

**Location:** `CropAnchorSelector.razor.cs`

The `CropAnchorSelector` receives:
- `OriginalWidth` = first tile width (wrong)
- `OriginalHeight` = first tile height (wrong)
- `NativePixelSizeMeters` = calculated from first tile's GeoTransform (wrong)

**Result:** The crop selector shows the wrong source size, wrong selection scale, and wrong geographic coordinates.

---

### 3. UI Information Display - Incorrect for Tiles

**Location:** `GenerateTerrain.razor` lines 179-213

```razor
@if (_geoTiffGeoTransform != null && _geoTiffOriginalWidth > 0)
{
    <MudText Typo="Typo.body2">
        <strong>DEM Resolution:</strong> @GetNativePixelSizeDescription()
    </MudText>
    <MudText Typo="Typo.body2">
        <strong>Source Size:</strong> @_geoTiffOriginalWidth × @_geoTiffOriginalHeight px
        (@GetRealWorldWidthKm().ToString("F1")km × @GetRealWorldHeightKm().ToString("F1")km)
    </MudText>
}
```

For tiles, this displays:
- **DEM Resolution:** First tile's resolution (correct if all tiles same resolution)
- **Source Size:** First tile dimensions (completely wrong!)
- **Real-world extent:** Calculated from first tile (wrong!)

---

### 4. RecalculateCroppedElevation() - Tiles Not Supported

**Location:** `GenerateTerrain.razor.cs` lines 460-510

```csharp
private async Task RecalculateCroppedElevation(CropResult cropResult)
{
    if (_heightmapSourceType == HeightmapSourceType.GeoTiffDirectory)
    {
        // ? Currently not implemented
        PubSubChannel.SendMessage(PubSubMessageType.Warning,
            "Cropped elevation recalculation for GeoTIFF directories is not yet supported. " +
            "Using full image elevation values.");
        return;
    }
    // ... single file implementation
}
```

---

### 5. Terrain Generation - Cropping Not Passed for Tiles

**Location:** `TerrainCreator.cs` `LoadFromGeoTiffDirectoryAsync()`

```csharp
private async Task<Image<L16>> LoadFromGeoTiffDirectoryAsync(...)
{
    var combiner = new GeoTiffCombiner();
    
    // ? MISSING: No crop parameters passed
    var result = await combiner.CombineAndImportAsync(geoTiffDirectory, parameters.Size);
    
    // Compare to single file:
    // ? if (parameters.CropGeoTiff && parameters.CropWidth > 0 && parameters.CropHeight > 0)
    //    result = reader.ReadGeoTiff(...cropParams);
}
```

---

### 6. GeoTiffCombiner - Missing Features

**Location:** `GeoTiffCombiner.cs`

**Missing:**
1. No crop support in `CombineAndImportAsync()`
2. No WGS84 transformation for combined bounding box
3. No validation of combined result
4. Returns only native bounding box
5. Doesn't return combined GeoTransform
6. Doesn't return combined dimensions

---

## Implementation Document: Making Tile Import Feature-Complete

### Phase 1: Fix Metadata Collection in ReadGeoTiffMetadata()

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor.cs`

**Changes needed:**

```csharp
// In the GeoTIFF directory branch of ReadGeoTiffMetadata():

// 1. Calculate COMBINED dimensions from all tiles
int combinedWidth = 0, combinedHeight = 0;
// Track tile positions to calculate total extent
List<(int x, int y, int width, int height)> tilePositions = new();

// 2. Calculate combined GeoTransform for the merged result
double[] combinedGeoTransform = new double[6];

// 3. Validate at least one tile
if (tiffFiles.Count > 0)
{
    var firstTileValidation = reader.ValidateGeoTiff(tiffFiles[0]);
    _geoTiffValidationResult = firstTileValidation;
    _canFetchOsmData = firstTileValidation.CanFetchOsmData;
    _osmBlockedReason = firstTileValidation.OsmBlockedReason;
}

// 4. After iterating all tiles, calculate combined dimensions:
// Combined width = (maxX - minX) / pixelSizeX
// Combined height = (maxY - minY) / pixelSizeY
combinedWidth = (int)Math.Round((nativeMaxX - nativeMinX) / firstGeoTransform[1]);
combinedHeight = (int)Math.Round((nativeMaxY - nativeMinY) / Math.Abs(firstGeoTransform[5]));

// 5. Create combined GeoTransform
combinedGeoTransform[0] = nativeMinX;           // Origin X
combinedGeoTransform[1] = firstGeoTransform[1]; // Pixel width
combinedGeoTransform[2] = 0;                     // Rotation X
combinedGeoTransform[3] = nativeMaxY;           // Origin Y (top)
combinedGeoTransform[4] = 0;                     // Rotation Y
combinedGeoTransform[5] = firstGeoTransform[5]; // Pixel height (negative)

// 6. Store combined values (not first tile values!)
originalWidth = combinedWidth;
originalHeight = combinedHeight;
geoTransform = combinedGeoTransform;
```

---

### Phase 2: Add Extended Tile Info Method

**File:** `BeamNgTerrainPoc\Terrain\GeoTiff\GeoTiffReader.cs`

**New method:**

```csharp
/// <summary>
/// Gets combined information about multiple GeoTIFF tiles in a directory.
/// </summary>
/// <param name="directoryPath">Directory containing GeoTIFF tiles</param>
/// <returns>Combined info with total dimensions, bounding box, etc.</returns>
public GeoTiffDirectoryInfoResult GetGeoTiffDirectoryInfoExtended(string directoryPath)
{
    // 1. Find all GeoTIFF files
    // 2. Validate first tile (or all tiles)
    // 3. Calculate combined bounding box
    // 4. Calculate combined dimensions
    // 5. Calculate combined GeoTransform
    // 6. Transform to WGS84
    // 7. Return comprehensive result
}
```

**New result class:**

```csharp
/// <summary>
/// Contains combined information about multiple GeoTIFF tiles.
/// </summary>
public class GeoTiffDirectoryInfoResult
{
    public int TileCount { get; init; }
    public int CombinedWidth { get; init; }
    public int CombinedHeight { get; init; }
    public GeoBoundingBox NativeBoundingBox { get; init; }
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }
    public double[] CombinedGeoTransform { get; init; }
    public string? Projection { get; init; }
    public string? ProjectionName { get; init; }
    public double? MinElevation { get; init; }
    public double? MaxElevation { get; init; }
    public List<GeoTiffTileInfo> Tiles { get; init; }
    public GeoTiffValidationResult? ValidationResult { get; init; }
    public bool CanFetchOsmData { get; init; }
    public string? OsmBlockedReason { get; init; }
}
```

---

### Phase 3: Add Cropping Support to GeoTiffCombiner

**File:** `BeamNgTerrainPoc\Terrain\GeoTiff\GeoTiffCombiner.cs`

**Update `CombineAndImportAsync`:**

```csharp
public async Task<GeoTiffImportResult> CombineAndImportAsync(
    string inputDirectory, 
    int? targetSize = null,
    int? cropOffsetX = null,
    int? cropOffsetY = null,
    int? cropWidth = null,
    int? cropHeight = null,
    string? tempDirectory = null)
{
    tempDirectory ??= Path.GetTempPath();
    var combinedPath = Path.Combine(tempDirectory, $"combined_{Guid.NewGuid():N}.tif");

    try
    {
        await CombineGeoTiffsAsync(inputDirectory, combinedPath);
        
        // Apply cropping to the combined result
        if (cropOffsetX.HasValue && cropOffsetY.HasValue && 
            cropWidth.HasValue && cropHeight.HasValue)
        {
            var reader = new GeoTiffReader();
            return reader.ReadGeoTiff(combinedPath, targetSize, 
                cropOffsetX, cropOffsetY, cropWidth, cropHeight);
        }
        
        return _reader.ReadGeoTiff(combinedPath, targetSize);
    }
    finally
    {
        // Clean up temporary file
        try { if (File.Exists(combinedPath)) File.Delete(combinedPath); }
        catch { /* Ignore cleanup errors */ }
    }
}
```

---

### Phase 4: Update TerrainCreator for Tile Cropping

**File:** `BeamNgTerrainPoc\Terrain\TerrainCreator.cs`

**Update `LoadFromGeoTiffDirectoryAsync`:**

```csharp
private async Task<Image<L16>> LoadFromGeoTiffDirectoryAsync(
    string geoTiffDirectory,
    TerrainCreationParameters parameters, 
    TerrainCreationLogger log)
{
    var combiner = new GeoTiffCombiner();

    // Apply cropping if enabled
    GeoTiffImportResult result;
    if (parameters.CropGeoTiff && parameters.CropWidth > 0 && parameters.CropHeight > 0)
    {
        log.Info($"Cropping combined GeoTIFF: offset ({parameters.CropOffsetX}, {parameters.CropOffsetY}), " +
                 $"size {parameters.CropWidth}x{parameters.CropHeight}");
        result = await combiner.CombineAndImportAsync(
            geoTiffDirectory, 
            parameters.Size,
            parameters.CropOffsetX,
            parameters.CropOffsetY,
            parameters.CropWidth,
            parameters.CropHeight);
    }
    else
    {
        result = await combiner.CombineAndImportAsync(geoTiffDirectory, parameters.Size);
    }
    
    // ... rest of method unchanged
}
```

---

### Phase 5: Implement RecalculateCroppedElevation for Tiles

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor.cs`

**Update `RecalculateCroppedElevation`:**

```csharp
private async Task RecalculateCroppedElevation(CropResult cropResult)
{
    if (_heightmapSourceType == HeightmapSourceType.GeoTiffDirectory)
    {
        // For directory mode, we need to:
        // 1. Combine tiles to a temp file
        // 2. Read the cropped elevation from the combined file
        // 3. Clean up temp file
        
        try
        {
            await Task.Run(async () =>
            {
                var combiner = new GeoTiffCombiner();
                var tempPath = Path.Combine(Path.GetTempPath(), $"combined_{Guid.NewGuid():N}.tif");
                
                try
                {
                    await combiner.CombineGeoTiffsAsync(_geoTiffDirectory!, tempPath);
                    
                    var reader = new GeoTiffReader();
                    var (croppedMin, croppedMax) = reader.GetCroppedElevationRange(
                        tempPath,
                        cropResult.OffsetX,
                        cropResult.OffsetY,
                        cropResult.CropWidth,
                        cropResult.CropHeight);

                    cropResult.CroppedMinElevation = croppedMin;
                    cropResult.CroppedMaxElevation = croppedMax;
                    
                    _maxHeight = (float)(croppedMax - croppedMin);
                    _terrainBaseHeight = (float)croppedMin;

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Cropped region elevation: {croppedMin:F1}m to {croppedMax:F1}m");
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            });
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not recalculate cropped elevation: {ex.Message}. Using combined values.");
        }
        return;
    }
    
    // ... existing single file code
}
```

---

### Phase 6: Fix UI to Display Combined Tile Info

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor`

No template changes needed - the existing display code will work correctly once the backing fields (`_geoTiffOriginalWidth`, `_geoTiffOriginalHeight`, `_geoTiffGeoTransform`) are populated with combined values instead of first-tile values.

---

## Implementation Priority

| Priority | Task | Effort | Impact |
|----------|------|--------|--------|
| **P1** | Fix dimension/transform collection in `ReadGeoTiffMetadata()` | Medium | High - Fixes crop selector display |
| **P1** | Add validation call for tiles | Low | High - Enables OSM warnings |
| **P2** | Create `GetGeoTiffDirectoryInfoExtended()` | Medium | Medium - Cleaner architecture |
| **P2** | Add cropping to `CombineAndImportAsync()` | Medium | High - Enables tile cropping |
| **P2** | Update `LoadFromGeoTiffDirectoryAsync()` | Low | High - Applies crop to generation |
| **P3** | Implement tile `RecalculateCroppedElevation()` | Medium | Medium - Better accuracy |

---

## Quick Win Fixes (Can Be Done Immediately)

1. **Set `_canFetchOsmData` and `_osmBlockedReason` for tiles** - Validate first tile
2. **Calculate combined dimensions** - Sum tile extents divided by pixel size
3. **Calculate combined GeoTransform** - Use combined origin and first tile's pixel size
4. **Add warning for tile cropping** - Inform user that cropping tiles is not yet supported

---

## Testing Checklist

After implementation, test with:

- [ ] Single GeoTIFF file (baseline)
- [ ] Single GeoTIFF file + cropping
- [ ] 2x2 grid of tiles (same size)
- [ ] 2x2 grid + cropping
- [ ] Mixed tile sizes (should warn/error)
- [ ] Tiles with different projections (should error)
- [ ] Tiles with gaps between them (should warn)

---

## Appendix: Key Files Reference

### Files to Modify

| File | Purpose |
|------|---------|
| `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor.cs` | Main UI logic - metadata collection |
| `BeamNgTerrainPoc\Terrain\GeoTiff\GeoTiffReader.cs` | Add directory info method |
| `BeamNgTerrainPoc\Terrain\GeoTiff\GeoTiffCombiner.cs` | Add cropping support |
| `BeamNgTerrainPoc\Terrain\TerrainCreator.cs` | Pass crop params for tiles |

### New Files to Create

| File | Purpose |
|------|---------|
| `BeamNgTerrainPoc\Terrain\GeoTiff\GeoTiffDirectoryInfoResult.cs` | Result class for combined tile info |

### Files That Will Work Without Changes (After Fixes)

| File | Reason |
|------|--------|
| `GenerateTerrain.razor` | Uses backing fields that will be correctly populated |
| `CropAnchorSelector.razor` | Receives parameters that will be correctly set |
| `CropAnchorSelector.razor.cs` | Logic is generic, works with any valid input |

---

## Architecture Diagram

```
???????????????????????????????????????????????????????????????????
?                    GenerateTerrain.razor.cs                      ?
?                                                                  ?
?  SelectGeoTiffDirectory() ??? ReadGeoTiffMetadata()             ?
?                                      ?                           ?
?                    ?????????????????????????????????????        ?
?                    ?                 ?                 ?        ?
?                    ?                 ?                 ?        ?
?            Single File         Tiles (Current)    Tiles (Fixed) ?
?                                                                  ?
?  ? ValidateGeoTiff()      ? No validation    ? ValidateGeoTiff?
?  ? GetInfoExtended()      ?? First tile only  ? Combined info ?
?  ? Combined dims          ? First tile dims  ? Calculated    ?
?  ? Combined transform     ? First tile       ? Calculated    ?
?  ? OSM check              ? Not done         ? From first    ?
?                                                                  ?
???????????????????????????????????????????????????????????????????
                              ?
                              ?
???????????????????????????????????????????????????????????????????
?                    CropAnchorSelector                            ?
?                                                                  ?
?  Receives: OriginalWidth, OriginalHeight, GeoTransform,         ?
?            NativePixelSizeMeters, OriginalBoundingBox            ?
?                                                                  ?
?  Single File: ? Correct values                                 ?
?  Tiles Now:   ? First tile values (wrong!)                     ?
?  Tiles Fixed: ? Combined values                                ?
?                                                                  ?
???????????????????????????????????????????????????????????????????
                              ?
                              ?
???????????????????????????????????????????????????????????????????
?                    TerrainCreator                                ?
?                                                                  ?
?  LoadFromGeoTiffAsync()           LoadFromGeoTiffDirectoryAsync()?
?  ? Passes crop params            ? No crop params (yet)       ?
?                                                                  ?
???????????????????????????????????????????????????????????????????
                              ?
                              ?
???????????????????????????????????????????????????????????????????
?                    GeoTiffCombiner                               ?
?                                                                  ?
?  CombineAndImportAsync()                                         ?
?  ? No crop support (yet)                                       ?
?  ? No WGS84 transform                                          ?
?  ? No combined GeoTransform returned                           ?
?                                                                  ?
???????????????????????????????????????????????????????????????????
```

---

## Summary

The tile import functionality is approximately **40% complete** compared to single file import. The main gaps are:

1. **Metadata collection** - Using first tile instead of combined values
2. **Validation** - No OSM availability check for tiles
3. **Cropping** - Not supported in the generation pipeline
4. **Elevation recalculation** - Not implemented for cropped tile regions

Implementing the changes in Phases 1-5 will bring tile support to feature parity with single file import.
