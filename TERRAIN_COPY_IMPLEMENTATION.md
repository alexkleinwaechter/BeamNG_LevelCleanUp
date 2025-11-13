# Copy Terrain Materials Feature - Implementation Summary

## Overview
This feature allows users to copy terrain materials from one BeamNG map to another. The implementation follows the existing asset copy pattern and includes special handling for terrain materials to ensure uniqueness with GUID-based naming.

## Key Features

### 1. **Dynamic Material Name Suffixing with GUID**
- **Key format**: `OriginalName_LevelName` (e.g., `Asphalt_driver_training`)
- **name property**: `OriginalName_LevelName` (e.g., `Asphalt_driver_training`)
- **internalName property**: `OriginalInternalName_LevelName` (e.g., `Asphalt_driver_training`)
- **persistentId**: New GUID is generated for each copied material
- This ensures complete uniqueness and prevents conflicts when copying from multiple sources

### 2. **Generic Property Scanning**
- Instead of hardcoded texture property list, dynamically scans ALL properties in the material JSON
- Detects texture paths by:
  - Properties ending with "Tex" or "Map"
  - Values containing "/levels/"
- Future-proof against BeamNG format changes
- Copies all material properties as-is from source

### 3. **Centralized Terrain Materials**
- All terrain materials are stored in a single location: `art/terrains/main.materials.json`
- Texture files are copied directly to the `art/terrains` folder (no subfolders)
- This matches BeamNG's terrain material structure

### 4. **Recursive Texture Path Updates**
- Dynamically updates ALL texture paths in the material JSON (not just known properties)
- Recursively searches through nested objects and arrays
- Handles any depth of nesting in the JSON structure
- Updates paths wherever they appear in the material definition

## Implementation Details

### Backend Classes

#### 1. **TerrainCopyScanner.cs** (Modified)
Location: `BeamNG_LevelCleanUp\LogicCopyAssets\TerrainCopyScanner.cs`

**Key Changes**:
- Removed hardcoded texture property list (`GetTerrainTextureProperties()` method removed)
- Added `ScanTextureFilesFromProperties()` method for dynamic property scanning
- Scans ALL properties in the material JSON, not just known ones
- Detects texture paths heuristically:
  ```csharp
  // Checks if property looks like a texture path
  if (propValue.Contains("/levels/") || 
      propName.EndsWith("Tex", StringComparison.OrdinalIgnoreCase) ||
      propName.EndsWith("Map", StringComparison.OrdinalIgnoreCase))
  ```

Key methods:
- `ScanTerrainMaterials()`: Main scanning logic
- `ScanTextureFilesFromProperties()`: Dynamic property scanning

#### 2. **AssetCopy.cs** (Modified)
Added terrain-specific methods with enhanced features:
- `CopyTerrain()`: Entry point for terrain copying
- `CopyTerrainMaterials()`: Handles the copying logic with GUID generation and name suffixing
- `UpdateTexturePathsInMaterial()`: Recursively updates texture paths throughout the entire JSON structure
- `GetTerrainTargetFileName()`: Determines target path for terrain textures

**Key Naming Logic**:
```csharp
var newGuid = Guid.NewGuid().ToString();
var newInternalName = $"{material.InternalName}_{levelNameCopyFrom}";
var newMaterialName = $"{material.Name}_{levelNameCopyFrom}_{newGuid}";
var newKey = $"{material.Name}_{levelNameCopyFrom}-{newGuid}";
```

**Recursive Path Update**:
- Traverses JsonObject and JsonArray nodes recursively
- Updates string values matching the old texture path
- Handles nested structures of any depth

#### 3. **BeamFileReader.cs** (Modified)
Added:
- `CopyTerrainMaterials()` method to scan terrain materials during asset copy
- `ReadTypeEnum.CopyTerrainMaterials` enum value
- Integration into `ReadAllForCopy()` workflow

#### 4. **CopyAsset.cs** (Modified)
Added to enum:
- `CopyAssetType.Terrain = 3`

Added properties:
- `TerrainMaterialName`: Original material name
- `TerrainMaterialInternalName`: Original internal name

#### 5. **Constants.cs** (Modified)
Added:
- `public const string Terrains = @"art\terrains";`

### Frontend

#### CopyTerrains.razor (New)
Location: `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyTerrains.razor`

Features:
- Source and target level selection (ZIP or folder)
- Vanilla level dropdown support
- Table view of available terrain materials
- Search/filter functionality
- Multi-select terrain materials
- Size calculation and display
- Copy confirmation dialog with warning about GUID-based naming
- Build deployment ZIP file option
- Error/warning/info message drawers
- Working directory management

Page route: `/CopyTerrains`

## Usage Workflow

1. **Select Source Level**: Choose the map containing terrain materials to copy
2. **Select Target Level**: Choose the destination map
3. **Review Materials**: View list of available terrain materials with sizes
4. **Select Materials**: Choose which terrain materials to copy
5. **Copy**: Click "Copy Terrain Materials" button (new GUID generated for each)
6. **Build**: Optionally build a deployment ZIP file

## Technical Notes

### Material Structure
Terrain materials in BeamNG follow this structure:
```json
{
  "MaterialName": {
    "name": "MaterialName",
    "internalName": "MaterialName",
    "class": "TerrainMaterial",
    "persistentId": "guid",
    "aoBaseTex": "/levels/levelname/art/terrains/texture.png",
    // ... any other properties
  }
}
```

### Name Suffixing Example
**Source**: `Asphalt` material from `driver_training` level

**After Copy**:
- **Key**: `Asphalt_driver_training`
- **name**: `Asphalt_driver_training`
- **internalName**: `Asphalt_driver_training`
- **persistentId**: `a1b2c3d4-e5f6-7890-abcd-ef1234567890` (new GUID)

### File Paths
- Source: `levels/{sourcemap}/art/terrains/main.materials.json`
- Target: `levels/{targetmap}/art/terrains/main.materials.json`
- Textures: `levels/{targetmap}/art/terrains/{texturefile.ext}`

## Advantages of Generic Approach

1. **Future-Proof**: Works with any BeamNG material format changes
2. **Complete Copy**: Preserves ALL material properties, not just known ones
3. **No Maintenance**: No need to update code when new texture properties are added
4. **Flexible**: Handles custom or experimental material properties
5. **Robust**: Dynamically finds texture references anywhere in the structure

## Error Handling

- Validates source terrain materials file exists
- Checks for texture file existence (with fallback to vanilla ZIPs)
- Prevents duplicate material keys in target
- Logs warnings for missing files or unreadable properties
- Stops on critical JSON parsing errors
- Writes detailed error and warning logs

## Future Enhancements

Potential improvements:
1. Preview terrain material textures before copying
2. Batch operations on multiple maps
3. Material property comparison/diff tool
4. Support for TerrainMaterialTextureSet copying
5. Validation of texture sizes and formats
6. Material library/catalog management

## Testing Recommendations

1. Test with vanilla maps (driver_training, west_coast_usa, etc.)
2. Test with custom maps with unusual material properties
3. Test copying to empty terrain folder
4. Test copying to folder with existing materials
5. Test with missing texture files
6. Verify all texture types are copied correctly
7. Verify GUIDs are unique across multiple copies
8. Check resulting main.materials.json is valid JSON
9. Test with materials having nested property structures
10. Verify texture paths are updated in all locations

## Integration Points

The terrain copy feature integrates with:
- Existing asset copy infrastructure
- Material scanning system
- File resolution and ZIP extraction
- Path resolution utilities
- JSON utilities with BeamNG format support
- PubSub messaging system
- Working directory management
