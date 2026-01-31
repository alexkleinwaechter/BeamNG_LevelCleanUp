# Road Parameter Methods - Standardized Structure

This document shows the standardized parameter ordering used across all three road type configuration methods.

---

## Standardized Parameter Order

All three methods (`CreateHighwayRoadParameters`, `CreateMountainRoadParameters`, `CreateDirtRoadParameters`) now follow this exact structure:

### 1. **APPROACH: SPLINE (OPTIMIZED)**
```csharp
Approach = RoadSmoothingApproach.Spline,
EnableTerrainBlending = true,
DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\{roadType}",
```

### 2. **ROAD GEOMETRY**
```csharp
RoadWidthMeters = {value}f,
TerrainAffectedRangeMeters = {value}f,
CrossSectionIntervalMeters = {value}f,
```

### 3. **SLOPE CONSTRAINTS**
```csharp
RoadMaxSlopeDegrees = {value}f,
SideMaxSlopeDegrees = {value}f,
```

### 4. **BLENDING**
```csharp
BlendFunctionType = BlendFunctionType.Cosine,
```

### 5. **POST-PROCESSING SMOOTHING**
```csharp
EnablePostProcessingSmoothing = true,
SmoothingType = PostProcessingSmoothingType.Gaussian,
SmoothingKernelSize = {value},
SmoothingSigma = {value}f,
SmoothingMaskExtensionMeters = {value}f,
SmoothingIterations = 1,
```

### 6. **SPLINE-SPECIFIC SETTINGS**

#### 6a. Skeletonization
```csharp
SkeletonDilationRadius = 0,
```

#### 6b. Junction handling
```csharp
PreferStraightThroughJunctions = false,
JunctionAngleThreshold = 90.0f,
MinPathLengthPixels = {value}f,
```

#### 6c. Connectivity & path extraction
```csharp
BridgeEndpointMaxDistancePixels = {value}f,
DensifyMaxSpacingPixels = {value}f,
SimplifyTolerancePixels = {value}f,
UseGraphOrdering = true,
OrderingNeighborRadiusPixels = 2.5f,
```

#### 6d. Spline curve fitting
```csharp
SplineTension = {value}f,
SplineContinuity = {value}f,
SplineBias = 0.0f,
```

#### 6e. Elevation smoothing
```csharp
SmoothingWindowSize = {value},
UseButterworthFilter = {bool},
ButterworthFilterOrder = {value},
GlobalLevelingStrength = 0.0f,
```

#### 6f. Debug output
```csharp
ExportSplineDebugImage = true,
ExportSkeletonDebugImage = true,
ExportSmoothedElevationDebugImage = true
```

---

## Side-by-Side Comparison

### Highway vs Mountain vs Dirt

