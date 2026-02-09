I'll analyze the codebase to understand the current architecture before providing the documentation. Let me examine the relevant files.
Now let me search for more context about how materials are processed and how the terrain generation orchestrates everything:
Now I have a comprehensive understanding of the current architecture. Let me produce the detailed documentation for the proposed refactoring.

---

# Terrain Generation Refactoring: Material-Agnostic Road Network Architecture

## Executive Summary

This document proposes a significant architectural refactoring of the road smoothing pipeline to address two critical issues:
1. **Junction Logic Failure**: Current T-junction prioritization produces suboptimal elevation transitions
2. **Isolated Per-Material Processing**: Terrain blending applied sequentially per-material destroys elevation smoothing of previously processed roads

The solution transforms the current **material-centric** processing into a **unified road network** approach while preserving the frontend's material-based configuration model.

---

## 1. Feasibility Analysis

### 1.1 Current Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    Current Processing Flow                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Material A (Highway)         Material B (Dirt Road)            │
│        │                              │                          │
│        ▼                              ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Extract      │              │ Extract      │                 │
│  │ Geometry     │              │ Geometry     │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         ▼                             ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Calculate    │              │ Calculate    │                 │
│  │ Elevations   │              │ Elevations   │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         ▼                             ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Harmonize    │              │ Harmonize    │                 │
│  │ Within Mat.  │              │ Within Mat.  │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         ▼                             ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Cross-Mat    │◄────────────►│ Cross-Mat    │  ← WEAK LINK   │
│  │ Harmonize    │              │ Harmonize    │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         ▼                             ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Blend to     │              │ Blend to     │  ← OVERWRITES! │
│  │ Terrain      │              │ Terrain      │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         ▼                             ▼                          │
│    HeightMap₁  ──────────────►  HeightMap₂ (FINAL)              │
│                                                                  │
│  ⚠️ Problem: Material B's blending destroys Material A's roads  │
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 Identified Problems

#### Problem 1: Junction Logic Failure
- **Location**: `JunctionElevationHarmonizer.ComputeJunctionElevations()` (lines ~200-260)
- **Current Behavior**: T-junction detection assigns the "continuous" road's elevation to the junction, forcing the "terminating" road to adapt
- **Issue**: This binary decision doesn't account for:
  - Road importance/hierarchy (highway vs. residential)
  - Actual elevation differences at connection points
  - Smooth gradient requirements on both roads

#### Problem 2: Sequential Terrain Blending
- **Location**: `MultiMaterialRoadSmoother.SmoothWithCrossMaterialHarmonization()` (lines ~100-200)
- **Current Behavior**: Terrain blending applies per-material in sequence:
```csharp
foreach (var data in allMaterialData)
{
    var result = ApplyTerrainBlending(heightMap, data.Geometry, ...);
    heightMap = result.ModifiedHeightMap; // ← Last material wins!
  }
```
- **Issue**: The distance-field-based blender (`DistanceFieldTerrainBlender`) modifies the heightmap within its `blendRange`, potentially overwriting perfectly smoothed road surfaces from previously processed materials.

### 1.3 Feasibility Assessment

| Aspect | Assessment | Notes |
|--------|------------|-------|
| **Data Structure Compatibility** | ✅ High | `CrossSection` and `RoadGeometry` are already material-agnostic |
| **Parameter Model** | ✅ Compatible | Parameters can be attached to splines via new `SplineParameters` wrapper |
| **Frontend Impact** | ✅ Minimal | UI remains material-centric; transformation happens internally |
| **Performance Impact** | ⚠️ Moderate | Single EDT pass is faster, but larger combined road network may offset gains |
| **Risk Level** | ⚠️ Medium | Core algorithm changes require comprehensive testing |
| **Reversibility** | ✅ High | Can maintain legacy path behind feature flag |

### 1.4 Technical Constraints

1. **Memory**: Combined road network for 8192×8192 terrain with dense roads could require ~500MB for distance fields
2. **Precision**: Cross-material junction detection must use consistent coordinate systems
3. **Edge Cases**: Roads that span material boundaries need special handling

---

We dont need any backward Compatibility! We dont' want old and unnecessary code in our codebase!

## 2. Proposed Architecture

### 2.1 High-Level Design

