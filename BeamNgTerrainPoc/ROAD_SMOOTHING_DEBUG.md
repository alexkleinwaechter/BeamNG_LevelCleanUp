# Road Smoothing Debug Visualization Guide

This document explains the debug images generated during the road smoothing process.

## Overview

The road smoothing system provides two types of debug images to help you visualize and verify the road extraction and smoothing pipeline:

1. **Skeleton Debug Image** (`skeleton_debug.png`) - Shows the raw road extraction process
2. **Smoothed Elevation Debug Image** (`spline_smoothed_elevation_debug.png`) - Shows the calculated elevation profile after smoothing

## Enabling Debug Images

Configure debug output in your `RoadSmoothingParameters`:

```csharp
var roadParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.SplineBased,
    
    // Enable debug outputs
    ExportSkeletonDebugImage = true,
    ExportSplineDebugImage = true,
    ExportSmoothedElevationDebugImage = true,
    DebugOutputDirectory = @"D:\temp\output",
    
    // Other parameters...
};
```

---

## 1. Skeleton Debug Image (`skeleton_debug.png`)

**Purpose:** Visualizes the road centerline extraction process from the raw road layer mask.

### Color Legend

| Color | Meaning | Description |
|-------|---------|-------------|
| **Very Dark Grey** (RGB: 25,25,25) | Original Road Mask | All pixels from the input road layer that were above the threshold (value > 128) |
| **Medium Grey** (RGB: 60,60,60) | Thinned Skeleton | The result of Zhang-Suen morphological thinning - reduces the road to a 1-pixel-wide centerline |
| **Red** | First Ordered Path | The first connected road segment after ordering and densification |
| **Green** | Second Ordered Path | The second connected road segment (if multiple paths exist) |
| **Blue** | Third Ordered Path | Additional road segments |
| **Orange** | Fourth+ Ordered Path | Further road segments (cycles through colors) |

### What to Look For

? **Good Extraction:**
- Colored path(s) cover the entire grey skeleton
- No gaps or breaks in the colored lines
- Smooth curves without abrupt direction changes

? **Problematic Extraction:**
- Colored path stops before the end of the grey skeleton ? Path ordering failed
- Multiple colored paths when you expect one continuous road ? Skeleton has breaks
- Missing sections ? Increase `BridgeEndpointMaxDistancePixels` parameter

### Troubleshooting Parameters

| Issue | Parameter to Adjust | Suggested Value |
|-------|-------------------|-----------------|
| Path stops at curves | `BridgeEndpointMaxDistancePixels` | Increase to 6.0 - 10.0 |
| Jagged path ordering | `OrderingNeighborRadiusPixels` | Increase to 3.0 - 3.5 |
| Too few control points | `DensifyMaxSpacingPixels` | Decrease to 0.5 |
| Ordering method fails | `UseGraphOrdering` | Toggle between true/false |

---

## 2. Spline Debug Image (`spline_debug.png`)

**Purpose:** Shows the extracted spline geometry with road width cross-sections.

### Color Legend

| Color | Meaning | Description |
|-------|---------|-------------|
| **Dark Grey** (RGB: 32,32,32) | Original Road Mask | Background reference showing the input road layer |
| **Yellow** | Spline Centerline Samples | Points sampled along the spline at `CrossSectionIntervalMeters` intervals |
| **Green Lines** | Road Width Cross-Sections | Perpendicular lines showing the road width at selected cross-sections |

### What to Look For

? **Good Geometry:**
- Yellow centerline follows the road smoothly
- Green cross-sections are perpendicular to the road direction
- Green lines have consistent spacing (interval × 2 by default)
- Green lines align with the road width

? **Problematic Geometry:**
- Yellow points cluster or skip areas ? Spline interpolation issues
- Green lines flip direction rapidly ? Noisy tangent/normal calculations
- Green lines don't match road width ? Check `RoadWidthMeters` parameter

---

## 3. Smoothed Elevation Debug Image (`spline_smoothed_elevation_debug.png`)

**Purpose:** Visualizes the calculated target elevations for the road surface after smoothing calculations.

### Color Legend (Elevation Gradient)

| Color | Elevation | Description |
|-------|-----------|-------------|
| **Blue** | Lowest | Minimum elevation along the road (valleys, low points) |
| **Cyan** | Low-Medium | Below average elevation |
| **Green** | Medium | Mid-range elevation |
| **Yellow** | Medium-High | Above average elevation |
| **Red** | Highest | Maximum elevation along the road (peaks, high points) |

### Interpretation

The color gradient represents the **smoothed road surface elevation** at each cross-section:

- **Smooth color transitions** = Gradual elevation changes (good for road design)
- **Abrupt color changes** = Steep road grades (may violate `RoadMaxSlopeDegrees`)
- **Consistent color along curves** = Level road through turns (goal of spline-based approach)

### What to Look For

? **Good Smoothing:**
- Gradual color transitions along the road
- Curves maintain consistent color (elevation) through the turn
- No sudden jumps from blue to red

