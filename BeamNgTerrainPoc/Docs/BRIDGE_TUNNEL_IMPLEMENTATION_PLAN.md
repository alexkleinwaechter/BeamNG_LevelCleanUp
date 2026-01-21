# Bridge and Tunnel Implementation Plan

## Overview

This document outlines the implementation plan for querying, caching, and integrating bridge and tunnel data from OpenStreetMap (OSM) into the terrain generation pipeline. The goal is to:

1. **Query** bridge and tunnel ways from the Overpass API
2. **Cache** the results for reuse
3. **Mark splines** that represent bridges/tunnels so they can be:
   - Excluded from road smoothing (terrain modification)
   - Excluded from material painting
   - Later replaced with procedural DAE geometry

## Implementation Status

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 | 🔲 TODO | Data Models for Bridges/Tunnels |
| Phase 2 | 🔲 TODO | Overpass Query Service Extension |
| Phase 3 | 🔲 TODO | Cache Implementation |
| Phase 4 | 🔲 TODO | Spline Annotation (Bridge/Tunnel Markers) |
| Phase 5 | 🔲 TODO | Pipeline Integration (Road Smoothing) |
| Phase 6 | 🔲 TODO | Pipeline Integration (Material Painting) |
| Phase 7 | 🔲 TODO | UI Integration (Optional Visualization) |
| Phase 8 | 🔲 TODO | Configuration Options (Enable/Disable) |

---

## Design Principle: Optional Feature

**Important**: Bridge and tunnel detection is an **optional feature** that can be independently enabled or disabled. When disabled, the pipeline behaves exactly as it does today - all road splines are processed normally without any special handling for structures.

This allows users to:
- Disable the feature entirely if they don't need it
- Enable only bridge detection (tunnels processed as normal roads)
- Enable only tunnel detection (bridges processed as normal roads)
- Enable both for full structure handling

---

## OSM Data Model for Bridges and Tunnels

### Bridge Tags in OSM

Bridges are tagged on ways (not nodes) with:

| Tag | Description | Example Values |
|-----|-------------|----------------|
| `bridge=yes` | Way passes over an obstacle | `yes`, `viaduct`, `cantilever`, `movable` |
| `bridge:structure` | Type of bridge structure | `beam`, `arch`, `suspension`, `cable-stayed` |
| `layer` | Vertical layer (default=0) | `-1`, `0`, `1`, `2` |
| `bridge:name` | Name of the bridge | `"Golden Gate Bridge"` |
| `man_made=bridge` | Alternative tagging (area bridges) | `bridge` |

### Tunnel Tags in OSM

Tunnels are also tagged on ways:

| Tag | Description | Example Values |
|-----|-------------|----------------|
| `tunnel=yes` | Way passes through terrain | `yes`, `building_passage`, `culvert` |
| `tunnel:name` | Name of the tunnel | `"Mont Blanc Tunnel"` |
| `layer` | Vertical layer (typically negative) | `-1`, `-2` |
| `covered=yes` | Alternative for covered passages | `yes` |

### Key OSM Relationships

- A single OSM way may be **partially** a bridge/tunnel
- OSM ways are often **split at bridge/tunnel start/end points**
- Bridges/tunnels may have **associated nodes** marking entry/exit points
- A highway way can have **both** `highway=*` and `bridge=yes` tags

---

## Phase 1: Data Models for Bridges/Tunnels

### Step 1.1: Create Bridge/Tunnel Data Models

