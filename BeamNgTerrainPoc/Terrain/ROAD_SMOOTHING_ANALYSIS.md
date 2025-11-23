# Road Smoothing Algorithm - Analysis & Path Forward

## Project Overview
**Project:** BeamNgTerrainPoc  
**Framework:** .NET 9, C# 13.0  
**Feature Branch:** feature/heightmap_road_smoothing  
**Goal:** Modify terrain heightmaps to create smooth, level road surfaces that blend seamlessly with surrounding terrain

---

## Current Implementation Status

### ? What's Working
1. **Direct Road Mask Approach** - Successfully implemented (Option A)
2. **Performance** - Fast processing (~30-45 seconds for 4K heightmap)
3. **No Circular Artifacts** - Fixed the moon crater pattern issue
4. **Basic Road Smoothing** - Roads are being smoothed and blended with terrain
5. **Orientation Fixed** - Heightmap Y-axis flipping corrected
6. **Memory Efficient** - No excessive cross-section generation

### ? Current Problem: Non-Horizontal Road Surface on Curves

**User Observation:**
> "The road surface isn't horizontal when it's a bit curved and windy"

**What This Means:**
- Roads are following terrain longitudinally (up/down slopes) ?
- BUT roads are NOT level side-to-side (transverse slope) ?
- On curves, the road surface has unwanted banking/tilt
- Should be flat/level across the width of the road

---

## Root Cause Analysis

### Why Current Approach Fails on Curves

The **Direct Road Mask Approach** (current implementation):

```csharp
// Current method in CalculateLocalRoadElevation()
// Samples in CROSS pattern (horizontal + vertical only)
for (int dx = -sampleRadius; dx <= sampleRadius; dx++)
    if (roadMask[y, sx] > 128)
        sum += heightMap[y, sx];  // Horizontal sampling

for (int dy = -sampleRadius; dy <= sampleRadius; dy++)
    if (roadMask[sy, x] > 128)
        sum += heightMap[sy, x];  // Vertical sampling
```

**Problem:**
1. Samples along **grid-aligned axes** (horizontal/vertical)
2. Does NOT know the **actual road direction**
3. On curves, this creates **diagonal bias** ? unwanted tilt

**Example:**
```
Straight road (works):        Curved road (fails):
   ?                             ?
   ?  ? samples across          ?  ? samples at wrong angle
   ?                           ?    ? creates tilt!
```

---

## Why Spline-Based Approach Would Work

### The Key Insight: Road Direction Awareness

A spline-based approach would:

1. **Know the road direction** at every point
2. **Calculate perpendicular cross-sections** accurately
3. **Sample along the actual road width** (not grid-aligned)
4. **Create level surfaces** across the road at any angle

### Spline-Based Algorithm Overview

```
1. Extract Road Centerline
   ?
2. Fit Spline to Centerline Points
   ?
3. Sample Points Along Spline (every 2-5 meters)
   ?
4. For Each Point:
   - Calculate tangent direction (road direction)
   - Calculate normal direction (perpendicular)
   - Create cross-section perpendicular to road
   - Sample elevations across cross-section
   - Average to get road elevation
   ?
5. Interpolate elevations along spline
   ?
6. Apply to heightmap with blending
```

### Why This Fixes the Problem

**Cross-section perpendicular to road:**
```
                ???????????
Terrain ?   ?????  ROAD   ?????  ? Terrain
                ???????????
                     ?
              True perpendicular
           (accounts for curve)
```

**Current approach (grid-aligned):**
```
                ???????????
Terrain ?   ?????    ?    ?????  ? Wrong angle!
                ???????????
                   ?
              Road curves but
           samples stay grid-aligned
```

---

## Implementation Approaches Compared

### Option A: Direct Road Mask (CURRENT)
**Status:** ? Implemented  
**Pros:**
- Fast and simple
- No centerline extraction needed
- Handles any road shape
- Good for straight roads

**Cons:**
- ? No road direction awareness
- ? Creates tilt on curves
- ? Can't create truly level cross-sections

**Use Case:** Straight roads, simple terrain

---

### Option B: Simplified Centerline + Cross-Sections (ATTEMPTED)
**Status:** ?? Attempted, had issues  
**What Went Wrong:**
- Generated 130,000 cross-sections (way too many!)
- Circular artifacts from discrete cross-sections
- Distance transform created too many centerline candidates
- OrderCenterlinePixels created disconnected segments

**Why It Failed:**
- Wrong sampling strategy (every pixel as potential centerline)
- No path simplification before cross-section generation
- Greedy nearest-neighbor doesn't work for networks

---

### Option C: Spline-Based Approach (RECOMMENDED NEXT)
**Status:** ?? Not yet implemented - BEST SOLUTION  

