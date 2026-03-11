# XYZ Fast Import — Performance Optimization Plan

**Date:** 2026-03-11
**Status:** Implementation Plan
**Related:** `ai_agent_md_files_history_some_outdated/UNIFIED_ELEVATION_IMPORT_PLAN.md`

---

## Problem

When importing XYZ ASCII elevation files, GDAL's `Gdal.Open()` performs a full sequential scan of the entire text file to build its raster dataset model (determining grid dimensions, geotransform, etc.). For large files — e.g., 100+ DGM1 tiles totaling gigabytes — this causes significant delays before the user even sees the crop selector.

**Current import flow (slow):**
1. `AutoDetectEpsg()` → `Gdal.Open()` on first XYZ file (full text parse)
2. `ReadFromXyzFileAsync()` / `ReadFromXyzFilesAsync()` → `Gdal.Open()` on every file (full parse each)
3. `GetInfoFromDataset()` → `ComputeRasterMinMax()` scans all Z values for elevation stats

Even though no pixel data is loaded into memory, GDAL must read every line of every ASCII file just to determine grid structure. For a 1GB XYZ file (~33M lines), this takes significant time.

---

## Solution: Two-Phase Approach

### Phase 1 — Fast Scan (Import Time)
A custom line-by-line XYZ scanner that extracts boundary metadata **without GDAL**:
- Stream through files with `StreamReader`
- Parse only X, Y (and optionally Z) coordinates per line
- Track min/max bounds, determine pixel size from first few lines
- Calculate grid dimensions from bounds + pixel size
- Transform to WGS84 using GDAL `SpatialReference` (fast, no file I/O)

**Result:** Crop selector gets its data (bounds, dimensions, pixel size) in seconds even for gigabytes of data.

### Phase 2 — Process Only Crop Region (After User Selects Crop)
- GDAL is invoked **only** when the user clicks "Reduce GeoTIFF to Selection" or generates terrain
- Only the cropped region is processed, converting XYZ → GeoTIFF for just the selection
- **NEW: Use GDAL high-level APIs** (`Gdal.Warp` with `-te` or `Gdal.BuildVRT` + `Gdal.wrapper_GDALTranslate` with `-projwin`) for geographic bbox-based cropping instead of pixel-offset-based cropping

**Performance estimate:** Streaming ~30 bytes/line at ~300 MB/s = ~3 seconds per 1GB file. Parallel across 8 cores = 100 tiles in well under a minute.

---

## GDAL High-Level API Discovery

Verified via reflection that **MaxRev.Gdal.Core 3.10.0.306** exposes all GDAL utility wrapper methods in .NET:

| API | .NET Method | Equivalent CLI | Use Case |
|-----|------------|----------------|----------|
| **Warp** | `Gdal.Warp(dest, datasets[], GDALWarpAppOptions, ...)` | `gdalwarp -te xmin ymin xmax ymax` | Crop by bounding box, reproject, merge tiles |
| **Translate** | `Gdal.wrapper_GDALTranslate(dest, dataset, GDALTranslateOptions, ...)` | `gdal_translate -projwin` | Crop single raster by bbox |
| **BuildVRT** | `Gdal.BuildVRT(dest, filenames[], GDALBuildVRTOptions, ...)` | `gdalbuildvrt` | Create virtual mosaic of multiple tiles |

**Available option types:** `GDALWarpAppOptions`, `GDALTranslateOptions`, `GDALBuildVRTOptions`

### Key Insight: `Gdal.Warp` Can Do Everything

For the Phase 2 crop/reduce step, `Gdal.Warp` with `-te` flag can:
1. Open one or more XYZ files (GDAL handles the text parsing)
2. Crop to a bounding box in the **native CRS** (UTM meters) — no pixel offset conversion needed
3. Apply projection override for XYZ files (via `-s_srs`)
4. Output a single cropped GeoTIFF with embedded CRS
5. Handle multiple input tiles seamlessly (merges + crops in one pass)

This is **significantly simpler** than the current pixel-offset approach in `GeoTiffCombiner`.

### Alternative: `BuildVRT` + `Translate` Pipeline

For very large multi-tile datasets:
1. `Gdal.BuildVRT()` creates a virtual raster from all XYZ tiles (fast — just reads metadata)
2. `Gdal.wrapper_GDALTranslate()` with `-projwin` crops the VRT to the selection bbox → GeoTIFF
3. Only tiles overlapping the crop region are actually read

This may be more efficient than `Warp` for large tile counts since VRT creation is essentially free.

---

## Implementation