**File**: `BeamNgTerrainPoc/Terrain/Osm/Models/OsmBridgeTunnel.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Type of elevated/underground structure.
/// </summary>
public enum StructureType
{
    /// <summary>Way passes over an obstacle (water, road, valley, etc.).</summary>
    Bridge,
    
    /// <summary>Way passes through terrain (underground).</summary>
    Tunnel,
    
    /// <summary>Covered passage through a building.</summary>
    BuildingPassage,
    
    /// <summary>Small tunnel for water drainage under road.</summary>
    Culvert
}

/// <summary>
/// Represents a bridge or tunnel segment from OSM.
/// </summary>
public class OsmBridgeTunnel
{
    /// <summary>OSM way ID.</summary>
    public long WayId { get; set; }
    
    /// <summary>Type of structure (bridge, tunnel, etc.).</summary>
    public StructureType StructureType { get; set; }
    
    /// <summary>
    /// The geometry of the structure as geographic coordinates.
    /// These are the actual OSM way coordinates.
    /// </summary>
    public List<GeoCoordinate> Coordinates { get; set; } = new();
    
    /// <summary>
    /// Vertical layer (default 0, positive for elevated, negative for underground).
    /// </summary>
    public int Layer { get; set; } = 0;
    
    /// <summary>Highway type (e.g., "primary", "secondary", "motorway").</summary>
    public string? HighwayType { get; set; }
    
    /// <summary>Name of the bridge/tunnel (from name or bridge:name/tunnel:name tag).</summary>
    public string? Name { get; set; }
    
    /// <summary>Bridge structure type (beam, arch, suspension, etc.).</summary>
    public string? BridgeStructure { get; set; }
    
    /// <summary>Original OSM tags for additional processing.</summary>
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// Approximate length of the structure in meters.
    /// Calculated from coordinates after projection.
    /// </summary>
    public float LengthMeters { get; set; }
    
    /// <summary>
    /// Road width in meters (from OSM width tag or estimated from highway type).
    /// </summary>
    public float WidthMeters { get; set; }
}
```

### Step 1.2: Create Query Result Container

**File**: `BeamNgTerrainPoc/Terrain/Osm/Models/OsmBridgeTunnelQueryResult.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Result of querying bridges and tunnels from OSM.
/// </summary>
public class OsmBridgeTunnelQueryResult
{
    /// <summary>List of all bridge/tunnel structures found.</summary>
    public List<OsmBridgeTunnel> Structures { get; set; } = new();
    
    /// <summary>The bounding box that was queried.</summary>
    public GeoBoundingBox? BoundingBox { get; set; }
    
    /// <summary>When this result was queried/cached.</summary>
    public DateTime QueryTime { get; set; } = DateTime.UtcNow;
    
    /// <summary>Whether this result came from cache.</summary>
    public bool IsFromCache { get; set; }
    
    /// <summary>Number of bridges found.</summary>
    public int BridgeCount => Structures.Count(s => s.StructureType == StructureType.Bridge);
    
    /// <summary>Number of tunnels found.</summary>
    public int TunnelCount => Structures.Count(s => 
        s.StructureType == StructureType.Tunnel || 
        s.StructureType == StructureType.BuildingPassage);
    
    /// <summary>Number of culverts found.</summary>
    public int CulvertCount => Structures.Count(s => s.StructureType == StructureType.Culvert);
}
```

---

## Phase 2: Overpass Query Service Extension

### Step 2.1: Create Bridge/Tunnel Query Service Interface

**File**: `BeamNgTerrainPoc/Terrain/Osm/Services/IOsmBridgeTunnelQueryService.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Services;

using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Service for querying bridge and tunnel data from OSM via Overpass API.
/// </summary>
public interface IOsmBridgeTunnelQueryService
{
    /// <summary>
    /// Queries all bridges and tunnels within a bounding box.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing all bridge/tunnel structures.</returns>
    Task<OsmBridgeTunnelQueryResult> QueryBridgesAndTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queries only bridges within a bounding box.
    /// </summary>
    Task<OsmBridgeTunnelQueryResult> QueryBridgesAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queries only tunnels within a bounding box.
    /// </summary>
    Task<OsmBridgeTunnelQueryResult> QueryTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);
}
```

### Step 2.2: Implement the Query Service

**File**: `BeamNgTerrainPoc/Terrain/Osm/Services/OsmBridgeTunnelQueryService.cs`

**Key Implementation Details**:

1. **Overpass Query for Bridges and Tunnels**:
```overpass
[out:json][timeout:180];
(
  // Bridges on highways
  way["bridge"]["highway"]({{bbox}});
  
  // Tunnels on highways  
  way["tunnel"]["highway"]({{bbox}});
  
  // Covered passages
  way["covered"="yes"]["highway"]({{bbox}});
);
out geom;
```

2. **Parse JSON response** using existing `OsmGeoJsonParser` patterns

3. **Transform to `OsmBridgeTunnel` objects** with proper type detection:
   - Check `bridge=*` tag for bridge types (yes, viaduct, cantilever, movable, etc.)
   - Check `tunnel=*` tag for tunnel types (yes, building_passage, culvert)
   - Extract `layer` tag (default 0 if not present)
   - Get highway type for width estimation