```
┌─────────────────────────────────────────────────────────────────┐
│                    Proposed Processing Flow                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Material A (Highway)         Material B (Dirt Road)            │
│        │                              │                          │
│        ▼                              ▼                          │
│  ┌──────────────┐              ┌──────────────┐                 │
│  │ Extract      │              │ Extract      │                 │
│  │ Splines +    │              │ Splines +    │                 │
│  │ Params       │              │ Params       │                 │
│  └──────┬───────┘              └──────┬───────┘                 │
│         │                             │                          │
│         └────────────┬────────────────┘                          │
│                      ▼                                           │
│         ┌────────────────────────┐                               │
│         │  UNIFIED ROAD NETWORK  │  ← NEW: All materials merged │
│         │  (Splines + Params)    │                               │
│         └────────────┬───────────┘                               │
│                      │                                           │
│                      ▼                                           │
│         ┌────────────────────────┐                               │
│         │  Calculate Target      │                               │
│         │  Elevations (per-path) │                               │
│         └────────────┬───────────┘                               │
│                      │                                           │
│                      ▼                                           │
│         ┌────────────────────────┐                               │
│         │  UNIFIED JUNCTION      │  ← NEW: All junctions at once│
│         │  HARMONIZATION         │                               │
│         └────────────┬───────────┘                               │
│                      │                                           │
│                      ▼                                           │
│         ┌────────────────────────┐                               │
│         │  SINGLE PASS TERRAIN   │  ← NEW: One EDT, one blend   │
│         │  BLENDING (Protected   │                               │
│         │  Road Network)         │                               │
│         └────────────┬───────────┘                               │
│                      │                                           │
│                      ▼                                           │
│         ┌────────────────────────┐                               │
│         │  Material Painting     │  ← Separate pass, no elev.   │
│         │  (per-spline surface   │     changes                   │
│         │   width)               │                               │
│         └────────────────────────┘                               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 New Data Structures

```csharp
/// <summary>
/// A road segment with attached smoothing parameters.
/// Enables per-spline parameter application in a unified network.
/// </summary>
public class ParameterizedRoadSpline
{
    /// <summary>
    /// The geometric spline data (positions, tangents, normals).
    /// </summary>
    public RoadSpline Spline { get; init; }
    
    /// <summary>
    /// Smoothing parameters from the originating material.
    /// </summary>
    public RoadSmoothingParameters Parameters { get; init; }
    
    /// <summary>
    /// Source material name (for debugging and material painting).
    /// </summary>
    public string MaterialName { get; init; }
    
    /// <summary>
    /// Priority for junction conflicts (higher = wins).
    /// Derived from road type or explicit configuration.
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// Unique identifier for cross-referencing.
    /// </summary>
    public int SplineId { get; init; }
}

/// <summary>
/// Unified road network containing all materials' roads.
/// </summary>
public class UnifiedRoadNetwork
{
    public List<ParameterizedRoadSpline> Splines { get; } = new();
    public List<UnifiedCrossSection> CrossSections { get; } = new();
    public List<NetworkJunction> Junctions { get; } = new();
    
    /// <summary>
    /// Maps SplineId -> MaterialName for painting phase.
    /// </summary>
    public Dictionary<int, string> SplineMaterialMap { get; } = new();
}

/// <summary>
/// Cross-section with reference to its source spline.
/// </summary>
public class UnifiedCrossSection : CrossSection
{
    /// <summary>
    /// Reference to the owning parameterized spline.
    /// </summary>
    public int OwnerSplineId { get; set; }
    
    /// <summary>
    /// Effective road width for THIS cross-section (from spline's params).
    /// </summary>
    public float EffectiveRoadWidth { get; set; }
    
    /// <summary>
    /// Effective blend range for THIS cross-section (from spline's params).
    /// </summary>
    public float EffectiveBlendRange { get; set; }
}

/// <summary>
/// Junction in the unified network with multi-material awareness.
/// </summary>
public class NetworkJunction
{
    public Vector2 Position { get; set; }
    public List<(UnifiedCrossSection CrossSection, ParameterizedRoadSpline Spline)> Contributors { get; } = new();
    public float HarmonizedElevation { get; set; }
    public JunctionType Type { get; set; } // T, Y, X, Endpoint
}

