# Dilation Size Parameter - Implementation Summary

## What Changed

Made the skeleton dilation radius **configurable** instead of hardcoded.

### New Parameter Added

```csharp
public class SplineRoadParameters
{
    /// <summary>
    /// Dilation radius (in pixels) applied to road mask before skeletonization.
    /// 
    /// 0 = no dilation (cleanest skeleton, may miss disconnected fragments)
    /// 1 = minimal dilation (RECOMMENDED - good balance, minimal tail artifacts)
    /// 2 = moderate dilation (better connectivity, minor blobs at curves)
    /// 3 = heavy dilation (maximum connectivity, SIGNIFICANT tail artifacts at hairpins)
    /// 
    /// Default: 1
    /// </summary>
    public int SkeletonDilationRadius { get; set; } = 1;
}
```

## Usage

### In Your Configuration:

```csharp
roadParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.Spline,
    
    SplineParameters = new SplineRoadParameters
    {
        SkeletonDilationRadius = 1,  // ? NEW: Control dilation size
        
        // ... other parameters
        PreferStraightThroughJunctions = true,
        JunctionAngleThreshold = 90.0f,
        MinPathLengthPixels = 50.0f,
    }
};
```

### Console Output:

```
After dilation (radius=1px): mask prepared for skeletonization
```

Now shows the actual radius being used.

## Tuning Guide

| Radius | Use Case | Results |
|--------|----------|---------|
| **0** | Very clean input layers, no gaps | Cleanest skeleton, may miss small disconnections |
| **1** | **Default - Recommended** | Good balance, minimal artifacts ? |
| **2** | Noisy input layers with small gaps | Better connectivity, slight blobs at curves |
| **3** | Very fragmented input with many gaps | Maximum connectivity, tail artifacts at hairpins ?? |

## Problem-Specific Recommendations

### If You See Tail Artifacts at Hairpins:
```csharp
SkeletonDilationRadius = 0,  // Disable dilation
// OR
SkeletonDilationRadius = 1,  // Keep at default
MinPathLengthPixels = 80.0f, // Increase spur pruning
```

### If You See Disconnected Road Fragments:
```csharp
SkeletonDilationRadius = 2,  // Increase connectivity
// OR
BridgeEndpointMaxDistancePixels = 50.0f, // Better path joining
```

### If You See Choppy Roads in Curves:
```csharp
SkeletonDilationRadius = 1,  // Keep minimal
JunctionAngleThreshold = 90.0f, // Allow wider merging angle
BridgeEndpointMaxDistancePixels = 40.0f, // Merge nearby fragments
```

## Files Modified

1. **`SplineRoadParameters.cs`**
   - Added `SkeletonDilationRadius` property (default: 1)
   - Added validation (0-5 range)

2. **`SkeletonizationRoadExtractor.cs`**
   - Uses parameter instead of hardcoded value
   - Shows radius in console output

3. **`Program.cs`**
   - Example usage added

4. **`RoadSmoothingPresets.cs`**
   - Updated preset to show parameter

5. **`HAIRPIN_TAIL_FIX.md`**
   - Updated documentation

## Backward Compatibility

? **Fully backward compatible**
- Default value: 1 (same as the previous hardcoded value)
- Existing code without this parameter will work unchanged
- Old behavior preserved by default

## Summary

You can now control the dilation size to fine-tune the skeleton extraction for your specific terrain:

```csharp
SplineParameters = new SplineRoadParameters
{
    SkeletonDilationRadius = 1,  // ? Adjust 0-5 as needed
}
```

**Recommended:** Start with default (1) and only adjust if you see specific issues.