4. **Width Estimation** (when not tagged):
```csharp
public static float EstimateRoadWidth(string? highwayType) => highwayType?.ToLower() switch
{
    "motorway" => 14.0f,      // 2x 3.5m lanes per direction
    "trunk" => 10.0f,         // 2x 3.5m lanes + shoulder
    "primary" => 8.0f,        // 2 lanes
    "secondary" => 7.0f,
    "tertiary" => 6.0f,
    "residential" => 5.5f,
    "service" => 4.0f,
    "track" => 3.0f,
    "path" or "footway" => 2.0f,
    _ => 6.0f  // Default
};
```

### Step 2.3: Integration with Existing OverpassApiService

The new service will:
- Reuse `OverpassApiService.ExecuteRawQueryAsync()` for HTTP communication
- Benefit from existing round-robin failover logic
- Use the same endpoint list and timeout configuration

```csharp
public class OsmBridgeTunnelQueryService : IOsmBridgeTunnelQueryService
{
    private readonly IOverpassApiService _overpassService;
    private readonly OsmBridgeTunnelCache _cache;
    
    public OsmBridgeTunnelQueryService(
        IOverpassApiService overpassService,
        OsmBridgeTunnelCache? cache = null)
    {
        _overpassService = overpassService;
        _cache = cache ?? new OsmBridgeTunnelCache();
    }
    
    // Implementation follows OverpassApiService patterns...
}
```

---

## Phase 3: Cache Implementation

### Step 3.1: Create Bridge/Tunnel Cache

**File**: `BeamNgTerrainPoc/Terrain/Osm/Services/OsmBridgeTunnelCache.cs`

Follow the same pattern as `OsmJunctionCache`:

- Memory cache with `Dictionary<string, OsmBridgeTunnelQueryResult>`
- Disk cache in `%LOCALAPPDATA%/BeamNG_LevelCleanUp/OsmCache/`
- Cache key format: `osm_bridges_tunnels_v{version}_{bbox}`
- 7-day default expiry
- Support for "containing bbox" optimization (reuse larger cached regions)

**Cache Key Strategy**:
```csharp
public string GetCacheKey(GeoBoundingBox bbox)
{
    return $"osm_bridge_tunnel_v{CacheVersion}_{bbox.MinLatitude:F4}_{bbox.MinLongitude:F4}_{bbox.MaxLatitude:F4}_{bbox.MaxLongitude:F4}";
}
```

### Step 3.2: Extend OsmCacheManager

**File**: `BeamNgTerrainPoc/Terrain/Osm/Services/OsmCacheManager.cs`

Add bridge/tunnel cache to the unified cache manager:

```csharp
public class OsmCacheManager
{
    private readonly OsmQueryCache _roadCache;
    private readonly OsmJunctionCache _junctionCache;
    private readonly OsmBridgeTunnelCache _bridgeTunnelCache;  // NEW
    
    public OsmBridgeTunnelCache BridgeTunnelCache => _bridgeTunnelCache;
    
    public void InvalidateAll(GeoBoundingBox bbox)
    {
        _roadCache.Invalidate(bbox);
        _junctionCache.Invalidate(bbox);
        _bridgeTunnelCache.Invalidate(bbox);  // NEW
    }
    
    public void ClearAll()
    {
        _roadCache.ClearAll();
        _junctionCache.ClearAll();
        _bridgeTunnelCache.ClearAll();  // NEW
    }
}
```

---

## Phase 4: Spline Annotation (Bridge/Tunnel Markers)

### Step 4.1: Extend RoadSpline with Structure Information

**Option A**: Add properties directly to `RoadSpline` (simpler but pollutes the class)

**Option B (Recommended)**: Create a wrapper or use the existing `ParameterizedRoadSpline`

**Extend** `ParameterizedRoadSpline.cs`:

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
    /// Vertical layer (0 = ground level, positive = elevated, negative = underground).
    /// Used for bridge/tunnel ordering and DAE placement.
    /// </summary>
    public int Layer { get; set; } = 0;
    
    /// <summary>
    /// Reference to the original OSM bridge/tunnel data (if applicable).
    /// Contains structure type, name, and other metadata for DAE generation.
    /// </summary>
    public OsmBridgeTunnel? StructureData { get; set; }
}
```

### Step 4.2: Extend UnifiedCrossSection

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`

