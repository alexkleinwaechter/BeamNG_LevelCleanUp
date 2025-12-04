# Parameter Cleanup Recommendations (2024 EDITION)

**DOCUMENT STATUS**: ? **ACCURATE AS OF 2024** - Reflects actual codebase after refactoring

## Executive Summary

The `RoadSmoothingParameters` class has been **successfully refactored** with:
- ? Clean separation: Common params + approach-specific sub-objects
- ? Backward compatibility via proxy properties
- ? No "ImprovedSpline" (that was speculative documentation - doesn't exist!)
- ? **NEW REQUIREMENT**: Priority-based protection for multi-road scenarios

---

## Current Architecture (ACTUAL CODE)

### Two Approaches
1. **DirectMask** - Simple, robust, handles intersections (grid-aligned sampling)
2. **Spline** - Recommended for smooth roads (EDT-based, perpendicular sampling, O(W×H) performance)

### Parameter Organization
```
RoadSmoothingParameters (top level)
??? Common parameters (used by all approaches)
??? SplineParameters (SplineRoadParameters sub-object)
??? DirectMaskParameters (DirectMaskRoadParameters sub-object)
??? Backward compatibility properties (proxy to sub-objects)
```

---

## ? ALL PARAMETERS ARE STILL NEEDED (2024 Reality Check)

### Common Parameters (All Approaches) - **KEEP ALL**

#### Core Geometry
| Parameter | Default | Status | Notes |
|-----------|---------|--------|-------|
| `RoadWidthMeters` | 8.0f | ? ESSENTIAL | Road surface width |
| `TerrainAffectedRangeMeters` | 12.0f | ? ESSENTIAL | Shoulder/blend zone |
| `CrossSectionIntervalMeters` | 0.5f | ? ESSENTIAL | Sampling density |
| `LongitudinalSmoothingWindowMeters` | 20.0f | ? **ACTIVE** | Affects road length smoothness |

**Note:** Original doc claimed `LongitudinalSmoothingWindowMeters` might be obsolete with "ImprovedSpline". FALSE - still actively used by Spline approach!

#### Slope Constraints - **KEEP ALL**
| Parameter | Default | Status |
|-----------|---------|--------|
| `RoadMaxSlopeDegrees` | 4.0f | ? ESSENTIAL |
| `SideMaxSlopeDegrees` | 30.0f | ? ESSENTIAL |

#### Blending - **KEEP ALL**
| Parameter | Default | Status |
|-----------|---------|--------|
| `BlendFunctionType` | Cosine | ? ACTIVE |
| `EnableTerrainBlending` | true | ? ACTIVE |

#### ?? Post-Processing Smoothing (Added 2024) - **KEEP ALL**
| Parameter | Default | Status | Purpose |
|-----------|---------|--------|---------|
| `EnablePostProcessingSmoothing` | false | ? **NEW** | Eliminates staircase artifacts |
| `SmoothingType` | Gaussian | ? **NEW** | Filter algorithm choice |
| `SmoothingKernelSize` | 7 | ? **NEW** | Blur kernel size |
| `SmoothingSigma` | 1.5f | ? **NEW** | Gaussian strength |
| `SmoothingMaskExtensionMeters` | 6.0f | ? **NEW** | Shoulder smoothing extent |
| `SmoothingIterations` | 1 | ? **NEW** | Number of passes |

**Impact:** These solve the "road surface staircase" problem identified in testing. **Critical for quality.**

#### Exclusion Zones - **KEEP**
| Parameter | Default | Status |
|-----------|---------|--------|
| `ExclusionLayerPaths` | null | ? ACTIVE |

#### Debug Output - **KEEP ALL**
| Parameter | Default | Status |
|-----------|---------|--------|
| `DebugOutputDirectory` | null | ? ACTIVE |

---

### Spline-Specific Parameters (SplineRoadParameters) - **KEEP ALL**

#### Skeletonization
- ? `SkeletonDilationRadius` = 0

#### Junction Handling
- ? `PreferStraightThroughJunctions` = false
- ? `JunctionAngleThreshold` = 90.0f
- ? `MinPathLengthPixels` = 100.0f

#### Connectivity & Path Extraction
- ? `BridgeEndpointMaxDistancePixels` = 40.0f
- ? `DensifyMaxSpacingPixels` = 1.5f
- ? `SimplifyTolerancePixels` = 0.5f
- ? `UseGraphOrdering` = true
- ? `OrderingNeighborRadiusPixels` = 2.5f

#### Spline Curve Fitting - **ALL ACTIVELY USED**
| Parameter | Default | Status | Reality Check |
|-----------|---------|--------|---------------|
| `SplineTension` | 0.2f | ? **ACTIVE** | ? Original doc claimed "less critical with upsampling" - FALSE! |
| `SplineContinuity` | 0.7f | ? **ACTIVE** | ? Original doc claimed "less critical with upsampling" - FALSE! |
| `SplineBias` | 0.0f | ? **ACTIVE** | ? Original doc claimed "less critical with upsampling" - FALSE! |

**CORRECTION:** These are used by the **actual** Spline implementation (Kochanek-Bartels TCB splines). The "upsampling" approach was never implemented.

#### Elevation Smoothing - **ALL ACTIVELY USED**
| Parameter | Default | Status | Reality Check |
|-----------|---------|--------|---------------|
| `SmoothingWindowSize` | 301 | ? **ACTIVE** | Used for elevation profile smoothing |
| `UseButterworthFilter` | true | ? **ACTIVE** | Butterworth vs Gaussian choice |
| `ButterworthFilterOrder` | 4 | ? **ACTIVE** | Filter sharpness |
| `GlobalLevelingStrength` | 0.0f | ? **ACTIVE** | ? Original doc claimed "less needed" - FALSE! |

**CORRECTION:** All elevation smoothing parameters are actively used. The "ImprovedSpline" that would replace them **never existed**.

#### Debug Output - **KEEP ALL (CRITICAL)**
- ? `ExportSplineDebugImage` = true - **Essential for debugging**
- ? `ExportSkeletonDebugImage` = true - **Essential for debugging**
- ? `ExportSmoothedElevationDebugImage` = true - **Essential for debugging**

---

### DirectMask-Specific Parameters (DirectMaskRoadParameters) - **KEEP ALL**

#### Sampling
- ? `RoadPixelSearchRadius` = 5

#### Elevation Smoothing
- ? `UseButterworthFilter` = true
- ? `ButterworthFilterOrder` = 4

---

## ? PARAMETERS THAT NEVER EXISTED (Documentation Errors)

The original `PARAMETER_CLEANUP_PLAN.md` referenced a hypothetical "ImprovedSpline" approach with these parameters:

| Parameter | Status | Reality |
|-----------|--------|---------|
| `UpscaleFactor` | ? **NEVER EXISTED** | No upsampling/virtual heightfield in codebase |
| `ShoulderSmoothingIterations` | ? **NEVER EXISTED** | Use `SmoothingIterations` instead |
| `ImprovedBlendCurve` | ? **NEVER EXISTED** | Use `BlendFunctionType` instead |
| `EnableParallelProcessing` | ? **NEVER EXISTED** | Not implemented |

**Root Cause:** The original document was **speculative** about a future "ImprovedSpline" approach that was never implemented. The actual Spline approach uses EDT (Euclidean Distance Transform), not upsampling.

---

## ?? NEW PARAMETERS NEEDED (2024 Requirements)

### 1. Priority-Based Road Protection (HIGH PRIORITY)

**Problem:** Sequential processing of multiple roads (Highway ? Mountain ? Dirt) causes interference:
- Mountain roads overwrite Highway slopes at intersections
- Shoulder blending from different roads conflicts
- Post-processing smoothing inconsistencies

**Solution:** Add priority field to `MaterialDefinition` (NOT `RoadSmoothingParameters`):

```csharp
/// <summary>
/// Priority for road smoothing when multiple roads overlap.
/// Higher priority roads are protected from being overwritten by lower priority roads.
/// Examples:
///   - 3 = Highway (highest priority - preserves gentle slopes)
///   - 2 = Mountain roads
///   - 1 = Dirt roads
///   - 0 = Default (lowest priority)
/// Default: 0
/// </summary>
public int SmoothingPriority { get; set; } = 0;
```

**Status:** ? **NOT YET IMPLEMENTED** - See `ROAD_SMOOTHING_INTERFERENCE_ANALYSIS.md`

**Impact:** Fixes 80% of multi-road interference issues with minimal code changes (~2-3 hours implementation).

**Usage Example:**
```csharp
materials.Add(new MaterialDefinition(
    "GROUNDMODEL_ASPHALT1",
    layerPath,
    CreateHighwayRoadParameters())
{
    SmoothingPriority = 3  // Highest priority
});
```

---

## ? CLEANUP RECOMMENDATION (2024)

### DO NOT CLEANUP - ALL PARAMETERS ARE ACTIVE!

**Status:** ? **NO CLEANUP NEEDED**

**Rationale:**
1. ? **All parameters are actively used** - No obsolete code found
2. ? **Clean architecture already** - Common params + sub-objects pattern
3. ? **Backward compatibility working** - Proxy properties function correctly
4. ? **Debug output is critical** - Spline debug images are invaluable

**The original document's cleanup recommendations were based on a WRONG assumption** that "ImprovedSpline" would replace the current Spline approach. This never happened.

---

## Migration Strategy (None Needed!)

### Original Doc Suggested:
```csharp
[Obsolete("Less effective with ImprovedSpline. Use UpscaleFactor instead.")]
public float LongitudinalSmoothingWindowMeters { get; set; }
```

### **2024 Reality:**
```csharp
// DO NOT MARK AS OBSOLETE - Still actively used!
public float LongitudinalSmoothingWindowMeters { get; set; } = 20.0f;
```

**No migration needed.** Current parameter structure is optimal.

---

## Action Items (2024 Forward)

### ? Completed
- ? Refactored to approach-specific sub-objects
- ? Added post-processing smoothing parameters
- ? Added backward compatibility proxies
- ? Comprehensive parameter validation

### ? Planned (Priority Order)
1. **HIGH**: Implement priority-based road protection (`SmoothingPriority`)
2. **MEDIUM**: Add weighted height blending at intersections (optional enhancement)
3. **LOW**: Add shoulder-only protection mode (optional enhancement)

### ? DO NOT DO
- ? Mark any parameters as obsolete
- ? Remove any parameters
- ? Create "SimpleRoadSmoothingParameters" class (not needed - current API is already simple)
- ? Add upsampling/virtual heightfield parameters (not implemented)

---

## Comparison: Old Doc vs Reality

| Old Doc Claim | 2024 Reality | Status |
|---------------|--------------|--------|
| "ImprovedSpline makes params unnecessary" | ImprovedSpline never existed | ? FALSE |
| "`LongitudinalSmoothingWindowMeters` obsolete" | Still actively used | ? FALSE |
| "`GlobalLevelingStrength` less needed" | Still actively used | ? FALSE |
| "`SplineTension` less critical" | Still actively used | ? FALSE |
| "Add `UpscaleFactor` parameter" | No upsampling in codebase | ? FALSE |
| "Keep debug images" | Correct - they're invaluable | ? TRUE |
| "Backward compatibility important" | Correctly implemented | ? TRUE |

---

## Summary

**OLD DOCUMENT**: Based on speculative future "ImprovedSpline" approach  
**2024 REALITY**: All parameters are actively used, no cleanup needed  
**NEXT STEP**: Add `SmoothingPriority` to fix multi-road interference  

**Bottom Line:** The parameter structure is **already optimal**. The only enhancement needed is priority-based protection for multi-road scenarios.

---

## File Locations

- **Parameter Definition:** `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`
- **Material Definition:** `BeamNgTerrainPoc/Terrain/Models/MaterialDefinition.cs` ? Add `SmoothingPriority` here
- **Interference Analysis:** `BeamNgTerrainPoc/Docs/ROAD_SMOOTHING_INTERFERENCE_ANALYSIS.md`
- **Usage Examples:** `BeamNgTerrainPoc/Program.cs` (Highway/Mountain/Dirt methods)

---

**Document Revision History:**
- 2024: Complete rewrite based on actual codebase audit
- Original: Speculative document referencing non-existent "ImprovedSpline"
