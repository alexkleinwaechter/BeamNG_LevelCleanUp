# Bridge and Tunnel Implementation Plan

## Overview

This document outlines the implementation plan for detecting and handling bridges and tunnels in the terrain generation pipeline. The approach is simple and elegant:

**OSM data is self-describing** - roads tagged with `bridge=yes` ARE bridges, roads tagged with `tunnel=yes` ARE tunnels. We read these tags during normal OSM parsing, not as a separate query.

### Goals

1. **Mark road splines** as bridges/tunnels based on their OSM tags
2. **Exclude** bridge/tunnel splines from terrain smoothing and material painting
3. **Preserve data** for future procedural DAE geometry generation

## Implementation Status

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 | 🔲 TODO | Extend OsmFeature with Bridge/Tunnel Properties |
| Phase 2 | 🔲 TODO | Extend ParameterizedRoadSpline with Structure Flags |
| Phase 3 | 🔲 TODO | Propagate Flags During Spline Creation |
| Phase 4 | 🔲 TODO | Pipeline Integration (Exclude from Smoothing) |
| Phase 5 | 🔲 TODO | Pipeline Integration (Exclude from Material Painting) |
| Phase 6 | 🔲 TODO | Configuration Options |
| Phase 7 | 🔲 TODO | UI Integration (Statistics Display) |

---

## Key Architectural Decision: Tag-Based Detection (No Separate Query)

## Read Tags During Parsing the OSM Data for feature retrieval

OSM ways with road features (`highway=*`) may also have structure tags:
- `bridge=yes` (or `bridge=viaduct`, `bridge=cantilever`, etc.)
- `tunnel=yes` (or `tunnel=building_passage`, `tunnel=culvert`, etc.)
- `covered=yes` (alternative for covered passages)

**The tag IS the data.** A way tagged `highway=primary` + `bridge=yes` IS a primary road that IS a bridge.

Since OSM convention is to split ways at structure boundaries, each `OsmFeature` we parse is already a coherent segment:
- Either entirely a bridge
- Or entirely a tunnel  
- Or entirely a normal road

---

## OSM Structure Tags Reference

### Bridge Tags

| Tag | Description | Example Values |
|-----|-------------|----------------|
| `bridge=*` | Way passes over an obstacle | `yes`, `viaduct`, `cantilever`, `movable` |
| `bridge:structure` | Structural type | `beam`, `arch`, `suspension`, `cable-stayed` |
| `layer` | Vertical layer (default=0) | `1`, `2`, `3` |

### Tunnel Tags

| Tag | Description | Example Values |
|-----|-------------|----------------|
| `tunnel=*` | Way passes through terrain | `yes`, `building_passage`, `culvert` |
| `covered=yes` | Alternative for covered passages | `yes` |
| `layer` | Vertical layer (typically negative) | `-1`, `-2` |

### Key Insight

A highway way can have **both** `highway=*` AND `bridge=yes` tags on the **same way**.
The existing road query already fetches these ways - we just need to read the structure tags.

---

## Phase 1: Extend OsmFeature with Bridge/Tunnel Properties

### Step 1.1: Add Structure Properties to OsmFeature

**File**: `BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs`

Add these properties to the existing `OsmFeature` class:

```csharp
/// <summary>
/// Type of structure (bridge, tunnel, etc.) if this feature represents one.
/// </summary>
public enum StructureType
{
    /// <summary>Not a structure - normal road at ground level.</summary>
    None,
    
    /// <summary>Way passes over an obstacle (water, road, valley, etc.).</summary>
    Bridge,
    
    /// <summary>Way passes through terrain (underground).</summary>
    Tunnel,
    
    /// <summary>Covered passage through a building.</summary>
    BuildingPassage,
    
    /// <summary>Small tunnel for water drainage under road.</summary>
    Culvert
}

// Add to OsmFeature class:

/// <summary>
/// Whether this feature represents a bridge (has bridge=* tag, excluding "no").
/// </summary>
public bool IsBridge
{
    get
    {
        if (!Tags.TryGetValue("bridge", out var bridgeValue))
            return false;
        
        // bridge=no means explicitly not a bridge
        return !bridgeValue.Equals("no", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Whether this feature represents a tunnel (has tunnel=* or covered=yes tag).
/// </summary>
public bool IsTunnel
{
    get
    {
        // Check tunnel tag
        if (Tags.TryGetValue("tunnel", out var tunnelValue))
        {
            if (!tunnelValue.Equals("no", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        // Check covered tag (alternative)
        if (Tags.TryGetValue("covered", out var coveredValue))
        {
            if (coveredValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        return false;
    }
}

/// <summary>
/// Whether this feature is any kind of elevated or underground structure.
/// </summary>
public bool IsStructure => IsBridge || IsTunnel;

/// <summary>
/// Gets the specific structure type from OSM tags.
/// </summary>
public StructureType GetStructureType()
{
    if (!IsStructure)
        return StructureType.None;
    
    if (IsBridge)
        return StructureType.Bridge;
    
    // Determine tunnel subtype
    if (Tags.TryGetValue("tunnel", out var tunnelValue))
    {
        return tunnelValue.ToLowerInvariant() switch
        {
            "building_passage" => StructureType.BuildingPassage,
            "culvert" => StructureType.Culvert,
            _ => StructureType.Tunnel
        };
    }
    
    // covered=yes defaults to building passage
    if (Tags.TryGetValue("covered", out var coveredValue) && 
        coveredValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
    {
        return StructureType.BuildingPassage;
    }
    
    return StructureType.Tunnel;
}

/// <summary>
/// Gets the vertical layer from OSM tags (default 0).
/// Bridges typically have positive layers, tunnels negative.
/// </summary>
public int Layer
{
    get
    {
        if (Tags.TryGetValue("layer", out var layerValue) && 
            int.TryParse(layerValue, out var layer))
        {
            return layer;
        }
        return 0;
    }
}

/// <summary>
/// Gets bridge structure type (beam, arch, suspension, etc.) if specified.
/// </summary>
public string? BridgeStructureType
{
    get
    {
        if (Tags.TryGetValue("bridge:structure", out var structureType))
            return structureType;
        
        // Some bridge types are specified in the bridge tag itself
        if (Tags.TryGetValue("bridge", out var bridgeValue))
        {
            return bridgeValue.ToLowerInvariant() switch
            {
                "viaduct" => "viaduct",
                "cantilever" => "cantilever",
                "suspension" => "suspension",
                "movable" => "movable",
                "aqueduct" => "aqueduct",
                _ => null
            };
        }
        
        return null;
    }
}
```

