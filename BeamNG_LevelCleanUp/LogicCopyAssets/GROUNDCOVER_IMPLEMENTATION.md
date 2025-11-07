# GroundCover Copy Implementation Summary

## Overview
Groundcover copying functionality has been implemented to copy vegetation GroundCover objects with their associated materials, terrain layer references, and DAE files from a source level to a target level.

**Key Design Decision**: Uses a **generic JSON preservation approach** instead of strongly-typed deserialization. This ensures all properties (including unknown/future ones) are preserved during the copy operation.

## Implementation Structure

### 1. **Data Models** (`BeamNG_LevelCleanUp/Objects/`)
- **`GroundCoverType.cs`**: **Minimal class** for dependency extraction only
  - Properties: `layer`, `shapeFilename` (only what we need to scan)
  - **Not used for writing** - original JSON is preserved
  
- **`GroundCover.cs`**: **Minimal class** for dependency extraction only
  - Properties: `name`, `material`, `Types` (only what we need to scan)
  - **Not used for writing** - original JSON line is stored and preserved
  - Used only to identify materials and DAE dependencies during scanning
  
- **`CopyAsset.cs`** (modified): 
  - Added `GroundCover = 4` to `CopyAssetType` enum
  - Added `GroundCoverData` property to store minimal GroundCover for reference
  - **`SourceMaterialJsonPath`**: Repurposed to store the **original complete JSON line** to preserve all properties

### 2. **Scanner** (`BeamNG_LevelCleanUp/LogicCopyAssets/`)
- **`GroundCoverCopyScanner.cs`**: Scans source level for GroundCover objects
  - Reads `items.level.json` files line-by-line (special BeamNG format)
  - Identifies GroundCover objects (class=="GroundCover")
  - **Stores original JSON line** in `CopyAsset.SourceMaterialJsonPath` for preservation
  - Extracts material references from `material` property
  - Extracts DAE file references from `Types[].shapeFilename`
  - Collects terrain layer references from `Types[].layer` for renaming
  - Creates `CopyAsset` entries for each GroundCover found

### 3. **Copier** (`BeamNG_LevelCleanUp/LogicCopyAssets/`)
- **`GroundCoverCopier.cs`**: Handles the actual copying process using **generic JSON manipulation**
  - **Material Copying**: Uses existing `MaterialCopier` to copy materials referenced by GroundCover
  - **DAE Copying**: Uses existing `DaeCopier` to copy DAE files from `Types[].shapeFilename`
  - **Generic JSON Preservation**: 
    - Parses original JSON line as `JsonNode`
    - Updates **only** the specific fields that need modification:
- `persistentId`: New GUID
      - `Types[].layer`: Appends level suffix
      - `Types[].shapeFilename`: Updates to new target path
    - **All other properties are preserved**, even if unknown to our code
  - **JSON Writing**: Serializes back to one-line format and appends to target `items.level.json`

### 4. **Integration Points**

#### `BeamFileReader.cs` (modified)
- Added `CopyGroundCovers()` method to scan source level vegetation
- Integrated into `ReadAllForCopy()` workflow
- Locates vegetation file at: `{source_level}/main/MissionGroup/Level_object/vegetation/items.level.json`

#### `AssetCopy.cs` (modified)
- Added `_groundCoverCopier` field
- Initialized in `InitializeCopiers()` method
- Added `CopyGroundCover` case in `Copy()` switch statement

#### `Constants.cs` (modified)
- Added `GroundCover = @"art\shapes\groundcover"` constant

## Workflow

### Scanning Phase (Source Level)
1. Locate `items.level.json` in vegetation folder
2. Parse each JSON line
3. Identify GroundCover objects
4. For each GroundCover:
   - **Store the original JSON line** (preserves all properties)
   - Deserialize to `GroundCover` object for dependency extraction
   - Extract material from `material` property
   - Extract DAE files from `Types[].shapeFilename`
   - Collect terrain layers from `Types[].layer`
   - Scan DAE files for their materials
   - Create `CopyAsset` entry with original JSON stored

### Copying Phase (Target Level)
1. Copy materials referenced by GroundCover
2. Copy DAE files with their materials
3. **Parse original JSON as `JsonNode`** (preserves unknown properties)
4. Update only specific fields:
   - `persistentId`: New GUID
   - `Types[].layer`: Add level suffix (e.g., `dirt_loose` ? `dirt_loose_driver_training`)
   - `Types[].shapeFilename`: Update path to new target location
