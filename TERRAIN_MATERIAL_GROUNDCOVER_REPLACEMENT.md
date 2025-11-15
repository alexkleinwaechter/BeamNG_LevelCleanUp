# Terrain Material & Groundcover Replacement System

## Overview
This system handles replacing terrain materials in BeamNG.drive levels while managing associated groundcovers. It supports two modes: **Add** (copy new materials) and **Replace** (replace existing materials).

---

## Architecture

### Key Components

```
AssetCopy.cs
??? TerrainMaterialCopier (Add Mode)
?   ??? GroundCoverCopier
?  ??? GroundCoverDependencyHelper
?
??? TerrainMaterialReplacer (Replace Mode)
    ??? GroundCoverReplacer
        ??? GroundCoverDependencyHelper (shared)
```

### Separation of Concerns

- **Add Mode**: Uses `GroundCoverCopier` - adds new groundcovers from source
- **Replace Mode**: Uses `GroundCoverReplacer` - modifies/removes existing groundcovers

---

## Add Mode (Working Correctly ?)

### Flow

1. **TerrainMaterialCopier.Copy()** copies source terrain material to target
2. **Calls GroundCoverCopier.AddGroundCoversForTerrainMaterial()** to queue groundcovers
3. **GroundCoverCopier.WriteAllGroundCovers()** writes all new groundcovers at once

### Groundcover Handling

```csharp
// Find source groundcovers that reference source material
var sourceGroundCovers = FindSourceGroundCoversByLayer(sourceMaterialName);

// Copy each with level suffix
foreach (var sourceGC in sourceGroundCovers)
{
    var newName = $"{sourceGCName}_{levelNameCopyFrom}";
    var newGC = CopyGroundCover(sourceGC);
    newGC["name"] = newName;
    newGC["persistentId"] = Guid.NewGuid();
    
    // Update layer references: "grass" ? "Grass2" (target material)
    UpdateLayerReferences(newGC, sourceMaterialName, targetMaterialName);
    
    // Copy material & DAE dependencies
    CopyGroundCoverDependencies(newGC, newName);
}
```

**Result**: New groundcovers added to vegetation file with source level suffix.

---

## Replace Mode (CURRENTLY BROKEN ?)

### Expected Flow

1. **TerrainMaterialReplacer.Replace()** replaces target terrain material
2. **Calls GroundCoverReplacer.ReplaceGroundCoversForTerrainMaterial()** to queue replacements
3. **GroundCoverReplacer.WriteAllGroundCoverReplacements()** processes ALL existing groundcovers

### Problem: Layers Not Being Removed

**What Should Happen** (Snow Example):
```
Before: SmallGrass1 has Types with layers: ["Grass2", "Grass2", "Grass2", "RockyDirt", "RockyDirt"]
After:  SmallGrass1 has Types with layers: ["RockyDirt", "RockyDirt"] ?
```

**What's Happening**:
```
Before: SmallGrass1 has layers: ["Grass2", "Grass2", "Grass2", "RockyDirt", "RockyDirt"]
After:  SmallGrass1 has layers: ["Grass2", "Grass2", "Grass2", "RockyDirt", "RockyDirt"] ? (unchanged)
```

---

## Current Implementation (GroundCoverReplacer.cs)

### WriteAllGroundCoverReplacements()