public enum JunctionType
{
    Endpoint,       // Single road ending
    TJunction,      // One road ends at another's side
    YJunction,      // Two roads merge/split
    CrossRoads,     // Four-way intersection
    Complex         // More than 4 roads
}
```

### 2.3 Key Algorithm Changes

#### Junction Harmonization Strategy

**Current (problematic)**:
```
T-Junction: Continuous road wins → Side road forced to match
```

**Proposed (gradient-aware)**:
```
T-Junction Resolution:
1. Identify continuous road (C) and terminating road (T)
2. Sample C's elevation at junction point: E_c
3. Sample T's elevation approaching junction: E_t
4. Calculate elevation difference: ΔE = |E_c - E_t|
5. If ΔE < threshold (e.g., 0.5m):
   → Use weighted average based on road priorities
6. If ΔE ≥ threshold:
   → Apply gradient ramp on T starting from blend distance
   → Ramp from E_t to E_c over JunctionBlendDistanceMeters
   → C remains unchanged (or micro-adjusted if T has higher priority)
```

#### Terrain Blending Strategy

**Current (destructive)**:
```
For each material M:
    Apply EDT-based blending for M's roads
    → Overwrites previous materials' work
```

**Proposed (protected network)**:
```
Phase 1: Build combined road core mask from ALL splines
Phase 2: Compute SINGLE global EDT from combined mask
Phase 3: Apply elevations:
    - Within road core (any spline): Use spline's target elevation
    - Within blend zone: Interpolate to terrain, respecting per-spline blend range
    - Protection rule: Road core pixels are NEVER modified by blend zones
```

---

## 3. Detailed Refactoring Implementation Plan

### Phase 1: Data Structure Foundation (2-3 AI agent sessions)

#### Step 1.1: Create New Model Classes
**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`

**Task**: Create the `ParameterizedRoadSpline` class as specified in section 2.2.

**Acceptance Criteria**:
- Class compiles and follows existing naming conventions
- XML documentation matches codebase style
- Unit-testable constructor and properties

#### Step 1.2: Create UnifiedRoadNetwork Container
**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedRoadNetwork.cs`

**Task**: Create the `UnifiedRoadNetwork` class with:
- Collection management methods (`AddSpline`, `AddCrossSection`)
- Spatial indexing support (prepare for fast junction detection)
- Serialization attributes if needed for debugging

**Acceptance Criteria**:
- Can hold splines from multiple materials
- Maintains SplineId → Material mapping
- Thread-safe for parallel cross-section generation

#### Step 1.3: Extend CrossSection with Owner Reference
**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/CrossSection.cs`

**Task**: Add `OwnerSplineId`, `EffectiveRoadWidth`, `EffectiveBlendRange` properties (or create `UnifiedCrossSection` subclass to avoid breaking changes).

**Acceptance Criteria**:
- Backward compatible with existing code
- Nullable or default values for legacy usage

---

### Phase 2: Network Builder Service (2-3 AI agent sessions)

#### Step 2.1: Create UnifiedRoadNetworkBuilder
**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

**Task**: Create service that:
1. Accepts `List<MaterialDefinition>` (existing model)
2. Extracts splines from each material (reuse existing extractors)
3. Attaches `RoadSmoothingParameters` to each spline
4. Assigns priority based on road width or explicit config
5. Returns `UnifiedRoadNetwork`

**Implementation Details**:
```csharp
public class UnifiedRoadNetworkBuilder
{
    public UnifiedRoadNetwork BuildNetwork(
        List<MaterialDefinition> materials,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var network = new UnifiedRoadNetwork();
        int splineIdCounter = 0;
        
        foreach (var material in materials.Where(m => m.RoadParameters != null))
        {
            // 1. Load layer image or generate from OSM
            var roadLayer = LoadOrGenerateLayer(material, terrainSize);
            
            // 2. Extract splines using existing MedialAxisRoadExtractor
            var geometry = ExtractGeometry(roadLayer, material.RoadParameters, metersPerPixel);
            
            // 3. Convert to ParameterizedRoadSpline
            foreach (var spline in ConvertToSplines(geometry))
            {
                var paramSpline = new ParameterizedRoadSpline
                {
                    Spline = spline,
                    Parameters = material.RoadParameters,
                    MaterialName = material.MaterialName,
                    SplineId = splineIdCounter++,
                    Priority = CalculatePriority(material.RoadParameters)
                };
                
                network.Splines.Add(paramSpline);
                network.SplineMaterialMap[paramSpline.SplineId] = material.MaterialName;
            }
        }
        
        return network;
    }
}
```