### 1. NEW FILE: `BeamNgTerrainPoc/Terrain/GeoTiff/XyzFastScanner.cs`

Core of the optimization. Static class with three public methods:

```csharp
public static class XyzFastScanner
{
    public class XyzScanResult
    {
        public double MinX, MaxX, MinY, MaxY;
        public double? MinZ, MaxZ;
        public double PixelSizeX, PixelSizeY;
        public int Width, Height;
        public long LineCount;
    }

    /// Scans a single XYZ file line-by-line. No GDAL.
    public static XyzScanResult ScanFile(string xyzPath, bool includeElevation = true);

    /// Scans multiple XYZ files in parallel and merges bounds.
    public static XyzScanResult ScanFiles(string[] xyzPaths,
        bool includeElevation = true, IProgress<string>? progress = null);

    /// Auto-detects EPSG from first data line coordinates. No GDAL.
    public static int? AutoDetectEpsg(string xyzPath);
}
```

**`ScanFile` logic:**
1. Open `StreamReader` (buffered)
2. Skip comment/header lines (`//`, `#`, or non-numeric first token)
3. Detect separator from first data line (try space/tab/semicolon — whichever yields 3+ numeric columns)
4. Parse first two data lines → `pixelSizeX = |line2.X - line1.X|`
5. Track first Y value; when Y changes → `pixelSizeY = |newY - firstY|`
6. Stream all remaining lines, tracking `minX, maxX, minY, maxY` (and `minZ, maxZ` if `includeElevation`)
7. Calculate: `Width = round((maxX - minX) / pixelSizeX) + 1`, same for Height

**`ScanFiles` logic:**
- `Parallel.For` across all files, each returns `XyzScanResult`
- Merge: global min/max across tiles, verify consistent pixel sizes, compute combined Width/Height
- Progress reporting every 20 files

**`AutoDetectEpsg` logic:**
- Read only the first data line, parse X and Y
- Apply coordinate range heuristics (same as current `GeoTiffReader.AutoDetectEpsg`)
- Return `25832` for German UTM 32N ranges, `null` otherwise

**Edge cases to handle:**
- Non-regular grids: if consecutive X values are identical (column-major order), read first N lines to find min X spacing
- Mixed whitespace separators: split on any whitespace
- Empty files / all-comment files: throw `InvalidOperationException` with descriptive message
- Inconsistent pixel sizes across tiles: log warning, use first tile's pixel size

---

### 2. MODIFY: `BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs`

**Add** `ConvertXyzScanToMetadata()` private helper:
```csharp
private GeoTiffMetadataResult ConvertXyzScanToMetadata(
    XyzFastScanner.XyzScanResult scan, int epsgCode, int tileCount = 1)
{
    // Build GeoTransform: [minX, pixelSizeX, 0, maxY, 0, -pixelSizeY]
    // Build native GeoBoundingBox from scan bounds
    // Transform to WGS84 using SpatialReference (GDAL, but fast — no file I/O)
    // Return GeoTiffMetadataResult with all fields populated
}
```

**Replace** `ReadFromXyzFileAsync()` body:
```csharp
public async Task<GeoTiffMetadataResult> ReadFromXyzFileAsync(string xyzPath, int epsgCode)
{
    return await Task.Run(() =>
    {
        var scan = XyzFastScanner.ScanFile(xyzPath);
        return ConvertXyzScanToMetadata(scan, epsgCode);
    });
}
```

**Replace** `ReadFromXyzFilesAsync()` body:
```csharp
public async Task<GeoTiffMetadataResult> ReadFromXyzFilesAsync(
    string[] xyzPaths, int epsgCode, IProgress<string>? progress = null)
{
    return await Task.Run(() =>
    {
        var scan = XyzFastScanner.ScanFiles(xyzPaths, includeElevation: true, progress);
        return ConvertXyzScanToMetadata(scan, epsgCode, xyzPaths.Length);
    });
}
```

**Note:** Need `GetProjectionWktFromEpsg()` — currently `private static` in `GeoTiffReader.cs`. Duplicate the 5-line method in `GeoTiffMetadataService` since it's in a different project. The method only uses GDAL's `SpatialReference.ImportFromEPSG()` + `ExportToWkt()`.

---

### 3. MODIFY: `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportService.cs`

**Replace** `AutoDetectEpsg` call (line ~261):
```csharp
// Before (slow — opens entire file via GDAL):
var detectedEpsg = GeoTiffReader.AutoDetectEpsg(firstXyz);

// After (fast — reads first line only):
var detectedEpsg = XyzFastScanner.AutoDetectEpsg(firstXyz);
```

