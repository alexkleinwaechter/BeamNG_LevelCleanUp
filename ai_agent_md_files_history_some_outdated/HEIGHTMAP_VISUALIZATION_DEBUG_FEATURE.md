# Heightmap Visualization with Road Outlines - Debug Feature

## Overview
A new debug visualization feature has been added that exports the smoothed heightmap as a grayscale image with road outline overlays. This helps visualize exactly where road smoothing has been applied and how the terrain blending zones are positioned.

## What It Shows

The exported image contains:

1. **Grayscale Heightmap Background**
   - Black pixels = lowest elevation in the terrain
   - White pixels = highest elevation in the terrain
   - Grayscale values = normalized height values between min and max
   - Shows the final smoothed terrain after road processing

2. **Cyan Outline** (Road Edge)
   - Thin line drawn at the road edge boundary
   - Position: ± `RoadWidthMeters / 2` from the road centerline
   - Shows exactly where the fully flattened road surface ends
   - Typically 1-2 pixels wide for clarity

3. **Magenta Outline** (Terrain Blending Edge)
   - Thin line drawn at the outer edge of the terrain blending zone
   - Position: ± `(RoadWidthMeters / 2) + TerrainAffectedRangeMeters` from centerline
   - Shows the maximum extent of terrain modification
   - Between cyan and magenta = shoulder/embankment blend zone

## How It Works

### Technical Implementation

The visualization uses the **distance field** computed during road smoothing to draw outlines:

```csharp
// Road edge: pixels where distance ? roadHalfWidth
if (Math.Abs(dist - roadHalfWidth) < edgeTolerance)
    image[x, y] = Cyan;

// Blend zone edge: pixels where distance ? max blend distance
else if (Math.Abs(dist - blendZoneMaxDist) < edgeTolerance)
    image[x, y] = Magenta;
```

### Edge Detection Tolerance
- Tolerance = `metersPerPixel × 0.75`
- Creates lines that are 1-2 pixels wide
- Ensures outlines are visible but don't obscure the heightmap

## Usage

### Enable in Code

Add this parameter to your `RoadSmoothingParameters`:

```csharp
var parameters = new RoadSmoothingParameters
{
    // ... other parameters ...
    
    ExportSmoothedHeightmapWithOutlines = true,
    DebugOutputDirectory = @"d:\temp\output\highway"
};
```

### Output

- **File Name**: `smoothed_heightmap_with_road_outlines.png`
- **Location**: `DebugOutputDirectory` (or current directory if not specified)
- **Format**: PNG (lossless, suitable for analysis)
- **Dimensions**: Same as input heightmap (e.g., 4096×4096)

### Console Output

When the export completes, you'll see:

```
Exporting smoothed heightmap with road outlines (4096x4096)...
Exported smoothed heightmap with outlines: d:\temp\output\highway\smoothed_heightmap_with_road_outlines.png
  Height range: 10.50m (black) to 192.54m (white)
  Road edge outline (cyan): 45,231 pixels at ±4.0m from centerline
  Blend zone edge outline (magenta): 67,842 pixels at ±10.0m from centerline
```

## Requirements

- **Approach**: Only works with `RoadSmoothingApproach.Spline`
- **Reason**: Requires the distance field that's only computed in the spline approach
- **DirectMask**: Not supported (uses different internal representation)

If you try to enable this with DirectMask approach, the export will be silently skipped.

## Example Configurations

### Highway (Wide Roads)

```csharp
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 12.0f,
ExportSmoothedHeightmapWithOutlines = true,
DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\highway"
```

Expected outlines:
- Cyan at ±4.0m (road edge)
- Magenta at ±16.0m (total blend zone = 32m wide)

### Mountain Road (Narrow)

```csharp
RoadWidthMeters = 6.0f,
TerrainAffectedRangeMeters = 8.0f,
ExportSmoothedHeightmapWithOutlines = true,
DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\mountain"
```

Expected outlines:
- Cyan at ±3.0m (road edge)
- Magenta at ±11.0m (total blend zone = 22m wide)

### Dirt Road (Minimal Blending)

```csharp
RoadWidthMeters = 5.0f,
TerrainAffectedRangeMeters = 6.0f,
ExportSmoothedHeightmapWithOutlines = true,
DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\dirt"
```

Expected outlines:
- Cyan at ±2.5m (road edge)
- Magenta at ±8.5m (total blend zone = 17m wide)

## Interpreting the Visualization

### What to Look For

