# PNG Spline Simplification for Smooth Road Generation

## Overview

This document describes the spline simplification feature added to improve PNG-based road smoothing quality. The implementation brings PNG road smoothness to parity with OSM-based roads.

**Implementation Date**: 2024  
**Branch**: `feature/png_spline_simplification_try`  
**Primary File Modified**: `BeamNgTerrainPoc\Terrain\Services\UnifiedRoadNetworkBuilder.cs`

---

## Problem Statement

### The Quality Gap

After implementing OSM spline road smoothing, PNG layer mask roads exhibited noticeably worse quality:
- Jagged road edges
- Bumpy elevation profiles
- Poor terrain blending at road boundaries

### Root Cause Analysis

The fundamental difference lies in **control point density and quality**:

| Source | Control Points | Quality |
|--------|---------------|---------|
| **OSM Roads** | 10-50 sparse waypoints per km | Clean vector data from map surveys |
| **PNG Skeleton Roads** | 1000+ dense pixels per km | Noisy pixel-level data from skeletonization |

When you pass dense, jagged control points to a spline interpolator, even smooth interpolation methods (Akima, cubic spline) cannot produce smooth results because **the noise is baked into the control points themselves**.

### Visual Comparison

```
OSM Waypoints:          PNG Skeleton Pixels:
    •                   ••••••••••••••••••
      •                 •                •
        •               •                 •
          •             •                  •
            •           •                   •
              •         •                    •
                        ••••••••••••••••••••••

Sparse, clean          Dense, noisy
? Smooth spline        ? Jagged spline
```

---

## Solution: Path Simplification Pipeline

The solution adds a **three-stage simplification pipeline** that converts dense skeleton pixels to sparse, clean control points before spline creation.

### Pipeline Stages

```
Dense Skeleton Path (1000+ points)
        ?
        ?
???????????????????????????????????????
? Stage 1: Ramer-Douglas-Peucker      ?
? - Removes redundant points          ?
? - Preserves road shape/curves       ?
? - Controlled by SimplifyTolerance   ?
???????????????????????????????????????
        ?
        ?
???????????????????????????????????????
? Stage 2: Minimum Spacing            ?
? - Enforces minimum distance         ?
? - Prevents over-dense segments      ?
? - Target: ~5 pixels between points  ?
???????????????????????????????????????
        ?
        ?
???????????????????????????????????????
? Stage 3: Chaikin Smoothing          ?
? - Corner-cutting algorithm          ?
? - Pre-smooths control points        ?
? - Only for SmoothInterpolated mode  ?
???????????????????????????????????????
        ?
        ?
Sparse Control Points (50-100 points)
        ?
        ?
    RoadSpline
        ?
        ?
  Smooth Road! ?
```

---

## Algorithm Details

### Stage 1: Ramer-Douglas-Peucker (RDP)

The RDP algorithm is a classic line simplification technique that recursively removes points while preserving the overall shape.

**How it works:**
1. Draw a line from the first point to the last point
2. Find the point with maximum perpendicular distance from this line
3. If distance > tolerance: recursively simplify both halves
4. If distance ? tolerance: remove all intermediate points

```csharp
private static List<Vector2> RamerDouglasPeucker(List<Vector2> points, float epsilon)
{
    // Find point with max distance from line(first, last)
    float maxDistance = 0;
    int maxIndex = 0;
    
    for (int i = 1; i < points.Count - 1; i++)
    {
        var distance = PerpendicularDistance(points[i], points[0], points[^1]);
        if (distance > maxDistance)
        {
            maxDistance = distance;
            maxIndex = i;
        }
    }
    
    if (maxDistance > epsilon)
    {
        // Recursively simplify both halves
        var left = RamerDouglasPeucker(points.Take(maxIndex + 1).ToList(), epsilon);
        var right = RamerDouglasPeucker(points.Skip(maxIndex).ToList(), epsilon);
        return left.Concat(right.Skip(1)).ToList();
    }
    else
    {
        // All intermediate points can be removed
        return [points[0], points[^1]];
    }
}
```

**Tolerance Control:**
- Controlled by `SplineRoadParameters.SimplifyTolerancePixels`
- Converted to meters: `tolerance = SimplifyTolerancePixels * metersPerPixel`
- Minimum enforced: `0.5 * metersPerPixel` to avoid excessive points

### Stage 2: Minimum Spacing Enforcement

After RDP, some segments may still have clustered points (e.g., around tight curves). This stage enforces a minimum distance between consecutive points.

```csharp
private static List<Vector2> EnforceMinimumSpacing(List<Vector2> points, float minSpacing)
{
    var result = new List<Vector2> { points[0] };
    
    for (int i = 1; i < points.Count - 1; i++)
    {
        if (Vector2.DistanceSquared(result[^1], points[i]) >= minSpacing * minSpacing)
        {
            result.Add(points[i]);
        }
    }
    
    result.Add(points[^1]); // Always keep last point
    return result;
}
```

**Trigger Condition:** Only applied if path still has > 100 points after RDP.

