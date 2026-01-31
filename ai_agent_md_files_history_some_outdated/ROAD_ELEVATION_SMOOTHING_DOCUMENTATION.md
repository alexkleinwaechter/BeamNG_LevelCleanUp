# Road Elevation Smoothing Pipeline - Comprehensive Technical Documentation

## Overview

This document provides a complete technical reference for the road elevation smoothing pipeline used in the `BeamNgTerrainPoc` project. This pipeline creates smooth, driveable roads by modifying heightmap elevations along road corridors.

The pipeline supports two approaches:
1. **Spline-Based Approach** (Recommended) - Uses centerline extraction, spline interpolation, and Euclidean Distance Transform (EDT) for high-quality curved roads
2. **DirectMask Approach** - Grid-aligned sampling for complex intersections

This documentation focuses primarily on the **Spline-Based Approach** as it produces the smoothest results.

---

## Pipeline Architecture

```
???????????????????????????????????????????????????????????????????????????????
?                        ROAD ELEVATION SMOOTHING PIPELINE                    ?
???????????????????????????????????????????????????????????????????????????????
?                                                                             ?
?  1. INPUT                                                                   ?
?     ??? Road Layer Mask (byte[,]) OR Pre-built OSM Splines                 ?
?     ??? Original Heightmap (float[,])                                      ?
?     ??? RoadSmoothingParameters                                            ?
?                                                                             ?
?  2. CENTERLINE EXTRACTION (SkeletonizationRoadExtractor)                   ?
?     ??? Binary mask conversion (threshold > 128)                           ?
?     ??? Optional dilation (SkeletonDilationRadius: 0-5px)                  ?
?     ??? Zhang-Suen thinning algorithm                                      ?
?     ??? Spur pruning (MinPathLengthPixels threshold)                       ?
?     ??? Keypoint detection (endpoints + junctions)                         ?
?     ??? Path extraction with junction awareness                            ?
?     ??? Path joining (BridgeEndpointMaxDistancePixels)                     ?
?     ??? Short path filtering                                               ?
?     ??? Path densification (DensifyMaxSpacingPixels)                       ?
?                                                                             ?
?  3. SPLINE CREATION (RoadSpline + MedialAxisRoadExtractor)                 ?
?     ??? Convert pixel coordinates to world coordinates (meters)            ?
?     ??? Create Akima/Natural/Linear spline interpolation                   ?
?     ??? Filter paths with < 10 estimated cross-sections                    ?
?     ??? Merge broken curves (MergeBrokenCurves)                            ?
?                                                                             ?
?  4. CROSS-SECTION GENERATION                                               ?
?     ??? Sample spline at CrossSectionIntervalMeters                        ?
?     ??? For each sample point:                                             ?
?     ?   ??? Position (on centerline)                                       ?
?     ?   ??? Tangent (road direction, normalized)                           ?
?     ?   ??? Normal (perpendicular to road, normalized)                     ?
?     ?   ??? PathId (which spline path)                                     ?
?     ?   ??? LocalIndex (position along path)                               ?
?     ??? Store in RoadGeometry.CrossSections list                           ?
?                                                                             ?
?  5. ELEVATION CALCULATION (OptimizedElevationSmoother)                     ?
?     ??? Group cross-sections by PathId                                     ?
?     ??? Sample terrain elevation at each cross-section center              ?
?     ??? Handle invalid elevations (NaN, Infinity, < -1000m)                ?
?     ??? Apply longitudinal smoothing filter:                               ?
?     ?   ??? Box Filter (prefix-sum, O(N)) OR                               ?
?     ?   ??? Butterworth Low-Pass Filter (zero-phase, order 1-8)            ?
?     ??? Apply GlobalLevelingStrength (blend toward network average)        ?
?     ??? Enforce RoadMaxSlopeDegrees constraint (if enabled)                ?
?                                                                             ?
?  6. JUNCTION HARMONIZATION (JunctionElevationHarmonizer)                   ?
?     ??? Find path endpoints (first/last cross-section per path)            ?
?     ??? Detect junction clusters:                                          ?
?     ?   ??? Multi-path junctions (Y, X intersections)                      ?
?     ?   ??? T-junctions (endpoint meets middle of another road)            ?
?     ??? Compute harmonized elevations:                                     ?
?     ?   ??? T-junctions: continuous road "wins"                            ?
?     ?   ??? Multi-endpoint: weighted average by distance                   ?
?     ??? Propagate junction constraints along side roads                    ?
?     ?   ??? Cosine blend over JunctionBlendDistanceMeters                  ?
?     ??? Apply endpoint tapering (isolated endpoints blend to terrain)      ?
?                                                                             ?
?  7. TERRAIN BLENDING (DistanceFieldTerrainBlender)                         ?
?     ??? Build road core mask (rasterize cross-section lines)               ?
?     ??? Compute Euclidean Distance Transform (Felzenszwalb algorithm)      ?
?     ??? Build elevation map (interpolate from nearest cross-sections)      ?
?     ??? Apply distance-based blending:                                     ?
?     ?   ??? Road core (d ? RoadWidthMeters/2): fully flattened             ?
?     ?   ??? Shoulder zone: blend using selected function                   ?
?     ??? Blend functions: Linear, Cosine, Cubic, Quintic                    ?
?                                                                             ?
?  8. POST-PROCESSING (Optional)                                             ?
?     ??? Build smoothing mask (road + extension zone)                       ?
?     ??? Apply filter (Gaussian, Box, or Bilateral)                         ?
?                                                                             ?
?  9. OUTPUT                                                                  ?
?     ??? Modified heightmap (float[,])                                      ?
?     ??? Delta map (changes from original)                                  ?
?     ??? SmoothingStatistics                                                ?
?     ??? RoadGeometry (for debugging/export)                                ?
?                                                                             ?
???????????????????????????????????????????????????????????????????????????????
```