Add structure-related properties that propagate from the owning spline:

```csharp
public class UnifiedCrossSection
{
    // ... existing properties ...
    
    /// <summary>
    /// Whether this cross-section is part of a bridge.
    /// Inherited from the owning spline's IsBridge property.
    /// </summary>
    public bool IsBridge { get; set; }
    
    /// <summary>
    /// Whether this cross-section is part of a tunnel.
    /// Inherited from the owning spline's IsTunnel property.
    /// </summary>
    public bool IsTunnel { get; set; }
    
    /// <summary>
    /// Whether this cross-section is part of any elevated/underground structure.
    /// When true, this cross-section should be excluded from terrain smoothing.
    /// </summary>
    public bool IsStructure => IsBridge || IsTunnel;
}
```

### Step 4.3: Create Bridge/Tunnel Spline Matcher

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/BridgeTunnelSplineMatcher.cs`

This service matches OSM bridge/tunnel segments to existing road splines:

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Matches OSM bridge/tunnel ways to road splines and marks them accordingly.
/// </summary>
public class BridgeTunnelSplineMatcher
{
    /// <summary>
    /// Matches bridge/tunnel structures to splines and marks the splines.
    /// </summary>
    /// <param name="splines">Road splines to annotate.</param>
    /// <param name="structures">Bridge/tunnel structures from OSM query.</param>
    /// <param name="bbox">Bounding box for coordinate transformation.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <returns>Statistics about matched structures.</returns>
    public BridgeTunnelMatchResult MatchAndAnnotate(
        List<ParameterizedRoadSpline> splines,
        OsmBridgeTunnelQueryResult structures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        // Implementation strategy:
        // 1. Transform structure coordinates to meter space
        // 2. For each structure, find splines that overlap spatially
        // 3. Use OSM way ID matching when available (best accuracy)
        // 4. Fall back to geometric matching (point-to-spline distance)
        // 5. Mark matched splines with IsBridge/IsTunnel
        // 6. Store structure data reference for later DAE generation
    }
}

public class BridgeTunnelMatchResult
{
    public int TotalStructures { get; set; }
    public int MatchedBridges { get; set; }
    public int MatchedTunnels { get; set; }
    public int UnmatchedStructures { get; set; }
    public List<OsmBridgeTunnel> UnmatchedList { get; set; } = new();
}
```

**Matching Algorithm**:

```
For each bridge/tunnel structure:
  1. Transform structure coordinates to world meters
  2. Build a polyline from the structure coordinates
  
  For each road spline:
    3. If we have OSM feature ID metadata, compare directly
    4. Otherwise, compute Hausdorff distance between structure and spline
    5. If distance < threshold (e.g., 5m), consider a match
    
  If matched:
    6. Mark spline.IsBridge = true or spline.IsTunnel = true
    7. Set spline.Layer = structure.Layer
    8. Store spline.StructureData = structure
```

---

## Phase 5: Pipeline Integration (Road Smoothing)

### Step 5.1: Query Bridges/Tunnels During OSM Data Loading

**File**: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` (or wherever OSM data is loaded)

After querying road features, also query bridges/tunnels:

```csharp
// Existing: Query road features
var osmResult = await _osmService.QueryAllFeaturesAsync(bbox, cancellationToken);

// NEW: Query bridges and tunnels
var bridgeTunnelResult = await _bridgeTunnelService.QueryBridgesAndTunnelsAsync(bbox, cancellationToken);