---

### 4. MODIFY: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

#### 4a. Fix `ReduceGeoTiffToCropAsync()` for single XYZ without cached GeoTIFF

**Problem:** With the fast scanner, `_cachedCombinedGeoTiffPath` won't exist at import time for single XYZ files. The current code (line 1438-1441) silently returns, making the Reduce button do nothing.

**Fix:** Add a fallback that uses `CombineXyzAndCropDirectAsync` with a single-element array:

```csharp
// At line 1438, replace the empty else { return; } with:
else if (!string.IsNullOrEmpty(_state.XyzPath))
{
    // Single XYZ, no cached GeoTIFF — combine-and-crop directly
    croppedPath = await _geoTiffService.CombineXyzAndCropDirectAsync(
        new[] { _state.XyzPath }, _xyzEpsgCode,
        _cropResult.OffsetX, _cropResult.OffsetY,
        _cropResult.CropWidth, _cropResult.CropHeight);
}
else
{
    return;
}
```

#### 4b. Fix `RecalculateCroppedElevation()` for single XYZ

**Problem:** At line 1561-1569, for single XYZ without `_cachedCombinedGeoTiffPath`, the code calls `GetCroppedElevationRangeAsync` with the raw `.xyz` path. GDAL can open this, but it requires the full file parse we're trying to avoid.

**Fix:** Lazy-create the cached GeoTIFF on first crop adjustment (same pattern as multi-XYZ at line 1550-1555):

```csharp
// Replace lines 1561-1569 with:
else
{
    // Single XYZ: lazy-create cached GeoTIFF on first crop adjustment
    if (string.IsNullOrEmpty(_cachedCombinedGeoTiffPath) ||
        !File.Exists(_cachedCombinedGeoTiffPath))
    {
        if (!string.IsNullOrEmpty(_state.XyzPath))
            _cachedCombinedGeoTiffPath =
                await _geoTiffService.CombineXyzTilesAsync(
                    new[] { _state.XyzPath }, _xyzEpsgCode);
    }
    if (string.IsNullOrEmpty(_cachedCombinedGeoTiffPath) ||
        !File.Exists(_cachedCombinedGeoTiffPath))
        return;
    elevationRange = await _geoTiffService.GetCroppedElevationRangeAsync(
        _cachedCombinedGeoTiffPath,
        cropResult.OffsetX, cropResult.OffsetY,
        cropResult.CropWidth, cropResult.CropHeight);
}
```

This means the GDAL conversion happens only when the user first moves the crop selector — not at import time.

---

### 5. ENHANCEMENT: Use `Gdal.Warp` for Phase 2 Crop/Reduce (optional improvement)

The current Phase 2 pipeline uses pixel-offset-based cropping (`ReadRaster` with offsets). We can optionally enhance this with GDAL's high-level `Warp` API for cleaner, bbox-based cropping.

**New method in `GeoTiffMetadataService.cs`:**

```csharp
/// <summary>
/// Crops one or more XYZ files to a geographic bounding box using GDAL Warp.
/// Outputs a single cropped GeoTIFF with embedded CRS.
/// This replaces the pixel-offset combine+crop pipeline for XYZ files.
/// </summary>
public async Task<string> WarpXyzToCroppedGeoTiffAsync(
    string[] xyzPaths, int epsgCode,
    double minX, double minY, double maxX, double maxY)
{
    return await Task.Run(() =>
    {
        GeoTiffReader.InitializeGdal();

        var outputPath = Path.Combine(AppPaths.TempFolder,
            $"xyz_cropped_{Guid.NewGuid():N}.tif");

        // Build warp options: crop to bounding box in native CRS
        var srsString = $"EPSG:{epsgCode}";
        var options = new GDALWarpAppOptions(new[]
        {
            "-te", minX.ToString(CultureInfo.InvariantCulture),
                   minY.ToString(CultureInfo.InvariantCulture),
                   maxX.ToString(CultureInfo.InvariantCulture),
                   maxY.ToString(CultureInfo.InvariantCulture),
            "-s_srs", srsString,    // Source CRS (XYZ files lack embedded CRS)
            "-t_srs", srsString,    // Target CRS (keep same)
            "-of", "GTiff",         // Output format
            "-co", "COMPRESS=LZW",  // Compress output
            "-co", "TILED=YES"      // Tile for efficient random access
        });

        // Open all input XYZ files as datasets
        var datasets = xyzPaths.Select(p => Gdal.Open(p, Access.GA_ReadOnly)).ToArray();
        try
        {
            using var result = Gdal.Warp(outputPath, datasets, options, null, null);
            result?.FlushCache();
        }
        finally
        {
            foreach (var ds in datasets) ds?.Dispose();
        }

        return outputPath;
    });
}
```