**Acceptance Criteria**:
- Produces identical splines to current per-material extraction
- Parameters correctly attached to each spline
- Performance within 10% of current extraction time

#### Step 2.2: Implement Priority Calculation
**File**: Same as 2.1

**Task**: Implement `CalculatePriority()` method:
- Default priority based on `RoadWidthMeters` (wider = higher priority)
- Optional explicit `RoadPriority` property on `RoadSmoothingParameters`
- Highway presets get priority 100, local roads 50, dirt tracks 25

**Acceptance Criteria**:
- Deterministic ordering for identical inputs
- Configurable via UI (future step)

---

### Phase 3: Unified Junction Harmonizer (3-4 AI agent sessions)

#### Step 3.1: Create NetworkJunctionDetector
**File**: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs`

**Task**: Detect junctions across the entire unified network:
1. Build spatial index of all cross-section endpoints
2. Cluster endpoints within `JunctionDetectionRadiusMeters`
3. Classify junction type (T, Y, X, Complex)
4. For T-junctions: Identify which spline is "continuous"

**Algorithm for T-Junction Detection**:
```csharp
private JunctionType ClassifyJunction(List<UnifiedCrossSection> cluster)
{
    var splineIds = cluster.Select(c => c.OwnerSplineId).Distinct().ToList();
    
    if (splineIds.Count == 1)
        return JunctionType.Endpoint;
    
    // Check if any spline has a non-endpoint cross-section in cluster
    // (indicates the junction is in the MIDDLE of that spline = T-junction)
    foreach (var splineId in splineIds)
    {
        var splineSections = cluster.Where(c => c.OwnerSplineId == splineId).ToList();
        if (splineSections.Any(cs => !IsEndpoint(cs)))
        {
            // This spline passes THROUGH the junction
            return JunctionType.TJunction;
        }
    }
    
    return splineIds.Count == 2 ? JunctionType.YJunction 
         : splineIds.Count == 4 ? JunctionType.CrossRoads 
         : JunctionType.Complex;
}
```

**Acceptance Criteria**:
- Correctly identifies T-junctions where dirt road meets highway
- Handles complex intersections (roundabouts) gracefully
- Exports debug visualization

#### Step 3.2: Create NetworkJunctionHarmonizer
**File**: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Task**: Harmonize elevations across the unified network:

1. **Input**: `UnifiedRoadNetwork` with calculated target elevations
2. **For T-Junctions**:
   - Identify continuous spline (C) and terminating spline (T)
   - If priorities differ significantly: T adapts to C
   - If priorities similar: Use gradient-based approach from section 2.3
3. **For Y/X Junctions**:
   - Weighted average based on priority and approach angle
4. **Output**: Modified `TargetElevation` on affected cross-sections

**Key Method Signature**:
```csharp
public void HarmonizeNetwork(
    UnifiedRoadNetwork network,
    float[,] heightMap,
    float metersPerPixel)
{
    // 1. Detect all junctions
    var junctions = _detector.DetectJunctions(network);
    
    // 2. Sort by priority (handle highest-priority roads first)
    var sortedJunctions = junctions.OrderByDescending(j => GetMaxPriority(j));
    
    // 3. Harmonize each junction
    foreach (var junction in sortedJunctions)
    {
        switch (junction.Type)
        {
            case JunctionType.TJunction:
                HarmonizeTJunction(junction, network);
                break;
            case JunctionType.YJunction:
            case JunctionType.CrossRoads:
                HarmonizeMultiWayJunction(junction, network);
                break;
            case JunctionType.Endpoint:
                ApplyEndpointTaper(junction, network, heightMap, metersPerPixel);
                break;
        }
    }
    
    // 4. Propagate changes along affected splines
    PropagateElevationConstraints(network);
}
```

**Acceptance Criteria**:
- Dirt road meeting highway no longer creates elevation cliff
- Highway elevation remains stable at T-junctions
- Debug image shows junction classifications

#### Step 3.3: Implement Gradient-Based T-Junction Resolution
**File**: Same as 3.2

**Task**: Implement the gradient-aware algorithm from section 2.3:

```csharp
private void HarmonizeTJunction(NetworkJunction junction, UnifiedRoadNetwork network)
{
    // 1. Identify continuous (C) and terminating (T) splines
    var continuous = junction.Contributors
        .First(c => !IsEndpointCrossSection(c.CrossSection));
    var terminating = junction.Contributors
        .Where(c => IsEndpointCrossSection(c.CrossSection))
        .ToList();
    
    float E_c = continuous.CrossSection.TargetElevation;
    
    foreach (var (cs, spline) in terminating)
    {
        float E_t = cs.TargetElevation;
        float deltaE = MathF.Abs(E_c - E_t);
        
        var junctionParams = spline.Parameters.JunctionHarmonizationParameters 
                           ?? new JunctionHarmonizationParameters();
        
        if (deltaE < 0.5f) // Small difference - weighted average
        {
            float totalPriority = continuous.Spline.Priority + spline.Priority;
            junction.HarmonizedElevation = 
                (E_c * continuous.Spline.Priority + E_t * spline.Priority) / totalPriority;
        }
        else // Significant difference - gradient ramp on T
        {
            // T must ramp to meet C
            ApplyGradientRamp(
                spline, 
                cs, 
                E_c, 
                junctionParams.JunctionBlendDistanceMeters,
                network);
            
            junction.HarmonizedElevation = E_c;
        }
    }
}
```

**Acceptance Criteria**:
- Roads with <0.5m elevation difference blend smoothly
- Roads with >0.5m difference show clear gradient ramp on terminating road
- No "dents" at junctions in final heightmap

---

### Phase 4: Protected Terrain Blender (3-4 AI agent sessions)

#### Step 4.1: Create UnifiedTerrainBlender
**File**: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs`