**Why this works:**

- The existing `OsmGeoJsonParser.Parse()` already reads all tags into `feature.Tags`
- We just need to add computed properties that read the tags
- Zero changes to parsing logic
- The data is already there, we just expose it

---

## Phase 2: Extend ParameterizedRoadSpline with Structure Flags

### Step 2.1: Add Structure Properties

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`

```csharp
public class ParameterizedRoadSpline
{
    // ... existing properties ...
    
    /// <summary>
    /// Whether this spline represents a bridge structure.
    /// Bridge splines are excluded from terrain smoothing and material painting.
    /// </summary>
    public bool IsBridge { get; set; }
    
    /// <summary>
    /// Whether this spline represents a tunnel structure.
    /// Tunnel splines are excluded from terrain smoothing and material painting.
    /// </summary>
    public bool IsTunnel { get; set; }
    
    /// <summary>
    /// Combined check for any elevated/underground structure.
    /// </summary>
    public bool IsStructure => IsBridge || IsTunnel;
    
    /// <summary>
    /// Detailed structure type (None, Bridge, Tunnel, BuildingPassage, Culvert).
    /// </summary>
    public StructureType StructureType { get; set; } = StructureType.None;
    
    /// <summary>
    /// Vertical layer (0 = ground level, positive = elevated, negative = underground).
    /// Used for multi-level crossings and DAE placement.
    /// </summary>
    public int Layer { get; set; } = 0;
    
    /// <summary>
    /// Bridge structure type (beam, arch, suspension, etc.) for DAE generation.
    /// Null if not a bridge or type not specified.
    /// </summary>
    public string? BridgeStructureType { get; set; }
}
```

### Step 2.2: UnifiedCrossSection - NOT REQUIRED

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`

> **NOTE**: Adding `IsBridge`/`IsTunnel` properties to `UnifiedCrossSection` is **NOT REQUIRED**.
>
> Cross-sections already have:
> - `OwnerSplineId` property - links back to the owning `ParameterizedRoadSpline`
> - `IsExcluded` property - can be set to `true` for structure cross-sections
>
> **Recommendation**: Look up the owning spline's `IsStructure` flag via `OwnerSplineId` during processing,
> then set `IsExcluded = true` for structure cross-sections. This avoids duplicating data and keeps
> the single source of truth on `ParameterizedRoadSpline`.

```csharp
// Example: Mark structure cross-sections as excluded during processing
foreach (var crossSection in crossSections)
{
    var owningSpline = network.Splines.FirstOrDefault(s => s.SplineId == crossSection.OwnerSplineId);
    if (owningSpline?.IsStructure == true)
    {
        crossSection.IsExcluded = true;
    }
}
```

---

## Phase 3: Propagate Flags Through the Spline Creation Pipeline

### Architecture Analysis

The current data flow loses OSM metadata:

```
OsmFeature → ConvertLinesToSplines() → RoadSpline → PreBuiltSplines → BuildNetwork() → ParameterizedRoadSpline
                    ↑
            Metadata LOST here!
```

The `OsmGeometryProcessor.ConvertLinesToSplines()` method returns `List<RoadSpline>` without preserving the original `OsmFeature` bridge/tunnel tags. The `RoadSmoothingParameters.PreBuiltSplines` property stores only `List<RoadSpline>`.

**Solution**: Add structure metadata directly to `RoadSpline`, then propagate to `ParameterizedRoadSpline` in `BuildNetwork()`.

---

### Step 3.1: Add Structure Metadata to RoadSpline

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`

Add these properties to carry structure information through the pipeline:

```csharp
public class RoadSpline
{
    // ... existing properties ...
    
    // ========================================
    // STRUCTURE METADATA (Bridge/Tunnel)
    // ========================================
    
    /// <summary>
    /// Whether this spline represents a bridge (from OSM bridge=* tag).
    /// Set during spline creation from OsmFeature.
    /// </summary>
    public bool IsBridge { get; set; }
    
    /// <summary>
    /// Whether this spline represents a tunnel (from OSM tunnel=* or covered=yes tag).
    /// Set during spline creation from OsmFeature.
    /// </summary>
    public bool IsTunnel { get; set; }
    
    /// <summary>
    /// Combined check for any elevated/underground structure.
    /// </summary>
    public bool IsStructure => IsBridge || IsTunnel;
    
    /// <summary>
    /// Detailed structure type (None, Bridge, Tunnel, BuildingPassage, Culvert).
    /// Set during spline creation from OsmFeature.GetStructureType().
    /// </summary>
    public StructureType StructureType { get; set; } = StructureType.None;
    
    /// <summary>
    /// Vertical layer from OSM (0 = ground level, positive = elevated, negative = underground).
    /// Set during spline creation from OsmFeature.Layer.
    /// </summary>
    public int Layer { get; set; } = 0;
    
    /// <summary>
    /// Bridge structure type (beam, arch, suspension, etc.) for future DAE generation.
    /// Set during spline creation from OsmFeature.BridgeStructureType.
    /// </summary>
    public string? BridgeStructureType { get; set; }
}
```

---

### Step 3.2: Update OsmGeometryProcessor.ConvertLinesToSplines()

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`

Modify `ConvertLinesToSplines()` to preserve structure metadata. The key change is in the spline creation loop (around line 750):

```csharp
// CURRENT CODE (around line 750):
try
{
    var spline = new RoadSpline(cleanPath, interpolationType);
    splines.Add(spline);
}

// UPDATED CODE - preserve structure metadata:
// This requires tracking which OsmFeature each path came from.
// Since ConnectAdjacentPaths() may merge paths from different features,
// we need a different approach.
```

