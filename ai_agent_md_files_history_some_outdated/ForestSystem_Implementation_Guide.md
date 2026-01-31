# Copy Forest Brushes - Implementation Guide

## Overview

This guide provides implementation details for the **Copy Forest Brushes** feature in the BeamNG Tools application. The feature allows users to copy forest brush definitions (painting templates) from a source BeamNG level to a target level, including all associated ForestItemData, shape files (.dae), materials, and textures.

**Key Concept**: We copy **ForestBrushes** (painting templates), NOT placed ForestItem instances. ForestItem instances are terrain-specific placements that would break on different levels. Users can then paint new forest items on the target level using the copied brushes.

---

## Table of Contents

1. [What Gets Copied](#what-gets-copied)
2. [BeamNG File Formats](#beamng-file-formats)
3. [Frontend UI Implementation](#frontend-ui-implementation)
4. [Backend Implementation](#backend-implementation)
5. [Folder Structure](#folder-structure)
6. [Reusable Components](#reusable-components)
7. [Implementation Checklist](#implementation-checklist)

---

## What Gets Copied

| Component | Copy? | File Location | Reason |
|-----------|-------|---------------|--------|
| ForestBrush | ✅ Yes | `main.forestbrushes4.json` | Brush groupings (paint palettes) |
| ForestBrushElement | ✅ Yes | `main.forestbrushes4.json` | Brush-to-mesh links with scale/probability |
| ForestItemData | ✅ Yes | `art/forest/managedItemData.json` | Mesh template definitions |
| Shape Files (.dae/.cdae) | ✅ Yes | `art/shapes/**/*.dae` | 3D mesh assets |
| Materials (.materials.json) | ✅ Yes | `art/shapes/**/*.materials.json` | Visual appearance |
| Textures (.png, .dds) | ✅ Yes | Various | Texture assets |
| ForestItem instances | ❌ No | `forest/*.forest4.json` | Level-specific placements |

### Dependency Chain

```
ForestBrush ("Trees_Tropical_1")
│
└── ForestBrushElement ("tro_tree_1_huge")
    │   - scaleMin: 1.2
    │   - scaleMax: 1.4
    │   - probability: 0.8
    │
    └── ForestItemData ("tro_tree_1_huge")
        │   - shapeFile: "/levels/.../tro_tree_1_huge.dae"
        │   - radius: 0.5
        │   - windScale: 0.6
        │
        └── DAE File (tro_tree_1_huge.dae)
            │
            ├── Materials (tro_tree_1_huge.materials.json)
            │   - tro_bark_material
            │   - tro_leaves_material
            │
            └── Textures
                - tro_tree_bark_d.color.png
                - tro_tree_bark_n.normal.png
                - tro_tree_leaves_d.color.png
```

---

## BeamNG File Formats

### main.forestbrushes4.json (NDJSON Format)

**Important**: This file uses **Newline Delimited JSON (NDJSON)**, NOT a standard JSON array. Each line is a separate JSON object:

```json
{"name":"ForestBrush_Trees_Tropical_1","internalName":"Trees_Tropical_1","class":"ForestBrush","persistentId":"4a8e0f39-...","__parent":"ForestBrushGroup","forestItemData":"Trees_Tropical_1"}
{"name":"ForestBrush_Trees_Palm","internalName":"Trees_Palm","class":"ForestBrush","persistentId":"4a8eac00-...","__parent":"ForestBrushGroup","forestItemData":"Trees_palm"}
{"internalName":"tro_tree_1_huge","class":"ForestBrushElement","persistentId":"4a8e0f3a-...","__parent":"ForestBrush_Trees_Tropical_1","forestItemData":"tro_tree_1_huge","probability":0.8,"scaleMax":1.4,"scaleMin":1.2}
{"name":"ForestBrushGroup","class":"SimGroup","persistentId":"ddd53bc3-..."}
```

**Key Properties**:
- `class`: Either `ForestBrush`, `ForestBrushElement`, or `SimGroup`
- `__parent`: Parent object name (ForestBrushGroup for brushes, brush name for elements)
- `forestItemData`: Reference to ForestItemData by name (for elements)
- `persistentId`: GUID - **generate new GUIDs when copying**

### art/forest/managedItemData.json (Standard JSON Object)

```json
{
  "tro_tree_1_huge": {
    "name": "tro_tree_1_huge",
    "internalName": "tro_tree_1_huge",
    "class": "TSForestItemData",
    "persistentId": "fd874777-...",
    "shapeFile": "/levels/jungle_rock_island/art/shapes/trees/trees_tropical_1/tro_tree_1_huge.dae",
    "radius": 0.5,
    "mass": 100,
    "windScale": 0.6,
    "branchAmp": 0.05,
    "detailAmp": 0.1
  }
}
```

**Key Properties**:
- `shapeFile`: Path to .dae file (BeamNG relative path with `/`)
- `class`: Always `TSForestItemData`
- Physics properties: `radius`, `mass`, `rigidity`, `dampingCoefficient`
- Wind animation: `windScale`, `branchAmp`, `trunkBendScale`, `detailAmp`, `detailFreq`

---

## Frontend UI Implementation

### New Page: CopyForestBrushes.razor

Create a new Blazor page following the pattern of `CopyTerrains.razor`. The page should support:

1. **Standard Mode**: User selects source and target maps manually
2. **Wizard Mode**: Target map is preset from CreateLevel wizard (optional future enhancement)

### UI Components Structure

```
CopyForestBrushes.razor
├── Header: "Copy Forest Brushes"
├── MudExpansionPanels (FileSelect)
│   ├── Panel 0: BeamNG Install Directory
│   ├── Panel 1: Source Level Selection
│   │   ├── FileSelectComponent (zip file)
│   │   └── MudSelect (vanilla levels dropdown)
│   └── Panel 2: Target Level Selection
│       ├── FileSelectComponent (zip file)
│       ├── FileSelectComponent (folder)
│       └── MudSelect (vanilla levels dropdown)
├── Reset Button (when both maps selected)
├── MudTable (forest brushes list)
│   ├── Column: Checkbox (multi-select)
│   ├── Column: Preview (optional - icon based on type)
│   ├── Column: Brush Name (internalName)
│   ├── Column: Element Count
│   ├── Column: Duplicate Status
│   └── Column: Size MB
├── MudDrawer (errors/warnings/messages)
└── Footer
    ├── Selection Summary
    ├── Copy Button
    ├── Log Buttons
    └── Build Zipfile Button
```

### Key Differences from CopyTerrains.razor

| Aspect | CopyTerrains | CopyForestBrushes |
|--------|--------------|-------------------|
| Asset Type | `CopyAssetType.Terrain` | `CopyAssetType.ForestBrush` (new) |
| Table Columns | Material name, colors, roughness | Brush name, element count, type |
| Replace Feature | Yes (target material dropdown) | No (brushes are additive) |
| Extra Options | PBR upgrade check | None needed |

### Page Code-Behind: CopyForestBrushes.razor.cs

Follow the pattern from `CopyTerrains.razor.cs`:

```csharp
public partial class CopyForestBrushes
{
    // Same structure as CopyTerrains.razor.cs
    // Key differences:
    
    // 1. Filter for forest brush assets only
    private void FillCopyList()
    {
        foreach (var asset in Reader.GetCopyList()
            .Where(x => x.CopyAssetType == CopyAssetType.ForestBrush))
        {
            var item = new GridFileListItem
            {
                Identifier = asset.Identifier,
                AssetType = asset.CopyAssetType.ToString(),
                FullName = asset.Name,
                SizeMb = asset.SizeMb,
                Duplicate = asset.Duplicate,
                DuplicateFrom = asset.DuplicateFrom,
                CopyAsset = asset
            };
            BindingListCopy.Add(item);
        }
    }
    
    // 2. Simplified copy dialog (no replace mode)
    private async Task CopyDialog()
    {
        // Similar to CopyTerrains but without replace logic
    }
    
    // 3. No PBR upgrade check needed
    // 4. No "Select Another Source" feature needed initially
}
```

### Navigation Entry

Add to `MyNavMenu.razor`:

```razor
<MudNavLink Href="CopyForestBrushes" Icon="@Icons.Material.Filled.Forest">
    Copy Forest Brushes
</MudNavLink>
```

---

## Backend Implementation

### New Folder Structure

Create a new folder `LogicCopyForest/` for forest-related copy logic:

```
BeamNG_LevelCleanUp/
├── LogicCopyForest/                    # NEW FOLDER
│   ├── ForestBrushCopyScanner.cs       # Scan source for brushes
│   ├── ForestBrushCopier.cs            # Main copy orchestrator
│   └── ForestItemDataCopier.cs         # Copy ForestItemData entries
├── LogicCopyAssets/                    # EXISTING - Reuse these
│   ├── DaeCopier.cs                    # Already handles DAE copying
│   ├── MaterialCopier.cs               # Already handles material copying
│   ├── FileCopyHandler.cs              # File operations
│   └── PathConverter.cs                # Path transformations
└── Objects/
    ├── CopyAsset.cs                    # Add ForestBrush to CopyAssetType
    ├── ForestBrushInfo.cs              # NEW - Forest brush data model
    └── ... existing files
```

### Step 1: Extend CopyAssetType Enum

In `Objects/AssetType.cs` (or `CopyAsset.cs`):

```csharp
public enum CopyAssetType
{
    Road,
    Dae,
    Decal,
    Terrain,
    ForestBrush  // ADD THIS
}
```

### Step 2: Create ForestBrushInfo.cs

```csharp
// Objects/ForestBrushInfo.cs
namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Represents a forest brush with its elements for copying
/// </summary>
public class ForestBrushInfo
{
    public string Name { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string PersistentId { get; set; } = string.Empty;
    public List<ForestBrushElementInfo> Elements { get; set; } = new();
    public List<string> ReferencedItemDataNames { get; set; } = new();
}

/// <summary>
/// Represents a brush element linking to ForestItemData
/// </summary>
public class ForestBrushElementInfo
{
    public string InternalName { get; set; } = string.Empty;
    public string PersistentId { get; set; } = string.Empty;
    public string ForestItemDataRef { get; set; } = string.Empty;
    public string ParentBrushName { get; set; } = string.Empty;
    
    // Optional properties to preserve
    public float? ScaleMin { get; set; }
    public float? ScaleMax { get; set; }
    public float? Probability { get; set; }
    public float? SinkMin { get; set; }
    public float? SinkMax { get; set; }
    public float? SlopeMin { get; set; }
    public float? SlopeMax { get; set; }
    public float? ElevationMin { get; set; }
    public float? ElevationMax { get; set; }
    public int? RotationRange { get; set; }
}

/// <summary>
/// Represents ForestItemData with shape file reference
/// </summary>
public class ForestItemDataInfo
{
    public string Name { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string PersistentId { get; set; } = string.Empty;
    public string ShapeFile { get; set; } = string.Empty;
    
    // All other properties stored as raw JSON to preserve unknown fields
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}
```

### Step 3: Create ForestBrushCopyScanner.cs

```csharp
// LogicCopyForest/ForestBrushCopyScanner.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyForest;

/// <summary>
/// Scans source level for forest brushes and builds copy asset list
/// </summary>
public class ForestBrushCopyScanner
{
    private readonly string _sourceLevelPath;
    private readonly string _targetLevelPath;
    
    public ForestBrushCopyScanner(string sourceLevelPath, string targetLevelPath)
    {
        _sourceLevelPath = sourceLevelPath;
        _targetLevelPath = targetLevelPath;
    }
    
    /// <summary>
    /// Scans source level and returns list of copyable forest brushes
    /// </summary>
    public List<CopyAsset> ScanForestBrushes()
    {
        var copyAssets = new List<CopyAsset>();
        
        // 1. Parse main.forestbrushes4.json (NDJSON)
        var brushesPath = FindForestBrushesFile(_sourceLevelPath);
        if (string.IsNullOrEmpty(brushesPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                "No main.forestbrushes4.json found in source level");
            return copyAssets;
        }
        
        var brushes = ParseForestBrushesNdjson(brushesPath);
        
        // 2. Parse art/forest/managedItemData.json
        var itemDataPath = Path.Combine(_sourceLevelPath, "art", "forest", "managedItemData.json");
        var itemDataMap = ParseManagedItemData(itemDataPath);
        
        // 3. Get existing brushes in target for duplicate detection
        var targetBrushNames = GetTargetBrushNames();
        
        // 4. Build copy assets for each brush
        foreach (var brush in brushes)
        {
            var copyAsset = new CopyAsset
            {
                Identifier = Guid.NewGuid(),
                Name = brush.InternalName,
                CopyAssetType = CopyAssetType.ForestBrush,
                Duplicate = targetBrushNames.Contains(brush.InternalName),
                DuplicateFrom = targetBrushNames.Contains(brush.InternalName) ? "Target Level" : null,
                // Store brush info for later copying
                ForestBrushInfo = brush
            };
            
            // Calculate total size from all referenced shapes
            var totalSize = 0L;
            foreach (var elementRef in brush.ReferencedItemDataNames)
            {
                if (itemDataMap.TryGetValue(elementRef, out var itemData))
                {
                    totalSize += GetShapeFileSize(itemData.ShapeFile);
                }
            }
            copyAsset.SizeMb = Math.Round(totalSize / 1024.0 / 1024.0, 2);
            
            copyAssets.Add(copyAsset);
        }
        
        return copyAssets;
    }
    
    private List<ForestBrushInfo> ParseForestBrushesNdjson(string filePath)
    {
        var brushes = new Dictionary<string, ForestBrushInfo>();
        var elements = new List<(string parentName, ForestBrushElementInfo element)>();
        
        foreach (var line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                
                var objClass = root.GetProperty("class").GetString();
                
                if (objClass == "ForestBrush")
                {
                    var brush = new ForestBrushInfo
                    {
                        Name = root.TryGetProperty("name", out var name) ? name.GetString() : "",
                        InternalName = root.TryGetProperty("internalName", out var iname) ? iname.GetString() : "",
                        PersistentId = root.TryGetProperty("persistentId", out var pid) ? pid.GetString() : ""
                    };
                    
                    // Some brushes have forestItemData directly (single-item brush)
                    if (root.TryGetProperty("forestItemData", out var fid))
                    {
                        brush.ReferencedItemDataNames.Add(fid.GetString());
                    }
                    
                    brushes[brush.Name] = brush;
                }
                else if (objClass == "ForestBrushElement")
                {
                    var element = new ForestBrushElementInfo
                    {
                        InternalName = root.TryGetProperty("internalName", out var iname) ? iname.GetString() : "",
                        PersistentId = root.TryGetProperty("persistentId", out var pid) ? pid.GetString() : "",
                        ForestItemDataRef = root.TryGetProperty("forestItemData", out var fid) ? fid.GetString() : "",
                        ParentBrushName = root.TryGetProperty("__parent", out var parent) ? parent.GetString() : ""
                    };
                    
                    // Parse optional properties
                    if (root.TryGetProperty("scaleMin", out var smin)) element.ScaleMin = smin.GetSingle();
                    if (root.TryGetProperty("scaleMax", out var smax)) element.ScaleMax = smax.GetSingle();
                    if (root.TryGetProperty("probability", out var prob)) element.Probability = prob.GetSingle();
                    if (root.TryGetProperty("sinkMin", out var skmin)) element.SinkMin = skmin.GetSingle();
                    if (root.TryGetProperty("sinkMax", out var skmax)) element.SinkMax = skmax.GetSingle();
                    if (root.TryGetProperty("slopeMin", out var slmin)) element.SlopeMin = slmin.GetSingle();
                    if (root.TryGetProperty("slopeMax", out var slmax)) element.SlopeMax = slmax.GetSingle();
                    if (root.TryGetProperty("elevationMin", out var elmin)) element.ElevationMin = elmin.GetSingle();
                    if (root.TryGetProperty("elevationMax", out var elmax)) element.ElevationMax = elmax.GetSingle();
                    if (root.TryGetProperty("rotationRange", out var rot)) element.RotationRange = rot.GetInt32();
                    
                    elements.Add((element.ParentBrushName, element));
                }
                // SimGroup (ForestBrushGroup) - just skip, we create our own
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Failed to parse forest brush line: {ex.Message}");
            }
        }
        
        // Link elements to brushes
        foreach (var (parentName, element) in elements)
        {
            if (brushes.TryGetValue(parentName, out var brush))
            {
                brush.Elements.Add(element);
                if (!string.IsNullOrEmpty(element.ForestItemDataRef))
                {
                    brush.ReferencedItemDataNames.Add(element.ForestItemDataRef);
                }
            }
        }
        
        return brushes.Values.ToList();
    }
    
    private Dictionary<string, ForestItemDataInfo> ParseManagedItemData(string filePath)
    {
        var result = new Dictionary<string, ForestItemDataInfo>();
        
        if (!File.Exists(filePath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                $"managedItemData.json not found at {filePath}");
            return result;
        }
        
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var itemData = new ForestItemDataInfo
                {
                    Name = prop.Value.TryGetProperty("name", out var name) ? name.GetString() : prop.Name,
                    InternalName = prop.Value.TryGetProperty("internalName", out var iname) ? iname.GetString() : prop.Name,
                    PersistentId = prop.Value.TryGetProperty("persistentId", out var pid) ? pid.GetString() : "",
                    ShapeFile = prop.Value.TryGetProperty("shapeFile", out var sf) ? sf.GetString() : ""
                };
                
                result[prop.Name] = itemData;
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Failed to parse managedItemData.json: {ex.Message}");
        }
        
        return result;
    }
    
    private string FindForestBrushesFile(string levelPath)
    {
        // Try common locations
        var candidates = new[]
        {
            Path.Combine(levelPath, "main.forestbrushes4.json"),
            Path.Combine(Directory.GetParent(levelPath)?.FullName ?? "", "main.forestbrushes4.json")
        };
        
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        
        // Search recursively
        var files = Directory.GetFiles(levelPath, "main.forestbrushes4.json", SearchOption.AllDirectories);
        return files.FirstOrDefault();
    }
    
    private HashSet<string> GetTargetBrushNames()
    {
        var names = new HashSet<string>();
        var brushesPath = FindForestBrushesFile(_targetLevelPath);
        
        if (string.IsNullOrEmpty(brushesPath)) return names;
        
        foreach (var line in File.ReadAllLines(brushesPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("class", out var cls) && 
                    cls.GetString() == "ForestBrush" &&
                    doc.RootElement.TryGetProperty("internalName", out var iname))
                {
                    names.Add(iname.GetString());
                }
            }
            catch { /* ignore parse errors */ }
        }
        
        return names;
    }
    
    private long GetShapeFileSize(string shapeFile)
    {
        if (string.IsNullOrEmpty(shapeFile)) return 0;
        
        var fullPath = PathResolver.ResolvePath(_sourceLevelPath, shapeFile, true);
        if (!File.Exists(fullPath)) return 0;
        
        var size = new FileInfo(fullPath).Length;
        
        // Add materials file size estimate
        var materialsPath = Path.ChangeExtension(fullPath, ".materials.json");
        if (File.Exists(materialsPath))
        {
            size += new FileInfo(materialsPath).Length;
        }
        
        return size;
    }
}
```

### Step 4: Create ForestBrushCopier.cs

```csharp
// LogicCopyForest/ForestBrushCopier.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Logic;

namespace BeamNG_LevelCleanUp.LogicCopyForest;

/// <summary>
/// Orchestrates copying of forest brushes between levels
/// </summary>
public class ForestBrushCopier
{
    private readonly string _sourceLevelPath;
    private readonly string _targetLevelPath;
    private readonly PathConverter _pathConverter;
    private readonly FileCopyHandler _fileCopyHandler;
    private readonly MaterialCopier _materialCopier;
    private readonly DaeCopier _daeCopier;
    
    // Track what we've already copied to avoid duplicates
    private readonly HashSet<string> _copiedItemDataNames = new();
    private readonly HashSet<string> _copiedShapeFiles = new();
    
    public ForestBrushCopier(
        string sourceLevelPath, 
        string targetLevelPath,
        PathConverter pathConverter,
        FileCopyHandler fileCopyHandler,
        MaterialCopier materialCopier,
        DaeCopier daeCopier)
    {
        _sourceLevelPath = sourceLevelPath;
        _targetLevelPath = targetLevelPath;
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
        _materialCopier = materialCopier;
        _daeCopier = daeCopier;
    }
    
    /// <summary>
    /// Copies selected forest brushes to target level
    /// </summary>
    public bool CopyBrushes(List<CopyAsset> selectedAssets)
    {
        // 1. Load source managedItemData.json
        var sourceItemDataPath = Path.Combine(_sourceLevelPath, "art", "forest", "managedItemData.json");
        var sourceItemData = LoadManagedItemData(sourceItemDataPath);
        
        // 2. Collect all ForestItemData and shapes to copy
        var itemDataToCopy = new Dictionary<string, JsonNode>();
        var brushesToCopy = new List<CopyAsset>();
        
        foreach (var asset in selectedAssets.Where(a => a.CopyAssetType == CopyAssetType.ForestBrush))
        {
            if (asset.ForestBrushInfo == null) continue;
            
            brushesToCopy.Add(asset);
            
            foreach (var itemDataName in asset.ForestBrushInfo.ReferencedItemDataNames)
            {
                if (_copiedItemDataNames.Contains(itemDataName)) continue;
                
                if (sourceItemData.TryGetValue(itemDataName, out var itemDataNode))
                {
                    itemDataToCopy[itemDataName] = itemDataNode;
                    _copiedItemDataNames.Add(itemDataName);
                }
            }
        }
        
        // 3. Copy all shape files and materials
        _materialCopier.BeginBatch();
        
        foreach (var (name, itemDataNode) in itemDataToCopy)
        {
            var shapeFile = itemDataNode["shapeFile"]?.GetValue<string>();
            if (string.IsNullOrEmpty(shapeFile)) continue;
            
            if (_copiedShapeFiles.Contains(shapeFile)) continue;
            _copiedShapeFiles.Add(shapeFile);
            
            if (!CopyShapeFileWithMaterials(shapeFile, itemDataNode))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Failed to copy shape file for {name}");
            }
        }
        
        _materialCopier.EndBatch();
        
        // 4. Write/merge managedItemData.json in target
        if (!MergeManagedItemData(itemDataToCopy))
        {
            return false;
        }
        
        // 5. Write/merge main.forestbrushes4.json in target
        if (!MergeForestBrushes(brushesToCopy))
        {
            return false;
        }
        
        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            $"Successfully copied {brushesToCopy.Count} forest brushes and {itemDataToCopy.Count} item definitions");
        
        return true;
    }
    
    private Dictionary<string, JsonNode> LoadManagedItemData(string filePath)
    {
        var result = new Dictionary<string, JsonNode>();
        
        if (!File.Exists(filePath)) return result;
        
        try
        {
            var json = File.ReadAllText(filePath);
            var node = JsonNode.Parse(json);
            
            foreach (var prop in node.AsObject())
            {
                result[prop.Key] = prop.Value.DeepClone();
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Failed to load managedItemData.json: {ex.Message}");
        }
        
        return result;
    }
    
    private bool CopyShapeFileWithMaterials(string shapeFile, JsonNode itemDataNode)
    {
        try
        {
            // Resolve source path
            var sourceShapePath = PathResolver.ResolvePath(_sourceLevelPath, shapeFile, true);
            if (!File.Exists(sourceShapePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Shape file not found: {shapeFile}");
                return true; // Continue with other files
            }
            
            // Calculate target path
            var targetShapePath = _pathConverter.GetTargetFileName(sourceShapePath);
            
            // Copy .dae and .cdae files
            Directory.CreateDirectory(Path.GetDirectoryName(targetShapePath));
            _fileCopyHandler.CopyFile(sourceShapePath, targetShapePath);
            
            var cdaePath = Path.ChangeExtension(sourceShapePath, ".cdae");
            if (File.Exists(cdaePath))
            {
                _fileCopyHandler.CopyFile(cdaePath, Path.ChangeExtension(targetShapePath, ".cdae"));
            }
            
            // Find and copy materials
            var materialsPath = Path.ChangeExtension(sourceShapePath, ".materials.json");
            if (File.Exists(materialsPath))
            {
                CopyMaterialsFile(materialsPath);
            }
            
            // Also check for main.materials.json in same directory
            var mainMaterialsPath = Path.Combine(Path.GetDirectoryName(sourceShapePath), "main.materials.json");
            if (File.Exists(mainMaterialsPath))
            {
                CopyMaterialsFile(mainMaterialsPath);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error copying shape file {shapeFile}: {ex.Message}");
            return false;
        }
    }
    
    private void CopyMaterialsFile(string sourceMaterialsPath)
    {
        try
        {
            var targetMaterialsPath = _pathConverter.GetTargetFileName(sourceMaterialsPath);
            
            // Read source materials
            var sourceJson = File.ReadAllText(sourceMaterialsPath);
            var sourceNode = JsonNode.Parse(sourceJson);
            
            // Read or create target materials
            JsonNode targetNode;
            if (File.Exists(targetMaterialsPath))
            {
                targetNode = JsonNode.Parse(File.ReadAllText(targetMaterialsPath));
            }
            else
            {
                targetNode = new JsonObject();
                Directory.CreateDirectory(Path.GetDirectoryName(targetMaterialsPath));
            }
            
            // Merge materials
            foreach (var mat in sourceNode.AsObject())
            {
                if (!targetNode.AsObject().ContainsKey(mat.Key))
                {
                    // Clone the material and update paths
                    var matCopy = mat.Value.DeepClone();
                    
                    // Generate new persistentId
                    matCopy["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();
                    
                    // Copy textures and update paths
                    CopyMaterialTextures(matCopy, sourceMaterialsPath);
                    
                    targetNode.AsObject().Add(mat.Key, matCopy);
                }
            }
            
            // Write merged materials
            File.WriteAllText(targetMaterialsPath, 
                targetNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                $"Error copying materials file: {ex.Message}");
        }
    }
    
    private void CopyMaterialTextures(JsonNode materialNode, string sourceMaterialsPath)
    {
        var textureProperties = new[] 
        { 
            "diffuseMap", "normalMap", "specularMap", "roughnessMap", 
            "metallicMap", "ambientOcclusionMap", "emissiveMap", "opacityMap",
            "detailMap", "detailNormalMap", "colorPaletteMap",
            // Also check Stages array
        };
        
        foreach (var prop in textureProperties)
        {
            if (materialNode[prop] is JsonValue textureValue)
            {
                var texturePath = textureValue.GetValue<string>();
                CopyTextureFile(texturePath, sourceMaterialsPath);
            }
        }
        
        // Check Stages array
        if (materialNode["Stages"] is JsonArray stages)
        {
            foreach (var stage in stages)
            {
                if (stage is JsonObject stageObj)
                {
                    foreach (var prop in stageObj)
                    {
                        if (prop.Key.EndsWith("Map", StringComparison.OrdinalIgnoreCase) ||
                            prop.Key.EndsWith("Tex", StringComparison.OrdinalIgnoreCase))
                        {
                            var texturePath = prop.Value?.GetValue<string>();
                            if (!string.IsNullOrEmpty(texturePath))
                            {
                                CopyTextureFile(texturePath, sourceMaterialsPath);
                            }
                        }
                    }
                }
            }
        }
    }
    
    private bool MergeManagedItemData(Dictionary<string, JsonNode> itemDataToCopy)
    {
        try
        {
            var targetDir = Path.Combine(_targetLevelPath, "art", "forest");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, "managedItemData.json");
            
            // Load or create target
            JsonNode targetNode;
            if (File.Exists(targetPath))
            {
                targetNode = JsonNode.Parse(File.ReadAllText(targetPath));
            }
            else
            {
                targetNode = new JsonObject();
            }
            
            // Merge item data
            foreach (var (name, itemData) in itemDataToCopy)
            {
                if (!targetNode.AsObject().ContainsKey(name))
                {
                    // Clone and update
                    var itemCopy = itemData.DeepClone();
                    
                    // Generate new persistentId
                    itemCopy["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();
                    
                    // Update shapeFile path to target level
                    if (itemCopy["shapeFile"] is JsonValue shapeValue)
                    {
                        var oldPath = shapeValue.GetValue<string>();
                        var newPath = _pathConverter.ConvertBeamNgPath(oldPath);
                        itemCopy["shapeFile"] = newPath;
                    }
                    
                    targetNode.AsObject().Add(name, itemCopy);
                }
            }
            
            // Write merged data
            File.WriteAllText(targetPath, 
                targetNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            
            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error merging managedItemData.json: {ex.Message}");
            return false;
        }
    }
    
    private bool MergeForestBrushes(List<CopyAsset> brushesToCopy)
    {
        try
        {
            var targetPath = Path.Combine(_targetLevelPath, "main.forestbrushes4.json");
            
            // Read existing lines
            var existingLines = new List<string>();
            var existingBrushNames = new HashSet<string>();
            var hasForestBrushGroup = false;
            
            if (File.Exists(targetPath))
            {
                foreach (var line in File.ReadAllLines(targetPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    existingLines.Add(line);
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("class", out var cls))
                        {
                            if (cls.GetString() == "ForestBrush" &&
                                doc.RootElement.TryGetProperty("name", out var name))
                            {
                                existingBrushNames.Add(name.GetString());
                            }
                            if (cls.GetString() == "SimGroup" &&
                                doc.RootElement.TryGetProperty("name", out var gname) &&
                                gname.GetString() == "ForestBrushGroup")
                            {
                                hasForestBrushGroup = true;
                            }
                        }
                    }
                    catch { }
                }
            }
            
            // Generate new lines for brushes and elements
            var newLines = new List<string>();
            
            foreach (var asset in brushesToCopy)
            {
                var brush = asset.ForestBrushInfo;
                if (brush == null) continue;
                if (existingBrushNames.Contains(brush.Name)) continue;
                
                // Create brush JSON
                var brushObj = new JsonObject
                {
                    ["name"] = brush.Name,
                    ["internalName"] = brush.InternalName,
                    ["class"] = "ForestBrush",
                    ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
                    ["__parent"] = "ForestBrushGroup"
                };
                
                // If brush has direct forestItemData reference
                if (brush.ReferencedItemDataNames.Count == 1 && brush.Elements.Count == 0)
                {
                    brushObj["forestItemData"] = brush.ReferencedItemDataNames[0];
                }
                
                newLines.Add(brushObj.ToJsonString());
                
                // Create element JSONs
                foreach (var element in brush.Elements)
                {
                    var elemObj = new JsonObject
                    {
                        ["internalName"] = element.InternalName,
                        ["class"] = "ForestBrushElement",
                        ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
                        ["__parent"] = brush.Name,
                        ["forestItemData"] = element.ForestItemDataRef
                    };
                    
                    // Add optional properties if set
                    if (element.ScaleMin.HasValue) elemObj["scaleMin"] = element.ScaleMin.Value;
                    if (element.ScaleMax.HasValue) elemObj["scaleMax"] = element.ScaleMax.Value;
                    if (element.Probability.HasValue) elemObj["probability"] = element.Probability.Value;
                    if (element.SinkMin.HasValue) elemObj["sinkMin"] = element.SinkMin.Value;
                    if (element.SinkMax.HasValue) elemObj["sinkMax"] = element.SinkMax.Value;
                    if (element.SlopeMin.HasValue) elemObj["slopeMin"] = element.SlopeMin.Value;
                    if (element.SlopeMax.HasValue) elemObj["slopeMax"] = element.SlopeMax.Value;
                    if (element.ElevationMin.HasValue) elemObj["elevationMin"] = element.ElevationMin.Value;
                    if (element.ElevationMax.HasValue) elemObj["elevationMax"] = element.ElevationMax.Value;
                    if (element.RotationRange.HasValue) elemObj["rotationRange"] = element.RotationRange.Value;
                    
                    newLines.Add(elemObj.ToJsonString());
                }
            }
            
            // Add ForestBrushGroup if not exists
            if (!hasForestBrushGroup)
            {
                var groupObj = new JsonObject
                {
                    ["name"] = "ForestBrushGroup",
                    ["class"] = "SimGroup",
                    ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant()
                };
                newLines.Add(groupObj.ToJsonString());
            }
            
            // Combine and write
            var allLines = existingLines.Concat(newLines);
            File.WriteAllLines(targetPath, allLines);
            
            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error merging forest brushes: {ex.Message}");
            return false;
        }
    }
}
```

### Step 5: Integrate into AssetCopy.cs

Add the `ForestBrushCopier` usage to the existing `AssetCopy.cs`:

```csharp
// In AssetCopy.cs, add to Copy() method:

public void Copy()
{
    // ... existing code for roads, decals, DAE files, terrain materials ...
    
    // Process forest brushes
    var forestBrushes = _assetsToCopy.Where(x => x.CopyAssetType == CopyAssetType.ForestBrush).ToList();
    if (forestBrushes.Any())
    {
        stopFaultyFile = !CopyForestBrushes(forestBrushes);
    }
    
    if (!stopFaultyFile)
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Done! Assets copied.");
}

private bool CopyForestBrushes(List<CopyAsset> forestBrushes)
{
    var forestCopier = new ForestBrushCopier(
        PathResolver.LevelPathCopyFrom,
        PathResolver.LevelNamePath,
        _pathConverter,
        _fileCopyHandler,
        _materialCopier,
        _daeCopier);
    
    return forestCopier.CopyBrushes(forestBrushes);
}
```

### Step 6: Extend CopyAsset.cs

Add the `ForestBrushInfo` property:

```csharp
// In Objects/CopyAsset.cs, add:

public ForestBrushInfo? ForestBrushInfo { get; set; }
```

---

## Folder Structure

After implementation, the folder structure should be:

```
BeamNG_LevelCleanUp/
├── BlazorUI/
│   └── Pages/
│       ├── CopyAssets.razor               # Existing
│       ├── CopyTerrains.razor             # Existing
│       ├── CopyTerrains.razor.cs          # Existing
│       ├── CopyForestBrushes.razor        # NEW
│       └── CopyForestBrushes.razor.cs     # NEW
├── LogicCopyAssets/                       # Existing - Reuse
│   ├── AssetCopy.cs                       # Modified
│   ├── DaeCopier.cs                       # Reuse
│   ├── MaterialCopier.cs                  # Reuse
│   ├── FileCopyHandler.cs                 # Reuse
│   └── PathConverter.cs                   # Reuse
├── LogicCopyForest/                       # NEW FOLDER
│   ├── ForestBrushCopyScanner.cs          # NEW
│   └── ForestBrushCopier.cs               # NEW
├── Objects/
│   ├── CopyAsset.cs                       # Modified (add ForestBrush type)
│   ├── ForestBrushInfo.cs                 # NEW
│   └── ... existing files
└── Logic/
    ├── BeamFileReader.cs                  # Modified (add forest scanning)
    └── ... existing files
```

---

## Reusable Components

### From LogicCopyAssets (Use Directly)

| Component | Purpose | Usage |
|-----------|---------|-------|
| `PathConverter` | Converts paths between source/target levels | Constructor injection |
| `FileCopyHandler` | Safe file copying with encoding detection | Copy shape/texture files |
| `MaterialCopier` | Copies materials and textures | Batch material operations |
| `DaeCopier` | Copies DAE files and associated materials | Copy shape files |

### From Existing Pages (Pattern Reference)

| Component | Pattern to Follow |
|-----------|-------------------|
| `CopyTerrains.razor.cs` | File selection, scanning, copy workflow |
| `CopyAssets.razor` | Table display, multi-selection, search |
| `FileSelectComponent` | Source/target file selection UI |

---

## Implementation Checklist

### Phase 1: Backend Core

- [x] Create `Objects/ForestBrushInfo.cs` with data models
- [x] Add `ForestBrush` to `CopyAssetType` enum
- [x] Add `ForestBrushInfo` property to `CopyAsset.cs`
- [x] Create `LogicCopyForest/` folder
- [x] Create `ForestBrushCopyScanner.cs` - NDJSON parsing
- [x] Create `ForestBrushCopier.cs` - copy orchestration
- [x] Integrate into `AssetCopy.Copy()` method

### Phase 2: BeamFileReader Integration

- [x] Add `CopyForestBrushes()` method to `BeamFileReader`
- [x] Add forest brush assets to `GetCopyList()` return (via CopyAssets list)
- [x] Integrate `CopyForestBrushes()` call into `ReadAllForCopy()` method
- [x] Add `ReadForestBrushesForCopy()` lightweight method for forest-only scanning
- [ ] Test scanning with jungle_rock_island example

### Phase 3: Frontend UI

- [x] Create `CopyForestBrushes.razor` page
- [x] Create `CopyForestBrushes.razor.cs` code-behind
- [x] Add navigation entry in `MyNavMenu.razor`
- [x] Use `ReadForestBrushesForCopy()` for lightweight scanning (no terrain/groundcover/road/decal scanning)
- [ ] Test UI flow with real BeamNG levels

### Phase 4: Testing & Polish

- [ ] Test with vanilla levels (jungle_rock_island, west_coast_usa)
- [ ] Test duplicate detection
- [ ] Test shape file copying with materials
- [ ] Test NDJSON writing
- [ ] Verify brushes appear in World Editor

---

## Notes on BeamNG Data Formats

### Why Use JsonNode/JsonDocument Instead of Fixed Classes

BeamNG data formats evolve over time. Using `System.Text.Json.Nodes` allows:

1. **Preserving unknown properties** - New properties added by BeamNG updates won't be lost
2. **Flexible parsing** - Handle both old and new format variations
3. **Dynamic property access** - Check for properties without compile-time knowledge

Example pattern:

```csharp
// Good: Preserve all properties
var itemCopy = itemData.DeepClone();
itemCopy["persistentId"] = Guid.NewGuid().ToString();

// Bad: Deserialize to fixed class, lose unknown properties
var item = JsonSerializer.Deserialize<ForestItemData>(json);
```

### NDJSON Handling

The `main.forestbrushes4.json` uses NDJSON (one JSON object per line):

```csharp
// Reading NDJSON
foreach (var line in File.ReadAllLines(filePath))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var obj = JsonDocument.Parse(line);
    // process...
}

// Writing NDJSON  
var lines = objects.Select(o => o.ToJsonString());
File.WriteAllLines(filePath, lines);
```

---

**Last Updated**: 2025-01-XX  
**Target Framework**: .NET 9  
**Application**: BeamNG Tools for Mapbuilders
