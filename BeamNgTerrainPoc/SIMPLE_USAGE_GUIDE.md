# ?? Simple Usage Guide - Clean Parameter Structure

## ? **What Changed**

Your parameters are now **organized into clear categories**:

```
RoadSmoothingParameters (Main)
??? Common parameters (all approaches)
?   ??? RoadWidthMeters
?   ??? TerrainAffectedRangeMeters
?   ??? RoadMaxSlopeDegrees
?   ??? etc.
?
??? SplineParameters (only for SplineBased approach)
?   ??? Spline curve fitting
?   ??? Junction handling
?   ??? Elevation smoothing (Butterworth/Gaussian)
?   ??? Global leveling
?
??? DirectMaskParameters (only for DirectMask approach)
    ??? Smoothing window
    ??? Search radius
    ??? Butterworth filter options
```

---

## ?? **Two Simple Ways to Use**

### **Option 1: Use a Preset (EASIEST)**

```csharp
// Just pick a preset and go!
roadParameters = RoadSmoothingPresets.TerrainFollowingSmooth;

// Customize debug output:
roadParameters.DebugOutputDirectory = @"D:\temp\output";
roadParameters.SplineParameters.ExportSplineDebugImage = true;
roadParameters.SplineParameters.ExportSmoothedElevationDebugImage = true;
```

**Available Presets:**
- `TerrainFollowingSmooth` ? **RECOMMENDED** - Smooth roads, terrain-following
- `HillyAggressive` - For hilly terrain with moderate leveling
- `MountainousUltraSmooth` - For extreme terrain (uses global leveling)
- `FlatModerate` - For flat terrain
- `FastTesting` - Quick DirectMask approach for testing
- `ExtremeNuclear` - Maximum smoothing (last resort)

---

### **Option 2: Manual Configuration (CLEAN STRUCTURE)**

```csharp
roadParameters = new RoadSmoothingParameters
{
    // ====================================
    // APPROACH & COMMON SETTINGS
    // ====================================
    Approach = RoadSmoothingApproach.SplineBased,
    EnableTerrainBlending = true,
    DebugOutputDirectory = @"D:\temp\output",
    
    // Common geometry (all approaches)
    RoadWidthMeters = 8.0f,
    TerrainAffectedRangeMeters = 12.0f,
    CrossSectionIntervalMeters = 0.5f,
    RoadMaxSlopeDegrees = 4.0f,
    SideMaxSlopeDegrees = 30.0f,
    BlendFunctionType = BlendFunctionType.Cosine,
    
    // ====================================
    // SPLINE-SPECIFIC SETTINGS
    // (Only used when Approach = SplineBased)
    // ====================================
    SplineParameters = new SplineRoadParameters
    {
        // Smoothing algorithm
        UseButterworthFilter = true,
        ButterworthFilterOrder = 4,
        SmoothingWindowSize = 201,
        GlobalLevelingStrength = 0.0f,       // 0 = terrain-following
        
        // Junction handling
        PreferStraightThroughJunctions = true,
        JunctionAngleThreshold = 45.0f,
        MinPathLengthPixels = 50.0f,
        
        // Path extraction
        BridgeEndpointMaxDistancePixels = 40.0f,
        DensifyMaxSpacingPixels = 1.5f,
        SimplifyTolerancePixels = 0.5f,
        
        // Curve fitting
        SplineTension = 0.2f,
        SplineContinuity = 0.7f,
        SplineBias = 0.0f,
        
        // Debug output
        ExportSplineDebugImage = true,
        ExportSkeletonDebugImage = true,
        ExportSmoothedElevationDebugImage = true
    }
};
```

---

### **Option 3: DirectMask Approach**

```csharp
roadParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.DirectMask,  // Fast & robust
    EnableTerrainBlending = true,
    
    // Common parameters
    RoadWidthMeters = 8.0f,
    TerrainAffectedRangeMeters = 15.0f,
    RoadMaxSlopeDegrees = 8.0f,
    SideMaxSlopeDegrees = 30.0f,
    
    // ====================================
    // DIRECTMASK-SPECIFIC SETTINGS
    // (Only used when Approach = DirectMask)
    // ====================================
    DirectMaskParameters = new DirectMaskRoadParameters
    {
        SmoothingWindowSize = 10,
        RoadPixelSearchRadius = 3,
        UseButterworthFilter = true,         // Optional
        ButterworthFilterOrder = 3
    }
};
```