**Problem**: `ConnectAdjacentPaths()` merges multiple OSM ways into single paths, breaking the 1:1 mapping between `OsmFeature` and `RoadSpline`.

**Solution**: Create splines BEFORE path connection, preserving per-feature metadata:

```csharp
/// <summary>
/// Converts line features to RoadSpline objects, preserving structure metadata.
/// This is an alternative to ConvertLinesToSplines() that maintains the 1:1
/// mapping between OsmFeature and RoadSpline for structure detection.
/// 
/// NOTE: This method does NOT connect adjacent paths because that would
/// merge bridges with non-bridges. Each OSM way becomes one spline.
/// </summary>
public List<RoadSpline> ConvertLinesToSplinesWithStructureMetadata(
    List<OsmFeature> lineFeatures,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated,
    float minPathLengthMeters = 1.0f,
    float duplicatePointToleranceMeters = 0.01f)
{
    var splines = new List<RoadSpline>();
    int skippedZeroLength = 0;
    int skippedTooFewPoints = 0;
    int bridgeCount = 0;
    int tunnelCount = 0;
    
    foreach (var feature in lineFeatures.Where(f => f.GeometryType == OsmGeometryType.LineString))
    {
        // Transform to terrain-space coordinates (bottom-left origin for heightmap)
        var terrainCoords = TransformToTerrainCoordinates(feature.Coordinates, bbox, terrainSize);
        
        // Crop to terrain bounds
        var croppedCoords = CropLineToTerrain(terrainCoords, terrainSize);
        
        if (croppedCoords.Count < 2)
        {
            skippedTooFewPoints++;
            continue;
        }
        
        // Convert from pixel coordinates to meter coordinates
        var meterCoords = croppedCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();
        
        // Remove duplicate consecutive points
        var uniqueCoords = RemoveDuplicateConsecutivePoints(meterCoords, duplicatePointToleranceMeters);
        
        if (uniqueCoords.Count < 2)
        {
            skippedTooFewPoints++;
            continue;
        }
        
        // Calculate total path length and skip if too short
        float totalLength = CalculatePathLength(uniqueCoords);
        if (totalLength < minPathLengthMeters)
        {
            skippedZeroLength++;
            continue;
        }
        
        try
        {
            var spline = new RoadSpline(uniqueCoords, interpolationType);
            
            // NEW: Copy structure metadata from OsmFeature
            spline.IsBridge = feature.IsBridge;
            spline.IsTunnel = feature.IsTunnel;
            spline.StructureType = feature.GetStructureType();
            spline.Layer = feature.Layer;
            spline.BridgeStructureType = feature.BridgeStructureType;
            
            splines.Add(spline);
            
            // Track statistics
            if (feature.IsBridge) bridgeCount++;
            if (feature.IsTunnel) tunnelCount++;
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to create spline from feature {feature.Id}: {ex.Message}");
        }
    }
    
    TerrainLogger.Info($"Created {splines.Count} splines from {lineFeatures.Count} OSM line features");
    if (skippedTooFewPoints > 0)
        TerrainLogger.Info($"  Skipped {skippedTooFewPoints} paths with too few points");
    if (skippedZeroLength > 0)
        TerrainLogger.Info($"  Skipped {skippedZeroLength} paths shorter than {minPathLengthMeters:F1}m");
    if (bridgeCount > 0 || tunnelCount > 0)
        TerrainLogger.Info($"  Structure metadata: {bridgeCount} bridges, {tunnelCount} tunnels");
    
    return splines;
}
```

**Key Decision**: We use **selective merging** based on structure type:
- **Structure splines** (bridges/tunnels) → Keep separate, never merge
- **Non-structure splines** (normal roads) → Can be merged for better continuity

This gives us the best of both worlds:
- Accurate bridge/tunnel boundaries (no merging across structure boundaries)
- Better road continuity for normal roads (fewer gaps, smoother junctions)

---

### Step 3.3: Add Selective Path Merging (Post-Processing)

After creating splines with metadata, merge only non-structure splines:

```csharp
/// <summary>
/// Merges adjacent non-structure splines while keeping structure splines separate.
/// This post-processing step improves road continuity without breaking structure detection.
/// 
/// Rules:
/// - Two non-structure splines can be merged if endpoints are within tolerance
/// - Structure splines (bridges/tunnels) are kept separate ONLY if their exclusion is enabled
/// - If a structure type's exclusion is disabled, those splines are treated as normal roads
/// - This preserves accurate structure boundaries while allowing backward-compatible behavior
/// </summary>
/// <param name="excludeBridges">When true, bridge splines are kept separate. When false, bridges are merged like normal roads.</param>
/// <param name="excludeTunnels">When true, tunnel splines are kept separate. When false, tunnels are merged like normal roads.</param>
public List<RoadSpline> MergeNonStructureSplines(
    List<RoadSpline> splines,
    float endpointJoinToleranceMeters = 1.0f,
    bool excludeBridges = true,
    bool excludeTunnels = true)
{
    if (splines.Count <= 1)
        return splines;
    
    // Determine which splines should be kept separate based on configuration
    // A spline is "protected" (kept separate) only if:
    // - It's a bridge AND excludeBridges is true, OR
    // - It's a tunnel AND excludeTunnels is true
    var protectedSplines = splines.Where(s => 
        (s.IsBridge && excludeBridges) || 
        (s.IsTunnel && excludeTunnels)).ToList();
    
    // All other splines are eligible for merging (including disabled structure types)
    var mergeableSplines = splines.Where(s => 
        !((s.IsBridge && excludeBridges) || (s.IsTunnel && excludeTunnels))).ToList();
    
    TerrainLogger.Info($"Selective merge: {protectedSplines.Count} protected splines (kept separate), {mergeableSplines.Count} splines (eligible for merging)");
    
    // Only merge the mergeable splines
    if (mergeableSplines.Count > 1)
    {
        // Extract control points from mergeable splines
        var mergeablePaths = mergeableSplines.Select(s => s.ControlPoints.ToList()).ToList();
        
        // Use existing ConnectAdjacentPaths logic
        var mergedPaths = ConnectAdjacentPaths(mergeablePaths, endpointJoinToleranceMeters);
        
        TerrainLogger.Info($"  Merged {mergeableSplines.Count} paths into {mergedPaths.Count}");
        
        // Recreate splines from merged paths (all treated as non-structure for terrain purposes)
        mergeableSplines = mergedPaths
            .Where(p => p.Count >= 2)
            .Select(p => new RoadSpline(p, SplineInterpolationType.SmoothInterpolated)
            {
                IsBridge = false,
                IsTunnel = false,
                StructureType = StructureType.None,
                Layer = 0
            })
            .ToList();
    }
    
    // Combine: protected splines (unchanged) + merged splines
    var result = new List<RoadSpline>();
    result.AddRange(protectedSplines);
    result.AddRange(mergeableSplines);
    
    return result;
}
```