**Task**: Single-pass terrain blending that protects the road network:

```csharp
public class UnifiedTerrainBlender
{
    public float[,] BlendNetworkWithTerrain(
        float[,] originalHeightMap,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        int height = originalHeightMap.GetLength(0);
        int width = originalHeightMap.GetLength(1);
        
        // 1. Build COMBINED road core mask from ALL splines
        var combinedCoreMask = BuildCombinedRoadCoreMask(network, width, height, metersPerPixel);
        
        // 2. Compute SINGLE global EDT
        var distanceField = ComputeDistanceField(combinedCoreMask, metersPerPixel);
        
        // 3. Build elevation map with per-pixel source spline tracking
        var (elevationMap, splineOwnerMap) = BuildElevationMapWithOwnership(
            network, width, height, metersPerPixel);
        
        // 4. Apply protected blending
        var result = ApplyProtectedBlending(
            originalHeightMap,
            distanceField,
            elevationMap,
            splineOwnerMap,
            network,
            metersPerPixel);
        
        return result;
    }
}
```

**Acceptance Criteria**:
- Single EDT computation for entire network
- Road core pixels never modified by neighbor's blend zone
- Performance comparable to current approach for single material

#### Step 4.2: Implement Per-Spline Blend Range
**File**: Same as 4.1

**Task**: Apply different blend ranges based on which spline owns each pixel:

```csharp
private float[,] ApplyProtectedBlending(
    float[,] original,
    float[,] distanceField,
    float[,] elevationMap,
    int[,] splineOwnerMap,  // -1 = no owner, else SplineId
    UnifiedRoadNetwork network,
    float metersPerPixel)
{
    var result = (float[,])original.Clone();
    var splineParams = network.Splines.ToDictionary(s => s.SplineId, s => s.Parameters);
    
    Parallel.For(0, result.GetLength(0), y =>
    {
        for (int x = 0; x < result.GetLength(1); x++)
        {
            float d = distanceField[y, x];
            int ownerId = splineOwnerMap[y, x];
            
            if (ownerId < 0) continue; // Not near any road
            
            var owner = splineParams[ownerId];
            float halfWidth = owner.RoadWidthMeters / 2.0f;
            float blendRange = owner.TerrainAffectedRangeMeters;
            
            if (d <= halfWidth)
            {
                // Road core - use target elevation (PROTECTED)
                result[y, x] = elevationMap[y, x];
            }
            else if (d <= halfWidth + blendRange)
            {
                // Blend zone - interpolate
                float t = (d - halfWidth) / blendRange;
                float blend = ApplyBlendFunction(t, owner.BlendFunctionType);
                result[y, x] = elevationMap[y, x] * (1 - blend) + original[y, x] * blend;
            }
            // else: outside all influence - keep original
        }
    });
    
    return result;
}
```

