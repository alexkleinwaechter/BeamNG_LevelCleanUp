# Terrain Preset System Enhancement Plan

## Overview

This document outlines the implementation plan for enhancing the terrain preset export/import system to support full page state restoration, including:
- GeoTIFF source configuration
- Crop/selection region settings
- Per-material OSM feature selections
- All terrain generation parameters

## Goal

**When a user generates a terrain and saves a preset, they should be able to load that preset in a new session and have ALL settings restored exactly as they were.**

---

## Implementation Status: ✅ COMPLETED

### Files Modified

1. **`TerrainPresetResult.cs`** - Added all new properties for preset data including road smoothing settings classes
2. **`TerrainPresetExporter.razor`** - Added new parameters and `_appSettings` export with road smoothing embedded per-material
3. **`TerrainPresetImporter.razor`** - Removed old separate file import, added `ImportRoadSmoothingFromJson` and `ApplyRoadSmoothingToMaterial` methods
4. **`GenerateTerrain.razor`** - Updated exporter bindings with new parameters
5. **`GenerateTerrain.razor.cs`** - Updated `OnPresetImported` to handle new settings

### Breaking Changes
- **Removed separate `*_roadSmoothing_*.json` files** - Road smoothing parameters are now embedded in `_appSettings.materialSettings.roadSmoothing` section of the preset JSON
- **No backward compatibility** - Old presets with separate road smoothing files will not import road smoothing settings (but won't error)

### Additional Enhancements
- **Layer maps for ALL materials** - The exporter now generates black (empty) layer map PNG files for materials without assigned layer maps, ensuring BeamNG can properly import the terrain preset
- **OSM-generated layer map export** - Materials using OSM features have their generated layer maps exported from `MT_TerrainGeneration/{materialName}_osm_layer.png`. If terrain generation hasn't been run yet, a warning is shown and empty layer maps are generated.

---

## What Was Implemented

### 1. Page-Level Heightmap Source Configuration
| Parameter | Type | Description |
|-----------|------|-------------|
| `HeightmapSourceType` | enum | Png, GeoTiffFile, GeoTiffDirectory |
| `GeoTiffPath` | string | Path to GeoTIFF file (for GeoTiffFile mode) |
| `GeoTiffDirectory` | string | Path to GeoTIFF tiles folder (for GeoTiffDirectory mode) |

### 2. Terrain Generation Options
| Parameter | Type | Description |
|-----------|------|-------------|
| `UpdateTerrainBlock` | bool | Whether to update MissionGroup items.level.json |
| `EnableCrossMaterialHarmonization` | bool | Smooth transitions between road types |

### 3. Crop/Selection Settings (for GeoTIFF)
| Parameter | Type | Description |
|-----------|------|-------------|
| `CropOffsetX` | int | X offset in source pixels |
| `CropOffsetY` | int | Y offset in source pixels |
| `CropWidth` | int | Selection width in source pixels |
| `CropHeight` | int | Selection height in source pixels |

### 4. GeoTIFF Metadata (for validation/UI)
| Parameter | Type | Description |
|-----------|------|-------------|
| `GeoTiffOriginalWidth` | int | Original GeoTIFF width |
| `GeoTiffOriginalHeight` | int | Original GeoTIFF height |
| `GeoTiffProjectionName` | string | CRS/projection name |
| `NativePixelSizeMeters` | float | Source resolution in m/px |

### 5. Per-Material Layer Source Configuration
| Parameter | Type | Description |
|-----------|------|-------------|
| `LayerSourceType` | enum | None, PngFile, OsmFeatures |
| `OsmFeatureSelections` | list | Selected OSM features (references only) |

---

## Implementation Strategy

### OSM Feature Handling Decision: **Save References, Re-fetch on Import**

**Rationale:**
1. Keeps preset files small
2. Ensures fresh OSM data on each import
3. Avoids stale/outdated road data
4. OSM features can be re-queried using the bounding box

**What to save per OSM feature:**
- `FeatureId` (long)
- `DisplayName` (string)
- `Category` (string, e.g., "highway")
- `SubCategory` (string, e.g., "primary")
- Tags are NOT saved (will be re-fetched)

**On Import:**
1. If material has `LayerSourceType == OsmFeatures` AND we have valid `GeoBoundingBox`
2. Re-fetch OSM data for the bounding box
3. Match saved feature references to fetched features by ID
4. If feature not found, log warning but continue

---

## JSON Structure

### Enhanced `*_terrainPreset.json` Format

```json
{
  // === EXISTING BEAMNG FIELDS (unchanged, BeamNG reads these) ===
  "name": "theTerrain",
  "type": "TerrainData",
  "heightScale": 500.0,
  "squareSize": 1.0,
  "pos": { "x": -1024.0, "y": -1024.0, "z": 150.0 },
  "heightMapPath": "/levels/myLevel/import/heightmap.png",
  "holeMapPath": "/levels/myLevel/import/holemap.png",
  "opacityMaps": [
    "/levels/myLevel/import/theTerrain_layerMap_0_Grass.png",
    "/levels/myLevel/import/theTerrain_layerMap_1_Asphalt.png"
  ],

  // === NEW APP-SPECIFIC EXTENSION (BeamNG ignores these) ===
  "_appSettings": {
    "version": "2.0",
    
    "heightmapSource": {
      "type": "GeoTiffFile",
      "geoTiffPath": "D:/Maps/elevation.tif",
      "geoTiffDirectory": null
    },
    
    "terrainOptions": {
      "updateTerrainBlock": true,
      "enableCrossMaterialHarmonization": true
    },
    
    "cropSettings": {
      "offsetX": 512,
      "offsetY": 256,
      "width": 2048,
      "height": 2048
    },
    
    "geoTiffMetadata": {
      "originalWidth": 4096,
      "originalHeight": 4096,
      "projectionName": "WGS 84 / UTM zone 32N",
      "nativePixelSizeMeters": 30.0
    },
    
    "materialSettings": {
      "Asphalt_road": {
        "layerSourceType": "OsmFeatures",
        "osmFeatureSelections": [
          {
            "featureId": 123456789,
            "displayName": "Primary Road",
            "category": "highway",
            "subCategory": "primary"
          },
          {
            "featureId": 987654321,
            "displayName": "Secondary Road", 
            "category": "highway",
            "subCategory": "secondary"
          }
        ]
      },
      "Grass": {
        "layerSourceType": "PngFile",
        "osmFeatureSelections": []
      }
    }
  }
}
```

---

## Files to Modify

### 1. `TerrainPresetResult.cs`
Add new properties to hold imported settings:
- `HeightmapSourceType`
- `GeoTiffPath`
- `GeoTiffDirectory`
- `UpdateTerrainBlock`
- `EnableCrossMaterialHarmonization`
- `CropOffsetX`, `CropOffsetY`, `CropWidth`, `CropHeight`
- `GeoTiffOriginalWidth`, `GeoTiffOriginalHeight`
- `GeoTiffProjectionName`, `NativePixelSizeMeters`
- `MaterialOsmFeatures` dictionary

### 2. `TerrainPresetExporter.razor`
Add parameters and export logic:
- Add new `[Parameter]` properties for all new settings
- Export to `_appSettings` JSON section
- Export per-material OSM feature selections

### 3. `TerrainPresetImporter.razor`
Add import logic:
- Parse `_appSettings` section if present
- Restore all page-level settings
- Restore per-material layer source type
- Restore OSM feature selections (references only)
- Handle missing/invalid data gracefully

### 4. `GenerateTerrain.razor` / `.razor.cs`
Update component bindings:
- Pass new parameters to `TerrainPresetExporter`
- Handle new properties in `OnPresetImported`
- Restore crop settings to `CropAnchorSelector` component
- Trigger GeoTIFF metadata read when GeoTIFF path is restored

---

## Implementation Order

1. **TerrainPresetResult.cs** - Add all new properties
2. **TerrainPresetExporter.razor** - Add parameters and export logic
3. **TerrainPresetImporter.razor** - Add import logic
4. **GenerateTerrain.razor** - Wire up new parameters
5. **GenerateTerrain.razor.cs** - Update `OnPresetImported` handler

---

## Testing Checklist

- [ ] Export preset in PNG mode, import and verify all settings restored
- [ ] Export preset in GeoTIFF File mode with crop, import and verify
- [ ] Export preset with OSM features selected, import and verify features restored
- [ ] Export preset with multiple road materials, import and verify road smoothing settings
- [ ] Import preset with missing `_appSettings` (backwards compatibility)
- [ ] Import preset with invalid GeoTIFF path (should show warning, not crash)
- [ ] Verify BeamNG can still read the preset file (ignores `_appSettings`)

---

## Notes

- The `_appSettings` key is prefixed with underscore to indicate it's not part of BeamNG's schema
- All paths stored in preset should be absolute for reliability
- The CropAnchorSelector component needs a way to programmatically set offsets (may need to add a method)
- OSM feature re-fetching should be optional (user can skip if offline)
