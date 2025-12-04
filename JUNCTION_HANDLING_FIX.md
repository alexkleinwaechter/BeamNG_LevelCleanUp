# Junction Handling Fix for Continuous Curves

## Problem

The **purple spline** was being incorrectly extracted even with `SkeletonDilationRadius = 0`. The issue was caused by the **junction handling logic** interfering with continuous hairpin curves.

### Root Cause

`PreferStraightThroughJunctions = true` was designed for **actual road intersections** (T-junctions, crossroads) but was being triggered by **false junctions** created during skeletonization at tight hairpin curves.

When the Zhang-Suen thinning algorithm processes a tight hairpin, it can create a 3-way junction where the curve bends sharply. The junction preference logic then:
1. Detects the junction (degree-3 skeleton pixel)
2. Tries to find the "straight through" path
3. Picks the wrong branch, creating the artifact

## Solution

**Disable junction preference for continuous curved roads:**

```csharp
SplineParameters = new SplineRoadParameters
{
    SkeletonDilationRadius = 0,                  // No dilation (cleanest skeleton)
    PreferStraightThroughJunctions = false,      // ? FIX: Disabled for curves
    JunctionAngleThreshold = 90.0f,              // (Unused when preference disabled)
    MinPathLengthPixels = 50.0f,
}
```

## When to Use Each Setting

### PreferStraightThroughJunctions = false (RECOMMENDED for most cases)
? **Use for:**
- Racing circuits
- Highway systems without complex intersections
- Serpentine/winding roads
- Mountain passes
- Any continuous curved roads

? **Benefits:**
- Follows actual road geometry faithfully
- No false junction artifacts
- Better curve extraction

### PreferStraightThroughJunctions = true
?? **Only use for:**
- Complex urban road networks
- Grid-based street systems
- Roads with many actual T-junctions and crossroads

?? **Drawbacks:**
- Can create artifacts at hairpin curves
- May split continuous curves at false junctions
- More complex path extraction logic

## Visual Results

### Before (PreferStraightThroughJunctions = true):
```
Purple spline: Incorrect path through hairpin
- Junction detected at curve apex
- Algorithm picks "straight through" (wrong branch)
- Creates artifact/tail
```

### After (PreferStraightThroughJunctions = false):
```
Purple spline: Correct path following curve
- Junction ignored (all branches processed equally)
- Path follows actual road geometry
- Clean continuous curve
```

## Recommended Configuration

### For Racing Circuits / Highways (Your Use Case):
```csharp
roadParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.Spline,
    
    SplineParameters = new SplineRoadParameters
    {
        // SKELETONIZATION
        SkeletonDilationRadius = 0,              // No dilation for cleanest skeleton
        
        // JUNCTION HANDLING - DISABLED
        PreferStraightThroughJunctions = false,  // Don't interfere with curves
        JunctionAngleThreshold = 90.0f,          // (Unused)
        MinPathLengthPixels = 50.0f,             // Filter tiny fragments
        
        // ... rest of settings
    }
};
```

### For Urban Road Networks:
```csharp
SplineParameters = new SplineRoadParameters
{
    SkeletonDilationRadius = 1,                 // Light dilation for connectivity
    PreferStraightThroughJunctions = true,      // Enable for intersections
    JunctionAngleThreshold = 45.0f,             // Strict angle for main roads
    MinPathLengthPixels = 30.0f,                // Keep shorter branches
}
```

## Updated Presets

All presets in `RoadSmoothingPresets.cs` have been updated with recommended settings:

| Preset | SkeletonDilationRadius | PreferStraightThroughJunctions |
|--------|------------------------|--------------------------------|
| **TerrainFollowingSmooth** | 0 | false ? (curves) |
| **MountainousUltraSmooth** | 1 | true (may have intersections) |
| **HillyAggressive** | 1 | true (may have intersections) |
| **FlatModerate** | 1 | true (may have intersections) |
| **ExtremeNuclear** | 1 | true (may have intersections) |

## Console Output Changes

**Before:**
```
Junction awareness enabled: preferring paths within 90° of current direction
  Junction: preferred path at 15.3° vs alternatives at 87.2°, 165.8°
```

**After (with PreferStraightThroughJunctions = false):**
```
(No junction preference messages - all branches processed equally)
```

## Summary

**The fix:**
1. ? Set `SkeletonDilationRadius = 0` (eliminated blob-based tails)
2. ? Set `PreferStraightThroughJunctions = false` (eliminated junction-based artifacts)

**Result:**
- Red spline: Fixed by dilation=0
- Purple spline: Fixed by junction preference=false
- Both curves now follow input geometry accurately

Your serpentine roads should now extract cleanly! ??