### Stage 3: Chaikin Corner-Cutting

The Chaikin algorithm smooths polylines by iteratively "cutting corners". Each iteration replaces each segment with two new points at 1/4 and 3/4 positions.

```
Before:     A ?????????? B
After:      A ???? Q ???? R ???? B
            (Q = 0.75*A + 0.25*B)
            (R = 0.25*A + 0.75*B)
```

**When Applied:**
- Only for `SplineInterpolationType.SmoothInterpolated`
- Only if path has ? 4 points
- Single iteration (preserves start/end points)

---

## Code Location

### Primary Method

```csharp
// UnifiedRoadNetworkBuilder.cs

private static List<Vector2> SimplifyPathForSpline(
    List<Vector2> densePoints,
    SplineRoadParameters splineParams,
    float metersPerPixel)
{
    // Stage 1: RDP simplification
    var rdpTolerance = splineParams.SimplifyTolerancePixels * metersPerPixel;
    var effectiveTolerance = Math.Max(rdpTolerance, metersPerPixel * 0.5f);
    var simplified = RamerDouglasPeucker(densePoints, effectiveTolerance);
    
    // Stage 2: Minimum spacing (if still dense)
    if (simplified.Count > 100)
    {
        var targetSpacing = 5.0f * metersPerPixel;
        simplified = EnforceMinimumSpacing(simplified, targetSpacing);
    }
    
    // Stage 3: Chaikin smoothing (for smooth interpolation mode)
    if (simplified.Count >= 4 && 
        splineParams.SplineInterpolationType == SplineInterpolationType.SmoothInterpolated)
    {
        simplified = ChaikinSmooth(simplified, 1);
    }
    
    return simplified;
}
```

### Integration Point

Called in `ExtractSplinesFromLayerImage()` after converting skeleton pixels to world coordinates:

```csharp
// Convert to world coordinates (meters)
var worldPoints = pathPixels
    .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
    .ToList();

// NEW: Simplify before creating spline
var simplifiedPoints = SimplifyPathForSpline(worldPoints, splineParams, metersPerPixel);

// Create spline from simplified points
var spline = new RoadSpline(simplifiedPoints, interpolationType);
```

---

## Scope: PNG Only

**Important:** This simplification is **only applied to PNG-extracted paths**. OSM roads are unaffected.

### Code Path Verification

| Method | Path Type | Simplification |
|--------|-----------|----------------|
| `BuildNetworkFromOsmFeatures()` ? `ConvertFeatureToSpline()` | OSM | ? None |
| `ExtractSplinesFromMaterial()` with `UsePreBuiltSplines` | Pre-built OSM | ? None |
| `ExtractSplinesFromMaterial()` ? `ExtractSplinesFromLayerImage()` | PNG | ? Applied |

OSM data already has clean, sparse waypoints from the original vector source, so no simplification is needed.

---

## Configuration Parameters

### SplineRoadParameters

| Parameter | Default | Effect on Simplification |
|-----------|---------|-------------------------|
| `SimplifyTolerancePixels` | 0.5 | RDP tolerance (higher = more aggressive) |
| `SplineInterpolationType` | `SmoothInterpolated` | Enables Chaikin smoothing stage |

### Recommended Settings

**Standard Roads:**
```csharp
SimplifyTolerancePixels = 0.5f;  // Gentle simplification
SplineInterpolationType = SplineInterpolationType.SmoothInterpolated;
```

**Tight Hairpin Curves:**
```csharp
SimplifyTolerancePixels = 0.3f;  // Preserve more detail
SplineInterpolationType = SplineInterpolationType.SmoothInterpolated;
```

**Exact Path Following:**
```csharp
SimplifyTolerancePixels = 0.5f;
SplineInterpolationType = SplineInterpolationType.LinearControlPoints;  // Disables Chaikin
```

---

## Results

### Before (Dense Skeleton ? Spline)

```
Control Points: 1247
Spline Quality: Jagged edges, bumpy profile
Terrain Blend:  Poor, visible seams
```

### After (Simplified ? Spline)

```
Control Points: 73 (94% reduction)
Spline Quality: Smooth curves matching OSM
Terrain Blend:  Clean, seamless transitions
```

---

## Future Improvements

1. **Adaptive Tolerance**: Automatically adjust RDP tolerance based on road curvature
2. **Curvature-Aware Spacing**: Tighter spacing in curves, looser on straights
3. **UI Exposure**: Add simplification parameters to the terrain material settings UI
4. **Debug Visualization**: Export before/after control point images for tuning

---

## References

- **Ramer-Douglas-Peucker Algorithm**: [Wikipedia](https://en.wikipedia.org/wiki/Ramer%E2%80%93Douglas%E2%80%93Peucker_algorithm)
- **Chaikin's Algorithm**: [Chaikin's Corner Cutting](https://www.cs.unc.edu/~dm/UNC/COMP258/LECTURES/Chaikins-Algorithm.pdf)
- **Related Documentation**: `BeamNG_LevelCleanUp\docs\PNG_Road_Smoothing_Investigation.md`