---

## Detailed Component Documentation

### 1. Centerline Extraction (`SkeletonizationRoadExtractor`)

The skeleton extraction converts a road mask into 1-pixel-wide centerlines.

#### 1.1 Binary Mask Conversion
```csharp
// Convert byte mask to boolean (threshold at 128)
b[y, x] = roadMask[y, x] > 128;
```

#### 1.2 Optional Dilation
- **Purpose**: Bridge small gaps and improve connectivity
- **Parameter**: `SkeletonDilationRadius` (0-5 pixels, default: 1)
- **Warning**: Higher values cause "tail artifacts" at hairpin curves
- **Recommendation**: Use 0-1 for clean skeletons

#### 1.3 Zhang-Suen Thinning Algorithm
Iterative morphological thinning that removes boundary pixels while preserving topology:

1. Two sub-iterations per pass (different neighbor conditions)
2. Pixel removed if:
   - Has 2-6 black neighbors
   - Has exactly 1 transition (0?1) in circular neighborhood
   - Satisfies sub-iteration-specific conditions
3. Repeats until no changes (up to 100 iterations)

**Performance**: Can take several seconds for large terrains (4096x4096)

#### 1.4 Spur Pruning
Removes short dead-end branches (noise artifacts):
- **Parameter**: `MinPathLengthPixels` (default: 20)
- Iteratively removes endpoint pixels up to max spur length
- Critical for preventing isolated spikes in final terrain

#### 1.5 Keypoint Detection
Classifies skeleton pixels:
- **Endpoints**: 1 neighbor (road terminates here)
- **Junctions**: ?3 neighbors AND ?3 transitions (intersection)

```csharp
// Count 8-connected neighbors
neighbors = CountNeighbors(skeleton, x, y);

// Count 0?1 transitions in circular neighborhood (junction criterion)
transitions = CountTransitions(skeleton, x, y);
```

#### 1.6 Path Extraction
Walks skeleton from keypoints to extract ordered paths:
- Starts from all skeleton pixels
- Junction-aware: prefers straight-through paths (`PreferStraightThroughJunctions`)
- **Angle threshold**: `JunctionAngleThreshold` (default: 45�)
- Tracks walked arms to avoid duplicate extraction