> **BACKWARD COMPATIBILITY NOTE**: When both `excludeBridges` and `excludeTunnels` are `false`,
> this method behaves identically to the original `ConnectAdjacentPaths()` - all splines are
> merged based on endpoint proximity, with no special handling for bridge/tunnel tags.

### Updated Method Signature

The main conversion method can now optionally merge non-structure splines, respecting configuration:

```csharp
/// <summary>
/// Converts line features to RoadSpline objects, preserving structure metadata.
/// Optionally merges adjacent splines for better road continuity.
/// Structure splines are kept separate only when their exclusion is enabled.
/// </summary>
/// <param name="mergeSplines">
/// When true, adjacent splines are merged after creation.
/// Structure splines (bridges/tunnels) are kept separate only if their exclusion is enabled.
/// Default: true (recommended for better road continuity)
/// </param>
/// <param name="excludeBridges">When true, bridge splines are kept separate. When false, bridges merge like normal roads.</param>
/// <param name="excludeTunnels">When true, tunnel splines are kept separate. When false, tunnels merge like normal roads.</param>
public List<RoadSpline> ConvertLinesToSplinesWithStructureMetadata(
    List<OsmFeature> lineFeatures,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated,
    float minPathLengthMeters = 1.0f,
    float duplicatePointToleranceMeters = 0.01f,
    float endpointJoinToleranceMeters = 1.0f,
    bool mergeSplines = true,
    bool excludeBridges = true,   // NEW: from configuration
    bool excludeTunnels = true)   // NEW: from configuration
{
    // ... existing spline creation code (Step 3.2) ...
    
    // Optionally merge splines, respecting exclusion configuration
    if (mergeSplines && splines.Count > 1)
    {
        splines = MergeNonStructureSplines(splines, endpointJoinToleranceMeters, 
            excludeBridges, excludeTunnels);
    }
    
    return splines;
}
```

> **BACKWARD COMPATIBILITY**: When `excludeBridges = false` AND `excludeTunnels = false`,
> this method produces identical results to the original `ConvertLinesToSplines()` method -
> all paths are merged based on endpoint proximity with no special structure handling.

### Why Selective Merging Works

```
BEFORE MERGING (excludeBridges=true, excludeTunnels=true):
  [Road A] → [Bridge B] → [Road C] → [Road D] → [Tunnel E] → [Road F]
     ↓           ↓           ↓           ↓           ↓           ↓
  Spline 1   Spline 2    Spline 3   Spline 4   Spline 5    Spline 6
  
AFTER SELECTIVE MERGING (excludeBridges=true, excludeTunnels=true):
  [Road A] → [Bridge B] → [Road C+D merged] → [Tunnel E] → [Road F]
     ↓           ↓              ↓                  ↓           ↓
  Spline 1   Spline 2       Spline 3           Spline 4    Spline 5
  (merged)   (PROTECTED)    (merged)           (PROTECTED) (merged)

BACKWARD COMPATIBLE MODE (excludeBridges=false, excludeTunnels=false):
  [Road A] → [Bridge B] → [Road C] → [Road D] → [Tunnel E] → [Road F]
     ↓           ↓           ↓           ↓           ↓           ↓
  Spline 1   Spline 2    Spline 3   Spline 4   Spline 5    Spline 6
                    ↓ ALL MERGED (same as original behavior) ↓
  [Road A + Bridge B + Road C + Road D + Tunnel E + Road F]
                              ↓
                          Spline 1 (single merged spline)
```

Benefits when structure exclusion is enabled:
- **Structure boundaries preserved**: Bridge and tunnel start/end points stay accurate
- **Better road continuity**: Normal road segments merge into longer splines
- **Fewer artificial endpoints**: Reduces junction detection noise at OSM way splits
- **Smoother elevation profiles**: Longer splines = smoother longitudinal smoothing

Benefits when structure exclusion is disabled (backward compatible):
- **Identical behavior to original code**: All paths merged regardless of tags
- **No changes to terrain output**: Bridges/tunnels treated as normal roads

---

### Step 3.4: Update Callers to Use New Method

The callers that populate `RoadSmoothingParameters.PreBuiltSplines` need to use the new method, passing configuration from `TerrainCreationParameters`:

**File**: Where OSM features are converted to splines (likely in UI/service code)

```csharp
// BEFORE:
var splines = osmProcessor.ConvertLinesToSplines(
    roadFeatures, bbox, terrainSize, metersPerPixel, interpolationType);
parameters.PreBuiltSplines = splines;

// AFTER (with structure detection, respecting configuration):
var splines = osmProcessor.ConvertLinesToSplinesWithStructureMetadata(
    roadFeatures, bbox, terrainSize, metersPerPixel, interpolationType,
    mergeSplines: true,
    excludeBridges: terrainParams.ExcludeBridgesFromTerrain,  // From config
    excludeTunnels: terrainParams.ExcludeTunnelsFromTerrain); // From config
parameters.PreBuiltSplines = splines;

// BACKWARD COMPATIBLE MODE (both exclusions disabled = original behavior):
// When terrainParams.ExcludeBridgesFromTerrain = false AND
//      terrainParams.ExcludeTunnelsFromTerrain = false:
// - All splines are merged (bridges/tunnels treated as normal roads)
// - Output is identical to the original ConvertLinesToSplines() method
```

---

### Step 3.5: Update UnifiedRoadNetworkBuilder.BuildNetwork()

**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