| Section | Parameter | Highway | Mountain | Dirt |
|---------|-----------|---------|----------|------|
| **Approach** | Approach | Spline | Spline | Spline |
| | EnableTerrainBlending | true | true | true |
| | DebugOutputDirectory | highway | mountain | dirt |
| **Geometry** | RoadWidthMeters | **8.0** | **6.0** | **5.0** |
| | TerrainAffectedRangeMeters | **6.0** | **8.0** | **6.0** |
| | CrossSectionIntervalMeters | **0.5** | **0.5** | **0.75** |
| **Slopes** | RoadMaxSlopeDegrees | **6.0** | **8.0** | **10.0** |
| | SideMaxSlopeDegrees | **45.0** | **35.0** | **40.0** |
| **Blending** | BlendFunctionType | Cosine | Cosine | Cosine |
| **Post-Process** | EnablePostProcessingSmoothing | true | true | true |
| | SmoothingType | Gaussian | Gaussian | Gaussian |
| | SmoothingKernelSize | **7** | **5** | **5** |
| | SmoothingSigma | **1.5** | **1.0** | **0.8** |
| | SmoothingMaskExtensionMeters | **6.0** | **4.0** | **3.0** |
| | SmoothingIterations | 1 | 1 | 1 |
| **Spline: Skeleton** | SkeletonDilationRadius | 0 | 0 | 0 |
| **Spline: Junction** | PreferStraightThroughJunctions | false | false | false |
| | JunctionAngleThreshold | 90.0 | 90.0 | 90.0 |
| | MinPathLengthPixels | **100.0** | **50.0** | **40.0** |
| **Spline: Connectivity** | BridgeEndpointMaxDistancePixels | **40.0** | **30.0** | **25.0** |
| | DensifyMaxSpacingPixels | **1.5** | **1.5** | **2.0** |
| | SimplifyTolerancePixels | **0.5** | **0.5** | **0.75** |
| | UseGraphOrdering | true | true | true |
| | OrderingNeighborRadiusPixels | 2.5 | 2.5 | 2.5 |
| **Spline: Curve** | SplineTension | **0.2** | **0.3** | **0.4** |
| | SplineContinuity | **0.7** | **0.5** | **0.3** |
| | SplineBias | 0.0 | 0.0 | 0.0 |
| **Spline: Elevation** | SmoothingWindowSize | **301** | **201** | **51** |
| | UseButterworthFilter | **true** | **true** | **false** |
| | ButterworthFilterOrder | **4** | **3** | **2** |
| | GlobalLevelingStrength | 0.0 | 0.0 | 0.0 |
| **Spline: Debug** | ExportSplineDebugImage | true | true | true |
| | ExportSkeletonDebugImage | true | true | true |
| | ExportSmoothedElevationDebugImage | true | true | true |

**Bold** = Values that differ between road types

---

## Logical Grouping Rationale

### Group 1: Approach Settings
**Why first?** These define the fundamental algorithm approach and must be set before any other parameters make sense.

### Group 2: Road Geometry
**Why second?** Physical dimensions of the road are the most important user-visible parameters. These directly affect the visual result.

### Group 3: Slope Constraints
**Why third?** After defining road size, slope constraints determine how steep the road can be - closely related to geometry.

### Group 4: Blending
**Why fourth?** How the road blends into terrain is a fundamental characteristic that affects all subsequent processing.

### Group 5: Post-Processing Smoothing
**Why fifth?** This is applied AFTER the main smoothing, so it logically comes after the core parameters but before advanced spline settings.

### Group 6: Spline-Specific Settings
**Why last?** These are advanced, algorithm-specific tuning parameters. Most users won't need to modify these, so they're at the end.

**Within Group 6, sub-ordering:**
1. **Skeletonization** - First step in spline extraction
2. **Junction handling** - How to handle intersections (if any)
3. **Connectivity** - How to connect skeleton fragments
4. **Curve fitting** - How to fit splines to skeleton
5. **Elevation smoothing** - How to smooth elevations along the road
6. **Debug output** - Optional visualization (last)

---

## Consistency Benefits

### ? **Easy Comparison**
All three methods have identical structure, making it trivial to compare values between road types.

### ? **Copy-Paste Friendly**
Need to create a new road type? Copy any method, change the values, done. The structure is identical.

### ? **Maintainability**
Adding a new parameter? Add it in the same location in all three methods to maintain consistency.

### ? **Documentation**
The standardized order makes documentation easier - you can describe parameters once and apply to all road types.

### ? **Code Reviews**
Reviewers can quickly scan for differences by comparing the same positions in each method.

---

## Example: Adding a New Parameter

If a new parameter `SomeNewParameter` is added to `RoadSmoothingParameters` in the "Road Geometry" section, add it **in the same position** in all three methods:

```csharp
// In CreateHighwayRoadParameters():
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 6.0f,
SomeNewParameter = 10.0f,              // NEW - add here in all methods
CrossSectionIntervalMeters = 0.5f,

// In CreateMountainRoadParameters():
RoadWidthMeters = 6.0f,
TerrainAffectedRangeMeters = 8.0f,
SomeNewParameter = 12.0f,              // NEW - same position
CrossSectionIntervalMeters = 0.5f,

// In CreateDirtRoadParameters():
RoadWidthMeters = 5.0f,
TerrainAffectedRangeMeters = 6.0f,
SomeNewParameter = 8.0f,               // NEW - same position
CrossSectionIntervalMeters = 0.75f,
```

