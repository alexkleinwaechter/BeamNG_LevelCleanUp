# Tile Filtering During Reduce — Only Combine Overlapping Tiles

**Date:** 2026-03-11
**Status:** Implemented
**Related:** `ai_docs/2026-03-11_XYZ_FAST_IMPORT_PLAN.md`

---

## Context

When importing 395 XYZ tiles and pressing Reduce, the code currently passes ALL 395 tiles to `CombineXyzTilesAsync` → `CombineFilesInternal`, which opens every file via GDAL even though only a few tiles overlap the crop region. This wastes enormous time.

The fast scanner already knows each tile's bounding box. We should use this to filter down to only the overlapping tiles before combining.

This applies to both XYZ tiles and GeoTIFF directory tiles.

---

## Implementation Summary

### 1. Per-tile bounds stored during import (Done)

**`BeamNgTerrainPoc/Terrain/GeoTiff/XyzFastScanner.cs`:**

Added `TileBoundsInfo` class (top-level, immutable with `init` properties):
```csharp
public class TileBoundsInfo
{
    public required string FilePath { get; init; }
    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinY { get; init; }
    public double MaxY { get; init; }
}
```

Added `List<TileBoundsInfo>? TileBounds` property to `XyzScanResult`.

Populated `TileBounds` in `ScanFiles` for both single-file and multi-file paths.

**`BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs`:**

Added `List<TileBoundsInfo>? TileBounds` property to `GeoTiffMetadataResult`.

Populated in `ConvertXyzScanToMetadata()` from `scan.TileBounds`.

Populated in `ReadFromDirectoryAsync()` by converting `GeoTiffTileInfo` to `TileBoundsInfo`:
```csharp
var tileBounds = dirInfo.Tiles.Select(t => new TileBoundsInfo
{
    FilePath = t.FilePath,
    MinX = t.BoundingBox.MinLongitude,
    MaxX = t.BoundingBox.MaxLongitude,
    MinY = t.BoundingBox.MinLatitude,
    MaxY = t.BoundingBox.MaxLatitude
}).ToList();
```

---

### 2. Tile bounds stored in page state (Done)

**`BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`:**

- Added field `private List<TileBoundsInfo>? _tileBoundsInfo;`
- Stored in `ApplyGeoTiffMetadata()` from `result.TileBounds`
- Cleared after reduce (`_tileBoundsInfo = null`) when source switches to single file

---

### 3. Tile filtering helper (Done)

**`XyzFastScanner.FilterTilesByBbox`:**
```csharp
public static string[] FilterTilesByBbox(
    List<TileBoundsInfo> tiles,
    double cropMinX, double cropMinY, double cropMaxX, double cropMaxY)
{
    return tiles
        .Where(t => t.MaxX > cropMinX && t.MinX < cropMaxX &&
                    t.MaxY > cropMinY && t.MinY < cropMaxY)
        .Select(t => t.FilePath)
        .ToArray();
}
```

---

### 4. Filter before combine in `ReduceGeoTiffToCropAsync` (Done)

**`GenerateTerrain.razor.cs` — `FilterOverlappingTiles` helper:**

Computes crop bbox in native CRS, filters tiles, and **adjusts pixel offsets** to be relative to the filtered tiles' combined extent (not the full mosaic):

```csharp
private (string[] Files, int OffsetX, int OffsetY)? FilterOverlappingTiles(CropResult crop)
{
    if (_tileBoundsInfo == null || _geoTiffGeoTransform == null)
        return null;

    var gt = _geoTiffGeoTransform;
    var pixelSizeX = gt[1];
    var pixelSizeY = Math.Abs(gt[5]);

    var cropMinX = gt[0] + crop.OffsetX * pixelSizeX;
    var cropMaxX = gt[0] + (crop.OffsetX + crop.CropWidth) * pixelSizeX;
    var cropMaxY = gt[3] - crop.OffsetY * pixelSizeY;
    var cropMinY = gt[3] - (crop.OffsetY + crop.CropHeight) * pixelSizeY;

    var filtered = XyzFastScanner.FilterTilesByBbox(
        _tileBoundsInfo, cropMinX, cropMinY, cropMaxX, cropMaxY);

    if (filtered.Length == 0)
        return (filtered, 0, 0);

    // Adjust offsets: crop offsets are relative to the full mosaic origin,
    // but the combine methods compute their origin from filtered tiles' min bounds.
    var filteredBounds = _tileBoundsInfo
        .Where(t => filtered.Contains(t.FilePath));
    var filteredMinX = filteredBounds.Min(t => t.MinX);
    var filteredMaxY = filteredBounds.Max(t => t.MaxY);

    var adjustedOffsetX = (int)Math.Round((cropMinX - filteredMinX) / pixelSizeX);
    var adjustedOffsetY = (int)Math.Round((filteredMaxY - cropMaxY) / pixelSizeY);

    return (filtered, adjustedOffsetX, adjustedOffsetY);
}
```

Applied in `ReduceGeoTiffToCropAsync` for:
- GeoTIFF directory branch → `CombineAndCropDirectAsync(filteredFiles, ...)`
- Multi-XYZ branch → `CombineXyzAndCropDirectAsync(filteredFiles, ...)`

Both use adjusted offsets when constructing the `CropResult` passed to the combine method.

---

### 5. Filter for `RecalculateCroppedElevation` (Done)

Same `FilterOverlappingTiles` helper applied in `RecalculateCroppedElevation` for:
- GeoTIFF directory branch → `GetCroppedElevationRangeFromTilesAsync(filteredFiles, ...)`
- Multi-XYZ branch → `CombineXyzTilesAsync` with filtered files and adjusted crop

