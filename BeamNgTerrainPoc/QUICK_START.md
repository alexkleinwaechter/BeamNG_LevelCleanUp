# Quick Start: Improved Road Smoothing

## TL;DR - Just Show Me The Code!

```csharp
roadParameters = new RoadSmoothingParameters
{
    // ?? THE MAGIC LINE - Use the new improved approach!
    Approach = RoadSmoothingApproach.ImprovedSpline,
    
    // Basic settings
    RoadWidthMeters = 8.0f,                     // Width of road surface
    TerrainAffectedRangeMeters = 12.0f,         // Blend distance to terrain
    RoadMaxSlopeDegrees = 4.0f,                 // Max road slope
    SideMaxSlopeDegrees = 30.0f,                // Max embankment slope
    
    // Debug output
    DebugOutputDirectory = @"D:\output",
    ExportSplineDebugImage = true,              // Yellow centerline
    ExportSmoothedElevationDebugImage = true    // Color-coded elevation
};
```

That's it! The upsampling, blending, and smoothing happen automatically.

## What You'll See in Console

```
=== IMPROVED SPLINE-BASED ROAD SMOOTHING ===
Using internal upsampling for smooth results
Upsampling heightmap 4096x4096 ? 16384x16384 (4x)...
  Upsampling complete in 2.3s
Processing road smoothing on 16384x16384 virtual heightfield...
  Progress: 25.0% (pixels: 1,234,567)
  Progress: 50.0% (pixels: 2,468,024)
  Progress: 75.0% (pixels: 3,701,482)
  Blending complete in 12.4s
  Modified pixels: 4,935,939 (1.84%)
  Road pixels: 1,234,567
Applying iterative shoulder smoothing (3 passes)...
  Identifying shoulder zone pixels...
  Shoulder zone: 3,701,372 pixels (1.38%)
  Shoulder smoothing iteration 1/3...
    Smoothed 3,701,372 shoulder pixels
  Shoulder smoothing iteration 2/3...
    Smoothed 3,701,372 shoulder pixels
  Shoulder smoothing iteration 3/3...
    Smoothed 3,701,372 shoulder pixels
Downsampling virtual heightfield 16384x16384 ? 4096x4096 (4x)...
  Applying Gaussian blur (kernel size: 7)...
  Decimating by factor 4...
  Downsampling complete in 1.8s
=== IMPROVED SMOOTHING COMPLETE ===
```

## Debug Images Explained

### spline_debug.png
- **Black background**: Non-road terrain
- **Dark gray**: Road mask pixels
- **Yellow line**: Extracted spline centerline
- **Green lines**: Road width cross-sections

**Use this to verify:**
- ? Spline follows road correctly
- ? Road width is reasonable
- ? No weird jumps or disconnections

### spline_smoothed_elevation_debug.png
- **Color gradient**: Blue (low) ? Green ? Yellow ? Red (high)
- **Shows**: Elevation along road after smoothing

**Use this to verify:**
- ? Smooth elevation transitions (no stairs!)
- ? No sudden jumps between segments
- ? Gradual color changes = smooth slopes

## Comparison: Before vs After

### ? Old SplineBased Approach
```
Problems:
- Jagged edges at road boundaries
- Visible "stairs" along road length
- Blocky artifacts on curves
- Hard transitions to terrain
- 42 parameters to tune (trial-and-error hell)
```

### ? New ImprovedSpline Approach
```
Solutions:
- Smooth edges (bicubic + Hermite blend)
- No stairs (4x upsampling)
- Clean curves (sub-pixel precision)
- Natural transitions (iterative shoulder smoothing)
- Same parameters work for most cases
```

## Performance Guide

### Typical Processing Times (4096×4096 terrain)

| Approach | Time | Quality | Use Case |
|----------|------|---------|----------|
| DirectMask | ~3s | Good | Complex intersections |
| SplineBased | ~8s | Mixed (stairs) | Legacy |
| **ImprovedSpline** | **~15s** | **Excellent** | **Smooth roads** |

**Worth the wait?** YES - for final terrain, absolutely!  
**For testing?** Use DirectMask for quick iterations, ImprovedSpline for final export.

## Troubleshooting

### Problem: "No road pixels modified!"

**Causes:**
- Road layer image has no white pixels
- RoadWidthMeters is too small
- Cross-sections failed to generate

**Fix:**
```csharp
// Check road layer image is correct
// Increase road width temporarily to test
RoadWidthMeters = 12.0f  // Was: 8.0f
```

### Problem: Roads still have stairs

**Causes:**
- Not using ImprovedSpline approach
- Using old SplineBased by mistake

**Fix:**
```csharp
// Double-check this line!
Approach = RoadSmoothingApproach.ImprovedSpline  // NOT SplineBased!
```

### Problem: Out of memory

**Causes:**
- Terrain is huge (8192×8192 or larger)
- 4x upsampling = 16x memory usage

**Fix:**
- Process in tiles (requires additional code)
- Reduce terrain size before processing
- Use DirectMask or SplineBased as fallback

### Problem: Too slow for my use case

**Solutions:**
1. Use DirectMask for quick previews
2. Process only once, cache result
3. Use SplineBased for "good enough" quality
4. Process overnight for final terrain

## Best Practices

### ?? DO:
- ? Use ImprovedSpline for **final terrain export**
- ? Check debug images before exporting
- ? Test on small section first
- ? Keep RoadWidthMeters realistic (6-10m for normal roads)
- ? Use TerrainAffectedRangeMeters ? 1.5× road width

### ? DON'T:
- ? Use ImprovedSpline for quick testing (too slow)
- ? Set GlobalLevelingStrength > 0 (use 0 for terrain-following)
- ? Make RoadWidthMeters < 4m (too narrow, aliasing issues)
- ? Skip debug images (they're your quality check!)

## Real-World Example

```csharp
// Realistic highway configuration
var highwayParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.ImprovedSpline,
    
    // Highway dimensions
    RoadWidthMeters = 10.0f,            // 2-lane highway
    TerrainAffectedRangeMeters = 15.0f, // Wide embankments
    
    // Gentle slopes for realistic highway
    RoadMaxSlopeDegrees = 3.0f,         // Very gentle
    SideMaxSlopeDegrees = 25.0f,        // Gentle embankment (1:2.5)
    
    // Spline settings
    SplineParameters = new SplineRoadParameters
    {
        SmoothingWindowSize = 201,      // 50m smoothing window
        UseButterworthFilter = true,    // Ultra-smooth
        ButterworthFilterOrder = 4,
        GlobalLevelingStrength = 0.0f,  // Follow terrain
        
        // Junction handling
        PreferStraightThroughJunctions = true,
        JunctionAngleThreshold = 45.0f,
        
        // Debug
        ExportSplineDebugImage = true,
        ExportSmoothedElevationDebugImage = true
    }
};
```

## Next Steps

1. **Test it**: Run with your terrain data
2. **Check debug images**: Verify spline and elevations look good
3. **Load in BeamNG**: Drive on your smooth road!
4. **Iterate**: Adjust parameters if needed
5. **Share results**: Help us improve the algorithm!

## Getting Help

If you encounter issues:

1. Check console output for warnings
2. Examine debug images for problems
3. Try DirectMask approach to isolate issue
4. Check parameter values are reasonable
5. Open issue with debug images attached

---

**Enjoy your smooth roads!** ????
