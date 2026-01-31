# Implementation Guide: Auto-Export OSM Layers as PNG Files

## Overview

This document describes the implementation plan for automatically exporting all available OSM feature types as 8-bit black and white PNG layer maps to the `MT_TerrainGeneration/osm_layer` subfolder during terrain generation.

**Goal**: When terrain generation is triggered with a GeoTIFF-based heightmap and valid WGS84 bounding box (enabling OSM data download from Overpass API), the system should silently export a PNG layer map for **every OSM feature type** available in `OsmFeatureSelectorDialog.razor`, not just the ones the user selected for materials.

## Key Requirements

1. **Automatic & Silent**: No user parameter needed - just happens as part of terrain generation
2. **GeoTIFF + OSM Condition**: Only execute when:
   - HeightmapSourceType is `GeoTiffFile` or `GeoTiffDirectory`
   - `_canFetchOsmData` is `true` (valid WGS84 bounding box available)
3. **Output Location**: `{workingDirectory}/MT_TerrainGeneration/osm_layer/`
4. **File Format**: 8-bit grayscale PNG (black=0, white=255)
5. **Feature Coverage**: Export ALL feature types from `OsmFeatureSelectorDialog.FeatureGroup`, not just user-selected ones

## Current Architecture Understanding

### OSM Data Flow

```
1. User selects GeoTIFF file/folder
   ?
2. GenerateTerrain.ReadGeoTiffMetadata()
   - Extracts WGS84 bounding box
   - Validates OSM availability ? sets _canFetchOsmData
   ?
3. User configures materials (optional OSM features per material)
   ?
4. TerrainGenerationOrchestrator.ExecuteAsync()
   - Fetches OSM data via OverpassApiService (if any material uses OSM)
   - Uses OsmQueryCache for caching
   - Processes materials ? creates layer maps
```

### Key Classes Involved

| Class | Location | Purpose |
|-------|----------|---------|
| `TerrainGenerationOrchestrator` | `BeamNG_LevelCleanUp/BlazorUI/Services/` | Orchestrates terrain generation |
| `OsmGeometryProcessor` | `BeamNgTerrainPoc/Terrain/Osm/Processing/` | Transforms OSM to layer maps |
| `OverpassApiService` | `BeamNgTerrainPoc/Terrain/Osm/Services/` | Fetches OSM data |
| `OsmQueryCache` | `BeamNgTerrainPoc/Terrain/Osm/Services/` | Caches OSM results |
| `OsmFeatureSelectorDialog` | `BeamNG_LevelCleanUp/BlazorUI/Components/` | Defines FeatureGroup structure |

### OsmFeatureSelectorDialog.FeatureGroup Structure

```csharp
public class FeatureGroup
{
    public string Category { get; set; }      // e.g., "highway", "landuse", "natural"
    public string SubCategory { get; set; }   // e.g., "primary", "forest", "water"
    public OsmGeometryType GeometryType { get; set; }  // LineString or Polygon
    public List<OsmFeature> Features { get; set; }
    
    public string GroupKey => $"{Category}|{SubCategory}|{GeometryType}";
    public string DisplayName { get; }
}
```

## Implementation Plan

### Phase 1: Create OSM Layer Export Service

**New File**: `BeamNgTerrainPoc/Terrain/Osm/Services/OsmLayerExporter.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Exports ALL OSM feature types as individual PNG layer maps.
/// This is called automatically during terrain generation when OSM data is available.
/// </summary>
public class OsmLayerExporter
{
    /// <summary>
    /// Exports all OSM feature types to PNG layer maps.
    /// </summary>
    /// <param name="osmResult">The OSM query result (from cache or API)</param>
    /// <param name="effectiveBoundingBox">The WGS84 bounding box (possibly cropped)</param>
    /// <param name="coordinateTransformer">Optional GDAL transformer for projected CRS</param>
    /// <param name="terrainSize">Size of terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="outputFolder">Target folder for osm_layer subfolder</param>
    /// <returns>Number of layer files exported</returns>
    public async Task<int> ExportAllOsmLayersAsync(
        OsmQueryResult osmResult,
        GeoBoundingBox effectiveBoundingBox,
        GeoCoordinateTransformer? coordinateTransformer,
        int terrainSize,
        float metersPerPixel,
        string outputFolder);
}
```

