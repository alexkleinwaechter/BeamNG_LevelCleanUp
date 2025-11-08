# Terrain Material and GroundCover Copy Implementation

## Overview

This system handles copying terrain materials and their associated groundcover vegetation from a source BeamNG level to a target level. The implementation uses a **two-phase batch approach** for efficiency and correctness.

## Key Design Principles

1. **Automatic Groundcover Copying**: Groundcovers are automatically copied when their referenced terrain materials are copied
2. **Batch Processing**: All terrain materials are processed first, then groundcovers are written once
3. **Complete Preservation**: Entire groundcovers are copied with all Types, suffixing all layer names
4. **Generic JSON Handling**: Uses `JsonNode` to preserve all properties, even unknown ones
5. **Minimal User Feedback**: Only essential progress messages (3-4 total)

## Architecture

### Core Components

1. **`TerrainMaterialCopier`** - Copies terrain materials with renamed properties
2. **`GroundCoverCopier`** - Collects and copies groundcovers in two phases
3. **`AssetCopy`** - Orchestrates the batch copy workflow
4. **`MaterialCopier`** - Copies material JSON and textures (shared utility)
5. **`DaeCopier`** - Copies DAE (Collada) mesh files (shared utility)
6. **`FileCopyHandler`** - Handles file copying with zip extraction fallback

### Data Flow

```
User selects terrain materials
    ?
AssetCopy.CopyTerrainMaterialsBatch()
    ?
For each terrain material:
    TerrainMaterialCopier.Copy(material)
        ? Copy material JSON + textures
    ? GroundCoverCopier.CollectGroundCoversForTerrainMaterials(materials)
            ? Mark groundcovers that reference this material
    ?
After all terrain materials:
    GroundCoverCopier.WriteAllGroundCovers()
        ? Copy all marked groundcovers with dependencies
```

## TerrainMaterialCopier Implementation

### Purpose
Copies terrain materials from source level to target level with renamed properties and updated texture paths.

### Key Features

**Naming Convention:**
- Original: `dirt_loose` (key), `dirt_loose` (name), `dirt_loose` (internalName)
- Target: `dirt_loose_driver_training` (key), `dirt_loose_driver_training` (name), `dirt_loose_driver_training` (internalName)

**GUID Handling:**
- Generates new `persistentId` GUID for each copied material
- Strips GUIDs from original names if present before adding level suffix

**Texture Copying:**
- Copies all texture files referenced in material Stages
- Updates all texture paths in the JSON to point to new location
- Uses recursive JSON traversal to find and replace all texture references

**Target Location:**
- Materials: `{target_level}/art/terrains/main.materials.json`
- Textures: `{target_level}/art/terrains/{texture_filename}`

**Integration:**
- Automatically triggers `GroundCoverCopier.CollectGroundCoversForTerrainMaterials()` after copying material
- This collects groundcovers that reference this terrain material

### Methods

```csharp
public bool Copy(CopyAsset item)
// Main entry point, processes all materials in the item

private bool CopyTerrainMaterial(MaterialJson material, FileInfo targetJsonFile)
// Copies a single terrain material with all transformations

private (string, string, string, string) GenerateTerrainMaterialNames(...)
// Creates new names: key, name, internalName, GUID

private void UpdateTerrainMaterialMetadata(JsonNode, string, string, string)
// Updates name, internalName, persistentId in JSON

private void CopyTerrainTextures(MaterialJson, JsonNode)
// Copies texture files and updates paths in JSON

private void UpdateTexturePathsInMaterial(JsonNode, string, string)
// Recursively finds and replaces texture paths in JSON

private void WriteTerrainMaterialJson(FileInfo, string, JsonNode)
// Writes material to target main.materials.json
```

## GroundCoverCopier Implementation

### Purpose
Copies entire groundcovers (vegetation) that reference terrain materials being copied. Uses a two-phase approach for batch efficiency.

### Key Design Decisions

**Why Copy Entire Groundcovers:**
- BeamNG groundcovers can have multiple Types (vegetation variants) referencing different terrain materials
- Filtering Types would be complex and error-prone
- Having extra layer references is **harmless** - they simply won't activate if the terrain material doesn't exist
- Simpler implementation: copy everything, suffix all layer names

**Two-Phase Approach:**
1. **Collection Phase**: Mark groundcovers for copying (called multiple times during terrain material copy)
2. **Write Phase**: Copy all marked groundcovers once (called after all terrain materials)

### Phase 1: Collection

```csharp
public void CollectGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
```

**When Called:** Automatically by `TerrainMaterialCopier` after copying each terrain material batch