? **Problematic Smoothing:**
- Rapid color oscillations ? Elevation calculations are unstable
- All one color ? No elevation variation (possible input data issue)
- Sharp color boundaries ? Exceeds slope constraints

### Console Output

When this image is exported, the console displays:
```
Exported smoothed elevation debug image: <path>
  Elevation range: <min>m (blue) to <max>m (red)
```

This tells you the actual elevation values corresponding to the color extremes.

---

## Debug Workflow

### Step 1: Verify Road Extraction
```csharp
ExportSkeletonDebugImage = true
ExportSplineDebugImage = true
EnableTerrainBlending = false  // Skip blending for faster iteration
```

Check `skeleton_debug.png` and `spline_debug.png`:
- Is the entire road covered by colored paths?
- Are the cross-sections oriented correctly?

### Step 2: Verify Elevation Calculation
```csharp
ExportSmoothedElevationDebugImage = true
EnableTerrainBlending = false  // Still fast, just calculates elevations
```

Check `spline_smoothed_elevation_debug.png`:
- Do the elevations make sense based on your terrain?
- Are there smooth transitions?

### Step 3: Enable Full Smoothing
```csharp
EnableTerrainBlending = true  // Apply the smoothing to heightmap
```

The system will generate a modified heightmap file: `<terrainName>_smoothed_heightmap.png`

Compare this to your original heightmap to see the actual terrain modifications.

---

## Common Issues and Solutions

### Issue: "No valid elevations to create smoothed debug image"

**Cause:** The elevation calculator didn't assign any elevations to cross-sections.

**Solutions:**
- Check that your heightmap is loaded correctly
- Verify `MetersPerPixel` matches your terrain scale
- Ensure cross-sections are within the heightmap bounds

### Issue: Skeleton has multiple disconnected segments

**Cause:** The input road mask has gaps or the thinning algorithm created breaks.

**Solutions:**
- Increase `BridgeEndpointMaxDistancePixels` (try 6.0 - 10.0)
- Check your input road layer for quality issues
- Consider using `RoadSmoothingApproach.DirectMask` for complex road networks

### Issue: Green cross-sections zig-zag wildly

**Cause:** High curvature with too few control points causes noisy tangent derivatives.

**Solutions:**
- Decrease `DensifyMaxSpacingPixels` to add more control points (try 0.5)
- Decrease `CrossSectionIntervalMeters` for denser sampling (try 0.5)
- Consider longitudinal smoothing (future enhancement)

---

## Parameter Reference

### Debug Parameters

| Parameter | Type | Default | Purpose |
|-----------|------|---------|---------|
| `ExportSkeletonDebugImage` | bool | false | Export skeleton extraction visualization |
| `ExportSplineDebugImage` | bool | false | Export spline geometry visualization |
| `ExportSmoothedElevationDebugImage` | bool | false | Export elevation-colored visualization |
| `DebugOutputDirectory` | string? | null | Output directory for debug images (defaults to working directory) |
| `EnableTerrainBlending` | bool | true | Set to false to skip terrain modification (debug mode) |

### Extraction Parameters

| Parameter | Type | Default | Purpose |
|-----------|------|---------|---------|
| `UseGraphOrdering` | bool | true | Use graph-based path ordering (more robust) |
| `BridgeEndpointMaxDistancePixels` | float | 4.0 | Max distance to connect skeleton gaps |
| `DensifyMaxSpacingPixels` | float | 1.0 | Max spacing between control points after densification |
| `OrderingNeighborRadiusPixels` | float | 2.5 | Max distance for neighbor connections in graph |

### Road Geometry Parameters

| Parameter | Type | Default | Purpose |
|-----------|------|---------|---------|
| `RoadWidthMeters` | float | 8.0 | Width of the road surface |
| `CrossSectionIntervalMeters` | float | 2.0 | Distance between cross-section samples |
| `RoadMaxSlopeDegrees` | float | 8.0 | Maximum longitudinal road grade |
| `SideMaxSlopeDegrees` | float | 30.0 | Maximum embankment slope |

---

## Tips for Best Results

1. **Start with skeleton debug** to verify extraction before enabling expensive smoothing
2. **Use graph ordering** (`UseGraphOrdering = true`) for complex paths
3. **Bridge small gaps** (`BridgeEndpointMaxDistancePixels = 4-6`) to handle thinning artifacts
4. **Densify curves** (`DensifyMaxSpacingPixels = 0.5-1.0`) for smooth spline interpolation
5. **Check elevation range** in console output to ensure realistic values
6. **Compare all three images** to understand the full pipeline

---

## Output Files Summary

When all debug options are enabled, you'll get:

1. `skeleton_debug.png` - Raw extraction (colored paths on grey skeleton)
2. `spline_debug.png` - Geometry with cross-sections (yellow centerline, green width lines)
3. `spline_smoothed_elevation_debug.png` - Elevation-colored result (blue to red gradient)
4. `<terrainName>_smoothed_heightmap.png` - Final modified heightmap (if `EnableTerrainBlending = true`)

Compare these images side-by-side to verify each stage of the pipeline works correctly.