### Phase 2: Build Feature Groups from OSM Result

The `OsmFeatureSelectorDialog` builds feature groups dynamically. We need to replicate this logic:

```csharp
private List<FeatureGroupInfo> BuildFeatureGroups(OsmQueryResult osmResult)
{
    return osmResult.Features
        .GroupBy(f => new { f.Category, f.SubCategory, f.GeometryType })
        .Select(g => new FeatureGroupInfo
        {
            Category = g.Key.Category,
            SubCategory = g.Key.SubCategory,
            GeometryType = g.Key.GeometryType,
            Features = g.ToList(),
            // Generate safe filename: category_subcategory_geometrytype.png
            // e.g., highway_primary_LineString.png, landuse_forest_Polygon.png
            SafeFileName = SanitizeFileName($"{g.Key.Category}_{g.Key.SubCategory}_{g.Key.GeometryType}")
        })
        .Where(g => g.Features.Count > 0)
        .OrderBy(g => g.Category)
        .ThenBy(g => g.SubCategory)
        .ToList();
}
```

### Phase 3: Rasterize Each Feature Group

For each feature group:

1. **Lines (Roads)** ? Use `OsmGeometryProcessor.RasterizeLinesToLayerMap()` with a default width (e.g., 20 pixels or derive from road type)
2. **Polygons (Areas)** ? Use `OsmGeometryProcessor.RasterizePolygonsToLayerMap()`

```csharp
private async Task ExportFeatureGroupAsync(
    FeatureGroupInfo group,
    GeoBoundingBox bbox,
    GeoCoordinateTransformer? transformer,
    int terrainSize,
    float metersPerPixel,
    string outputFolder)
{
    var processor = new OsmGeometryProcessor();
    if (transformer != null)
        processor.SetCoordinateTransformer(transformer);
    
    byte[,] layerMap;
    
    if (group.GeometryType == OsmGeometryType.LineString)
    {
        // For lines, use a sensible width based on road type
        var lineWidthMeters = GetDefaultLineWidth(group.Category, group.SubCategory);
        var lineWidthPixels = lineWidthMeters / metersPerPixel;
        layerMap = processor.RasterizeLinesToLayerMap(
            group.Features, bbox, terrainSize, lineWidthPixels);
    }
    else
    {
        layerMap = processor.RasterizePolygonsToLayerMap(
            group.Features, bbox, terrainSize);
    }
    
    // Save as 8-bit PNG
    var filePath = Path.Combine(outputFolder, $"{group.SafeFileName}.png");
    await SaveLayerMapAsync(layerMap, filePath);
}
```

### Phase 4: Integrate into TerrainGenerationOrchestrator

**Location**: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`

Add call after OSM data is fetched:

```csharp
// In ExecuteInternalAsync(), after fetching OSM data:

// NEW: Export ALL OSM layers to osm_layer subfolder
if (osmQueryResult != null && effectiveBoundingBox != null && state.CanFetchOsmData)
{
    var osmLayerExporter = new OsmLayerExporter();
    var osmLayerOutputFolder = Path.Combine(debugPath, "osm_layer");
    
    var exportedCount = await osmLayerExporter.ExportAllOsmLayersAsync(
        osmQueryResult,
        effectiveBoundingBox,
        coordinateTransformer,
        state.TerrainSize,
        state.MetersPerPixel,
        osmLayerOutputFolder);
    
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Exported {exportedCount} OSM layer maps to osm_layer folder");
}
```

### Phase 5: Handle Road Line Widths

Different road types should have different default widths:

```csharp
private static float GetDefaultLineWidth(string category, string subCategory)
{
    if (category != "highway")
        return 10.0f; // Default 10 meters for non-roads
    
    return subCategory switch
    {
        "motorway" => 20.0f,
        "trunk" => 18.0f,
        "primary" => 16.0f,
        "secondary" => 14.0f,
        "tertiary" => 12.0f,
        "residential" => 10.0f,
        "service" => 6.0f,
        "track" => 4.0f,
        "path" => 2.0f,
        "footway" => 2.0f,
        "cycleway" => 3.0f,
        _ => 8.0f // Default for unrecognized road types
    };
}
```

## Output Folder Structure

```
{levelFolder}/
??? MT_TerrainGeneration/
    ??? {material}_osm_layer.png          # Existing: material-specific layers
    ??? analysis_preview.png               # Existing: analysis output
    ??? osm_layer/                         # NEW: All OSM feature layers
        ??? highway_motorway_LineString.png
        ??? highway_primary_LineString.png
        ??? highway_secondary_LineString.png
        ??? highway_residential_LineString.png
        ??? highway_service_LineString.png
        ??? highway_track_LineString.png
        ??? landuse_forest_Polygon.png
        ??? landuse_farmland_Polygon.png
        ??? landuse_residential_Polygon.png
        ??? natural_wood_Polygon.png
        ??? natural_water_Polygon.png
        ??? building_yes_Polygon.png
        ??? waterway_river_LineString.png
        ??? railway_rail_LineString.png
        ??? ...