```csharp
public void WriteAllGroundCoverReplacements()
{
    // 1. Load existing groundcovers from target vegetation file
  var existingGroundCovers = LoadExistingGroundCovers(targetFile);
    
    // 2. Collect all replaced material names
    var allReplacedMaterialNames = _groundCoversToReplace.Keys.ToHashSet();
    // Example: {"Grass2", "Grass3", "Grass4"}
    
    // 3. Process each replacement request
    foreach (var kvp in _groundCoversToReplace)
    {
        var targetMaterialName = kvp.Key;        // "Grass2"
        var sourceMaterials = kvp.Value;
   var sourceMaterialName = sourceMaterials.First().Name; // "snow"
        
        var sourceGroundCovers = FindSourceGroundCoversByLayer(sourceMaterialName);
        
        if (!sourceGroundCovers.Any())
      {
            // SCENARIO B: Source has NO groundcovers (e.g., snow)
            // Just log message - actual removal happens in step 4
 PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Source material '{sourceMaterialName}' has no groundcovers. Removing '{targetMaterialName}' layers...");
        }
        else
        {
       // SCENARIO A: Source HAS groundcovers
        // Copy them with level suffix (like Add mode)
         foreach (var sourceGC in sourceGroundCovers)
        {
       var newGroundCover = CopySourceGroundCover(...);
   newGroundCovers.Add(newGroundCover);
            }
  }
    }
    
    // 4. Process ALL existing groundcovers to remove replaced layers
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        "Processing existing groundcovers to remove replaced layers...");
    
    foreach (var existingGC in existingGroundCovers.ToList())
    {
        var gcNode = existingGC.Value;
    var gcName = gcNode["name"]?.ToString();
   
        if (string.IsNullOrEmpty(gcName) || gcNode["class"]?.ToString() != "GroundCover")
       continue; // Skip non-groundcover entries
    
        // THIS IS THE CRITICAL PART - Remove layers
        var (modified, shouldDelete) = RemoveReplacedLayers(gcNode, allReplacedMaterialNames);
        
        if (shouldDelete)
        {
   deletedGroundCoverNames.Add(gcName);
        }
        else if (modified)
      {
            modifiedGroundCovers.Add(gcNode); // ? THIS SHOULD BE POPULATED!
     }
}
 
    // 5. Write changes back to file
if (newGroundCovers.Any() || deletedGroundCoverNames.Any() || modifiedGroundCovers.Any())
    {
        WriteGroundCoverChangesToFile(...);
    }
  else
    {
        PubSubChannel.SendMessage(PubSubMessageType.Warning,
            "No groundcover changes to write."); // ? CURRENTLY HITTING THIS!
    }
}
```

### RemoveReplacedLayers()

```csharp
private (bool modified, bool shouldDelete) RemoveReplacedLayers(
    JsonNode groundCover, 
    HashSet<string> replacedMaterialNames)
{
    if (groundCover["Types"] is not JsonArray types)
        return (false, false);
  
    var typesToRemove = new List<JsonNode>();
    
    // Find all Types that reference replaced materials
    foreach (var type in types)
    {
        var layerName = type?["layer"]?.ToString();
     if (!string.IsNullOrEmpty(layerName) && replacedMaterialNames.Contains(layerName))
        {
        typesToRemove.Add(type); // ? THIS SHOULD FIND MATCHES!
     }
    }
    
    // Remove the identified Types
    foreach (var typeToRemove in typesToRemove)
    {
   types.Remove(typeToRemove); // ? THIS SHOULD MODIFY THE JSON!
    }
    
    if (typesToRemove.Count > 0)
    {
      var remainingCount = types.Count;
        
    if (remainingCount == 0)
  {
      // All layers removed ? delete entire groundcover
      return (true, true);
    }
        else
        {
// Some layers removed ? keep modified groundcover
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Groundcover '{gcName}': Removed {typesToRemove.Count} layer(s)...");
            return (true, false); // ? THIS SHOULD RETURN!
        }
    }
    
    return (false, false); // ? CURRENTLY RETURNING THIS (no changes)
}
```

---

## Debugging Checklist

### 1. Is WriteAllGroundCoverReplacements() Being Called?
Check logs for:
```
"Processing X groundcover replacement(s)..."
```

### 2. Are Replaced Materials Correctly Identified?
Check logs for:
```
"Replaced materials: Grass2, Grass3, Grass4"
```

### 3. Is RemoveReplacedLayers() Being Called?
Check logs for:
```
"Processing existing groundcovers to remove replaced layers..."
```

### 4. Are Layers Being Found?
Check logs for:
```
"Groundcover 'SmallGrass1': Removed X layer(s) referencing replaced materials..."
```

### 5. Is modifiedGroundCovers Being Populated?
If NO logs from step 4, then `RemoveReplacedLayers()` is returning `(false, false)`.

**Possible causes**:
- Material name mismatch (case sensitivity?)
- `allReplacedMaterialNames` is empty
- `types.Remove()` is not modifying the JsonArray
- Loop is not iterating over groundcovers

---

## Expected Log Output (Snow Example)