#### 1.7 Path Joining
Connects paths with close endpoints:
- **Parameter**: `BridgeEndpointMaxDistancePixels` (default: 30-40)
- Tests all 4 endpoint pairings (start-start, start-end, end-start, end-end)
- Reverses paths as needed for proper connection

#### 1.8 Path Densification
Ensures sufficient control points for smooth splines:
- **Parameter**: `DensifyMaxSpacingPixels` (default: 2.0)
- Linearly interpolates between points with gaps larger than threshold

---

### 2. Spline Creation (`RoadSpline`)

Converts discrete centerline points into smooth parametric curves.

#### 2.1 Arc-Length Parameterization
```csharp
// Calculate cumulative arc lengths for parameter t
_distances = [0, dist(p0,p1), dist(p0,p1)+dist(p1,p2), ...]
_totalLength = sum of all segment lengths
```

#### 2.2 Interpolation Method Selection
- **?5 points**: Akima spline (avoids overshoot, recommended)
- **3-4 points**: Natural cubic spline
- **2 points**: Linear interpolation

#### 2.3 Spline Sampling
```csharp
// Sample at regular distance intervals
for (distance = 0; distance <= totalLength; distance += intervalMeters)
{
    sample = {
        Position: GetPointAtDistance(distance),
        Tangent: GetTangentAtDistance(distance),   // Road direction
        Normal: GetNormalAtDistance(distance)      // Perpendicular (rotate tangent 90�)
    };
}
```

#### 2.4 Tangent and Normal Calculation
```csharp
// Tangent = derivative of spline at distance
tangent = Normalize(splineX.Differentiate(d), splineY.Differentiate(d))

// Normal = rotate tangent 90� counterclockwise: (x, y) ? (-y, x)
normal = Vector2(-tangent.Y, tangent.X)
```

---

### 3. Cross-Section Generation

Cross-sections are perpendicular slices across the road at regular intervals.

#### 3.1 CrossSection Structure
```csharp
public class CrossSection
{
    Vector2 CenterPoint;      // World position on centerline (meters)
    Vector2 TangentDirection; // Unit vector along road
    Vector2 NormalDirection;  // Unit vector perpendicular to road
    float TargetElevation;    // Calculated target height (initialized to NaN)
    float WidthMeters;        // Road width at this point
    int Index;                // Global index
    int PathId;               // Which spline path
    int LocalIndex;           // Position along path
    bool IsExcluded;          // Skip this cross-section
}
```