// Store for later use in pipeline
_currentBridgeTunnelData = bridgeTunnelResult;
```

### Step 5.2: Annotate Splines After Network Building

**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

Add a new method or extend `BuildNetwork()`:

```csharp
public UnifiedRoadNetwork BuildNetwork(
    List<MaterialDefinition> materials,
    float[,] heightMap,
    float metersPerPixel,
    int terrainSize,
    OsmBridgeTunnelQueryResult? bridgeTunnelData = null,  // NEW parameter
    bool flipMaterialProcessingOrder = true)
{
    // ... existing network building logic ...
    
    // NEW: Annotate splines with bridge/tunnel information
    if (bridgeTunnelData != null && bridgeTunnelData.Structures.Count > 0)
    {
        var matcher = new BridgeTunnelSplineMatcher();
        var matchResult = matcher.MatchAndAnnotate(
            network.Splines,
            bridgeTunnelData,
            /* parameters */);
        
        TerrainLogger.Info($"Bridge/Tunnel matching: {matchResult.MatchedBridges} bridges, " +
                          $"{matchResult.MatchedTunnels} tunnels matched");
        
        if (matchResult.UnmatchedStructures > 0)
        {
            TerrainLogger.Warning($"  {matchResult.UnmatchedStructures} structures could not be matched to splines");
        }
    }
    
    // ... continue with cross-section generation ...
}
```

### Step 5.3: Propagate Structure Flags to Cross-Sections

When generating cross-sections in `GenerateCrossSections()`:

```csharp
// In UnifiedCrossSection.FromSplineSample():
public static UnifiedCrossSection FromSplineSample(
    SplineSample sample,
    ParameterizedRoadSpline ownerSpline,
    int globalIndex,
    int localIndex)
{
    return new UnifiedCrossSection
    {
        // ... existing assignments ...
        
        // NEW: Propagate structure flags
        IsBridge = ownerSpline.IsBridge,
        IsTunnel = ownerSpline.IsTunnel,
    };
}
```

### Step 5.4: Exclude Structure Cross-Sections from Smoothing

**File**: `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` (or similar)

When applying elevation smoothing:

```csharp
foreach (var crossSection in network.CrossSections)
{
    // Skip bridge/tunnel cross-sections - they don't modify terrain
    if (crossSection.IsStructure)
    {
        crossSection.IsExcluded = true;
        continue;
    }
    
    // ... existing smoothing logic ...
}
```

### Step 5.5: Handle Junction Harmonization at Structure Boundaries

Special handling needed where regular road meets bridge/tunnel:

```csharp
// In junction harmonization:
// When a ground-level spline connects to a bridge/tunnel spline:
// 1. The last few cross-sections of the ground spline form a "ramp"
// 2. These should have gradual elevation change to meet the structure
// 3. Mark these as "structure_approach" for potential special handling

public bool IsStructureApproach { get; set; }
public float DistanceToStructure { get; set; }
```

---

## Phase 6: Pipeline Integration (Material Painting)

### Step 6.1: Exclude Structure Areas from Layer Maps

When rasterizing road splines to layer maps for material painting:

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`

```csharp
public byte[,] RasterizeSplinesToLayerMap(
    List<RoadSpline> splines,
    int terrainSize,
    float metersPerPixel,
    float roadSurfaceWidthMeters,
    HashSet<int>? excludeSplineIds = null)  // NEW: Allow exclusion
{
    var result = new byte[terrainSize, terrainSize];
    
    foreach (var spline in splines)
    {
        // Skip excluded splines (bridges/tunnels)
        if (excludeSplineIds?.Contains(spline.SplineId) == true)
            continue;
        
        // ... existing rasterization ...
    }
}
```

### Step 6.2: Create Structure Exclusion Mask

Generate a mask of areas covered by bridges/tunnels for other processing:

```csharp
public byte[,] RasterizeStructureMask(
    UnifiedRoadNetwork network,
    int terrainSize,
    float metersPerPixel)
{
    var mask = new byte[terrainSize, terrainSize];
    
    var structureSplines = network.Splines
        .Where(s => s.IsStructure)
        .Select(s => s.Spline)
        .ToList();
    
    // Rasterize all structure splines
    foreach (var spline in structureSplines)
    {
        var width = network.GetSplineById(spline.SplineId)?.Parameters.RoadWidthMeters ?? 6f;
        RasterizeSplineToMask(mask, spline, width, metersPerPixel);
    }
    
    return mask;
}
```

---

## Phase 7: UI Integration (Optional Visualization)

### Step 7.1: Display Bridge/Tunnel Statistics

In the UI (Blazor), show the user what structures were detected:

```razor
@if (BridgeTunnelData != null)
{
    <MudAlert Severity="Severity.Info">
        <MudText>Detected Structures:</MudText>
        <MudText>• @BridgeTunnelData.BridgeCount bridges</MudText>
        <MudText>• @BridgeTunnelData.TunnelCount tunnels</MudText>
        @if (BridgeTunnelData.CulvertCount > 0)
        {
            <MudText>• @BridgeTunnelData.CulvertCount culverts</MudText>
        }
    </MudAlert>
}
```

