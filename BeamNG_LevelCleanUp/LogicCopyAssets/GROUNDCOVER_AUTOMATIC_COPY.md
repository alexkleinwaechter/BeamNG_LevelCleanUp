# GroundCover Automatic Copying with Terrain Materials

## Overview
GroundCover objects are now automatically copied when terrain materials are copied, since they are tightly coupled through the `Types[].layer` property which references terrain material internal names.

## Key Design Decision

**GroundCovers are NOT selectable** - They are automatically copied with their associated terrain materials.

### Why?
- GroundCovers reference terrain materials via `Types[].layer` property
- A GroundCover is meaningless without its referenced terrain materials
- Only the `Types` entries that reference the selected terrain materials are copied (filtered copying)

## Architecture

### Scanning Phase (BeamFileReader)
1. `CopyGroundCovers()` scans `items.level.json` in vegetation folder
2. Complete JSON lines are stored in static `BeamFileReader.GroundCoverJsonLines`
3. No `CopyAsset` objects are created (unlike other asset types)

### Initialization Phase (AssetCopy)
1. `GroundCoverCopier` is created before `TerrainMaterialCopier`
2. Scanned groundcover JSON lines are loaded into `GroundCoverCopier`
3. `GroundCoverCopier` is passed to `TerrainMaterialCopier` constructor

### Copying Phase (TerrainMaterialCopier ? GroundCoverCopier)
1. User selects terrain materials to copy
2. `TerrainMaterialCopier.Copy()` copies terrain materials
3. **Automatically calls** `GroundCoverCopier.CopyGroundCoversForTerrainMaterials()`
4. GroundCoverCopier filters and copies related groundcovers

## Filtered Copying

Only `Types` array entries that match the selected terrain materials are copied:

**Example:**
```json
// Source GroundCover with multiple types
{
  "Types": [
  {"layer": "dirt_loose", "shapeFilename": "plant1.dae"},
    {"layer": "grass", "shapeFilename": "grass1.dae"},
    {"layer": "rock", "shapeFilename": "rock1.dae"}
  ]
}

// If only "dirt_loose" terrain material is selected, copied GroundCover becomes:
{
  "Types": [
    {"layer": "dirt_loose_source_level", "shapeFilename": "/levels/target/.../ plant1.dae"}
  ]
}
```

## Flow Diagram

```
User Selects Terrain Materials
    ?
AssetCopy.Copy()
    ?
TerrainMaterialCopier.Copy(terrain materials)
  ??> Copies terrain material files
    ??> Writes to main.materials.json
    ??> GroundCoverCopier.CopyGroundCoversForTerrainMaterials(terrain materials)
            ??> Filters all scanned groundcovers
    ??> For each groundcover:
         ?   ??> Filters Types[] to matching layers only
            ?   ??> Updates layer names (add suffix)
            ?   ??> Updates shapeFilename paths
            ?   ??> Copies DAE files
            ?   ??> Generates new GUID
            ?   ??> Writes to items.level.json
            ??> Reports count of copied groundcovers
```

## Key Classes

### `GroundCoverCopyScanner` (Simplified)
- **Purpose**: Scan and store complete JSON lines
- **Input**: `items.level.json` file
- **Output**: List of complete JSON strings (exposed via property)
- **No longer**: Creates CopyAsset objects or extracts dependencies

### `GroundCoverCopier`
- **New Method**: `CopyGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)`
- **Process**:
  1. Get internal names from terrain materials
  2. For each scanned groundcover JSON:
     - Parse as `JsonNode`
     - Filter `Types[]` to only entries matching terrain materials
     - If any matches found:
  - Clone groundcover JSON
  - Replace `Types[]` with filtered array
       - Update layer names (add suffix)
       - Update shapeFilename paths
  - Copy DAE files
       - Write to target

### `TerrainMaterialCopier` (Modified)
- **New Dependency**: `GroundCoverCopier` passed in constructor
- **New Behavior**: After copying terrain materials, automatically calls `CopyGroundCoversForTerrainMaterials()`

### `AssetCopy` (Modified)
- **Initialization Order**: 
  1. Create `GroundCoverCopier` FIRST
  2. Load scanned JSON lines into it
  3. Create `TerrainMaterialCopier` with `GroundCoverCopier` reference

### `BeamFileReader` (Modified)
- **New Static Field**: `GroundCoverJsonLines` - stores scanned groundcover JSON
- **CopyGroundCovers()**: Scans and populates static field

## Removed

- **`CopyAssetType.GroundCover`** enum value - no longer needed
- **Switch case for GroundCover** in `AssetCopy.Copy()` - automatic now
- **GroundCover CopyAsset creation** in scanner - not user-selectable

## JSON Preservation

Complete JSON preservation is maintained:
- Original JSON line is parsed as `JsonNode`
- Only specific fields are modified:
  - `persistentId`: New GUID
  - `Types[]`: Filtered and updated
  - `Types[].layer`: Add level suffix
  - `Types[].shapeFilename`: Update path
- All other properties preserved (wind, rendering, custom, etc.)

## Benefits

1. **Automatic** - User doesn't need to select groundcovers manually
2. **Coupled** - Groundcovers always copied with their terrain materials
3. **Filtered** - Only relevant `Types` entries are copied
4. **Efficient** - No unnecessary data copied
5. **Future-proof** - Works with unknown/future GroundCover properties

## Example User Workflow

1. User loads source and target levels
2. User navigates to "Copy Assets"  
3. User sees list including "Terrain Materials"
4. User selects terrain materials (e.g., "dirt_loose", "grass")
5. User clicks "Copy"
6. **Automatically**:
   - Terrain materials are copied
   - Related groundcovers are found
   - Only matching `Types` are copied
   - DAE files are copied
   - User sees: "Copied 3 groundcover(s) for terrain materials"

## File Locations

### Source
- Terrain Materials: `source_level/art/terrains/main.materials.json`
- GroundCovers: `source_level/main/MissionGroup/Level_object/vegetation/items.level.json`

### Target
- Terrain Materials: `target_level/art/terrains/main.materials.json`
- GroundCovers: `target_level/main/MissionGroup/Level_object/vegetation/items.level.json`
- DAE Files: `target_level/art/shapes/groundcover/MT_source_level/*.dae`
- Materials: `target_level/art/shapes/groundcover/MT_source_level/materials.json`

## Error Handling

- Missing vegetation folder: Info message (not error)
- No matching groundcovers: Info message
- DAE copy failure: Warning (continues with others)
- JSON parsing error: Warning (skips that groundcover)
- Material copy failure: Error (stops process)

## Testing Recommendations

1. **Test filtering**: Select only some terrain materials, verify only matching Types are copied
2. **Test suffix**: Verify layer names have correct suffix
3. **Test paths**: Verify shapeFilename paths point to correct location
4. **Test DAE copying**: Verify DAE files are actually copied
5. **Test preservation**: Verify unknown properties are preserved
6. **Test multiple**: Select multiple terrain materials, verify all related groundcovers copied
7. **Test edge cases**: 
   - GroundCover with no matching Types (should be skipped)
   - GroundCover with all Types matching (all should be copied)
   - GroundCover with some Types matching (only those copied)