Now that `RoadSpline` carries structure metadata, copy it to `ParameterizedRoadSpline` in the loop (around line 113):

```csharp
// Current code (around line 113):
foreach (var spline in splines)
{
    // Determine OSM road type if available
    string? osmRoadType = null;
    string? displayName = null;

    var paramSpline = new ParameterizedRoadSpline
    {
        Spline = spline,
        Parameters = parameters,
        MaterialName = material.MaterialName,
        SplineId = splineIdCounter,
        OsmRoadType = osmRoadType,
        DisplayName = displayName
    };
    
    // ... rest of code ...
}

// UPDATED code - copy structure flags from RoadSpline:
foreach (var spline in splines)
{
    string? osmRoadType = null;
    string? displayName = null;

    var paramSpline = new ParameterizedRoadSpline
    {
        Spline = spline,
        Parameters = parameters,
        MaterialName = material.MaterialName,
        SplineId = splineIdCounter,
        OsmRoadType = osmRoadType,
        DisplayName = displayName,
        
        // NEW: Copy structure flags from RoadSpline (which got them from OsmFeature)
        IsBridge = spline.IsBridge,
        IsTunnel = spline.IsTunnel,
        StructureType = spline.StructureType,
        Layer = spline.Layer,
        BridgeStructureType = spline.BridgeStructureType
    };
    
    // ... rest of code ...
}
```

---

### Step 3.5: Log Structure Statistics

Add logging after building the network (around line 160):

```csharp
// Log network statistics
var stats = network.GetStatistics();
TerrainLogger.Info(stats.ToString());

// NEW: Log structure statistics
var bridgeCount = network.Splines.Count(s => s.IsBridge);
var tunnelCount = network.Splines.Count(s => s.IsTunnel);

if (bridgeCount > 0 || tunnelCount > 0)
{
    TerrainLogger.Info($"Structure detection: {bridgeCount} bridge spline(s), {tunnelCount} tunnel spline(s) marked for exclusion");
}

return network;
```

---

### Data Flow After Changes

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    UPDATED BRIDGE/TUNNEL DATA FLOW                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  1. OSM PARSING (Phase 1 changes)                                            │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmFeature                                         │                  │
│     │ - Tags["bridge"], Tags["tunnel"] (existing)        │                  │
│     │ - NEW: IsBridge, IsTunnel computed properties      │                  │
│     │ - NEW: GetStructureType(), Layer, BridgeStructureType│                │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  2. SPLINE CREATION (Phase 3 changes)                                        │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmGeometryProcessor.ConvertLinesToSplinesWithStructureMetadata()│    │
│     │ - Creates RoadSpline for each OsmFeature           │                  │
│     │ - NEW: Copies IsBridge/IsTunnel/StructureType/Layer│                  │
│     │        from OsmFeature to RoadSpline               │                  │
│     │ - Does NOT merge adjacent paths (preserves 1:1)    │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  3. PREBUILT SPLINES (existing, no changes)                                  │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ RoadSmoothingParameters.PreBuiltSplines            │                  │
│     │ - List<RoadSpline> with structure metadata         │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  4. NETWORK BUILDING (Phase 3 changes)                                       │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ UnifiedRoadNetworkBuilder.BuildNetwork()           │                  │
│     │ - Creates ParameterizedRoadSpline from RoadSpline  │                  │
│     │ - NEW: Copies IsBridge/IsTunnel/StructureType/Layer│                  │
│     │        from RoadSpline to ParameterizedRoadSpline  │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  5. TERRAIN PROCESSING (Phase 4-5 changes)                                   │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Smoothing & Material Painting                      │                  │
│     │ - Check paramSpline.IsStructure                    │                  │
│     │ - Skip if bridge/tunnel                            │                  │
│     └────────────────────────────────────────────────────┘                  │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

### Alternative: Parallel Metadata List

If modifying `RoadSpline` is not desirable, an alternative is to add a parallel metadata list:

```csharp
// In RoadSmoothingParameters:
public List<RoadSpline>? PreBuiltSplines { get; set; }

// NEW: Parallel list of structure metadata (same length as PreBuiltSplines)
public List<SplineStructureMetadata>? PreBuiltSplineMetadata { get; set; }

// NEW: Helper class
public class SplineStructureMetadata
{
    public bool IsBridge { get; set; }
    public bool IsTunnel { get; set; }
    public StructureType StructureType { get; set; }
    public int Layer { get; set; }
    public string? BridgeStructureType { get; set; }
}
```

This approach keeps `RoadSpline` unchanged but requires careful index synchronization. The recommended approach is to add properties to `RoadSpline` directly.

---

## Phase 4: Pipeline Integration (Exclude from Smoothing)

### Step 4.1: Exclude Structure Splines from Terrain Smoothing

There are two approaches, depending on where in the pipeline exclusion is applied:

#### Option A: Spline-Level Exclusion (RECOMMENDED)

Filter at the spline level before cross-sections are processed:

```csharp
// When iterating splines for terrain modification:
foreach (var spline in network.Splines)
{
    // Skip bridges and tunnels - they float above/below terrain
    if (spline.IsStructure)
    {
        TerrainLogger.Debug($"Skipping terrain smoothing for {spline.StructureType}: {spline.DisplayName}");
        continue;
    }
    
    // ... apply smoothing ...
}
```

#### Option B: Cross-Section Level Using OwnerSplineId

If processing happens at cross-section level, look up the owning spline's structure status:

```csharp
// Build a lookup dictionary for fast spline access
var splineLookup = network.Splines.ToDictionary(s => s.SplineId);

// When applying elevation smoothing to cross-sections:
foreach (var crossSection in crossSections)
{
    // Look up owning spline to check structure status
    if (splineLookup.TryGetValue(crossSection.OwnerSplineId, out var owningSpline) 
        && owningSpline.IsStructure)
    {
        // Mark as excluded and skip - no terrain modification for structures
        crossSection.IsExcluded = true;
        continue;
    }
    
    // ... existing smoothing logic ...
}
```