---

## ?? **Your Current Program.cs (Simplified)**

```csharp
if (info.MaterialName.Contains("GROUNDMODEL_ASPHALT1", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Configuring road smoothing for layer {info.Index}");
    
    // Use the recommended preset
    roadParameters = RoadSmoothingPresets.TerrainFollowingSmooth;
    
    // Customize debug output
    roadParameters.DebugOutputDirectory = @"D:\temp\TestMappingTools\_output";
    roadParameters.SplineParameters.ExportSplineDebugImage = true;
    roadParameters.SplineParameters.ExportSkeletonDebugImage = true;
    roadParameters.SplineParameters.ExportSmoothedElevationDebugImage = true;
}
```

**That's it! Much cleaner!** ??

---

## ?? **Quick Reference: When to Tweak**

### Roads too bumpy?
```csharp
roadParameters.SplineParameters.SmoothingWindowSize = 301;  // Increase window
roadParameters.SplineParameters.ButterworthFilterOrder = 5; // Higher order
```

### Roads follow terrain too much (too hilly)?
```csharp
roadParameters.SplineParameters.GlobalLevelingStrength = 0.3f;  // Light leveling
roadParameters.RoadMaxSlopeDegrees = 3.0f;                      // Stricter limit
```

### Seeing dots/gaps again?
```csharp
roadParameters.TerrainAffectedRangeMeters = 15.0f;              // Widen blend
roadParameters.CrossSectionIntervalMeters = 0.4f;               // Denser samples
```

### Want ultra-flat racing circuit?
```csharp
roadParameters = RoadSmoothingPresets.MountainousUltraSmooth;   // Use this preset!
// OR
roadParameters.SplineParameters.GlobalLevelingStrength = 0.85f; // Enable leveling
roadParameters.TerrainAffectedRangeMeters = 20.0f;              // MUST widen!
```

---

## ?? **Parameter Organization Summary**

| Parameter Category | Where It Lives | Used By |
|-------------------|----------------|---------|
| **Road geometry** (width, blend range) | `RoadSmoothingParameters` | All approaches |
| **Slope constraints** | `RoadSmoothingParameters` | All approaches |
| **Blending type** | `RoadSmoothingParameters` | All approaches |
| **Spline extraction** | `SplineParameters` | SplineBased only |
| **Junction handling** | `SplineParameters` | SplineBased only |
| **Curve fitting** | `SplineParameters` | SplineBased only |
| **Elevation smoothing** | `SplineParameters` | SplineBased only |
| **Global leveling** | `SplineParameters` | SplineBased only |
| **Direct sampling** | `DirectMaskParameters` | DirectMask only |

---

## ? **Benefits of New Structure**

1. **Clear separation** - No confusion about which params are for which approach
2. **IntelliSense friendly** - IDE shows only relevant options
3. **Backward compatible** - Old code still works (params auto-forward to sub-objects)
4. **Easier presets** - Can share/copy configs more easily
5. **Type safety** - Can't set spline params when using DirectMask

---

## ?? **Test It Now**

```bash
dotnet run -- complex
```

**Look for clean, organized output:**
```
Configuring road smoothing for layer 16
  Using SPLINE-BASED approach
  Common settings: 8m road, 12m blend (28m total)
  Spline settings: Butterworth order=4, window=201, leveling=0.0
  ? No warnings
```

---

**TL;DR**: Parameters are now organized into **RoadSmoothingParameters** (common) + **SplineParameters** (spline-specific) + **DirectMaskParameters** (directmask-specific). Much cleaner! Use **presets** for simplicity or **manual config** for full control. ??

---

**YES, NOW I'M DONE!** ?

All fixed:
- ? Butterworth filter implemented
- ? Global leveling made optional (default=0)
- ? Parameters reorganized into clear structure
- ? Dotted roads fixed (terrain-following approach)
- ? Massive terrain impact fixed (12m blend instead of 30m)
- ? Build successful
- ? Documentation created