**How It Would Work:**

#### Step 1: Extract Sparse Centerline
```csharp
// Sample road pixels at large intervals (64-128 pixels)
// Find centerline candidates in each region
// Connect into path(s)
// Result: ~500-2000 initial points for 4K map
```

#### Step 2: Fit Spline
```csharp
// Use Catmull-Rom spline or cubic B-spline
// Smooth path through centerline points
// Result: Continuous, differentiable curve
```

#### Step 3: Sample Along Spline
```csharp
// Sample at regular intervals (every 2-5 meters in world space)
// Calculate tangent (road direction) at each point
// Calculate normal (perpendicular) at each point
// Result: ~1000-3000 oriented cross-sections for 4K map
```

#### Step 4: Calculate Elevations
```csharp
// For each cross-section:
//   - Sample heights perpendicular to road
//   - Average across road width
//   - Ensure level (same height) side-to-side
// Apply longitudinal slope constraints
```

#### Step 5: Blend with Terrain
```csharp
// For each heightmap pixel:
//   - Find nearest spline point
//   - Get road elevation at that point
//   - Blend based on distance from road
```

**Pros:**
- ? True cross-sectional leveling
- ? Handles curves correctly
- ? Direction-aware sampling
- ? Smooth longitudinal profiles
- ? Professional road engineering approach

**Cons:**
- More complex to implement
- Needs spline library or custom implementation
- More computational cost (but still acceptable)

**Expected Performance:**
- Spline fitting: ~1-2 seconds
- Cross-section generation: ~2-3 seconds
- Elevation calculation: ~5-10 seconds
- Blending: ~20-30 seconds
- **Total: ~30-45 seconds** (same as current!)

---

## Spline Implementation Details

### Libraries Available in .NET

1. **MathNet.Numerics** (Recommended)
   ```csharp
   // NuGet: MathNet.Numerics
   using MathNet.Numerics.Interpolation;
   
   var spline = CubicSpline.InterpolatePchipSorted(x, y);
   // or
   var spline = CubicSpline.InterpolateNatural(x, y);
   ```

2. **Custom Catmull-Rom Spline**
   ```csharp
   // Simple to implement
   // Good for smooth curves through points
   // ~100 lines of code
   ```

### Recommended Spline Type

**Catmull-Rom Spline** - Best for our use case because:
- Passes through all control points (the centerline points)
- C1 continuous (smooth first derivative ? smooth tangents)
- Local control (moving one point affects nearby segments only)
- Easy to calculate tangent/normal vectors
- Good for road-like curves

---

## Proposed Implementation Plan

### Phase 1: Sparse Centerline Extraction (1-2 hours)
```csharp
// File: SimpleCenterlineExtractor.cs
public class SimpleCenterlineExtractor
{
    // Sample road mask at 64-128 pixel intervals
    // Find highest distance transform value in each region
    // Connect points with nearest-neighbor (distance < threshold)
    // Use connected components to handle road networks
}
```

### Phase 2: Spline Fitting (1-2 hours)
```csharp
// File: CatmullRomSpline.cs or use MathNet.Numerics
public class RoadSpline
{
    public Vector2 GetPoint(float t);           // Position at parameter t
    public Vector2 GetTangent(float t);         // Direction at t
    public Vector2 GetNormal(float t);          // Perpendicular at t
    public float GetTotalLength();              // Arc length
    public List<SplineSample> SampleByDistance(float interval);
}
```

### Phase 3: Cross-Sectional Sampling (2-3 hours)
```csharp
// File: SplineBasedHeightCalculator.cs
public class SplineBasedHeightCalculator
{
    public void CalculateTargetElevations(
        RoadGeometry geometry,      // Contains spline
        float[,] heightMap,
        RoadSmoothingParameters parameters)
    {
        // Sample spline at regular intervals
        // For each sample:
        //   - Calculate perpendicular cross-section
        //   - Sample heightmap along cross-section
        //   - Ensure level (average or median)
        //   - Store elevation
        // Apply longitudinal slope constraints
    }
}
```

### Phase 4: Integration (1 hour)
- Update RoadGeometry to include spline
- Update TerrainBlender to use spline-based elevations
- Test with curved roads

### Phase 5: Testing & Refinement (2-3 hours)
- Test with various road types (straight, curved, intersections)
- Tune parameters (sample interval, spline tension, etc.)
- Validate results

**Total Estimated Time: 7-12 hours**

---

## Alternative: Hybrid Approach

If full spline implementation is too complex, we could do a **hybrid**:

### Hybrid Option: Direction-Aware Sampling (Simpler)

