# GroundCover Implementation Summary

## Overview
Implemented automatic groundcover copying when terrain materials are copied. Groundcovers reference terrain materials via their `Types[].layer` property and are now automatically filtered and copied with their associated terrain materials.

## Key Architecture Decisions

### 1. **Automatic Coupling with Terrain Materials**
- Groundcovers are NOT user-selectable assets
- Automatically copied when terrain materials are selected
- Only `Types` entries matching the selected terrain materials are included (filtered copying)
- Removed `CopyAssetType.GroundCover` enum value

### 2. **Generic JSON Preservation**
- Original groundcover JSON lines are stored during scanning
- Minimal data models (`GroundCover`, `GroundCoverType`) only for dependency extraction
- Full JSON manipulated via `JsonNode` to preserve all properties (even unknown/future ones)
- Only 3 fields modified: `persistentId`, `Types[].layer`, `Types[].shapeFilename`

### 3. **Material Copying Flow**
- Groundcover `material` property (e.g., "Groundcover_spring") is automatically identified
- Material is copied to `/art/shapes/groundcover/MT_{source_level}/materials.json`
- Uses existing `MaterialCopier` with same logic as road materials

## Critical Bug Fix - MaterialFileScanner

**Problem**: `MaterialFileScanner` was treating ALL properties of `MaterialStage` as texture paths, including `List<double>` properties like `DiffuseColor`, causing invalid paths like:
```
C:\...\System.Collections.Generic.List`1[System.Double].dds
```

**Root Cause**: 
```csharp
foreach (var prop in stage.GetType().GetProperties())
{
    var val = prop.GetValue(stage, null) != null ? prop.GetValue(stage, null).ToString() : string.Empty;
    // This called .ToString() on List<double>, boolean, etc.
}
```

**Fix Applied** (`MaterialFileScanner.cs`):
```csharp
foreach (var prop in stage.GetType().GetProperties())
{
    // Only process string properties (texture paths), skip collections and other types
  if (prop.PropertyType != typeof(string))
    {
        continue; // Skip List<double>, bool, etc.
    }
    
    var val = prop.GetValue(stage, null) != null ? prop.GetValue(stage, null).ToString() : string.Empty;
 // Now only processes actual texture map paths
}
```

This ensures only string properties (texture paths like `BaseColorMap`, `NormalMap`, etc.) are processed, while properties like `DiffuseColor` (List<double>), `Glow` (bool), etc. are skipped.

## Complete Flow

```
User selects terrain material "dirt_grass"
  ?
TerrainMaterialCopier.Copy()
  ?
GroundCoverCopier.CopyGroundCoversForTerrainMaterials()
  ?
For each scanned groundcover:
  1. Parse JSON, check Types[].layer for "dirt_grass"
  2. If match: filter Types to only matching entries
  3. Extract material name from "material" property
  4. Find material in MaterialsJsonCopy
  5. Copy material using MaterialCopier (NEW - fixes missing materials)
  6. Update layer names: "dirt_grass" ? "dirt_grass_driver_training"
  7. Update DAE paths to new location
  8. Copy DAE files
  9. Write filtered groundcover to items.level.json
```

## Files Created

1. **GroundCover.cs** - Minimal model (3 properties: name, material, Types)
2. **GroundCoverType.cs** - Minimal model (2 properties: layer, shapeFilename)
3. **GroundCoverCopyScanner.cs** - Scans and stores JSON lines
4. **GroundCoverCopier.cs** - Handles filtering and copying

## Files Modified

1. **MaterialFileScanner.cs** - **CRITICAL FIX**: Only process string properties
2. **AssetCopy.cs** - Load `MaterialsJsonCopy` into copier, initialize GroundCoverCopier
3. **TerrainMaterialCopier.cs** - Call groundcover copier after terrain copy
4. **BeamFileReader.cs** - Add `CopyGroundCovers()` method
5. **Constants.cs** - Added `GroundCover` path constant
6. **CopyAsset.cs** - Removed `GroundCover` enum, kept data property

## Key Methods

### GroundCoverCopier
- `LoadGroundCoverJsonLines()` - Load scanned groundcover JSON
- `LoadMaterialsJsonCopy()` - Load scanned materials for lookup (NEW)
- `CopyGroundCoversForTerrainMaterials()` - Filter and copy matching groundcovers
- `CopyFilteredGroundCover()` - Copy material, DAE files, update JSON (ENHANCED with material copying)

### MaterialFileScanner
- `GetMaterialFiles()` - **FIXED**: Now only processes string properties

## Target Paths

- **Materials**: `/art/shapes/groundcover/MT_{source_level}/materials.json`
- **DAE files**: `/art/shapes/groundcover/MT_{source_level}/*.dae`
- **GroundCovers**: `/main/MissionGroup/Level_object/vegetation/items.level.json`

## Empty Line Fix

Fixed issue where blank lines appeared in `items.level.json`:

```csharp
private void WriteGroundCoverToTarget(string jsonLine, string groundCoverName)
{
    // ...
    if (!targetFile.Exists)
    {
        File.WriteAllText(targetFile.FullName, jsonLine);
    }
    else
    {
        // Check if file ends with newline to avoid double newlines
var existingContent = File.ReadAllText(targetFile.FullName);
        var needsNewline = !existingContent.EndsWith(Environment.NewLine) && !string.IsNullOrEmpty(existingContent);
        
        if (needsNewline)
        {
            File.AppendAllText(targetFile.FullName, Environment.NewLine + jsonLine);
        }
        else
        {
    File.AppendAllText(targetFile.FullName, jsonLine + Environment.NewLine);
        }
    }
}
```

## Testing Recommendations

1. **Test Material Copying**: Verify groundcover materials are copied
2. **Test Filtering**: Select only some terrain materials, verify only matching Types are copied
3. **Test Property Scanning**: Verify no `System.Collections.Generic.List` paths appear
4. **Test Path Updates**: Verify layer names and shapeFilename paths are correct
5. **Test JSON Preservation**: Verify unknown properties are preserved
6. **Test Multiple Terrains**: Select multiple terrain materials, verify all related groundcovers copied
7. **Test Empty Lines**: Verify no blank lines in `items.level.json`

## Known Limitations

- DAE file copying in `CopyGroundCoverDaeFile()` is incomplete (placeholder implementation)
- No duplicate detection for groundcovers
- No size calculation for groundcover assets

## Future Enhancements

1. Complete DAE file copying implementation
2. Add duplicate detection
3. Add size calculation for asset list
4. Add validation for terrain layer references
5. Add position offset option when copying