> **Note**: Option B uses the existing `OwnerSplineId` property on `UnifiedCrossSection` to look up
> structure status from `ParameterizedRoadSpline`. This avoids adding redundant `IsBridge`/`IsTunnel`
> properties to `UnifiedCrossSection` (see Phase 2 Step 2.2).

### Step 4.2: Handle Structure Entry/Exit Points (Future Enhancement)

At the boundaries where normal road meets a bridge/tunnel:
- The last few meters of road before a bridge may need a slight ramp
- This is a future enhancement for DAE generation

For now, simply excluding structures from smoothing is sufficient.

---

## Phase 5: Pipeline Integration (Exclude from Material Painting)

### Step 5.1: Exclude Structure Areas from Layer Map Rasterization

When generating the material layer map, exclude bridge/tunnel splines:

```csharp
public byte[,] RasterizeSplinesToLayerMap(
    UnifiedRoadNetwork network,
    int terrainSize,
    float metersPerPixel,
    bool excludeStructures = true)  // NEW parameter
{
    var result = new byte[terrainSize, terrainSize];
    
    foreach (var spline in network.Splines)
    {
        // Skip structure splines when exclusion is enabled
        if (excludeStructures && spline.IsStructure)
        {
            continue;
        }
        
        // ... existing rasterization ...
    }
    
    return result;
}
```

### Step 5.2: Generate Structure Mask (Optional, for Visualization)

Create a mask showing where structures are located:

```csharp
public byte[,] RasterizeStructureMask(UnifiedRoadNetwork network, int terrainSize, float metersPerPixel)
{
    var mask = new byte[terrainSize, terrainSize];
    
    foreach (var spline in network.Splines.Where(s => s.IsStructure))
    {
        RasterizeSplineToMask(mask, spline, metersPerPixel);
    }
    
    return mask;
}
```

---

## Phase 6: Configuration Options

### Step 6.1: Add Configuration Parameters

**File**: `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

```csharp
// ========================================
// BRIDGE/TUNNEL CONFIGURATION
// ========================================

/// <summary>
/// When true, bridges are excluded from terrain smoothing and material painting.
/// When false, bridge ways are treated as normal roads (current behavior).
/// Default: true (bridges are excluded)
/// </summary>
public bool ExcludeBridgesFromTerrain { get; set; } = true;

/// <summary>
/// When true, tunnels are excluded from terrain smoothing and material painting.
/// When false, tunnel ways are treated as normal roads (current behavior).  
/// Default: true (tunnels are excluded)
/// </summary>
public bool ExcludeTunnelsFromTerrain { get; set; } = true;

/// <summary>
/// Convenience check for any structure exclusion.
/// </summary>
public bool ExcludeStructuresFromTerrain => ExcludeBridgesFromTerrain || ExcludeTunnelsFromTerrain;
```

### Step 6.2: Conditional Exclusion in Pipeline

```csharp
// Check configuration when processing splines:
bool shouldExclude = spline.IsStructure && (
    (spline.IsBridge && parameters.ExcludeBridgesFromTerrain) ||
    (spline.IsTunnel && parameters.ExcludeTunnelsFromTerrain)
);

if (shouldExclude)
{
    continue;  // Skip this spline
}
```

### Behavior Summary

| ExcludeBridges | ExcludeTunnels | Bridge Behavior | Tunnel Behavior | Spline Merging |
|----------------|----------------|-----------------|-----------------|----------------|
| `true` | `true` | Excluded | Excluded | Bridges & tunnels kept separate |
| `true` | `false` | Excluded | Normal road | Only bridges kept separate |
| `false` | `true` | Normal road | Excluded | Only tunnels kept separate |
| `false` | `false` | Normal road | Normal road | **All merged (backward compatible)** |

### Step 6.3: Backward Compatibility Guarantee

When BOTH `ExcludeBridgesFromTerrain` AND `ExcludeTunnelsFromTerrain` are set to `false`:

1. **Spline Merging**: All splines are merged based on endpoint proximity - identical to original `ConvertLinesToSplines()` behavior
2. **Terrain Smoothing**: All roads (including bridges/tunnels) receive terrain smoothing - identical to original behavior
3. **Material Painting**: All roads (including bridges/tunnels) are painted - identical to original behavior
4. **Output**: Terrain files are byte-for-byte identical to what the original code would produce

This ensures users can completely disable the bridge/tunnel feature and get the exact same results as before the feature was implemented.

```csharp
// To achieve full backward compatibility, set both to false:
terrainParams.ExcludeBridgesFromTerrain = false;
terrainParams.ExcludeTunnelsFromTerrain = false;

// This results in:
// - ConvertLinesToSplinesWithStructureMetadata() merges ALL splines (no protection)
// - Smoothing processes ALL splines (no exclusion check passes)
// - Painting includes ALL splines (no exclusion check passes)
// - Output identical to original code
```

---

## Phase 7: UI Integration

### Step 7.1: Display Structure Statistics

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/TerrainCreator.razor` (or relevant UI)

```razor
@if (RoadNetwork != null)
{
    var bridgeCount = RoadNetwork.Splines.Count(s => s.IsBridge);
    var tunnelCount = RoadNetwork.Splines.Count(s => s.IsTunnel);
    
    @if (bridgeCount > 0 || tunnelCount > 0)
    {
        <MudAlert Severity="Severity.Info" Dense="true" Class="mt-2">
            <MudText Typo="Typo.body2">
                <strong>Structures detected:</strong> 
                @bridgeCount bridge(s), @tunnelCount tunnel(s)
                @if (Parameters.ExcludeStructuresFromTerrain)
                {
                    <span> - excluded from terrain</span>
                }
            </MudText>
        </MudAlert>
    }
}
```

### Step 7.2: Configuration Toggle

```razor
<MudSwitch @bind-Value="Parameters.ExcludeBridgesFromTerrain" 
           Label="Exclude bridges from terrain" 
           Color="Color.Primary" />

<MudSwitch @bind-Value="Parameters.ExcludeTunnelsFromTerrain" 
           Label="Exclude tunnels from terrain" 
           Color="Color.Primary" />
```

---