```
Processing 3 groundcover replacement(s)...
Loaded 14 existing entries from target vegetation file.
Replaced materials: Grass2, Grass3, Grass4
Source material 'snow' has no groundcovers. Removing 'Grass2' layers...
Source material 'snow' has no groundcovers. Removing 'Grass3' layers...
Source material 'snow' has no groundcovers. Removing 'Grass4' layers...
Processing existing groundcovers to remove replaced layers...
Groundcover 'SmallGrass1': Removed 5 layer(s) referencing replaced materials (2 layer(s) remaining)
Groundcover 'Weed2': Removed 1 layer(s) referencing replaced materials (4 layer(s) remaining)
Groundcover 'Purple_flowers': Removed 2 layer(s) referencing replaced materials (3 layer(s) remaining)
Groundcover 'SmallGrass': Removed 5 layer(s) referencing replaced materials (2 layer(s) remaining)
Groundcover 'MediumGrass': Removed 2 layer(s) referencing replaced materials (4 layer(s) remaining)
Groundcover 'Daisies': Removed 2 layer(s) referencing replaced materials (3 layer(s) remaining)
Groundcover 'LongGrass': Removed 3 layer(s) referencing replaced materials (3 layer(s) remaining)
Groundcover 'LongGrass1': Removed 4 layer(s) referencing replaced materials (2 layer(s) remaining)
Groundcover 'Buttercups': Removed 4 layer(s) referencing replaced materials (1 layer(s) remaining)
Updated groundcovers: 0 added, 9 modified (layers removed), 0 deleted in ...
```

---

## Critical Questions for Debugging

1. **What do the actual logs show?** (Need exact log output from last run)
2. **Is `allReplacedMaterialNames` correct?** (Check log: "Replaced materials: ...")
3. **Is the loop over `existingGroundCovers` running?** (Should log "Processing existing groundcovers...")
4. **Is `RemoveReplacedLayers()` finding any matches?** (Should log "Removed X layer(s)...")
5. **Is `modifiedGroundCovers.Any()` true?** (If false, check why `RemoveReplacedLayers()` returns `(false, false)`)

---

## Known Working Code Path (Add Mode)

For comparison, here's the working Add mode flow:

```csharp
// AssetCopy.CopyTerrainMaterialsBatch()
foreach (var item in materialsToAdd)
{
    _terrainMaterialCopier.Copy(item); // ? Calls GroundCoverCopier.AddGroundCoversForTerrainMaterial()
}

// Write collected groundcovers ONCE
if (materialsToAdd.Any())
{
    _groundCoverCopier.WriteAllGroundCovers(); // ? WORKS PERFECTLY ?
}
```

---

## File Locations

- **AssetCopy.cs**: `BeamNG_LevelCleanUp\LogicCopyAssets\AssetCopy.cs`
- **GroundCoverReplacer.cs**: `BeamNG_LevelCleanUp\LogicCopyAssets\GroundCoverReplacer.cs`
- **TerrainMaterialReplacer.cs**: `BeamNG_LevelCleanUp\LogicCopyAssets\TerrainMaterialReplacer.cs`
- **Target vegetation file**: `{level}\main\MissionGroup\Level_object\vegetation\items.level.json`

---

## Next Steps for New AI Agent

1. **Request actual log output** from the user's last test run
2. **Add debug logging** to `RemoveReplacedLayers()` to trace:
   - What `replacedMaterialNames` contains
   - What `layerName` values are being checked
   - Why the comparison `replacedMaterialNames.Contains(layerName)` is failing
3. **Verify JSON modification** - ensure `types.Remove()` actually modifies the JsonArray
4. **Check case sensitivity** - material names might not match exactly

---

## Example Groundcover JSON Structure

```json
{
  "name": "SmallGrass1",
  "class": "GroundCover",
  "Types": [
    {"layer": "Grass2", ...},  ? Should be removed
    {"layer": "Grass2", ...},  ? Should be removed
    {"layer": "RockyDirt", ...}, ? Should be kept
  {"layer": "RockyDirt", ...}  ? Should be kept
  ]
}
```

After replacement with snow (no groundcovers):
```json
{
  "name": "SmallGrass1",
  "class": "GroundCover",
  "Types": [
    {"layer": "RockyDirt", ...}, ? Kept
    {"layer": "RockyDirt", ...}  ? Kept
  ]
}
```