**What It Does:**
1. Extracts `internalName` from terrain materials (e.g., `dirt_loose`, `Grass2`)
2. Parses all scanned groundcover JSON lines
3. Checks each groundcover's `Types[].layer` properties
4. If ANY Type references ANY of the terrain materials ? mark groundcover name in `_groundCoversToCopy` HashSet
5. **Does NOT copy yet** - only marks for later

**Accumulation:**
- Can be called multiple times (once per terrain material batch)
- HashSet prevents duplicates
- Groundcovers are accumulated across all calls

**Messages:**
- Only logs if new groundcovers are collected: `"Collected N groundcover(s) for copying"`

### Phase 2: Writing

```csharp
public void WriteAllGroundCovers()
```

**When Called:** Once by `AssetCopy.CopyTerrainMaterialsBatch()` after all terrain materials are copied

**What It Does:**
1. For each marked groundcover name:
   - Find original JSON line from scanned data
   - Copy dependencies (materials and DAE files)
   - Build final groundcover JSON with transformations
   - Add to in-memory collection
2. Write all groundcovers to target file in one operation
3. Clear the collection for next operation

**Transformations Applied:**

```csharp
private JsonNode BuildFinalGroundCover(JsonNode originalGroundCover, string originalName)
```

1. **Name**: Add level suffix
 - Original: `"FIeldplants"`
   - Target: `"FIeldplants_driver_training"`

2. **PersistentId**: New GUID
   - Original: `"bbc159b3-fb45-474c-a3a7-25260a8de7f0"`
   - Target: `"a1b2c3d4-e5f6-7890-a1b2-c3d4e5f67890"` (new GUID)

3. **All Types[].layer**: Add level suffix to **all** layers (even non-copied materials)
   - Original: `"Ploughed"`
   - Target: `"Ploughed_driver_training"`

4. **All Types[].shapeFilename**: Update to new target path
   - Original: `"/levels/driver_training/art/shapes/groundcover/fieldplant01var1.dae"`
 - Target: `"/levels/my_level/art/shapes/groundcover/MT_driver_training/fieldplant01var1.dae"`

**Merging Logic:**
- Loads existing groundcovers from target `items.level.json`
- If groundcover with same name exists: **replaces** it
- If groundcover doesn't exist: **adds** it
- Final write: **overwrites** entire file with all groundcovers (existing + new)

**Messages:**
- Start: `"Copying N groundcover(s)..."`
- End: `"Groundcover copy complete: N created, M updated"`

### Dependency Copying

```csharp
private void CopyGroundCoverDependencies(JsonNode groundCoverNode, string groundCoverName)
```

**What It Copies:**

1. **Groundcover Material** (if present):
   - From `material` property
   - Uses `MaterialCopier` to copy material JSON + textures
   - Target: `{target_level}/art/shapes/groundcover/MT_{source_level}/materials.json`

2. **DAE Files** (from all Types):
   - From each `Types[].shapeFilename`
   - Deduplicates (same DAE can be in multiple Types)
   - Uses `FileCopyHandler` to copy .dae file
   - Target: `{target_level}/art/shapes/groundcover/MT_{source_level}/{filename}.dae`

**Note:** Materials used by DAE files are scanned and copied separately by the DAE scanner during initial scan phase.

### Target Structure

```
{target_level}/
  main/MissionGroup/Level_object/vegetation/
    items.level.json  ? Groundcover definitions
  art/shapes/groundcover/
    MT_{source_level}/
      materials.json       ? Groundcover materials
      *.dae    ? Groundcover mesh files
      textures/               ? Textures from materials
```

### Data Loading

```csharp
public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
```

**When Called:** During `AssetCopy` initialization, loads data scanned by `BeamFileReader`

**What It Stores:**
- `_allGroundCoverJsonLines`: Complete JSON lines for all groundcovers in source level
- `_materialsJsonCopy`: Reference to all scanned materials (for dependency lookup)

### Methods Reference

```csharp
// Phase 1: Collection
public void CollectGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
// Marks groundcovers that reference the terrain materials

// Phase 2: Writing
public void WriteAllGroundCovers()
// Copies all marked groundcovers with dependencies

// Data Loading
public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)

// Building
private JsonNode BuildFinalGroundCover(JsonNode originalGroundCover, string originalName)
// Creates final JSON with all transformations

// Dependencies
private void CopyGroundCoverDependencies(JsonNode groundCoverNode, string groundCoverName)
private void CopyGroundCoverMaterial(string materialName, string groundCoverName)
private void CopyGroundCoverDaeFile(string daeFilePath)

// File Operations
private Dictionary<string, JsonNode> LoadExistingGroundCovers(FileInfo targetFile)
private void WriteAllGroundCoversToFile(FileInfo targetFile, IEnumerable<JsonNode> groundCovers)

// Deprecated (backward compatibility)
[Obsolete]
public void CopyGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
// Old method that combines both phases - still works but deprecated
```