---

## Comment Standardization

All section headers follow this format:

```csharp
// ========================================
// SECTION NAME
// Optional description
// ========================================
```

**Sub-section comments** (within Spline-Specific):
```csharp
// Sub-section name (lowercase)
```

**Inline comments** (parameter explanations):
```csharp
ParameterName = value,                   // Short explanation
```

### Comment Alignment
- Section headers: Left-aligned with parameters
- Sub-section comments: Left-aligned with parameters
- Inline comments: Aligned at column 45 (after parameter value)

---

## Verification Checklist

When modifying any of the three road type methods, verify:

- [ ] All 6 main sections present in same order
- [ ] Within Spline-Specific, all 6 sub-sections in same order
- [ ] Section header format matches exactly
- [ ] Inline comment alignment consistent
- [ ] Debug output directory updated for road type
- [ ] All parameters present (even if value is same across types)
- [ ] No orphaned parameters outside logical groupings

---

## Future Road Type Template

When creating a new road type method, use this template:

```csharp
/// <summary>
/// Creates road smoothing parameters for [ROAD TYPE NAME].
/// - [Width] meters wide
/// - [Key characteristic 1]
/// - [Key characteristic 2]
/// </summary>
static RoadSmoothingParameters Create[RoadType]RoadParameters()
{
    return new RoadSmoothingParameters
    {
        // ========================================
        // APPROACH: SPLINE (OPTIMIZED)
        // ========================================
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\[roadtype]",

        // ========================================
        // ROAD GEOMETRY - [Description]
        // ========================================
        RoadWidthMeters = [value]f,
        TerrainAffectedRangeMeters = [value]f,
        CrossSectionIntervalMeters = [value]f,

        // ========================================
        // SLOPE CONSTRAINTS - [Description]
        // ========================================
        RoadMaxSlopeDegrees = [value]f,
        SideMaxSlopeDegrees = [value]f,

        // ========================================
        // BLENDING
        // ========================================
        BlendFunctionType = BlendFunctionType.Cosine,

        // ========================================
        // POST-PROCESSING SMOOTHING
        // [Description of smoothing intensity]
        // ========================================
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = [value],
        SmoothingSigma = [value]f,
        SmoothingMaskExtensionMeters = [value]f,
        SmoothingIterations = 1,

        // ========================================
        // SPLINE-SPECIFIC SETTINGS
        // ========================================
        SplineParameters = new SplineRoadParameters
        {
            // Skeletonization
            SkeletonDilationRadius = 0,

            // Junction handling
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = [value]f,

            // Connectivity & path extraction
            BridgeEndpointMaxDistancePixels = [value]f,
            DensifyMaxSpacingPixels = [value]f,
            SimplifyTolerancePixels = [value]f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline curve fitting
            SplineTension = [value]f,
            SplineContinuity = [value]f,
            SplineBias = 0.0f,

            // Elevation smoothing
            SmoothingWindowSize = [value],
            UseButterworthFilter = [true/false],
            ButterworthFilterOrder = [value],
            GlobalLevelingStrength = 0.0f,

            // Debug output
            ExportSplineDebugImage = true,
            ExportSkeletonDebugImage = true,
            ExportSmoothedElevationDebugImage = true
        }
    };
}
```

---

## Summary

All three road type parameter methods now have:
- ? **Identical structure** - Same 6 sections in same order
- ? **Consistent sub-grouping** - Same 6 spline sub-sections
- ? **Standardized comments** - Same header and inline comment format
- ? **Logical ordering** - Parameters flow from general to specific
- ? **Easy comparison** - Values differ but structure is identical

This standardization makes the codebase more maintainable, easier to understand, and simpler to extend with new road types.