**Acceptance Criteria**:
- Highway with 12m blend range doesn't affect dirt road with 6m blend range
- Overlapping blend zones resolve to nearest spline's parameters
- No visible seams between materials

#### Step 4.3: Implement Ownership Map Builder
**File**: Same as 4.1

**Task**: Build a 2D map tracking which spline "owns" each pixel:

```csharp
private (float[,] elevations, int[,] owners) BuildElevationMapWithOwnership(
    UnifiedRoadNetwork network,
    int width, int height,
    float metersPerPixel)
{
    var elevations = new float[height, width];
    var owners = new int[height, width];
    var distances = new float[height, width];
    
    // Initialize
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            elevations[y, x] = float.NaN;
            owners[y, x] = -1;
            distances[y, x] = float.MaxValue;
        }
    
    // For each cross-section, stamp its influence
    foreach (var cs in network.CrossSections)
    {
        float maxDist = cs.EffectiveRoadWidth / 2.0f + cs.EffectiveBlendRange;
        
        // Rasterize influence zone
        foreach (var (px, py, dist) in RasterizeInfluenceZone(cs, metersPerPixel, maxDist))
        {
            if (px < 0 || px >= width || py < 0 || py >= height) continue;
            
            if (dist < distances[py, px])
            {
                distances[py, px] = dist;
                elevations[py, px] = cs.TargetElevation;
                owners[py, px] = cs.OwnerSplineId;
            }
        }
    }
    
    return (elevations, owners);
}
```

**Acceptance Criteria**:
- Correct ownership assignment at material boundaries
- Performance acceptable for 8192×8192 terrain

---

### Phase 5: Material Painting (1-2 AI agent sessions)

#### Step 5.1: Create MaterialPainter Service
**File**: `BeamNgTerrainPoc/Terrain/Services/MaterialPainter.cs`

**Task**: Paint material layers based on spline surface widths (separate from elevation):

```csharp
public class MaterialPainter
{
    /// <summary>
    /// Generates layer masks for each material based on spline ownership.
    /// Uses RoadSurfaceWidthMeters for painting (may differ from RoadWidthMeters).
    /// </summary>
    public Dictionary<string, byte[,]> PaintMaterials(
        UnifiedRoadNetwork network,
        int width, int height,
        float metersPerPixel)
    {
        var layers = new Dictionary<string, byte[,]>();
        
        // Initialize layer for each material
        var materialNames = network.Splines.Select(s => s.MaterialName).Distinct();
        foreach (var name in materialNames)
        {
            layers[name] = new byte[height, width];
        }
        
        // Paint each spline onto its material's layer
        foreach (var spline in network.Splines)
        {
            var layer = layers[spline.MaterialName];
            float surfaceHalfWidth = spline.Parameters.EffectiveRoadSurfaceWidthMeters / 2.0f;
            
            // Sample spline and paint road surface
            var samples = spline.Spline.SampleByDistance(spline.Parameters.CrossSectionIntervalMeters);
            foreach (var sample in samples)
            {
                PaintCrossSection(layer, sample, surfaceHalfWidth, metersPerPixel);
            }
        }
        
        return layers;
    }
}
```

**Acceptance Criteria**:
- Layer masks match existing output format
- `RoadSurfaceWidthMeters` respected when different from `RoadWidthMeters`
- Clean edges without aliasing artifacts

---

### Phase 6: Integration and Migration (2-3 AI agent sessions)

#### Step 6.1: Create UnifiedRoadSmoother Orchestrator
**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

**Task**: Top-level orchestrator that replaces `MultiMaterialRoadSmoother`:

```csharp
public class UnifiedRoadSmoother
{
    private readonly UnifiedRoadNetworkBuilder _networkBuilder;
    private readonly NetworkJunctionHarmonizer _harmonizer;
    private readonly UnifiedTerrainBlender _blender;
    private readonly MaterialPainter _painter;
    
    public UnifiedSmoothingResult SmoothAllRoads(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization)
    {
        // 1. Build unified network
        var network = _networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size);
        
        // 2. Calculate target elevations (per-spline, using spline's params)
        CalculateNetworkElevations(network, heightMap, metersPerPixel);
        
        // 3. Harmonize junctions (unified across all materials)
        if (enableCrossMaterialHarmonization)
        {
            _harmonizer.HarmonizeNetwork(network, heightMap, metersPerPixel);
        }
        
        // 4. Apply protected terrain blending (single pass)
        var smoothedHeightMap = _blender.BlendNetworkWithTerrain(heightMap, network, metersPerPixel);
        
        // 5. Paint material layers (uses surface width, not elevation width)
        var materialLayers = _painter.PaintMaterials(network, size, size, metersPerPixel);
        
        return new UnifiedSmoothingResult
        {
            ModifiedHeightMap = smoothedHeightMap,
            MaterialLayers = materialLayers,
            Network = network,
            Statistics = CalculateStatistics(heightMap, smoothedHeightMap)
        };
    }
}
```

**Acceptance Criteria**:
- Drop-in replacement for `MultiMaterialRoadSmoother.SmoothAllRoads()`
- Backward compatible result format
- Remove old code, no backward compatibility needed

#### Step 6.2: Update GenerateTerrain.razor Integration
**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

**Task**: Wire up the new `UnifiedRoadSmoother`:
- Update progress reporting for new phases
- Handle new `UnifiedSmoothingResult` type

**Acceptance Criteria**:
- UI shows network building progress
- Junction detection count displayed
- Material painting phase visible in logs

#### Step 6.3: Remove Legacy Path
**File**: `BeamNgTerrainPoc/Terrain/Services/MultiMaterialRoadSmoother.cs`

**Task**: Remove existing `MultiMaterialRoadSmoother`


---

### Phase 7: Testing and Validation (2-3 AI agent sessions)

#### Step 7.1: Create Test Scenarios
**Task**: Document test cases with expected outcomes:

| Scenario | Input | Expected Result |
|----------|-------|-----------------|
| Highway + Dirt T-Junction | Dirt road ends at highway side | Smooth ramp on dirt, highway unchanged |
| Same-Priority Y-Junction | Two identical roads merge | Weighted average elevation |
| Complex Roundabout | 4+ roads meeting | All roads converge smoothly |
| Isolated Endpoint | Road ends in field | Taper to terrain elevation |
| Overlapping Blend Zones | Two roads 10m apart | Each blends independently |

#### Step 7.2: Create Debug Visualization
**Task**: Export comprehensive debug images:
- Combined road network skeleton
- Junction classifications with color coding
- Per-spline ownership map
- Before/after elevation comparison

---

## 4. Questions Requiring Clarification

### 4.1 Priority Configuration

**Q1**: Should road priority be:
- (a) Automatically derived from `RoadWidthMeters`
- (b) Explicitly configurable per-material in the UI
- (c) Based on OSM road classification when using OSM source
- (d) Combination of the above

**Context**: Priority determines which road "wins" at junctions. OSM data includes road classifications (motorway > primary > residential) that could inform this.

**A1**:
if we have OSM roads we take the classification as priority. if not we take RoadWidthMeters, if the same we take the material priority order (given by sorting of the materials in the ui).
OSM road types:
Motorways & Trunk: motorway, trunk, motorway_link, trunk_link (major highways/ramps).
Primary & Secondary: primary, secondary, primary_link, secondary_link (main roads).
Tertiary & Local: tertiary, tertiary_link, residential, unclassified, service, living_street (local & urban roads).
Paths/Tracks: path, footway, cycleway, track, steps, bridleway, pedestrian (footpaths, trails, farm tracks).
Special: busway, raceway, proposed. 

### 4.2 Blend Zone Overlap Resolution