## Data Flow Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    SIMPLIFIED BRIDGE/TUNNEL DATA FLOW                        │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  1. OSM QUERY (existing - unchanged)                                         │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OverpassApiService.QueryAllFeaturesAsync()         │                  │
│     │ - Queries highway=* features                       │                  │
│     │ - Features ALREADY include bridge/tunnel tags      │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  2. PARSING (existing, tags already read)                                    │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmGeoJsonParser.Parse()                           │                  │
│     │ - Creates OsmFeature objects                       │                  │
│     │ - Tags already in feature.Tags dictionary          │                  │
│     │ - NEW: feature.IsBridge, feature.IsTunnel (computed)│                 │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  3. SPLINE CREATION (modified - NEW METHOD)                                  │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmGeometryProcessor.ConvertLinesToSplinesWithStructureMetadata()│    │
│     │ - Converts OsmFeature → RoadSpline (1:1 mapping)   │                  │
│     │ - NEW: Copies IsBridge/IsTunnel from feature       │                  │
│     │        spline.IsBridge = feature.IsBridge          │                  │
│     │        spline.IsTunnel = feature.IsTunnel          │                  │
│     │ - Does NOT merge adjacent paths (preserves metadata)│                 │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  4. PREBUILT SPLINES (existing structure, carries new data)                  │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ RoadSmoothingParameters.PreBuiltSplines            │                  │
│     │ - List<RoadSpline> with structure metadata attached│                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  5. NETWORK BUILDING (modified)                                              │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ UnifiedRoadNetworkBuilder.BuildNetwork()           │                  │
│     │ - Converts RoadSpline → ParameterizedRoadSpline    │                  │
│     │ - NEW: Copies structure flags from RoadSpline      │                  │
│     │        paramSpline.IsBridge = spline.IsBridge      │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  6. TERRAIN SMOOTHING (modified)                                             │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Elevation Smoothing                                │                  │
│     │ - FOR EACH spline:                                 │                  │
│     │   - if (spline.IsStructure) SKIP                   │                  │
│     │   - else: apply smoothing                          │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  7. MATERIAL PAINTING (modified)                                             │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Layer Map Rasterization                            │                  │
│     │ - FOR EACH spline:                                 │                  │
│     │   - if (spline.IsStructure) SKIP                   │                  │
│     │   - else: rasterize to layer map                   │                  │
│     └────────────────────────────────────────────────────┘                  │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

**Key differences from original plan:**

| Original Plan | New Plan |
|---------------|----------|
| Separate Overpass query for bridges/tunnels | No new query - use existing data |
| New cache for bridge/tunnel data | No new cache needed |
| Complex geometric matching | No matching - direct 1:1 mapping |
| New data models for query results | Minimal additions to existing models |
| ~8 phases of work | ~7 simpler phases |

---

## File Changes Summary

```
BeamNgTerrainPoc/
├── Terrain/
│   ├── Osm/
│   │   ├── Models/
│   │   │   └── OsmFeature.cs                 (MODIFIED - add IsBridge, IsTunnel properties)
│   │   └── Processing/
│   │       └── OsmGeometryProcessor.cs       (MODIFIED - add ConvertLinesToSplinesWithStructureMetadata)
│   ├── Models/
│   │   ├── TerrainCreationParameters.cs      (MODIFIED - add config options)
│   │   └── RoadGeometry/
│   │       ├── RoadSpline.cs                 (MODIFIED - add structure metadata properties)
│   │       ├── ParameterizedRoadSpline.cs    (MODIFIED - add structure flags)
│   │       └── UnifiedCrossSection.cs        (NO CHANGES - use existing OwnerSplineId + IsExcluded)
│   └── Services/
│       └── UnifiedRoadNetworkBuilder.cs      (MODIFIED - copy flags from RoadSpline)
└── Docs/
    └── BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md  (THIS FILE)

BeamNG_LevelCleanUp/
└── BlazorUI/
    └── Pages/
        └── TerrainCreator.razor              (MODIFIED - add UI controls)
```

**Total: 6-7 files modified, 0 new files (compared to 10+ new files in original plan)**

---

## Testing Strategy

### Unit Tests

1. **OsmFeature Structure Detection**
   - Test `IsBridge` with various bridge tag values
   - Test `IsTunnel` with tunnel and covered tags
   - Test `GetStructureType()` classification
   - Test edge cases: `bridge=no`, empty tags

2. **Spline Flag Propagation**
   - Verify flags copy correctly from feature to spline
   - Verify Layer property copies correctly

### Integration Tests

1. **Real OSM Data**
   - Load area with known bridges (e.g., major highway crossing)
   - Verify bridge splines are marked
   - Verify surrounding road splines are NOT marked

2. **Pipeline Exclusion**
   - Generate terrain with bridges
   - Verify bridge areas have no terrain smoothing applied
   - Verify bridge areas are not painted with road material

### Visual Verification

1. **Debug Images**
   - Export debug image highlighting bridge/tunnel splines
   - Compare with satellite imagery to verify accuracy

---

## Future Extensions (Out of Scope)

1. **Procedural Bridge DAE Generation**
   - Use marked bridge splines and `BridgeStructureType`
   - Generate appropriate 3D geometry

2. **Procedural Tunnel DAE Generation**  
   - Use marked tunnel splines and `StructureType`
   - Generate tunnel portals and interior

3. **Multi-Level Interchange Handling**
   - Use `Layer` property for proper stacking
   - Handle overlapping structures

4. **Structure Elevation Profiles**
   - Calculate proper bridge/tunnel elevations
   - Smooth transitions at entry/exit points

---

## Why This Approach is Better

| Aspect | Original Plan | New Plan |
|--------|---------------|----------|
| **Complexity** | High - separate query, cache, matching | Low - read existing data |
| **API Calls** | 2 (roads + bridges) | 1 (roads only) |
| **Risk of Errors** | High - geometric matching can fail | Low - direct tag reading |
| **Maintenance** | Two caches to maintain | No new caches |
| **Code Changes** | 10+ new files | 5-6 modified files |
| **Accuracy** | Depends on matching quality | 100% accurate by definition |

The OSM data model is self-describing. We should trust it.

---

## Appendix A: Structure Elevation Profiles (Future Enhancement)