```csharp
// For each road pixel:
// 1. Estimate local road direction from nearby road pixels
// 2. Calculate perpendicular direction
// 3. Sample along perpendicular (not grid-aligned!)
// 4. Average to get elevation

private Vector2 EstimateRoadDirection(byte[,] roadMask, int x, int y)
{
    // Find nearby road pixels
    // Fit line to nearby pixels (PCA or simple regression)
    // Return direction vector
}

private float CalculateLocalRoadElevation(...)
{
    Vector2 roadDir = EstimateRoadDirection(roadMask, x, y);
    Vector2 perpDir = new Vector2(-roadDir.Y, roadDir.X);
    
    // Sample along perpendicular direction (not grid axes!)
    float sum = 0;
    int count = 0;
    for (float t = -radius; t <= radius; t += 1.0f)
    {
        int sx = x + (int)(perpDir.X * t);
        int sy = y + (int)(perpDir.Y * t);
        if (InBounds(sx, sy) && roadMask[sy, sx] > 128)
        {
            sum += heightMap[sy, sx];
            count++;
        }
    }
    return count > 0 ? sum / count : heightMap[y, x];
}
```

**Pros:**
- Simpler than full spline approach
- Still direction-aware
- Might be "good enough"

**Cons:**
- Local direction estimation can be noisy
- Not as elegant as spline solution
- Still some artifacts possible

---

## Recommendation

### For Best Results: **Option C - Spline-Based Approach**

**Why:**
1. Solves the curve leveling problem properly
2. Professional engineering approach
3. Handles all road shapes correctly
4. Performance is acceptable
5. More maintainable (clear geometric meaning)

**Implementation Strategy:**
1. Use **Catmull-Rom spline** (simple to implement)
2. Sample at **2-5 meter intervals** (world space)
3. Generate **~1000-3000 cross-sections** (manageable)
4. Use existing TerrainBlender with spline-based elevations

### If Time is Limited: **Hybrid Direction-Aware Sampling**

Could be implemented in 2-3 hours and might be "good enough" for most cases.

---

## Next Steps

**Before coding, we should:**

1. ? Confirm user wants spline-based approach
2. ?? Decide on spline implementation:
   - Option A: Use MathNet.Numerics (add NuGet package)
   - Option B: Implement Catmull-Rom manually (~100 LOC)
3. ?? Confirm expected road behavior:
   - Level side-to-side on all curves? YES
   - Follow terrain longitudinally? YES (within slope limits)
   - Banking on curves? NO (or optional later?)

**Implementation Order:**
1. Create sparse centerline extractor
2. Implement/integrate spline
3. Generate cross-sections from spline
4. Calculate elevations with proper perpendiculars
5. Update TerrainBlender to use spline data
6. Test and refine

---

## Files to Create/Modify

**New Files:**
- `BeamNgTerrainPoc/Terrain/Algorithms/SparseCenterlineExtractor.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/CatmullRomSpline.cs` (or use MathNet)
- `BeamNgTerrainPoc/Terrain/Algorithms/SplineBasedHeightCalculator.cs`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`

**Modified Files:**
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadGeometry.cs` - add Spline property
- `BeamNgTerrainPoc/Terrain/Algorithms/MedialAxisRoadExtractor.cs` - use sparse extraction
- `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` - use spline calculator
- `BeamNgTerrainPoc/Terrain/Algorithms/TerrainBlender.cs` - use spline-based elevations

---

## Technical Notes

### Catmull-Rom Spline Formula

For points P?, P?, P?, P? and parameter t ? [0,1]:

```
P(t) = 0.5 * [
    (2*P?) +
    (-P? + P?) * t +
    (2*P? - 5*P? + 4*P? - P?) * t² +
    (-P? + 3*P? - 3*P? + P?) * t³
]
```

**Tangent (derivative):**
```
P'(t) = 0.5 * [
    (-P? + P?) +
    2*(2*P? - 5*P? + 4*P? - P?) * t +
    3*(-P? + 3*P? - 3*P? + P?) * t²
]
```

**Normal:** Perpendicular to tangent
```
Normal = (-Tangent.Y, Tangent.X) / |Tangent|
```

---

## Success Criteria

The implementation will be successful when:

? Roads are level (horizontal) side-to-side on all curves  
? Roads follow terrain longitudinally within slope limits  
? No circular artifacts or patterns  
? Smooth transitions at road edges  
? Handles intersections and road networks  
? Processing time < 60 seconds for 4K heightmap  
? Memory usage reasonable (< 4GB)  
? Visual quality acceptable in BeamNG.drive  

---

**Document Created:** 2024  
**Status:** Analysis Complete - Ready for Implementation Decision  
**Recommendation:** Proceed with Spline-Based Approach (Option C)