## AssetCopy Orchestration

### Batch Processing Workflow

```csharp
public void Copy()
{
    // Separate terrain materials from other assets
    var terrainMaterials = _assetsToCopy.Where(x => x.CopyAssetType == CopyAssetType.Terrain).ToList();
    var otherAssets = _assetsToCopy.Where(x => x.CopyAssetType != CopyAssetType.Terrain).ToList();

    // Copy non-terrain assets first (roads, decals, DAE files)
    foreach (var item in otherAssets)
    {
     // Copy based on type...
    }

    // Process all terrain materials in batch
    if (terrainMaterials.Any())
    {
        CopyTerrainMaterialsBatch(terrainMaterials);
    }
}

private bool CopyTerrainMaterialsBatch(List<CopyAsset> terrainMaterials)
{
    // Copy all terrain materials (this also collects groundcovers)
    foreach (var item in terrainMaterials)
    {
        _terrainMaterialCopier.Copy(item); // ? Calls CollectGroundCoversForTerrainMaterials
    }

  // Write all collected groundcovers ONCE at the end
    _groundCoverCopier.WriteAllGroundCovers();

    return true;
}
```

### Initialization

```csharp
public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, 
        string namePath, string levelName, string levelNameCopyFrom)
{
    InitializeCopiers(namePath, levelName, levelNameCopyFrom);
  
    // Load groundcover data
    if (Logic.BeamFileReader.GroundCoverJsonLines != null)
    {
 _groundCoverCopier.LoadGroundCoverJsonLines(Logic.BeamFileReader.GroundCoverJsonLines);
    }
    
    // Load materials for lookup
    if (Logic.BeamFileReader.MaterialsJsonCopy != null)
    {
        _groundCoverCopier.LoadMaterialsJsonCopy(Logic.BeamFileReader.MaterialsJsonCopy);
    }
}
```

## Example Scenario

### Source Level: driver_training

**Terrain Materials:**
- `dirt_loose` (selected for copy)
- `Grass2` (selected for copy)
- `asphalt` (not selected)

**Groundcovers:**
```json
// WetWeed1
{
  "name": "WetWeed1",
  "material": "invisible",
  "Types": [
    {"layer": "dirt_loose", "shapeFilename": "/levels/driver_training/art/shapes/groundcover/weed1.dae"},
    {"layer": "Grass2", "shapeFilename": "/levels/driver_training/art/shapes/groundcover/weed2.dae"},
    {"layer": "asphalt", "shapeFilename": "/levels/driver_training/art/shapes/groundcover/weed3.dae"}
  ]
}

// FieldPlants
{
  "name": "FieldPlants",
  "material": "invisible",
  "Types": [
    {"layer": "Ploughed", "shapeFilename": "/levels/driver_training/art/shapes/groundcover/fieldplant01.dae"}
  ]
}
```

### Processing Flow

**Step 1: Copy dirt_loose terrain material**
- `TerrainMaterialCopier.Copy()` ? copies material to `art/terrains/main.materials.json` as `dirt_loose_driver_training`
- Calls `GroundCoverCopier.CollectGroundCoversForTerrainMaterials([dirt_loose])`
- Scans all groundcovers, finds `WetWeed1` has Type with `layer: "dirt_loose"`
- Marks `WetWeed1` for copying
- `FieldPlants` has no matching layers ? not marked

**Step 2: Copy Grass2 terrain material**
- `TerrainMaterialCopier.Copy()` ? copies material as `Grass2_driver_training`
- Calls `GroundCoverCopier.CollectGroundCoversForTerrainMaterials([Grass2])`
- Scans all groundcovers, finds `WetWeed1` has Type with `layer: "Grass2"`
- `WetWeed1` already marked ? skips (HashSet deduplication)

**Step 3: Write groundcovers**
- `GroundCoverCopier.WriteAllGroundCovers()`
- Processes `WetWeed1`:
  - Copies `invisible` material
  - Copies `weed1.dae`, `weed2.dae`, `weed3.dae` (all DAEs, even from `asphalt` Type)
  - Creates final JSON with ALL 3 Types
  - Suffixes ALL layers: `dirt_loose_driver_training`, `Grass2_driver_training`, `asphalt_driver_training`
  - Updates ALL shapeFilenames to new paths
  - Writes to `items.level.json` as `WetWeed1_driver_training`

### Result in Target Level

**Terrain Materials** (`art/terrains/main.materials.json`):
```json
{
  "dirt_loose_driver_training": {...},
  "Grass2_driver_training": {...}
}
```