5. Serialize back to one-line JSON format (preserving all original properties)
6. Append to target `items.level.json` (create if doesn't exist)

## Generic JSON Preservation Approach

### Why This Approach?
- BeamNG's JSON format may contain undocumented properties
- Future game updates may add new properties
- User-created mods may use custom properties
- We only need to modify 3 specific fields
- **We only need to READ 3 properties (name, material, Types) for dependency scanning**

### Minimal Data Models
The `GroundCover` and `GroundCoverType` classes contain **only the minimum properties** needed for scanning:

```csharp
public class GroundCover
{
    public string Name { get; set; }      // For display/logging
    public string Material { get; set; }      // To find material dependencies
    public List<GroundCoverType> Types { get; set; }  // To find DAE and layer dependencies
}

public class GroundCoverType
{
    public string Layer { get; set; }  // To update terrain references
    public string ShapeFilename { get; set; } // To copy DAE files
}
```

**All other properties** (position, rendering settings, wind, etc.) are **ignored during deserialization** but **preserved in the original JSON line**.

### How It Works
```csharp
// 1. Deserialize ONLY what we need for scanning
var groundCover = jsonElement.Deserialize<GroundCover>();
// Extract: name, material, Types[].layer, Types[].shapeFilename

// 2. Store COMPLETE original JSON line
copyAsset.SourceMaterialJsonPath = originalJsonLine;

// 3. During copy, parse COMPLETE JSON as JsonNode
var jsonNode = JsonNode.Parse(originalJson);

// 4. Update only what we need
jsonNode["persistentId"] = Guid.NewGuid().ToString();
jsonNode["Types"][0]["layer"] = $"{originalLayer}_{levelName}";
jsonNode["Types"][0]["shapeFilename"] = newPath;

// 5. Serialize back (all other properties preserved)
var updatedJson = jsonNode.ToJsonString();
```

### Properties We Read (for scanning)
- **`name`**: For display and logging
- **`material`**: To identify material dependencies
- **`Types[].layer`**: To identify terrain layer references
- **`Types[].shapeFilename`**: To identify DAE file dependencies

### Properties We Modify (during copy)
- **`persistentId`**: New GUID for the copied instance
- **`Types[].layer`**: Terrain layer references (add level suffix)
- **`Types[].shapeFilename`**: DAE file paths (update to new location)

### Properties We Preserve (everything else)
- **Everything else**: All rendering properties, wind settings, positions, custom properties, etc.
- Even properties we don't know about or that don't exist yet

## Target Paths

- **Materials**: `/art/shapes/groundcover/MT_{source_level}/materials.json`
- **DAE Files**: `/art/shapes/groundcover/MT_{source_level}/{dae_filename}`
- **GroundCover JSON**: `/main/MissionGroup/Level_object/vegetation/items.level.json`

## Dependencies

### Reused Components
- `MaterialCopier`: For copying GroundCover materials
- `DaeCopier`: For copying DAE files referenced in Types
- `PathConverter`: For path resolution and conversion
- `FileCopyHandler`: For file copying with zip extraction fallback
- `DaeScanner`: For extracting materials from DAE files
- `JsonUtils`: For JSON parsing
- **`JsonNode`**: For generic JSON manipulation (System.Text.Json)

## Key Features

1. **Generic JSON Preservation**: Preserves all properties, even unknown/future ones
   - Only modifies the 3 fields we need to change
   - Future-proof against BeamNG updates

2. **Terrain Layer Renaming**: Automatically renames terrain layer references
   - Original: `"layer":"dirt_loose"` 
   - Copied: `"layer":"dirt_loose_driver_training"`

3. **DAE File Handling**: Automatically copies DAE files and updates paths
   - Original: `/levels/source_level/art/shapes/groundcover/plant01.dae`
   - Copied: `/levels/target_level/art/shapes/groundcover/MT_source_level/plant01.dae`

4. **Material Dependencies**: Resolves and copies all materials used by:
   - The GroundCover's `material` property
   - All DAE files referenced in `Types[].shapeFilename`

5. **GUID Management**: Generates new `persistentId` for each copied GroundCover

6. **Special JSON Format**: Handles BeamNG's one-line-per-object JSON format

## Example Usage

When copying from `driver_training` to `my_level`:

**Source GroundCover** (original JSON preserved):
```json
{"name":"FIeldplants","class":"GroundCover","persistentId":"bbc159b3-fb45-474c-a3a7-25260a8de7f0","__parent":"vegetation","position":[1245.28564,1575.9552,68.2450027],"Types":[{"layer":"Ploughed","probability":1,"shapeFilename":"/levels/driver_training/art/shapes/groundcover/fieldplant01var1.dae","sizeMax":1.5}],"material":"invisible","customProperty":"someValue"}
```

**Copied Result** (only 3 fields changed, custom property preserved):
```json
{"name":"FIeldplants","class":"GroundCover","persistentId":"a1b2c3d4-e5f6-7890-a1b2-c3d4e5f67890","__parent":"vegetation","position":[1245.28564,1575.9552,68.2450027],"Types":[{"layer":"Ploughed_driver_training","probability":1,"shapeFilename":"/levels/my_level/art/shapes/groundcover/MT_driver_training/fieldplant01var1.dae","sizeMax":1.5}],"material":"invisible","customProperty":"someValue"}
```

**Files Copied**:
- `/levels/my_level/art/shapes/groundcover/MT_driver_training/fieldplant01var1.dae`
- `/levels/my_level/art/shapes/groundcover/MT_driver_training/materials.json` (if material needed copying)
- All textures referenced by the material

## Error Handling

- Missing vegetation files are logged as Info (not an error)
- Individual GroundCover parsing errors are logged as Warnings
- DAE scan failures are logged as Warnings (continues with next DAE)
- Material/DAE copy failures are logged as Errors
- JSON parsing errors are caught and logged

## Testing Recommendations

1. **Test with GroundCovers that have**:
   - Multiple Types with different DAE files
   - Both material property and DAE materials
   - Terrain layer references
   - Missing DAE files (verify error handling)
   - Custom/unknown properties (verify preservation)

2. **Verify**:
   - All original properties are preserved
   - Only 3 fields are modified (persistentId, layer, shapeFilename)
   - Terrain layer names are updated correctly
   - DAE paths point to new locations
 - Materials are copied to correct location
   - New GUIDs are generated
   - Target `items.level.json` is created/appended correctly

3. **Edge Cases**:
   - Source level with no vegetation folder
   - Empty items.level.json
   - GroundCovers with no Types
   - GroundCovers with no material property
   - GroundCovers with additional unknown properties

## Future Enhancements

Potential improvements:
1. Add duplicate detection for GroundCovers (like materials/decals)
2. Support filtering GroundCovers by name pattern
3. Add option to offset positions when copying
4. Support batch copying multiple GroundCovers
5. Add validation for terrain layer references (check if terrain material exists)
6. Add preview of JSON changes before copying