### Step 7.2: Optional: Visualize on Debug Maps

Extend debug image exporters to highlight bridges/tunnels:

- **Bridge cross-sections**: Blue color
- **Tunnel cross-sections**: Orange/brown color
- **Structure approach zones**: Gradient colors

---

## Data Flow Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          TERRAIN CREATION PIPELINE                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  1. OSM QUERY PHASE                                                          │
│     ┌──────────────────┐    ┌──────────────────────┐                        │
│     │ Query Road       │    │ Query Bridges/Tunnels│                        │
│     │ Features         │    │ (NEW)                │                        │
│     └────────┬─────────┘    └──────────┬───────────┘                        │
│              │                         │                                     │
│              ▼                         ▼                                     │
│     ┌──────────────────┐    ┌──────────────────────┐                        │
│     │ Road Cache       │    │ Bridge/Tunnel Cache  │                        │
│     │ (OsmQueryCache)  │    │ (NEW)                │                        │
│     └────────┬─────────┘    └──────────┬───────────┘                        │
│              │                         │                                     │
│              └──────────┬──────────────┘                                     │
│                         │                                                    │
│  2. NETWORK BUILDING PHASE                                                   │
│                         ▼                                                    │
│     ┌────────────────────────────────────────┐                              │
│     │ UnifiedRoadNetworkBuilder              │                              │
│     │ - Build splines from OSM features      │                              │
│     │ - Match bridges/tunnels to splines     │◄── BridgeTunnelSplineMatcher │
│     │ - Mark splines: IsBridge, IsTunnel     │                              │
│     └────────────────────┬───────────────────┘                              │
│                          │                                                   │
│  3. CROSS-SECTION GENERATION                                                 │
│                          ▼                                                   │
│     ┌────────────────────────────────────────┐                              │
│     │ Generate Cross-Sections                │                              │
│     │ - Propagate IsBridge/IsTunnel flags    │                              │
│     │ - Mark structure cross-sections        │                              │
│     └────────────────────┬───────────────────┘                              │
│                          │                                                   │
│  4. ROAD SMOOTHING PHASE                                                     │
│                          ▼                                                   │
│     ┌────────────────────────────────────────┐                              │
│     │ Elevation Smoothing Service            │                              │
│     │ - SKIP cross-sections where IsStructure│                              │
│     │ - Handle approach zones gracefully     │                              │
│     └────────────────────┬───────────────────┘                              │
│                          │                                                   │
│  5. MATERIAL PAINTING PHASE                                                  │
│                          ▼                                                   │
│     ┌────────────────────────────────────────┐                              │
│     │ Layer Map Rasterization                │                              │
│     │ - EXCLUDE structure splines from mask  │                              │
│     │ - Generate structure exclusion mask    │                              │
│     └────────────────────┬───────────────────┘                              │
│                          │                                                   │
│  6. FUTURE: DAE GENERATION                                                   │
│                          ▼                                                   │
│     ┌────────────────────────────────────────┐                              │
│     │ Procedural Bridge/Tunnel DAE Generator │                              │
│     │ (Phase 2 - Not in this plan)           │                              │
│     └────────────────────────────────────────┘                              │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## File Structure Summary

```
BeamNgTerrainPoc/
├── Terrain/
│   ├── Osm/
│   │   ├── Models/
│   │   │   ├── OsmBridgeTunnel.cs              (NEW)
│   │   │   └── OsmBridgeTunnelQueryResult.cs   (NEW)
│   │   ├── Services/
│   │   │   ├── IOsmBridgeTunnelQueryService.cs (NEW)
│   │   │   ├── OsmBridgeTunnelQueryService.cs  (NEW)
│   │   │   ├── OsmBridgeTunnelCache.cs         (NEW)
│   │   │   └── OsmCacheManager.cs              (MODIFIED)
│   │   └── Processing/
│   │       └── BridgeTunnelSplineMatcher.cs    (NEW)
│   ├── Models/
│   │   ├── TerrainCreationParameters.cs        (MODIFIED - add Enable flags)
│   │   └── RoadGeometry/
│   │       ├── ParameterizedRoadSpline.cs      (MODIFIED)
│   │       └── UnifiedCrossSection.cs          (MODIFIED)
│   └── Services/
│       └── UnifiedRoadNetworkBuilder.cs        (MODIFIED)
└── Docs/
    └── BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md    (THIS FILE)

BeamNG_LevelCleanUp/
└── BlazorUI/
    └── Pages/
        └── TerrainCreator.razor                (MODIFIED - add UI controls)
```