**Q2**: When two splines have overlapping blend zones but different blend ranges, how should the overlap be resolved?
- (a) Nearest spline wins
- (b) Higher-priority spline wins
- (c) Smooth interpolation between both
- (d) Use maximum blend value (most aggressive blending)

**Context**: Highway with 12m blend and dirt road with 6m blend, roads are 15m apart. Some pixels are in both blend zones.

**A2**:
(c) Smooth interpolation between both

### 4.3 Cross-Material Junction Detection Threshold

**Q3**: Should `JunctionDetectionRadiusMeters` be:
- (a) A global setting (current: per-material)
- (b) Per-material, using the maximum of connected materials
- (c) Calculated dynamically based on road widths at the junction
- (d) Configurable globally with per-material override

**Context**: Different road widths mean junctions have different physical sizes. A 10m detection radius may miss wide highway junctions but over-detect narrow trail crossings.

**A3**:
(d) Configurable globally with per-material override


### 4.4 Backward Compatibility

**Q4**: For existing presets and saved configurations:
- (a) Auto-migrate to new parameters
- (b) Maintain legacy path indefinitely
- (c) One-time migration with user confirmation
- (d) Version presets and support both

**Context**: Users may have carefully tuned presets that work with the current system. Migration could change their results.

**A4**:
(a) Auto-migrate to new parameters
We dont need any backward Compatibility for old parameters! We dont' want old and unnecessary code in our codebase! The result of course should be the coreect painted and road elevation smoothed terrain.

### 4.5 Performance Budget

**Q5**: What is the acceptable performance impact?
- (a) Must be faster than current (single EDT vs. multiple)
- (b) Up to 2x slower is acceptable for better quality
- (c) User-configurable quality/speed tradeoff
- (d) No performance regression allowed

**Context**: Single global EDT is faster, but combined network processing may add overhead. Need to set expectations.

**A5**
b) Up to 2x slower is acceptable for better quality. But we must take care the we don't get memory leaks and perhaps get a concept for memory consumption 
if trying to smooth a terrain 0f 30.000 x 30.000 + pixels
(not too important right now)

### 4.6 Material Painting Behavior

**Q6**: When two splines from different materials overlap (e.g., road widening at intersection):
- (a) Higher-priority material wins
- (b) Last material in order wins (current behavior)
- (c) Blend materials at boundary
- (d) Error/warning to user

**Context**: Currently material order determines overlap winner. New system could use priority instead.

**A6**:
(b) Last material in order wins (current behavior)
This is the BeamNG default behaviour.

### 4.7 OSM vs. PNG Source Mixing

**Q7**: Should a single unified network allow mixing:
- (a) OSM splines and PNG-extracted splines together
- (b) Only one source type per generation
- (c) OSM for major roads, PNG for custom additions

**Context**: User might have OSM highways but want to add a custom racing circuit from PNG. Current system handles this implicitly by material.

**A7**:
 (a) OSM splines and PNG-extracted splines together. If possible.
---

## 5. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Performance regression | Medium | High | Single EDT pass should offset; benchmark early |
| Junction detection edge cases | High | Medium | Extensive testing with varied road networks |
| Backward compatibility breaks | Medium | High | Feature flag for gradual rollout |
| Memory pressure on large terrains | Low | Medium | Streaming/tiled processing if needed |
| Complex intersection artifacts | Medium | Medium | Fallback to weighted average |

---

## 6. Timeline Estimate

| Phase | Sessions | Estimated Time |
|-------|----------|----------------|
| 1: Data Structures | 2-3 | 1-2 days |
| 2: Network Builder | 2-3 | 1-2 days |
| 3: Junction Harmonizer | 3-4 | 2-3 days |
| 4: Protected Blender | 3-4 | 2-3 days |
| 5: Material Painter | 1-2 | 1 day |
| 6: Integration | 2-3 | 1-2 days |
| 7: Testing | 2-3 | 1-2 days |
| **Total** | **15-22** | **9-15 days** |

---

This documentation provides a complete roadmap for transforming the terrain generation pipeline from material-centric to network-centric processing, while preserving the user-facing configuration model. Each phase is designed to be independently executable by an AI agent with clear inputs, outputs, and acceptance criteria.