**Groundcovers** (`main/MissionGroup/Level_object/vegetation/items.level.json`):
```json
{
  "name": "WetWeed1_driver_training",
  "persistentId": "NEW-GUID-HERE",
  "material": "invisible",
  "Types": [
 {"layer": "dirt_loose_driver_training", "shapeFilename": "/levels/my_level/art/shapes/groundcover/MT_driver_training/weed1.dae"},
    {"layer": "Grass2_driver_training", "shapeFilename": "/levels/my_level/art/shapes/groundcover/MT_driver_training/weed2.dae"},
    {"layer": "asphalt_driver_training", "shapeFilename": "/levels/my_level/art/shapes/groundcover/MT_driver_training/weed3.dae"}
  ]
}
```

**Files Copied:**
```
art/terrains/main.materials.json
art/terrains/*.png (textures from terrain materials)
art/shapes/groundcover/MT_driver_training/weed1.dae
art/shapes/groundcover/MT_driver_training/weed2.dae
art/shapes/groundcover/MT_driver_training/weed3.dae
art/shapes/groundcover/MT_driver_training/materials.json (invisible material)
main/MissionGroup/Level_object/vegetation/items.level.json
```

**Behavior:**
- `dirt_loose_driver_training` terrain ? activates weed1.dae on that terrain
- `Grass2_driver_training` terrain ? activates weed2.dae on that terrain
- `asphalt_driver_training` terrain ? **doesn't exist**, so weed3.dae is **never activated** (harmless!)

## Key Benefits

### 1. Simplicity
- ? **Straightforward logic**: Mark, then copy
- ? **No complex filtering**: Copy entire groundcovers
- ? **Minimal state**: Simple HashSet for tracking

### 2. Correctness
- ? **Complete coverage**: All Types preserved
- ? **No missing dependencies**: All DAEs copied
- ? **Batch efficiency**: Single write operation

### 3. User Experience
- ? **Minimal messages**: 3-4 progress updates total
- ? **Fast execution**: Batch processing
- ? **Automatic**: Groundcovers copied without user action

### 4. Robustness
- ? **Harmless extra references**: Unused layers don't cause problems
- ? **Generic JSON**: Preserves all properties
- ? **Duplicate handling**: Replaces existing groundcovers

## Error Handling

### TerrainMaterialCopier
- Invalid JSON ? logs error, returns false, stops copy
- Missing textures ? logs error, continues with other textures
- Duplicate material names ? logs warning, skips duplicate

### GroundCoverCopier
- Parse errors during collection ? logs warning, continues with other groundcovers
- Missing dependencies ? logs warning, continues with copy
- Write errors ? logs error, throws exception

### Recovery
- Partial terrain material copy ? fails fast, user must retry
- Partial groundcover collection ? collects what it can
- Partial groundcover write ? transaction-like (all or nothing)

## Integration Points

### BeamFileReader
- Scans source level during initialization
- Populates `GroundCoverJsonLines` with all groundcover JSON lines
- Populates `MaterialsJsonCopy` with all scanned materials
- These are loaded into `GroundCoverCopier` during `AssetCopy` construction

### PathConverter
- Generates target filenames with level suffix
- Converts Windows paths to BeamNG JSON paths
- Handles terrain-specific path patterns

### FileCopyHandler
- Copies files with fallback to zip extraction
- Handles missing source files by checking zip archives
- Used for both textures and DAE files

## Performance Characteristics

### Time Complexity
- Collection: O(G × T) where G = groundcovers, T = terrain materials
- Writing: O(G) where G = marked groundcovers
- Overall: Linear with number of assets

### Memory Usage
- Stores all groundcover JSON lines in memory (small, ~1KB each)
- HashSet of groundcover names (minimal)
- In-memory JSON for writing (cleared after write)

### I/O Operations
- Terrain materials: 1 read + 1 write per material
- Groundcovers: 1 read (existing) + 1 write (all) total
- Textures/DAEs: 1 copy per file
- **Optimized**: Single groundcover file write instead of multiple appends

## Testing Recommendations

### Unit Testing
1. Terrain material name generation with/without GUIDs
2. Texture path replacement in nested JSON
3. Groundcover collection with multiple terrain materials
4. Groundcover merging with existing items

### Integration Testing
1. Full copy workflow with real BeamNG data
2. Duplicate terrain material handling
3. Groundcover with missing dependencies
4. Multiple terrain materials ? single groundcover write

### Edge Cases
1. Groundcover with no matching terrain materials
2. Terrain material with no groundcovers
3. Groundcover already exists in target
4. Empty Types array
5. Missing DAE files
6. Circular material references

## Future Enhancements

Potential improvements:
1. Parallel groundcover processing
2. Progress reporting with percentage
3. Dry-run mode (preview without copying)
4. Selective groundcover exclusion
5. Groundcover Type filtering (advanced mode)
6. Validation of terrain material references
7. Automatic cleanup of orphaned groundcovers