#### 3.2 Key Parameters
- **CrossSectionIntervalMeters** (default: 0.5m): Distance between samples
  - **Critical**: Must be ? (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3 to avoid gaps!
  - Auto-adjusted if too large

#### 3.3 Path Filtering
- Paths generating < 10 cross-sections are skipped
- Prevents isolated spikes from short skeleton fragments

---

### 4. Elevation Calculation (`OptimizedElevationSmoother`)

**This is the core smoothing step that determines road surface elevation.**

#### 4.1 Terrain Sampling
```csharp
// Sample heightmap at cross-section center
px = (int)(cs.CenterPoint.X / metersPerPixel);
py = (int)(cs.CenterPoint.Y / metersPerPixel);
rawElevation = heightMap[py, px];
```

**Invalid elevation handling**:
- NaN, Infinity, or < -1000m are rejected
- Replaced with previous valid value or neighbor interpolation

#### 4.2 Longitudinal Smoothing

##### Box Filter (Prefix-Sum, O(N))
```csharp
// Build prefix sum array
prefixSum[i] = prefixSum[i-1] + input[i-1]

// Compute window average in O(1)
avg = (prefixSum[right+1] - prefixSum[left]) / count
```
- **Parameter**: `SmoothingWindowSize` (default: 101 cross-sections)
- Simple, fast, but may allow more variation

##### Butterworth Low-Pass Filter (Recommended)
- **Parameter**: `UseButterworthFilter` (default: true)
- **Parameter**: `ButterworthFilterOrder` (default: 3, range 1-8)
- Maximally flat passband = smoothest possible roads
- Zero-phase implementation (forward-backward filtering) eliminates phase shift

```csharp
// Cutoff frequency derived from window size
cutoffNormalized = 2.0f / windowSize

// Apply cascaded biquad sections with forward-backward filtering
for each section:
    result = ApplyBiquadSectionZeroPhase(result, coefficients)
```

#### 4.3 Global Leveling (Optional)
- **Parameter**: `GlobalLevelingStrength` (default: 0.0, range 0-1)
- Blends all road elevations toward network average
- **WARNING**: Values > 0.5 require wider `TerrainAffectedRangeMeters` (20m+) to prevent "dotted roads"

```csharp
smoothed[i] = smoothed[i] * (1 - strength) + globalAverage * strength
```

#### 4.4 Max Slope Constraint (Optional)
- **Parameter**: `EnableMaxSlopeConstraint` (default: false)
- **Parameter**: `RoadMaxSlopeDegrees` (default: 4�)
- Iterative forward-backward passes limit maximum grade:

```csharp
maxRise = tan(maxSlopeDegrees) * crossSectionSpacing

// Forward pass: limit uphill
if (elevation[i] > elevation[i-1] + maxRise)
    elevation[i] = elevation[i-1] + maxRise

// Backward pass: limit downhill
if (elevation[i] > elevation[i+1] + maxRise)
    elevation[i] = elevation[i+1] + maxRise
```

---

### 5. Junction Harmonization (`JunctionElevationHarmonizer`)

**Eliminates elevation discontinuities where roads meet.**

#### 5.1 Problem Solved
- Each path is smoothed independently ? elevation jumps at intersections
- Spline endpoints can have abrupt drops ("road drops off into terrain")

#### 5.2 Junction Detection

##### Multi-Path Junctions (Y, X intersections)
- Cluster endpoints within `JunctionDetectionRadiusMeters` (default: 10m)
- Uses transitive closure (if A near B and B near C, all in same cluster)

##### T-Junctions
- Endpoint meets the **middle** of another path (not just other endpoints)
- Check if any isolated endpoint is near ANY cross-section of different paths

```csharp
// For each isolated endpoint:
foreach (var cs in allCrossSections)
{
    if (cs.PathId == endpoint.PathId) continue;  // Skip same path
    
    float dist = Vector2.Distance(endpoint.CenterPoint, cs.CenterPoint);
    if (dist <= radiusMeters)
    {
        // T-junction detected! Add cs to junction cluster
        junction.CrossSections.Add(cs);
        junction.IsIsolatedEndpoint = false;
    }
}
```

#### 5.3 Harmonized Elevation Calculation

##### T-Junctions
- **Continuous road "wins"** - side road adopts main road's elevation
- Prevents the main through-road from being distorted by side roads

##### Multi-Endpoint Junctions
- Weighted average by inverse distance from junction center:
```csharp
weight = 1.0f / (distance + 0.1f)
harmonizedElevation = sum(elevation[i] * weight[i]) / sum(weight[i])
```

##### Isolated Endpoints
- Slight blend toward terrain elevation
- **Parameter**: `EndpointTerrainBlendStrength` (default: 0.3)

#### 5.4 Constraint Propagation

Side roads blend from junction elevation back to their original calculated elevation:

```csharp
// Only propagate along the side road (the one ending at junction)
// The continuous road keeps its original elevation

for each cross-section in path:
    if (distance >= blendDistance) continue
    
    // Cosine blend: 0 at junction, 1 at blend distance
    t = distance / blendDistance
    blend = 0.5 - 0.5 * cos(PI * t)
    
    // Blend junction elevation with original
    elevation = junction.HarmonizedElevation * (1 - blend) + original * blend
```

- **Parameter**: `JunctionBlendDistanceMeters` (default: 30m)

#### 5.5 Endpoint Tapering

For truly isolated endpoints (dead ends), blend back toward terrain:

```csharp
// Quintic smoothstep for ultra-smooth taper
t = distance / taperDistance
blend = t� * (t * (t * 6 - 15) + 10)

elevation = targetAtEndpoint * (1 - blend) + original * blend
```

- **Parameter**: `EndpointTaperDistanceMeters` (default: 25m)

---

### 5.6 Junction Surface Constraints (T-Junctions)

For T-junctions where a road terminates at a higher-priority road, the terminating road's 
edge elevations must match the primary road's surface at the connection point. This prevents
"step" artifacts when the primary road is sloped or banked.

#### Constraint Fields
```csharp
public class UnifiedCrossSection
{
    // ... existing fields ...
    
    /// <summary>
    /// Constrained elevation for the left edge at junction cross-sections.
    /// When set, this overrides the banking-calculated edge elevation.
    /// </summary>
    public float? ConstrainedLeftEdgeElevation { get; set; }
    
    /// <summary>
    /// Constrained elevation for the right edge at junction cross-sections.
    /// When set, this overrides the banking-calculated edge elevation.
    /// </summary>
    public float? ConstrainedRightEdgeElevation { get; set; }
    
    /// <summary>
    /// True if this cross-section has junction surface constraints applied.
    /// </summary>
    public bool HasJunctionConstraint => 
        ConstrainedLeftEdgeElevation.HasValue || ConstrainedRightEdgeElevation.HasValue;
}
```

#### How Constraints Work
1. **Surface Calculation**: At T-junctions, `ComputeTJunctionElevation()` calculates the exact
   surface elevation at the connection point, accounting for both banking (lateral tilt) and
   longitudinal slope of the primary road.

2. **Edge Constraint Application**: `JunctionSurfaceCalculator.ApplyEdgeConstraints()` sets
   `ConstrainedLeftEdgeElevation` and `ConstrainedRightEdgeElevation` on the terminating
   road's junction cross-section.

3. **Constraint Propagation**: Constraints are interpolated along the terminating road with
   distance-based falloff, creating a smooth transition from constrained to unconstrained edges.

4. **Terrain Blending**: `BankedTerrainHelper.GetEdgeElevation()` checks constraint fields
   FIRST before returning banking-calculated values, ensuring constraints take priority.

#### Why Not Modify BankAngleRadians?
An earlier approach (JunctionSlopeAdapter) tried to match slopes by modifying `BankAngleRadians`,
but this was fundamentally flawed because:
- `BankAngleRadians` represents **lateral tilt** (roll) for curves, not longitudinal slope (pitch)
- Modifying it corrupted banking data and created incorrect terrain
- The correct solution is to **constrain edge elevations directly** at the surface where roads meet

---

### 6. Terrain Blending (`DistanceFieldTerrainBlender`)

**High-performance terrain modification using Euclidean Distance Transform.**

#### 6.1 Road Core Mask Construction
```csharp
// Rasterize cross-section lines (Bresenham)
foreach cross-section:
    left = center - normal * (roadWidth / 2)
    right = center + normal * (roadWidth / 2)
    DrawLine(mask, left, right)
```

#### 6.2 Euclidean Distance Transform (Felzenszwalb & Huttenlocher)
- **Complexity**: O(W � H) - linear in terrain size
- **Performance**: ~0.3s for 4096�4096 terrain
- Returns exact Euclidean distance to nearest road pixel

```csharp
// 1D EDT per row, then per column
// Uses parabola envelope algorithm
// Output: distance[y,x] = distance to nearest road pixel in meters
```

#### 6.3 Elevation Map Construction
```csharp
// For each pixel in road corridor:
worldPos = pixel * metersPerPixel
nearest = FindNearestCrossSection(worldPos)  // Spatial index lookup

if (distance(worldPos, nearest) <= roadHalfWidth + blendRange)
    elevationMap[y,x] = nearest.TargetElevation
```

**Spatial Index**:
- Grid-based (32-pixel cells)
- 3�3 cell search for nearest neighbor
- **Critical**: Filters cross-sections with invalid TargetElevation (NaN, < -1000m)

#### 6.4 Distance-Based Blending

```csharp
foreach pixel:
    d = distanceField[y, x]
    
    if (d > maxDist) continue  // Outside influence zone
    
    if (d <= roadHalfWidth)
        // Road core - fully flattened to target elevation
        newHeight = targetElevation
    else
        // Shoulder zone - blend between road and terrain
        t = (d - roadHalfWidth) / blendRange
        blend = ApplyBlendFunction(t, blendType)
        newHeight = targetElevation * (1 - blend) + originalHeight * blend
```

#### 6.5 Blend Functions

| Function | Formula | Character |
|----------|---------|-----------|
| Linear | `t` | Visible transition points |
| Cosine | `0.5 - 0.5 * cos(? * t)` | Smooth S-curve (default) |
| Cubic | `t� * (3 - 2t)` | Very smooth, zero 1st derivative at ends |
| Quintic | `t� * (t * (6t - 15) + 10)` | Ultra-smooth, zero 1st & 2nd derivatives |

---

### 7. Post-Processing (Optional)

Eliminates staircase artifacts from cross-section intervals.

#### 7.1 Smoothing Mask
```csharp
// Smooth road core + extension zone
maxSmoothingDist = roadWidthMeters/2 + SmoothingMaskExtensionMeters
mask[y,x] = (distanceField[y,x] <= maxSmoothingDist)
```

#### 7.2 Filter Types

##### Gaussian Blur
- **Parameter**: `SmoothingKernelSize` (default: 7, odd number)
- **Parameter**: `SmoothingSigma` (default: 1.5)
- Best quality, uses separable 2D Gaussian kernel

##### Box Blur
- Simple averaging within kernel
- Faster but less smooth

##### Bilateral Filter
- Edge-preserving smoothing
- Considers both spatial distance and elevation difference
- Best for preserving sharp road edges while smoothing surface

#### 7.3 Multiple Iterations
- **Parameter**: `SmoothingIterations` (default: 1)
- More iterations = smoother result

---

## Key Parameters Reference

### Critical Parameters for Road Smoothness

| Parameter | Default | Impact on Smoothness |
|-----------|---------|---------------------|
| `CrossSectionIntervalMeters` | 0.5 | **Lower = smoother** (more samples) |
| `SmoothingWindowSize` | 101 | **Higher = smoother** (more averaging) |
| `UseButterworthFilter` | true | **Enable for smoothest roads** |
| `ButterworthFilterOrder` | 3 | 3-4 = aggressive smoothing |
| `RoadWidthMeters` | 8.0 | Width of flat road core |
| `TerrainAffectedRangeMeters` | 12.0 | Width of blend zone (shoulder) |
| `BlendFunctionType` | Cosine | Cosine/Quintic for smooth transitions |
| `EnablePostProcessingSmoothing` | false | Enable to remove staircase artifacts |

### Junction/Endpoint Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `EnableJunctionHarmonization` | true | Smooth junctions between roads |
| `JunctionDetectionRadiusMeters` | 10.0 | How close endpoints must be |
| `JunctionBlendDistanceMeters` | 30.0 | How far junction influence extends |
| `EnableEndpointTaper` | true | Blend dead ends to terrain |
| `EndpointTaperDistanceMeters` | 25.0 | Taper distance at dead ends |
| `EndpointTerrainBlendStrength` | 0.3 | How much to blend toward terrain |

### Skeleton Extraction Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `SkeletonDilationRadius` | 1 | Expand mask before thinning (0-5) |
| `MinPathLengthPixels` | 20.0 | Filter short paths/fragments |
| `BridgeEndpointMaxDistancePixels` | 30.0 | Join close path endpoints |
| `DensifyMaxSpacingPixels` | 2.0 | Max gap between control points |
| `SimplifyTolerancePixels` | 0.5 | Path simplification (0 = none) |

---

## Common Issues and Solutions

### Issue: Roads appear "dotted" or disconnected
**Causes**:
1. `CrossSectionIntervalMeters` too large
2. `GlobalLevelingStrength` > 0.5 with small `TerrainAffectedRangeMeters`

**Solution**:
- Ensure `CrossSectionIntervalMeters` ? (RoadWidth/2 + BlendRange) / 3
- If using GlobalLeveling > 0.5, increase `TerrainAffectedRangeMeters` to 20m+

### Issue: Elevation spikes/drops at road segments
**Causes**:
1. Invalid elevation samples (NaN, 0, negative)
2. Cross-sections with uninitialized TargetElevation
3. Short skeleton fragments

**Solution**:
- The code handles invalid samples via interpolation
- Spatial index filters invalid cross-sections
- Increase `MinPathLengthPixels` to filter short fragments

### Issue: Visible staircase pattern on road surface
**Cause**: Cross-section interval visible as elevation steps

**Solution**:
- Enable `EnablePostProcessingSmoothing`
- Use Gaussian or Bilateral filter
- Increase `SmoothingKernelSize` (7-15)

### Issue: Roads don't follow terrain on hills
**Cause**: Too much longitudinal smoothing

**Solution**:
- Reduce `SmoothingWindowSize`
- Disable or reduce `GlobalLevelingStrength`
- Use Butterworth filter (preserves terrain-following better)

### Issue: Elevation jumps at intersections
**Cause**: Junction harmonization not working properly

**Solution**:
- Ensure `EnableJunctionHarmonization` is true
- Increase `JunctionDetectionRadiusMeters` if roads are wide
- Check T-junction detection (endpoints meeting mid-road)

### Issue: Roads "float" above terrain at dead ends
**Cause**: No endpoint tapering

**Solution**:
- Enable `EnableEndpointTaper`
- Increase `EndpointTaperDistanceMeters`
- Increase `EndpointTerrainBlendStrength` (0.3-0.5)

---

## Multi-Material Support

The `MultiMaterialRoadSmoother` handles multiple road types (highways, local roads) with cross-material junction harmonization.

### Processing Phases

1. **Phase 1**: Extract geometry and calculate elevations for ALL materials
2. **Phase 2**: Detect and harmonize cross-material junctions
3. **Phase 3**: Apply terrain blending for all materials

### Cross-Material Junction Detection
- Collects endpoints from all materials
- Clusters endpoints from DIFFERENT materials within detection radius
- Only creates junction if endpoints from multiple materials are present

### Cross-Material Harmonization
```csharp
// Calculate harmonized elevation (weighted average)
foreach endpoint in junction:
    weight = 1 / (distance + 0.1)
    weightedSum += elevation * weight
harmonizedElevation = weightedSum / totalWeight

// Propagate along each contributing path
foreach path:
    for each cross-section within blendDistance:
        t = distance / blendDistance
        blend = 0.5 - 0.5 * cos(PI * t)
        elevation = harmonized * (1 - blend) + original * blend
```

---

## Performance Characteristics

| Operation | Complexity | Typical Time (4096�4096) |
|-----------|------------|--------------------------|
| Zhang-Suen Thinning | O(iterations � pixels) | 2-5 seconds |
| Distance Transform | O(W � H) | ~0.3 seconds |
| Elevation Smoothing | O(N) per path | < 100ms |
| Terrain Blending | O(W � H) | ~0.5 seconds |
| **Total** | | **3-6 seconds** |

Memory usage scales with terrain size: ~50MB per 4096�4096 float array.

---

## Debug Output Options

| Parameter | Output File | Shows |
|-----------|-------------|-------|
| `ExportSkeletonDebugImage` | skeleton_debug.png | Road mask, skeleton, extracted paths |
| `ExportSplineDebugImage` | spline_debug.png | Centerline, road width, cross-sections |
| `ExportSmoothedElevationDebugImage` | smoothed_elevation_debug.png | Color-coded elevations |
| `ExportJunctionDebugImage` | junction_harmonization_debug.png | Junctions, endpoints, elevation changes |
| `ExportSmoothedHeightmapWithOutlines` | smoothed_heightmap_with_road_outlines.png | Final heightmap with road outlines |

---

## Summary: What Makes Roads Smooth

1. **Adequate cross-section density**: `CrossSectionIntervalMeters` ? 1.0m
2. **Aggressive longitudinal smoothing**: Butterworth filter, order 3-4, window 101+
3. **Wide blend zones**: `TerrainAffectedRangeMeters` ? 12m
4. **Smooth blend function**: Cosine or Quintic
5. **Junction harmonization**: Eliminates discontinuities at intersections
6. **Endpoint tapering**: Smooth transitions at dead ends
7. **Post-processing** (optional): Gaussian blur to eliminate staircase artifacts
8. **Clean skeleton extraction**: Proper spur pruning and path filtering