```

## Filename Sanitization

```csharp
private static string SanitizeFileName(string name)
{
    // Replace invalid characters
    var invalid = Path.GetInvalidFileNameChars();
    var result = new StringBuilder(name.Length);
    
    foreach (var c in name)
    {
        if (invalid.Contains(c) || c == ' ' || c == '-')
            result.Append('_');
        else
            result.Append(c);
    }
    
    return result.ToString().ToLowerInvariant();
}
```

## Edge Cases

### 1. Empty Feature Groups
Skip groups with no features after filtering.

### 2. Very Large Areas
OSM query might timeout. The existing retry logic in `OverpassApiService` handles this.

### 3. No OSM Data Available
If `_canFetchOsmData` is false (e.g., projected GeoTIFF without valid WGS84 transformation), skip the export silently.

### 4. Duplicate Filenames
If two groups produce the same filename (unlikely), append a counter.

## Testing Checklist

- [ ] Generate terrain with GeoTIFF that has valid WGS84 bbox
- [ ] Verify `osm_layer` folder is created under `MT_TerrainGeneration`
- [ ] Check that PNG files are 8-bit grayscale
- [ ] Verify roads appear white (255) on black (0) background
- [ ] Verify polygons (landuse, natural) are correctly filled
- [ ] Test with different terrain sizes (1024, 2048, 4096)
- [ ] Test with cropped GeoTIFF (smaller bounding box)
- [ ] Verify no export happens when using PNG heightmap source
- [ ] Verify no export happens when OSM is unavailable (blocked reason)

## Dependencies

No new NuGet packages required. Uses existing:
- `SixLabors.ImageSharp` for PNG export
- `OsmGeometryProcessor` for rasterization
- `OverpassApiService` / `OsmQueryCache` for OSM data

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Performance impact on large terrains | Export runs after main generation, uses async I/O |
| Disk space for many layers | Layer maps are compressed PNGs, typically 10-100KB each |
| OSM API rate limiting | Uses existing cache; won't re-fetch if cached |

## Implementation Order

1. Create `OsmLayerExporter.cs` with core export logic
2. Add helper `FeatureGroupInfo` class (or reuse from dialog)
3. Integrate into `TerrainGenerationOrchestrator.ExecuteInternalAsync()`
4. Add unit tests (if test project exists)
5. Manual testing with sample GeoTIFF

## Code Implementation Summary

### ? Implementation Complete

### New Files Created
- `BeamNgTerrainPoc/Terrain/Osm/Services/OsmLayerExporter.cs`
  - `OsmLayerExporter` class with `ExportAllOsmLayersAsync()` method
  - `FeatureGroupInfo` private class for grouping features
  - `GetDefaultLineWidth()` method for road type widths
  - `SanitizeFileName()` and `EnsureUniqueFileName()` helpers
  - `SaveLayerMapAsync()` for 8-bit PNG export

### Modified Files
- `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`
  - Added `ExportAllOsmLayersAsync()` private static method
  - Called after material processing, before terrain creation
  - Silently skips if conditions not met (PNG source, no WGS84 bbox)
  - Uses existing `OsmQueryResult` if available, fetches if needed

### No Changes Required
- `OsmFeatureSelectorDialog.razor` - Only provides UI structure reference
- `OsmGeometryProcessor.cs` - Existing methods sufficient
- `OverpassApiService.cs` - Existing caching handles data fetch

### Build Status: ? Successful