---

## Testing Strategy

### Unit Tests

1. **OsmBridgeTunnelQueryService**
   - Test Overpass query generation
   - Test JSON parsing for various structure types
   - Test layer extraction

2. **OsmBridgeTunnelCache**
   - Test cache hit/miss scenarios
   - Test containing bbox optimization
   - Test cache expiry

3. **BridgeTunnelSplineMatcher**
   - Test exact OSM ID matching
   - Test geometric matching with various distances
   - Test edge cases (partial overlaps, split ways)

### Integration Tests

1. **Full Pipeline Test**
   - Load real-world OSM data with known bridges/tunnels
   - Verify splines are marked correctly
   - Verify smoothing excludes structure areas
   - Verify material painting excludes structure areas

2. **Visual Verification**
   - Export debug images showing bridge/tunnel locations
   - Compare with satellite imagery
   - Verify exclusion zones are correct

---

## Phase 8: Configuration Options (Enable/Disable)

### Step 8.1: Add Configuration to TerrainCreationParameters

**File**: `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

```csharp
// ========================================
// BRIDGE/TUNNEL CONFIGURATION
// ========================================

/// <summary>
/// When true, bridges are detected from OSM and excluded from terrain smoothing.
/// Bridge splines will be marked for later DAE generation.
/// When false, bridge ways are treated as normal roads (current behavior).
/// Default: false (feature disabled by default)
/// </summary>
public bool EnableBridgeDetection { get; set; } = false;

/// <summary>
/// When true, tunnels are detected from OSM and excluded from terrain smoothing.
/// Tunnel splines will be marked for later DAE generation.
/// When false, tunnel ways are treated as normal roads (current behavior).
/// Default: false (feature disabled by default)
/// </summary>
public bool EnableTunnelDetection { get; set; } = false;

/// <summary>
/// Convenience property to check if any structure detection is enabled.
/// </summary>
public bool EnableStructureDetection => EnableBridgeDetection || EnableTunnelDetection;
```

### Step 8.2: Conditional Query Logic

**File**: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` (or wherever OSM data is loaded)

```csharp
// Only query bridges/tunnels if detection is enabled
OsmBridgeTunnelQueryResult? bridgeTunnelResult = null;

if (parameters.EnableStructureDetection)
{
    TerrainLogger.Info("Structure detection enabled - querying bridges/tunnels from OSM...");
    bridgeTunnelResult = await _bridgeTunnelService.QueryBridgesAndTunnelsAsync(bbox, cancellationToken);
    
    // Filter based on what's actually enabled
    if (!parameters.EnableBridgeDetection)
    {
        bridgeTunnelResult.Structures.RemoveAll(s => s.StructureType == StructureType.Bridge);
        TerrainLogger.Info("  Bridge detection disabled - bridge structures will be treated as normal roads");
    }
    
    if (!parameters.EnableTunnelDetection)
    {
        bridgeTunnelResult.Structures.RemoveAll(s => 
            s.StructureType == StructureType.Tunnel || 
            s.StructureType == StructureType.BuildingPassage ||
            s.StructureType == StructureType.Culvert);
        TerrainLogger.Info("  Tunnel detection disabled - tunnel structures will be treated as normal roads");
    }
    
    TerrainLogger.Info($"  Will process: {bridgeTunnelResult.BridgeCount} bridges, {bridgeTunnelResult.TunnelCount} tunnels");
}
else
{
    TerrainLogger.Info("Structure detection disabled - all roads will be processed normally");
}
```

### Step 8.3: Conditional Spline Annotation

**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

