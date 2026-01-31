# Road Smoothing Parameter Tuning Guide

This document provides guidance for tuning road smoothing parameters to eliminate staircase artifacts and achieve smooth, realistic road surfaces in BeamNG terrain files.

## Table of Contents

1. [Overview](#overview)
2. [Algorithm Pipeline](#algorithm-pipeline)
3. [Common Artifacts and Causes](#common-artifacts-and-causes)
4. [Parameter Reference](#parameter-reference)
5. [Recommended Presets](#recommended-presets)
6. [Troubleshooting](#troubleshooting)

---

## Overview

The road smoothing system uses a spline-based approach with the following key components:

- **Road Extraction**: Medial axis skeletonization ? Spline fitting
- **Elevation Sampling**: Per-cross-section terrain height sampling
- **Elevation Smoothing**: Box filter (prefix-sum) or Butterworth low-pass filter
- **Distance Field Blending**: Exact EDT (Felzenszwalb-Huttenlocher) + analytical blend functions
- **Post-Processing**: Gaussian/Box/Bilateral smoothing to eliminate residual artifacts

---

## Algorithm Pipeline

```
???????????????????    ????????????????????    ???????????????????????
?  Road Mask      ??????  Skeletonization ??????  Spline Fitting     ?
?  (Layer Image)  ?    ?  (Medial Axis)   ?    ?  (TCB Spline)       ?
???????????????????    ????????????????????    ???????????????????????
                                                         ?
                                                         ?
???????????????????    ????????????????????    ???????????????????????
?  Cross-Section  ??????  Elevation       ??????  Sample Along       ?
?  Target Heights ?    ?  Smoothing       ?    ?  Spline Path        ?
???????????????????    ?  (Box/Butter.)   ?    ???????????????????????
         ?             ????????????????????
         ?
???????????????????    ????????????????????    ???????????????????????
?  EDT Distance   ??????  Analytical      ??????  Post-Processing    ?
?  Field          ?    ?  Blending        ?    ?  Smoothing          ?
???????????????????    ????????????????????    ???????????????????????
```

---

## Common Artifacts and Causes

### 1. Longitudinal Stair-Steps (Along Road Direction)

**Symptom**: Visible steps/terraces along the road's length direction.

**Cause**: `CrossSectionIntervalMeters` is too large, creating discrete elevation jumps between cross-sections.

**Solution**:
```csharp
CrossSectionIntervalMeters = 0.25f;  // For 1m/pixel terrain
CrossSectionIntervalMeters = 0.5f;   // For 2m/pixel terrain
```

**Rule of Thumb**: `CrossSectionIntervalMeters` should be ? `metersPerPixel * 0.5`

---

### 2. Transverse Stair-Steps (Across Road Width)

**Symptom**: Visible steps across the road surface perpendicular to travel direction.

**Cause**: Post-processing smoothing kernel is too small to bridge discretization artifacts.

**Solution**:
```csharp
EnablePostProcessingSmoothing = true,
SmoothingType = PostProcessingSmoothingType.Gaussian,
SmoothingKernelSize = 9,    // Increase from default 7
SmoothingSigma = 2.0f,      // Increase from default 1.5
SmoothingIterations = 2     // Multiple passes for stubborn artifacts
```

---

### 3. Bumpy/Wavy Road Surface

**Symptom**: Road surface undulates or has visible bumps that follow terrain features.

**Cause**: `SmoothingWindowSize` is too small, allowing terrain noise to pass through.

**Solution**:
```csharp
SplineParameters = new SplineRoadParameters
{
    SmoothingWindowSize = 301,      // ~150m window for highways
    UseButterworthFilter = true,    // Maximally flat passband
    ButterworthFilterOrder = 4      // Aggressive high-frequency rejection
}
```

**Smoothing Window Sizes by Road Type**:
| Road Type | Window Size | Effective Radius |
|-----------|-------------|------------------|
| Highway   | 301-401     | 75-100m          |
| Mountain  | 151-201     | 40-50m           |
| Local     | 51-101      | 15-25m           |
| Dirt      | 21-51       | 5-15m            |

---

### 4. Disconnected Road Segments ("Dotted Roads")

**Symptom**: Road appears as disconnected dots or segments instead of continuous surface.

**Cause**: `GlobalLevelingStrength` is too high combined with small `TerrainAffectedRangeMeters`.

**Solution**:
```csharp
// Option A: Disable global leveling (recommended for terrain-following roads)
GlobalLevelingStrength = 0.0f,

// Option B: If global leveling is needed, use wide blend zones
GlobalLevelingStrength = 0.5f,
TerrainAffectedRangeMeters = 20.0f  // Must be wide for high leveling
```

**Warning**: Never use `GlobalLevelingStrength > 0.5` with `TerrainAffectedRangeMeters < 15m`.

---

### 5. Box Filter Ringing Artifacts

**Symptom**: Subtle ripples or overshoots near sharp terrain transitions.

**Cause**: Box filter (moving average) has non-ideal frequency response with side lobes.

**Solution**: Use Butterworth filter for "maximally flat" passband:
```csharp
SplineParameters = new SplineRoadParameters
{
    UseButterworthFilter = true,
    ButterworthFilterOrder = 4  // Higher order = sharper cutoff
}
```

**Butterworth vs Box Filter**:
| Characteristic | Box Filter | Butterworth Filter |
|----------------|------------|-------------------|
| Passband       | Ripples    | Maximally flat    |
| Cutoff         | Gradual    | Sharp (order-dependent) |
| Ringing        | Yes        | Minimal           |
| Performance    | O(N)       | O(N × order)      |
| Best For       | Flat terrain | Hilly terrain   |

---

## Parameter Reference

### Road Geometry Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `RoadWidthMeters` | float | 8.0 | Width of the flat road surface |
| `TerrainAffectedRangeMeters` | float | 12.0 | Distance from road edge where blending occurs |
| `CrossSectionIntervalMeters` | float | 0.5 | Spacing between cross-section samples |

### Slope Constraints

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `RoadMaxSlopeDegrees` | float | 4.0 | Maximum longitudinal slope (along road) |
| `SideMaxSlopeDegrees` | float | 30.0 | Maximum transverse slope (road to terrain) |

### Blending

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `BlendFunctionType` | enum | Cosine | Interpolation function (Linear/Cosine/Cubic/Quintic) |

### Elevation Smoothing (Spline Parameters)

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `SmoothingWindowSize` | int | 101 | Number of cross-sections in smoothing window |
| `UseButterworthFilter` | bool | true | Use Butterworth instead of box filter |
| `ButterworthFilterOrder` | int | 3 | Filter order (1-8, higher = sharper cutoff) |
| `GlobalLevelingStrength` | float | 0.0 | Blend toward network average (0=terrain-following) |

### Post-Processing Smoothing

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `EnablePostProcessingSmoothing` | bool | false | Apply spatial smoothing after blending |
| `SmoothingType` | enum | Gaussian | Filter type (Gaussian/Box/Bilateral) |
| `SmoothingKernelSize` | int | 7 | Kernel size in pixels (odd number) |
| `SmoothingSigma` | float | 1.5 | Gaussian standard deviation |
| `SmoothingMaskExtensionMeters` | float | 6.0 | Extend smoothing beyond road edge |
| `SmoothingIterations` | int | 1 | Number of smoothing passes |

### Preset Comparison Table

| Preset | TerrainAffectedRangeMeters | GlobalLevelingStrength | SideMaxSlopeDegrees | SplineTension | SplineContinuity | Use Case |
|--------|---------------------------|------------------------|---------------------|---------------|------------------|----------|
| **RacingCircuit** | 8m | 0.0 | 25° | 0.55 | 0.15 | Racing tracks, karting, hairpins |
| **MountainRoad** | 4m | 0.0 | 50° | 0.5 | 0.2 | Steep cliffs, switchbacks |
| **DirtRoad** | 3m | 0.0 | 45° | 0.4 | 0.3 | Rustic trails |
| **Highway** | 10m | 0.0 | 35° | 0.2 | 0.7 | Standard highways |
| **TerrainFollowingSmooth** | 12m | 0.0 | 30° | 0.2 | 0.7 | General purpose |
| **HillyAggressive** | 15m | 0.4 | 28° | 0.3 | 0.5 | Rolling hills |
| **MountainousUltraSmooth** | 22m | 0.5 | 25° | 0.2 | 0.7 | Flat road networks |
| **ExtremeNuclear** | 30m | 0.5 | 20° | 0.1 | 0.9 | Artificial environments |

---

## Spline Parameters for Sharp Turns

When dealing with hairpin turns, switchbacks, or tight curves, the following parameters are critical:

### SplineTension (0.0 - 1.0)
Controls how tightly the spline follows control points:
- **0.1-0.3**: Loose curves, spline may cut corners (good for highways)
- **0.4-0.5**: Balanced, follows curves more accurately
- **0.5-0.6**: Tight following, stays close to centerline (best for hairpins)

### SplineContinuity (-1.0 to 1.0)
Controls corner sharpness:
- **-0.5 to 0.0**: Allows sharp corners, good for chicanes
- **0.1-0.3**: Balanced, works for most curves
- **0.5-1.0**: Forces smooth corners (may cut hairpins)

### Path Extraction for Tight Curves

| Parameter | Normal Roads | Hairpin/Racing |
|-----------|--------------|----------------|
| DensifyMaxSpacingPixels | 1.5-2.0 | 0.75-1.0 |
| SimplifyTolerancePixels | 0.5-0.75 | 0.1-0.25 |
| MinPathLengthPixels | 50-100 | 20-30 |
| CrossSectionIntervalMeters | 0.5 | 0.25 |

### Recommended Settings by Turn Type

**Hairpin (180° turn):**
```csharp
SplineTension = 0.55f,
SplineContinuity = 0.15f,
DensifyMaxSpacingPixels = 0.75f,
SimplifyTolerancePixels = 0.1f
```

**Chicane (quick S-curve):**
```csharp
SplineTension = 0.5f,
SplineContinuity = 0.2f,
DensifyMaxSpacingPixels = 1.0f,
SimplifyTolerancePixels = 0.25f
```

**Sweeping curve:**
```csharp
SplineTension = 0.3f,
SplineContinuity = 0.5f,
DensifyMaxSpacingPixels = 1.5f,
SimplifyTolerancePixels = 0.5f
```

---

## Recommended Presets

### Racing Circuit (Hairpins & Tight Turns)

```csharp
new RoadSmoothingParameters
{
    RoadWidthMeters = 10.0f,
    TerrainAffectedRangeMeters = 8.0f,
    CrossSectionIntervalMeters = 0.25f,  // Ultra-dense
    RoadMaxSlopeDegrees = 3.0f,
    BlendFunctionType = BlendFunctionType.Cosine,
    
    EnablePostProcessingSmoothing = true,
    SmoothingType = PostProcessingSmoothingType.Gaussian,
    SmoothingKernelSize = 9,
    SmoothingSigma = 2.0f,
    SmoothingIterations = 2,
    
    SplineParameters = new SplineRoadParameters
    {
        // HAIRPIN-OPTIMIZED
        SplineTension = 0.55f,       // Tight following
        SplineContinuity = 0.15f,    // Allow sharp corners
        
        // High precision extraction
        DensifyMaxSpacingPixels = 0.75f,
        SimplifyTolerancePixels = 0.1f,
        MinPathLengthPixels = 20.0f,
        
        SmoothingWindowSize = 201,
        UseButterworthFilter = true,
        ButterworthFilterOrder = 4,
        GlobalLevelingStrength = 0.0f
    }
}
```

### Highway (Professional Quality)

```csharp
new RoadSmoothingParameters
{
    RoadWidthMeters = 8.0f,
    TerrainAffectedRangeMeters = 12.0f,
    CrossSectionIntervalMeters = 0.25f,
    RoadMaxSlopeDegrees = 4.0f,
    BlendFunctionType = BlendFunctionType.Cosine,
    
    EnablePostProcessingSmoothing = true,
    SmoothingType = PostProcessingSmoothingType.Gaussian,
    SmoothingKernelSize = 9,
    SmoothingSigma = 2.0f,
    SmoothingIterations = 2,
    
    SplineParameters = new SplineRoadParameters
    {
        SmoothingWindowSize = 301,
        UseButterworthFilter = true,
        ButterworthFilterOrder = 4,
        GlobalLevelingStrength = 0.0f
    }
}
```

### Mountain Road

```csharp
new RoadSmoothingParameters
{
    RoadWidthMeters = 6.0f,
    TerrainAffectedRangeMeters = 8.0f,
    CrossSectionIntervalMeters = 0.5f,
    RoadMaxSlopeDegrees = 8.0f,
    BlendFunctionType = BlendFunctionType.Cosine,
    
    EnablePostProcessingSmoothing = true,
    SmoothingType = PostProcessingSmoothingType.Gaussian,
    SmoothingKernelSize = 5,
    SmoothingSigma = 1.0f,
    SmoothingIterations = 1,
    
    SplineParameters = new SplineRoadParameters
    {
        SmoothingWindowSize = 151,
        UseButterworthFilter = true,
        ButterworthFilterOrder = 3,
        GlobalLevelingStrength = 0.0f
    }
}
```

### Dirt Road (Rustic Character)

```csharp
new RoadSmoothingParameters
{
    RoadWidthMeters = 5.0f,
    TerrainAffectedRangeMeters = 4.0f,
    CrossSectionIntervalMeters = 0.75f,
    RoadMaxSlopeDegrees = 12.0f,
    BlendFunctionType = BlendFunctionType.Cosine,
    
    EnablePostProcessingSmoothing = true,
    SmoothingType = PostProcessingSmoothingType.Gaussian,
    SmoothingKernelSize = 3,
    SmoothingSigma = 0.5f,
    SmoothingIterations = 1,
    
    SplineParameters = new SplineRoadParameters
    {
        SmoothingWindowSize = 31,
        UseButterworthFilter = false,  // Box filter preserves some character
        GlobalLevelingStrength = 0.0f
    }
}
```

---

## Troubleshooting

### Debug Outputs

Enable debug visualization to diagnose issues:

```csharp
parameters.DebugOutputDirectory = @"C:\temp\road_debug";
parameters.ExportSmoothedHeightmapWithOutlines = true;
parameters.SplineParameters.ExportSplineDebugImage = true;
parameters.SplineParameters.ExportSkeletonDebugImage = true;
parameters.SplineParameters.ExportSmoothedElevationDebugImage = true;
```

### Debug Image Interpretation

| File | Shows | Look For |
|------|-------|----------|
| `skeleton_debug.png` | Extracted centerline | Gaps, branches, noise |
| `spline_debug.png` | Fitted spline + cross-sections | Coverage, spacing |
| `spline_smoothed_elevation_debug.png` | Color-coded elevations | Sudden color changes = bumps |
| `smoothed_heightmap_with_road_outlines.png` | Final result | Cyan = road edge, Magenta = blend edge |

### Common Issues Checklist

- [ ] Is `CrossSectionIntervalMeters` ? terrain resolution?
- [ ] Is `SmoothingWindowSize` appropriate for road type?
- [ ] Is `UseButterworthFilter = true` for hilly terrain?
- [ ] Is post-processing enabled with adequate kernel size?
- [ ] Is `SmoothingMaskExtensionMeters` ? `CrossSectionIntervalMeters × 2`?
- [ ] Is `GlobalLevelingStrength = 0` for terrain-following roads?

---

## Performance Notes

| Terrain Size | EDT Time | Smoothing Time | Total Time |
|--------------|----------|----------------|------------|
| 1024×1024    | ~0.05s   | ~0.1s          | ~0.5s      |
| 2048×2048    | ~0.15s   | ~0.3s          | ~1.5s      |
| 4096×4096    | ~0.5s    | ~1.0s          | ~4s        |
| 8192×8192    | ~2s      | ~4s            | ~15s       |

All algorithms are O(N) or O(N log N), scaling linearly with terrain area.

---

## Version History

- **v1.0** (2025-01): Initial documentation
- Added Butterworth filter implementation
- Added parameter tuning guide based on artifact analysis
