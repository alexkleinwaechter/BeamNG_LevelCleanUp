# Quick Reference: Heightmap with Road Outlines Debug Export

## Enable the Feature

```csharp
var roadParams = new RoadSmoothingParameters
{
    // Required: Use Spline approach (not DirectMask)
    Approach = RoadSmoothingApproach.Spline,
    
    // Enable the export
    ExportSmoothedHeightmapWithOutlines = true,
    
    // Optional: Set output directory
    DebugOutputDirectory = @"d:\temp\output"
};
```

## What You Get

**Output File**: `smoothed_heightmap_with_road_outlines.png`

**Visual Elements**:
- **Grayscale background**: Smoothed heightmap (black=low, white=high)
- **Cyan outline**: Road edge at ±RoadWidthMeters/2
- **Magenta outline**: Blend zone edge at ±(RoadWidthMeters/2 + TerrainAffectedRangeMeters)

## Quick Examples

### Highway (8m wide, 12m blend)
```csharp
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 12.0f,
ExportSmoothedHeightmapWithOutlines = true
```
? Cyan at ±4m, Magenta at ±16m (32m total width)

### Mountain Road (6m wide, 8m blend)
```csharp
RoadWidthMeters = 6.0f,
TerrainAffectedRangeMeters = 8.0f,
ExportSmoothedHeightmapWithOutlines = true
```
? Cyan at ±3m, Magenta at ±11m (22m total width)

### Dirt Road (5m wide, 6m blend)
```csharp
RoadWidthMeters = 5.0f,
TerrainAffectedRangeMeters = 6.0f,
ExportSmoothedHeightmapWithOutlines = true
```
? Cyan at ±2.5m, Magenta at ±8.5m (17m total width)

## Console Output Example

```
Exporting smoothed heightmap with road outlines (4096x4096)...
Exported smoothed heightmap with outlines: d:\temp\output\smoothed_heightmap_with_road_outlines.png
  Height range: 10.50m (black) to 192.54m (white)
  Road edge outline (cyan): 45,231 pixels at ±4.0m from centerline
  Blend zone edge outline (magenta): 67,842 pixels at ±10.0m from centerline
```

## Other Debug Exports (Enable Together)

```csharp
// Spline centerline and road width visualization
ExportSplineDebugImage = true,

// Skeleton extraction quality check
ExportSkeletonDebugImage = true,

// Elevation color-coded along road
ExportSmoothedElevationDebugImage = true,

// NEW: Final heightmap with outlines
ExportSmoothedHeightmapWithOutlines = true
```

## Limitations

- ? **Only works with Spline approach** (not DirectMask)
- ? Works with all blend functions (Cosine, Cubic, Quintic, Linear)
- ? Works with post-processing smoothing enabled/disabled
- ? Performance: ~0.5-1.0s export time for 4096×4096

## Troubleshooting

| Issue | Solution |
|-------|----------|
| No outlines visible | Check console - if pixel counts are low, increase RoadWidthMeters |
| Broken/dotted outlines | Reduce CrossSectionIntervalMeters |
| Wrong approach error | Set `Approach = RoadSmoothingApproach.Spline` |
| File not found | Check DebugOutputDirectory path exists |

## Full Example (Highway)

```csharp
var parameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.Spline,
    EnableTerrainBlending = true,
    DebugOutputDirectory = @"d:\temp\output\highway",
    
    // Road geometry
    RoadWidthMeters = 8.0f,
    TerrainAffectedRangeMeters = 12.0f,
    CrossSectionIntervalMeters = 0.5f,
    
    // Blending
    BlendFunctionType = BlendFunctionType.Cosine,
    
    // Post-processing smoothing
    EnablePostProcessingSmoothing = true,
    SmoothingKernelSize = 7,
    SmoothingSigma = 1.5f,
    
    // Debug exports
    ExportSplineDebugImage = true,
    ExportSmoothedElevationDebugImage = true,
    ExportSmoothedHeightmapWithOutlines = true, // ? NEW!
    
    SplineParameters = new SplineRoadParameters
    {
        SplineTension = 0.2f,
        SplineContinuity = 0.7f,
        SmoothingWindowSize = 301,
        UseButterworthFilter = true,
        ButterworthFilterOrder = 4
    }
};
```

## See Full Documentation

? `HEIGHTMAP_VISUALIZATION_DEBUG_FEATURE.md`