```csharp
public UnifiedRoadNetwork BuildNetwork(
    List<MaterialDefinition> materials,
    float[,] heightMap,
    float metersPerPixel,
    int terrainSize,
    OsmBridgeTunnelQueryResult? bridgeTunnelData = null,
    bool flipMaterialProcessingOrder = true)
{
    // ... existing network building logic ...
    
    // Only annotate if we have structure data (which only exists if detection was enabled)
    if (bridgeTunnelData != null && bridgeTunnelData.Structures.Count > 0)
    {
        var matcher = new BridgeTunnelSplineMatcher();
        var matchResult = matcher.MatchAndAnnotate(
            network.Splines,
            bridgeTunnelData,
            /* parameters */);
        
        TerrainLogger.Info($"Bridge/Tunnel matching: {matchResult.MatchedBridges} bridges, " +
                          $"{matchResult.MatchedTunnels} tunnels marked for exclusion");
    }
    // If bridgeTunnelData is null, no splines are marked - they all process normally
    
    // ... continue with cross-section generation ...
}
```

### Step 8.4: Graceful Degradation in Smoothing

**File**: `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs`

```csharp
foreach (var crossSection in network.CrossSections)
{
    // Structure exclusion only applies if the feature is enabled and spline was marked
    // When disabled, IsStructure will always be false, so all cross-sections are processed
    if (crossSection.IsStructure)
    {
        crossSection.IsExcluded = true;
        continue;
    }
    
    // ... existing smoothing logic (unchanged) ...
}
```

### Step 8.5: UI Controls

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/TerrainCreator.razor` (or relevant UI)

```razor
<MudExpansionPanel Text="Advanced: Bridge & Tunnel Detection">
    <MudText Typo="Typo.body2" Class="mb-2">
        When enabled, bridges and tunnels from OSM data will be detected and excluded 
        from terrain smoothing. This allows for later procedural DAE generation.
    </MudText>
    
    <MudSwitch @bind-Value="Parameters.EnableBridgeDetection" 
               Label="Detect Bridges" 
               Color="Color.Primary" />
    <MudText Typo="Typo.caption" Class="ml-10 mb-2">
        Bridge splines will be excluded from terrain modification
    </MudText>
    
    <MudSwitch @bind-Value="Parameters.EnableTunnelDetection" 
               Label="Detect Tunnels" 
               Color="Color.Primary" />
    <MudText Typo="Typo.caption" Class="ml-10 mb-2">
        Tunnel splines will be excluded from terrain modification
    </MudText>
    
    @if (Parameters.EnableStructureDetection && BridgeTunnelData != null)
    {
        <MudAlert Severity="Severity.Info" Dense="true" Class="mt-2">
            Detected: @BridgeTunnelData.BridgeCount bridges, @BridgeTunnelData.TunnelCount tunnels
        </MudAlert>
    }
</MudExpansionPanel>
```

---

## Behavior Summary by Configuration

| EnableBridgeDetection | EnableTunnelDetection | Behavior |
|-----------------------|-----------------------|----------|
| `false` | `false` | **Current behavior** - All roads processed normally, no structure detection |
| `true` | `false` | Bridges excluded from smoothing, tunnels treated as normal roads |
| `false` | `true` | Tunnels excluded from smoothing, bridges treated as normal roads |
| `true` | `true` | Both bridges and tunnels excluded from smoothing |

**Key Point**: When a feature is disabled, the affected splines:
- Are NOT marked with `IsBridge`/`IsTunnel` flags
- ARE included in terrain smoothing
- ARE included in material painting
- Behave exactly as they do in the current implementation

---

## Future Extensions (Out of Scope for This Plan)

1. **Procedural Bridge DAE Generation**
   - Use `BeamNG.Procedural3D` library
   - Generate bridge deck, supports, railings
   - Material assignment for different bridge types

2. **Procedural Tunnel DAE Generation**
   - Generate tunnel opening portals
   - Interior tunnel geometry (optional)
   - Lighting fixtures

3. **Complex Interchange Handling**
   - Multi-level interchanges with multiple bridges
   - Proper layer sorting for overlapping structures

4. **User-Configurable Bridge Styles**
   - Bridge type selection (beam, arch, truss)
   - Material and texture options
   - Width and clearance overrides

---

## Dependencies

### Existing Dependencies (No Changes)
- `OverpassApiService` - HTTP communication
- `OsmGeoJsonParser` - JSON parsing
- `GeoCoordinateTransformer` - Coordinate transformation
- `UnifiedRoadNetwork` - Road network container

### New Dependencies
- None required - all functionality builds on existing infrastructure