? **Good Road Smoothing**:
- Cyan outlines form smooth, continuous curves along the road
- Magenta outlines are parallel to cyan outlines (consistent blend zone)
- Heightmap shows smooth grayscale gradients in the road area
- No visible "steps" or discontinuities on the road surface

?? **Potential Issues**:
- **Dotted/Broken Outlines**: `CrossSectionIntervalMeters` too large
- **Jagged Outlines**: Road mask has irregular edges (check skeletonization)
- **Missing Outlines**: Road too narrow or parameters misconfigured
- **Staircase Patterns**: Enable `EnablePostProcessingSmoothing`

### Comparing Before/After

1. Look at the grayscale heightmap inside the cyan outline
2. Should be very smooth (post-processing smoothing helps)
3. Compare to areas outside the magenta outline (natural terrain)
4. Transition between cyan and magenta should be gradual

## Performance

- **Export Time**: ~0.5-1.0 seconds for 4096×4096 heightmaps
- **Memory**: Minimal (reuses existing distance field)
- **Impact**: No performance impact when disabled

## Related Debug Visualizations

This feature complements existing debug exports:

1. **`spline_debug.png`** (`ExportSplineDebugImage = true`)
   - Shows spline centerline (yellow)
   - Shows road width cross-sections (green)
   - Useful for verifying road extraction

2. **`spline_smoothed_elevation_debug.png`** (`ExportSmoothedElevationDebugImage = true`)
   - Shows elevation color-coded along road (blue=low, red=high)
   - Useful for verifying elevation smoothing
   - Shows road width cross-sections with color

3. **`smoothed_heightmap_with_road_outlines.png`** (NEW - `ExportSmoothedHeightmapWithOutlines = true`)
   - Shows final smoothed heightmap with outlines
   - Useful for verifying final result and blend zones
   - Shows exact extent of terrain modification

## Code Location

### Files Modified

1. **`BeamNgTerrainPoc\Terrain\Services\RoadSmoothingService.cs`**
   - Added `ExportSmoothedHeightmapWithRoadOutlines()` method
   - Integrated into smoothing workflow (spline approach only)

2. **`BeamNgTerrainPoc\Terrain\Models\RoadSmoothingParameters.cs`**
   - Added `ExportSmoothedHeightmapWithOutlines` property
   - Added documentation in debug output section

3. **`BeamNgTerrainPoc\Program.cs`**
   - Enabled in `CreateHighwayRoadParameters()` example
   - Demonstrates proper usage

## Future Enhancements

Potential improvements:

1. **Color-coded heightmap** instead of grayscale (like the elevation debug)
2. **Centerline overlay** (yellow line showing spline path)
3. **Cross-section markers** (dots or ticks at sampling points)
4. **Elevation labels** on the outlines showing height values
5. **Difference map** (original vs smoothed) as a separate export
6. **Multi-layer export** (separate PNG layers for compositing)

## Troubleshooting

### Outline Not Visible

**Problem**: Can't see cyan or magenta outlines in the image.

**Solutions**:
- Check console output - it reports pixel counts for each outline
- If counts are very low (< 1000 pixels), road may be too small
- Try increasing `RoadWidthMeters` or `TerrainAffectedRangeMeters`
- Ensure road mask has actual road pixels

### Outlines Too Thick

**Problem**: Outlines obscure the heightmap.

**Solution**: Edge tolerance is auto-calculated, but you can modify it:
```csharp
float edgeTolerance = metersPerPixel * 0.5f; // Thinner lines (was 0.75f)
```

### Outlines Look Broken

**Problem**: Outlines have gaps or are discontinuous.

**Solutions**:
- Check `spline_debug.png` to verify road extraction quality
- Reduce `CrossSectionIntervalMeters` for more samples
- Check `SimplifyTolerancePixels` (too aggressive simplification)
- Verify road mask has continuous road pixels

### Wrong Colors

**Problem**: Expected cyan/magenta but seeing different colors.

**Cause**: Image viewer color management or monitor calibration.

**Verification**: Open in image editor and check RGB values:
- Cyan = (0, 255, 255)
- Magenta = (255, 0, 255)

## Summary

This debug visualization provides a comprehensive view of:
- ? Final smoothed terrain heights (grayscale)
- ? Exact road edge location (cyan)
- ? Terrain blending zone extent (magenta)
- ? Quality of road smoothing (smooth gradients)
- ? Proper parameter configuration (outline positions)

Perfect for verifying that road smoothing is working correctly and tuning parameters for optimal results!