This section describes elevation handling for bridges and tunnels. It is **not required for the initial implementation** but documents the approach for future DAE generation.

### Bridge Elevation

Bridges typically have one of these profiles:
- **Short (< 50m)**: Linear interpolation between entry/exit
- **Medium (50-200m)**: Slight sag curve for drainage
- **Long (> 200m)**: Arch profile for cable-stayed/suspension bridges

### Tunnel Elevation

Tunnels must maintain clearance below terrain:
1. Sample terrain elevation along tunnel path
2. Calculate required depth for clearance
3. Use S-curve if linear interpolation isn't deep enough

### Configuration Parameters (Future)

```csharp
// For future elevation profile implementation:
public float TunnelMinClearanceMeters { get; set; } = 5.0f;
public float TunnelInteriorHeightMeters { get; set; } = 5.0f;
public float TunnelMaxGradePercent { get; set; } = 6.0f;
public float BridgeMinClearanceMeters { get; set; } = 5.0f;
```

---

## Appendix B: Original Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          TERRAIN CREATION PIPELINE                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  1. OSM QUERY (existing - unchanged)                                         │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OverpassApiService.QueryAllFeaturesAsync()         │                  │
│     │ - Queries highway=* features                       │                  │
│     │ - Response includes bridge/tunnel tags             │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  2. PARSING (existing - tags already read)                                   │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmGeoJsonParser.Parse()                           │                  │
│     │ - Creates OsmFeature objects with all tags         │                  │
│     │ - NEW: Computed IsBridge/IsTunnel properties       │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  3. SPLINE CREATION (modified - new method)                                  │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ OsmGeometryProcessor.ConvertLinesToSplinesWithStructureMetadata()│    │
│     │ - Converts OsmFeature → RoadSpline (1:1 mapping)   │                  │
│     │ - NEW: spline.IsBridge = feature.IsBridge          │                  │
│     │        spline.IsTunnel = feature.IsTunnel          │                  │
│     │ - Stored in RoadSmoothingParameters.PreBuiltSplines│                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  4. NETWORK BUILDING (modified - copy flags)                                 │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ UnifiedRoadNetworkBuilder.BuildNetwork()           │                  │
│     │ - Converts RoadSpline → ParameterizedRoadSpline    │                  │
│     │ - NEW: paramSpline.IsBridge = spline.IsBridge      │                  │
│     │        paramSpline.IsTunnel = spline.IsTunnel      │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  5. CROSS-SECTION GENERATION                                                 │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Generate Cross-Sections                            │                  │
│     │ - Uses existing OwnerSplineId to link to spline    │                  │
│     │ - NO changes needed to UnifiedCrossSection         │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  6. ROAD SMOOTHING (modified - skip structures)                              │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Elevation Smoothing Service                        │                  │
│     │ - NEW: if (spline.IsStructure) SKIP                │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  7. MATERIAL PAINTING (modified - skip structures)                           │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Layer Map Rasterization                            │                  │
│     │ - NEW: if (spline.IsStructure) SKIP                │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  8. FUTURE: DAE GENERATION                                                   │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Procedural Bridge/Tunnel DAE Generator             │                  │
│     │ (Future Phase - use marked splines)                │                  │
│     └────────────────────────────────────────────────────┘                  │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Implementation Checklist

### Phase 1: OsmFeature Properties
- [ ] Add `StructureType` enum to `OsmFeature.cs` (or separate file)
- [ ] Add `IsBridge` computed property
- [ ] Add `IsTunnel` computed property
- [ ] Add `IsStructure` computed property
- [ ] Add `GetStructureType()` method
- [ ] Add `Layer` property
- [ ] Add `BridgeStructureType` property

### Phase 2: ParameterizedRoadSpline Properties
- [ ] Add `IsBridge` property
- [ ] Add `IsTunnel` property  
- [ ] Add `IsStructure` computed property
- [ ] Add `StructureType` property
- [ ] Add `Layer` property
- [ ] Add `BridgeStructureType` property

### Phase 3: Spline Creation Pipeline
- [ ] Add structure metadata properties to `RoadSpline` class
- [ ] Create `ConvertLinesToSplinesWithStructureMetadata()` method in `OsmGeometryProcessor`
- [ ] Copy metadata from `OsmFeature` to `RoadSpline` during conversion
- [ ] Implement `MergeNonStructureSplines()` for selective path merging
- [ ] Update callers to use new method when structure detection is needed
- [ ] Modify `UnifiedRoadNetworkBuilder.BuildNetwork()` to copy flags from `RoadSpline`
- [ ] Add logging for structure detection statistics

### Phase 4: Terrain Smoothing
- [ ] Add spline-level exclusion check (recommended) OR cross-section level using `OwnerSplineId`
- [ ] Use existing `IsExcluded` property on cross-sections for structure exclusion
- [ ] Test with real bridge/tunnel data

### Phase 5: Material Painting
- [ ] Add exclusion check in rasterization
- [ ] Test layer map excludes structures

### Phase 6: Configuration
- [ ] Add `ExcludeBridgesFromTerrain` parameter
- [ ] Add `ExcludeTunnelsFromTerrain` parameter
- [ ] Wire up conditional exclusion

### Phase 7: UI
- [ ] Add structure statistics display
- [ ] Add configuration toggles

---

## Dependencies

### Existing Dependencies (No Changes Needed)
- `OverpassApiService` - HTTP communication
- `OsmGeoJsonParser` - JSON parsing (tags already read)
- `GeoCoordinateTransformer` - Coordinate transformation
- `UnifiedRoadNetwork` - Road network container

### New Dependencies
- **None** - All functionality builds on existing infrastructure

---

## Summary

This implementation plan takes the simplest possible approach:

1. **No new API queries** - Bridge/tunnel tags are already in the road data
2. **No new caches** - No additional data to cache
3. **No geometric matching** - Direct 1:1 mapping from feature to spline
4. **Minimal code changes** - ~5 files modified, 0 new files
5. **100% accurate** - OSM tags are authoritative, no heuristics needed

The key insight is that OSM conventions already handle the hard work:
- Ways are split at structure boundaries
- Tags explicitly mark what each segment is
- We just need to read and propagate this information
