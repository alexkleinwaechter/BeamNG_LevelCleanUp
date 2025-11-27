# Parameter Cleanup Recommendations

## Current Parameter Situation

The `RoadSmoothingParameters` class has accumulated many parameters through extensive trial-and-error development. The new `ImprovedSpline` approach makes many of these unnecessary.

## Parameters Still Needed (Keep)

### Core Geometry
- ? `RoadWidthMeters` - Essential
- ? `TerrainAffectedRangeMeters` - Essential
- ? `CrossSectionIntervalMeters` - Essential
- ? `RoadMaxSlopeDegrees` - Constraint
- ? `SideMaxSlopeDegrees` - Constraint

### Blending
- ? `BlendFunctionType` - Useful (Cosine/Hermite choice)
- ? `EnableTerrainBlending` - Debug flag

### Spline Extraction (For both SplineBased and ImprovedSpline)
- ? `PreferStraightThroughJunctions` - Junction handling
- ? `JunctionAngleThreshold` - Junction handling
- ? `MinPathLengthPixels` - Path extraction
- ? `BridgeEndpointMaxDistancePixels` - Path extraction
- ? `DensifyMaxSpacingPixels` - Path extraction
- ? `SimplifyTolerancePixels` - Path extraction
- ? `UseGraphOrdering` - Path ordering
- ? `OrderingNeighborRadiusPixels` - Path ordering

### Elevation Smoothing
- ? `SmoothingWindowSize` - Still used
- ? `UseButterworthFilter` - Filter choice
- ? `ButterworthFilterOrder` - Filter tuning

### Debug
- ? `DebugOutputDirectory` - Essential for debugging
- ? `ExportSplineDebugImage` - Visualization
- ? `ExportSkeletonDebugImage` - Visualization
- ? `ExportSmoothedElevationDebugImage` - Visualization

## Parameters Less Useful with ImprovedSpline

### May Be Obsolete
- ?? `LongitudinalSmoothingWindowMeters` - Covered by internal upsampling
- ?? `GlobalLevelingStrength` - Less needed with better smoothing
- ?? `SplineTension` - Less critical with upsampling
- ?? `SplineContinuity` - Less critical with upsampling  
- ?? `SplineBias` - Less critical with upsampling

These can be kept for backward compatibility but may not significantly affect `ImprovedSpline` results.

## New Parameters to Consider Adding

### For ImprovedSpline Approach

```csharp
public class ImprovedSplineParameters
{
    /// <summary>
    /// Upscaling factor for virtual heightfield (2, 4, or 8).
    /// Higher = smoother but slower. Default: 4
    /// </summary>
    public int UpscaleFactor { get; set; } = 4;
    
    /// <summary>
    /// Number of shoulder smoothing iterations (1-5).
    /// Higher = smoother transitions. Default: 3
    /// </summary>
    public int ShoulderSmoothingIterations { get; set; } = 3;
    
    /// <summary>
    /// Blend curve type for improved approach.
    /// Hermite (C²) or SmootherStep (C³). Default: Hermite
    /// </summary>
    public ImprovedBlendCurve BlendCurve { get; set; } = ImprovedBlendCurve.Hermite;
    
    /// <summary>
    /// Enable parallel processing for virtual heightfield.
    /// Faster on multi-core systems. Default: true
    /// </summary>
    public bool EnableParallelProcessing { get; set; } = true;
}
```

## Recommended Cleanup Strategy (Future)

### Phase 1: Mark as Obsolete (Non-Breaking)
```csharp
[Obsolete("Less effective with ImprovedSpline. Use UpscaleFactor instead.")]
public float LongitudinalSmoothingWindowMeters { get; set; }
```

### Phase 2: Add New Simplified Class
```csharp
// New streamlined parameters for ImprovedSpline only
public class SimpleRoadSmoothingParameters
{
    public float RoadWidthMeters { get; set; } = 8.0f;
    public float BlendDistanceMeters { get; set; } = 12.0f;
    public int UpscaleFactor { get; set; } = 4;
    public int ShoulderIterations { get; set; } = 3;
    // ... only essential parameters
}
```

### Phase 3: Migration Helper
```csharp
public static SimpleRoadSmoothingParameters ToSimple(this RoadSmoothingParameters legacy)
{
    return new SimpleRoadSmoothingParameters
    {
        RoadWidthMeters = legacy.RoadWidthMeters,
        BlendDistanceMeters = legacy.TerrainAffectedRangeMeters,
        // ... map relevant parameters
    };
}
```

## Current Recommendation: **DO NOT CLEANUP YET**

### Why Keep Everything For Now:
1. ? **Backward Compatibility** - Existing code still works
2. ? **A/B Testing** - Can compare SplineBased vs ImprovedSpline
3. ? **User Choice** - Some parameters may help in edge cases
4. ? **Not Breaking** - Extra parameters don't hurt

### When to Cleanup:
- ? After 6+ months of testing ImprovedSpline
- ? When deprecating old SplineBased approach
- ? When user feedback confirms parameters are useless
- ? When creating v2.0 with breaking changes allowed

## Summary

**CURRENT STATUS**: Keep all parameters, add new ones via sub-objects  
**FUTURE**: Deprecate gradually after validation  
**NEVER**: Remove without major version bump  

The nice spline debug images should **ALWAYS BE KEPT** - they're invaluable for understanding what's happening!