---

### 6. New method overloads in `GeoTiffMetadataService` (Done)

Added overloads that accept a file list instead of scanning a directory:
- `CombineAndCropDirectAsync(string[] inputFiles, ...)` — for GeoTIFF tiles
- `GetCroppedElevationRangeFromTilesAsync(string[] inputFiles, ...)` — for elevation range

---

## Bugs Found and Fixed During Implementation

### Bug 1: "Crop region exceeds image bounds" after filtering

**Problem:** When passing only filtered tiles to `CombineAndCropDirect`, the method computes its origin from the filtered tiles' minimum bounds, making the virtual canvas smaller. But crop offsets were still relative to the full mosaic origin, causing `offsetX + cropWidth > combinedWidth`.

**Fix:** `FilterOverlappingTiles` computes adjusted offsets relative to the filtered tiles' combined extent:
```
adjustedOffsetX = (cropMinX - filteredMinX) / pixelSizeX
adjustedOffsetY = (filteredMaxY - cropMaxY) / pixelSizeY
```

### Bug 2: "PROJ: proj_create_from_database: Cannot find proj.db"

**Problem:** The XYZ fast scanner path doesn't use GDAL, so when importing XYZ files without first loading a preset (which would trigger GDAL init), `SpatialReference` in `ConvertXyzScanToMetadata` couldn't find `proj.db`.

**Fix:** Added `GeoTiffReader.InitializeGdal()` calls before all `SpatialReference` usages in `GeoTiffMetadataService.cs`:
- In `GetProjectionWktFromEpsg()`
- In `ConvertXyzScanToMetadata()`

---

## Additional Fix: OSM Feature Selector Bounding Box Reactivity

### Problem

`TerrainMaterialSettings.razor` (rendered inside `MudDropContainer`'s `ItemRenderer`) received a stale `GeoBoundingBox` parameter when the crop changed. The building selector on the main page worked correctly (called directly from `GenerateTerrain.razor.cs` where `EffectiveBoundingBox` is fresh), but material-level OSM feature selection loaded features for the uncropped geographic area.

Root cause: `MudDropContainer.Refresh()` doesn't reliably re-pass changed parameters to child components rendered via `ItemRenderer`.

### Fix

**`BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`:**

Wrapped entire page content with a `CascadingValue`:
```razor
<CascadingValue Value="@EffectiveBoundingBox" Name="EffectiveBoundingBox">
<ErrorBoundary>
    ...
</ErrorBoundary>
</CascadingValue>
```

**`BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`:**

Added cascading parameter and `ActiveBoundingBox` property:
```csharp
[CascadingParameter(Name = "EffectiveBoundingBox")]
private GeoBoundingBox? CascadingBoundingBox { get; set; }

private GeoBoundingBox? ActiveBoundingBox => CascadingBoundingBox ?? GeoBoundingBox;
private bool HasGeoBoundingBox => ActiveBoundingBox != null;
```

Updated `OpenOsmFeatureSelector` to use `ActiveBoundingBox` instead of `GeoBoundingBox`.

---

## Crop-to-native-CRS conversion

Given `_geoTiffGeoTransform = [originX, pixelSizeX, 0, originY, 0, -pixelSizeY]`:
- `cropMinX = originX + offsetX * pixelSizeX`
- `cropMaxX = originX + (offsetX + cropWidth) * pixelSizeX`
- `cropMaxY = originY - offsetY * pixelSizeY`
- `cropMinY = originY - (offsetY + cropHeight) * pixelSizeY`

A tile overlaps if: `tile.MaxX > cropMinX && tile.MinX < cropMaxX && tile.MaxY > cropMinY && tile.MinY < cropMaxY`

---

## Files modified

| # | File | Changes |
|---|------|---------|
| 1 | `BeamNgTerrainPoc/Terrain/GeoTiff/XyzFastScanner.cs` | Added `TileBoundsInfo` class, `TileBounds` on `XyzScanResult`, `FilterTilesByBbox` static method, populated `TileBounds` in `ScanFiles` |
| 2 | `BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs` | Added `TileBounds` to `GeoTiffMetadataResult`, populated in `ConvertXyzScanToMetadata` and `ReadFromDirectoryAsync`, added `CombineAndCropDirectAsync` and `GetCroppedElevationRangeFromTilesAsync` overloads accepting file lists, added `GeoTiffReader.InitializeGdal()` calls |
| 3 | `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | Added `_tileBoundsInfo` field, `FilterOverlappingTiles` helper with adjusted offsets, filtering in `ReduceGeoTiffToCropAsync` and `RecalculateCroppedElevation`, cleared bounds after reduce |
| 4 | `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Wrapped page with `CascadingValue` for `EffectiveBoundingBox` |
| 5 | `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Added `CascadingParameter` for bounding box, `ActiveBoundingBox` property, updated OSM selector to use it |

**Files NOT changed:** `GeoTiffCombiner.cs` methods stay the same — they just receive fewer input files. `GeoTiffReader.cs` unchanged.

---

## Verification

1. [x] Load 100+ XYZ tiles → crop to a small region → Reduce → verify only a few tiles are processed
2. [ ] Load GeoTIFF directory tiles → crop → Reduce → verify filtered
3. [x] Verify recalculate-elevation also uses filtering
4. [ ] Edge case: crop covers all tiles → all should be passed through
5. [ ] Edge case: crop covers no tiles → should handle gracefully
6. [x] OSM feature selector in `TerrainMaterialSettings` uses cropped bounding box
7. [x] XYZ import without loading preset first (GDAL not pre-initialized) works correctly