**Advantages over pixel-offset approach:**
- Works directly with geographic bounding box (no pixel offset conversion needed)
- Handles multiple input files natively (no manual tile stitching)
- Sets CRS on output automatically
- Compressed + tiled output for efficient downstream use
- GDAL internally skips non-overlapping tiles

**Integration point:** The `ReduceGeoTiffToCropAsync()` XYZ branch can call this instead of the current `CombineXyzAndCropDirectAsync()` path. The crop selector already computes the native CRS bounding box (available from `_geoTiffNativeBoundingBox` + crop offsets), so we just need to pass geographic coordinates instead of pixel offsets.

**Alternative: `BuildVRT` + `Translate` for very large tile counts:**

```csharp
// Step 1: Create virtual mosaic (instant — no data read)
var vrtPath = Path.Combine(AppPaths.TempFolder, $"xyz_mosaic_{Guid.NewGuid():N}.vrt");
var vrtOptions = new GDALBuildVRTOptions(new[] { "-a_srs", $"EPSG:{epsgCode}" });
using var vrt = Gdal.BuildVRT(vrtPath, xyzPaths, vrtOptions, null, null);
vrt?.FlushCache();

// Step 2: Crop VRT to selection → GeoTIFF (only reads overlapping tiles)
var translateOptions = new GDALTranslateOptions(new[]
{
    "-projwin", minX.ToString(), maxY.ToString(), maxX.ToString(), minY.ToString(),
    "-of", "GTiff",
    "-co", "COMPRESS=LZW"
});
using var cropped = Gdal.wrapper_GDALTranslate(outputPath, vrt, translateOptions, null, null);
cropped?.FlushCache();
```

**Note:** Whether `Gdal.Warp` or `BuildVRT+Translate` is better for XYZ files specifically needs testing. GDAL's Warp may need to fully parse each XYZ file even if it doesn't overlap the crop region (since XYZ files don't have spatial indexing). The VRT approach might be smarter about skipping non-overlapping tiles. This should be verified empirically.

**Decision:** Implement as optional enhancement. Keep the existing pixel-offset pipeline as fallback. Test `Gdal.Warp` with real XYZ data first to verify behavior and performance before committing to the approach.

---

## File Summary

| # | File | Action | Description |
|---|------|--------|-------------|
| 1 | `BeamNgTerrainPoc/Terrain/GeoTiff/XyzFastScanner.cs` | **NEW** | Custom line-by-line XYZ scanner (no GDAL) |
| 2 | `BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs` | MODIFY | Replace XYZ metadata methods with fast scanner; add `ConvertXyzScanToMetadata`; optionally add `WarpXyzToCroppedGeoTiffAsync` |
| 3 | `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportService.cs` | MODIFY | Replace `AutoDetectEpsg` call |
| 4 | `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | MODIFY | Fix Reduce button for single XYZ; fix lazy-load in crop elevation recalc |

**Files NOT changed:** All existing GDAL-based XYZ reading methods in `GeoTiffReader.cs` are preserved. They're still used by the terrain generation pipeline (`ReadXyz()`, `LoadFromXyzAsync()`, etc.) and by `CombineXyzTilesAsync()`. The fast scanner replaces only the import-time metadata path.

---

## Verification

1. **Single small XYZ file:** Import → verify crop selector shows correct bounds and elevation → Reduce → verify GeoTIFF created → Generate terrain
2. **Single large XYZ file (1GB+):** Import → verify it completes in seconds (not minutes) → adjust crop → verify elevation updates (lazy GeoTIFF creation) → Reduce → verify output
3. **Multiple XYZ tiles (100+ files):** Import → verify parallel scan with progress → verify combined bounds correct → crop → Reduce → verify combined+cropped GeoTIFF
4. **ZIP with XYZ files:** Import ZIP → verify extraction + fast scan → crop + Reduce
5. **EPSG auto-detection:** Import German DGM1 tiles → verify 25832 detected from first line (no GDAL open)
6. **EPSG change:** After fast import → change EPSG code → verify metadata re-reads with new projection (still fast)
7. **Reduce button end-to-end:** For all XYZ variants (single, multi, with/without prior crop adjustment) → verify Reduce creates a valid GeoTIFF → verify source switches to GeoTiffFile → verify subsequent operations use GeoTIFF
8. **Edge cases:** Files with comment headers, semicolon separators, very small files (< 10 lines)
