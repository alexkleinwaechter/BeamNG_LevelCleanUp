# Road Smoothing Improvements: Implementation Plan

**Date:** 2026-02-25
**Based on:** `ai_agent_md_files_history_some_outdated/road-smoothing-survey.md`
**Scope:** 10 ordered work items addressing 7 root causes of junction bumpiness, derived from 8 survey proposals.

---

## Current Pipeline (Reference)

Orchestrated by `UnifiedRoadSmoother.SmoothAllRoads()` in [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs):

```
Phase 1:   Build Unified Road Network        (UnifiedRoadNetworkBuilder)
Phase 1.5: Identify Roundabout Splines       (closed-loop detection)
Phase 1.8: Early Junction Detection           (NetworkJunctionDetector, topology-only) [WI-5]
─── Iteration Loop Start (WI-4) ───
Phase 2:   Calculate Target Elevations        (OptimizedElevationSmoother, per-spline + endpoint anchoring [WI-6])
Phase 2.3: Structure Elevation Profiles       (StructureElevationIntegrator, bridges/tunnels)
Phase 2.5: Banking Pre-calculation            (BankingOrchestrator)
Phase 2.6: Roundabout Elevation Harmonization (RoundaboutElevationHarmonizer)
Phase 3:   Junction Harmonization             (NetworkJunctionHarmonizer, uses pre-detected junctions)
─── Iteration Loop End (converge or max 3) ───
Phase 3.5: Banking Finalization               (BankingOrchestrator)
Phase 4:   Terrain Blending                   (UnifiedTerrainBlender, single-pass protected)
Phase 5:   Material Painting                  (MaterialPainter)
```

## Pipeline After All Work Items

```
Phase 1:   Build Unified Road Network
Phase 1.5: Identify Roundabout Splines
Phase 1.7: Road Corridor Grouping             (NEW - WI-10)
Phase 1.8: Early Junction Detection            (WI-5, implemented)
─── Iteration Loop Start (WI-4) ───
Phase 2:   Calculate Target Elevations         (+ endpoint anchoring from WI-6)
Phase 2.3: Structure Elevation Profiles
Phase 2.5: Banking Pre-calculation             (first iteration only)
Phase 2.6: Roundabout Elevation Harmonization  (first iteration only)
Phase 3:   Junction Harmonization              (+ adaptive blend WI-2, Hermite blend WI-3)
─── Iteration Loop End (converge or max 3) ───
Phase 3.5: Banking Finalization
Phase 4:   Terrain Blending                    (+ bilinear road core WI-1, junction plateau WI-9,
                                                  junction-aware IDW WI-8)
Phase 5:   Material Painting
```

---

## Dependency Graph

```
WI-1  (Per-Pixel Bilinear)         standalone
WI-2  (Adaptive Blend Distance)    standalone
WI-3  (C1 Hermite Blend)           benefits from WI-2
WI-4  (Iterative Refinement)       benefits from WI-2, WI-3
WI-5  (Early Junction Detection)   standalone (prerequisite for WI-6, WI-9)
WI-6  (Endpoint Anchoring)         requires WI-5, benefits from WI-4 ✅
WI-7  (Auto-Calculate Params)      standalone
WI-8  (Junction-Aware IDW)         standalone, benefits from WI-9
WI-9  (Junction Plateau)           requires WI-5
WI-10 (Road Corridors)             standalone, benefits from WI-1
```

---

## Work Items

---

### WI-1: Per-Pixel Bilinear Road Core Elevation

**Survey Proposal:** 6
**Root Cause:** #2 — Cross-section-to-pixel discretization creates staircasing

#### Problem

Non-banked road segments use `GetSegmentAverageElevation(cs1, cs2)` which returns `(cs1.TargetElevation + cs2.TargetElevation) / 2` for the ENTIRE quad polygon between two cross-sections. With 0.5m cross-section spacing, this creates a staircase ribbing pattern on the road surface. The banked code path already computes per-pixel elevation correctly.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [BankedTerrainHelper.cs](BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankedTerrainHelper.cs) | `GetBankedElevationForPixel()` line 284 | Remove early exit that calls `GetSegmentAverageElevation()` when neither cs has banking. Instead always call `GetBankedElevationInSegment()`. |
| [RoadMaskBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs) | `FillConvexPolygonWithOwnershipAndBanking()` line 213-216 | Remove `hasBanking` conditional. Always compute per-pixel elevation using the same bilinear interpolation. Remove the `averageElevation` pre-computation. |

#### Implementation

In `GetBankedElevationForPixel()` at [BankedTerrainHelper.cs:284](BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankedTerrainHelper.cs#L284), the current code:
```csharp
if (!HasBanking(cs1) && !HasBanking(cs2))
    return GetSegmentAverageElevation(cs1, cs2);
```
Replace with a call to `GetBankedElevationInSegment(cs1, cs2, worldPos)` which already handles the non-banked case by interpolating along the segment direction using the `t` parameter (fraction along segment). The function projects the pixel position onto the segment direction vector and lerps between the two cross-section elevations.

In `FillConvexPolygonWithOwnershipAndBanking()` at [RoadMaskBuilder.cs:213](BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L213), remove:
```csharp
var averageElevation = hasBanking ? 0f : BankedTerrainHelper.GetSegmentAverageElevation(cs1, cs2);
```
And in the pixel loop, always call `BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos)` instead of conditionally using `averageElevation`.

**Performance:** Adds one `Vector2` dot product + lerp per road core pixel. Road cores are ~2-5% of total pixels, so overhead is negligible.

#### Acceptance Criteria

- Non-banked road surfaces show a smooth elevation gradient along the road direction
- No visible ribbing/staircase pattern on straight roads at any grade
- Banked road behavior is unchanged (already per-pixel)
- Blend zone behavior is unchanged

#### Verification

1. Generate terrain with a single non-banked road on a 5% grade slope
2. Export debug heightmap (`ExportSmoothedHeightmapWithOutlines` — already exists)
3. Zoom to a straight road section; measure pixel-to-pixel elevation along road direction
4. **Before:** elevation is flat within each 0.5m segment, then jumps at segment boundaries
5. **After:** elevation changes smoothly from pixel to pixel with no visible ribbing

#### Dependencies

None.

---

### WI-2: Adaptive Blend Distance Based on Elevation Difference

**Survey Proposal:** 8
**Root Cause:** #6 — Junction harmonization blend is not re-smoothed (fixed blend too short on steep terrain)

#### Problem

The blend distance for junction constraint propagation is a fixed value (`JunctionBlendDistanceMeters`, default 30m). On steep terrain where a junction requires correcting a 5m elevation difference, 30m of blend creates a 9.5% grade ramp — visibly steep. The blend distance should scale with the actual elevation correction needed.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) | `PropagateJunctionConstraints()` ~line 856 | Calculate adaptive blend distance per contributor |
| [NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) | `PropagateEdgeConstraintsForTJunctions()` ~line 1032 | Same adaptive calculation |

#### Implementation

In `PropagateJunctionConstraints()`, where the blend distance is determined per contributor, replace the fixed lookup:

```csharp
// CURRENT:
var blendDistance = /* from JunctionBlendDistanceMeters */;

// PROPOSED:
var elevDiff = MathF.Abs(junction.HarmonizedElevation - contributor.CrossSection.TargetElevation);
var maxSlopeDeg = contributor.Spline.Parameters.RoadMaxSlopeDegrees;
// If max slope is disabled, use a sensible default (e.g. 6 degrees)
var effectiveSlopeDeg = contributor.Spline.Parameters.EnableMaxSlopeConstraint
    ? maxSlopeDeg
    : 6.0f;
var slopeBasedDistance = elevDiff / MathF.Tan(effectiveSlopeDeg * MathF.PI / 180f);
var minBlendDistance = /* existing JunctionBlendDistanceMeters lookup */;
var blendDistance = MathF.Max(minBlendDistance, slopeBasedDistance);
```

Apply the same logic in `PropagateEdgeConstraintsForTJunctions()` and `CollectBidirectionalInfluences()`.

Log the adaptive blend distance for debugging: `$"Junction {junctionId}: contributor spline {splineId} blend distance {blendDistance:F1}m (elev diff {elevDiff:F2}m)"`.

#### Parameter Change: `JunctionBlendDistanceMeters`

This parameter stays in the UI but its semantics change from "the blend distance" to "minimum blend distance". The actual distance is `max(this, computed)`. On flat terrain, behavior is identical. The UI label should update to "Minimum Junction Blend Distance (m)" and the tooltip should explain the adaptive behavior.

#### Acceptance Criteria

- On flat terrain: blend distance equals `JunctionBlendDistanceMeters` (unchanged behavior)
- On steep terrain with 5m elevation correction: blend distance increases to `5.0 / tan(4deg)` = ~71m
- All existing presets continue to work
- Log output shows variable blend distances per junction

#### Verification

1. Generate terrain on a hilly area (>5% grade at junctions)
2. Compare before/after junction debug images (existing `ExportJunctionDebugImage`)
3. Log output confirms variable blend distances
4. Visual: junction approach ramps are gentler on steep terrain
5. On flat terrain: verify no change in behavior (diff output heightmap)

#### Dependencies

None (can be done in parallel with WI-1).

---

### WI-3: C1 Hermite Junction Blending

**Survey Proposal:** 5
**Root Cause:** #6 — C0 but not C1 continuity at blend boundaries

#### Problem

Current blend functions (Cosine, Cubic, Quintic) guarantee elevation continuity at the junction blend boundary but not slope continuity. The slope can jump at the point where the junction blend zone meets the original smoothed profile, creating a visible "kink".

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [JunctionHarmonizationParameters.cs](BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs) | `JunctionBlendFunctionType` enum, line 207 | Add `CubicHermiteC1` value |
| [NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) | `PropagateJunctionConstraints()` ~line 887 | Add Hermite blend path |
| [NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) | New method `ApplyHermiteBlend()` | Cubic Hermite interpolation |

#### Implementation

1. Add to `JunctionBlendFunctionType` enum:
   ```csharp
   /// <summary>
   ///     Cubic Hermite interpolation matching elevation AND slope at both endpoints.
   ///     Guarantees C1 continuity (no slope discontinuity at blend boundary).
   /// </summary>
   CubicHermiteC1
   ```

2. New method `ApplyHermiteBlend()`:
   ```csharp
   /// <summary>
   /// Cubic Hermite interpolation for C1-continuous junction blending.
   /// Matches both elevation and slope at junction center (t=0) and blend boundary (t=1).
   /// </summary>
   private static float ApplyHermiteBlend(
       float t,                  // 0..1 normalized distance from junction
       float e0,                 // elevation at junction (t=0)
       float s0,                 // slope at junction (rise per meter, in blend direction)
       float e1,                 // elevation at blend boundary (t=1)
       float s1,                 // slope at blend boundary (rise per meter)
       float blendDistance)      // total blend distance in meters
   {
       // Scale slopes to the [0,1] parameter domain
       var m0 = s0 * blendDistance;
       var m1 = s1 * blendDistance;
       // Hermite basis functions
       var t2 = t * t;
       var t3 = t2 * t;
       var h00 = 2 * t3 - 3 * t2 + 1;
       var h10 = t3 - 2 * t2 + t;
       var h01 = -2 * t3 + 3 * t2;
       var h11 = t3 - t2;
       return h00 * e0 + h10 * m0 + h01 * e1 + h11 * m1;
   }
   ```

3. Calculate slopes:
   - **Junction slope (`s0`)**: Already available from `CalculatePrimaryRoadSlope()` at [NetworkJunctionHarmonizer.cs:1012](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs#L1012). For non-T-junctions, use finite difference between the junction cross-section and its next neighbor.
   - **Boundary slope (`s1`)**: Finite difference at the blend boundary position: `(cs[boundaryIdx+1].TargetElevation - cs[boundaryIdx-1].TargetElevation) / (2 * crossSectionInterval)`.

4. In `PropagateJunctionConstraints()`, when applying influences, check for `CubicHermiteC1` blend type and call `ApplyHermiteBlend()` instead of `ApplyBlendFunction()`.

5. Make `CubicHermiteC1` the new default in `JunctionHarmonizationParameters.BlendFunctionType`. Existing Cosine/Cubic/Quintic paths remain for backward compatibility.

#### Acceptance Criteria

- At the blend boundary, slope is continuous (no visible kink)
- Existing blend function types (Linear, Cosine, Cubic, Quintic) continue to work unchanged
- New `CubicHermiteC1` is the default for new materials

#### Verification

1. Generate terrain with a T-junction on a slope
2. Extract a 1D elevation profile along the terminating road from junction center outward
3. Compute first derivative (finite difference) of the profile
4. **Before:** derivative jumps at blend boundary
5. **After:** derivative is continuous through the blend boundary
6. Visual: no visible kink where junction blend meets smoothed profile

#### Dependencies

Benefits from WI-2 (adaptive blend distance provides better endpoint matching). Functionally independent.

---

### WI-4: Iterative Junction Refinement

**Survey Proposal:** 4
**Root Cause:** #6 — Harmonized elevation corrections are not re-smoothed

#### Problem

The pipeline is single-pass: smooth then harmonize. Phase 3 "patches" Phase 2's output with junction corrections, but these patches are not themselves smoothed. The result is that junction corrections create profiles that would benefit from another smoothing pass.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) | `SmoothAllRoads()` ~lines 199-341 | Wrap Phase 2+3 in convergence loop |
| [OptimizedElevationSmoother.cs](BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs) | `CalculateTargetElevations()` | Add flag to re-smooth from existing TargetElevation instead of re-sampling heightmap |
| [NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) | `HarmonizeNetwork()` return value | Return `MaxElevationChange` for convergence check |

#### Implementation

In `SmoothAllRoads()`, wrap the elevation + harmonization phases in a loop:

```csharp
const int maxIterations = 3;
const float convergenceThresholdMeters = 0.01f;
float previousMaxCorrection = float.MaxValue;

for (int iteration = 0; iteration < maxIterations; iteration++)
{
    // Phase 2: Calculate/re-smooth elevations
    // On iteration 0: sample from heightmap (existing behavior)
    // On iteration > 0: re-smooth using existing TargetElevation values as input
    CalculateNetworkElevations(network, heightMap, metersPerPixel, reSmoothFromExisting: iteration > 0);

    // Phases 2.3, 2.5, 2.6 run ONLY on iteration 0
    if (iteration == 0)
    {
        // Structure elevation profiles (bridges/tunnels)
        // Banking pre-calculation
        // Roundabout elevation harmonization
    }

    // Phase 3: Harmonize junctions
    // On iteration 0: detect + harmonize
    // On iteration > 0: re-harmonize only (reuse detected junctions)
    var result = _junctionHarmonizer.HarmonizeNetwork(..., skipDetection: iteration > 0);

    var maxCorrection = result.MaxElevationChange;
    LogInfo($"Iteration {iteration + 1}: max elevation correction = {maxCorrection:F3}m");

    if (maxCorrection < convergenceThresholdMeters)
    {
        LogInfo($"Converged after {iteration + 1} iteration(s)");
        break;
    }
    if (maxCorrection > previousMaxCorrection * 0.9f && iteration > 0)
    {
        LogInfo($"Not improving, stopping after {iteration + 1} iteration(s)");
        break;
    }
    previousMaxCorrection = maxCorrection;
}
```

The key detail: `CalculateTargetElevations()` with `reSmoothFromExisting = true` uses the existing `TargetElevation` values on cross-sections as the raw input array (instead of re-sampling from the heightmap). This way the smoother operates on the already-harmonized profile.

`HarmonizeNetwork()` needs to return the max elevation change it applied, for convergence checking. Add a `MaxElevationChange` field to the return/result.

#### Acceptance Criteria

- Max correction in iteration 2 is < 10% of iteration 1 on typical terrain
- On flat terrain, converges in 1 iteration (iteration 2 max correction < 0.01m)
- Total runtime increase < 3x for Phases 2+3 (these are not the bottleneck; Phase 4 is)
- Output is identical or better for all existing presets

#### Verification

1. Generate terrain on hilly area
2. Log output shows convergence: e.g. `Iteration 1: 2.3m, Iteration 2: 0.18m, Iteration 3: 0.01m → converged`
3. Visual: junction approach ramps are smoother
4. On flat terrain: log shows `Iteration 1: 0.008m → converged after 1 iteration`

#### Dependencies

Benefits from WI-2 (adaptive blend) and WI-3 (C1 blend), which reduce the initial corrections and improve convergence.

---

### WI-5: Early Junction Detection ✅ IMPLEMENTED

**Survey Proposal:** Prerequisite for Proposal 1
**Root Cause:** Structural — junction info needed before elevation smoothing

#### Problem

Junction detection currently runs in Phase 3, after elevation smoothing. To anchor spline endpoints to junction elevations (WI-6), we need junction locations available before Phase 2. This is a restructuring-only change with no behavioral impact.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) | `SmoothAllRoads()` ~line 199 | Add Phase 1.8: detection-only pass before Phase 2 |
| [NetworkJunctionDetector.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs) | `DetectJunctions()` | No changes — already produces junctions from network topology alone |

#### Implementation

1. Move the `_junctionDetector.DetectJunctions(network, globalDetectionRadius)` call from Phase 3 to a new Phase 1.8 between Phase 1.5 (roundabout identification) and Phase 2 (elevation calculation).

2. The `NetworkJunctionHarmonizer.HarmonizeNetwork()` already handles pre-detected junctions — it checks if `network.Junctions` is populated (lines 112-127) and skips re-detection. No changes needed in the harmonizer.

3. The crossroad-to-T-junction conversion remains in Phase 3 because it depends on elevation data for splitting decisions.

4. Roundabout junctions from Phase 2.6 are merged with the early-detected junctions using the existing `RestoreRoundaboutJunctions` pattern.

#### Acceptance Criteria

- Junction detection results are identical whether run in Phase 1.8 or Phase 3
- `HarmonizeNetwork()` correctly uses the pre-detected junctions (no re-detection)
- Output heightmap is bit-identical before and after restructuring

#### Verification

1. Generate terrain with a known junction layout
2. Compare junction debug image before and after — should be identical
3. Log shows same junction count and type classification
4. Diff output heightmap: bit-identical

#### Dependencies

None.

---

### WI-6: Junction-Aware Elevation Smoothing with Endpoint Anchoring ✅ IMPLEMENTED

**Survey Proposal:** 1
**Root Cause:** #1 — Per-spline smoothing creates junction elevation mismatches

#### Problem

Each spline is smoothed independently with no knowledge of where it connects to other splines. On hilly terrain, a spline's smoothed endpoint can be meters away from the junction's eventual harmonized elevation, forcing Phase 3 to create large corrections (visible ramps).

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) | `CalculateNetworkElevations()` | Pass detected junctions to elevation calculator |
| [OptimizedElevationSmoother.cs](BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs) | `CalculateTargetElevations()` ~line 45 | Add endpoint anchoring after filtering |

#### Implementation

1. Pass `network.Junctions` (from WI-5's early detection) to `CalculateTargetElevations()`.

2. Build a lookup: `Dictionary<(int splineId, bool isStart), float anchorElevation>` mapping spline endpoints that participate in junctions to the terrain elevation at the junction center.

3. After Butterworth/Box filtering produces the `smoothed[]` array, apply anchoring:
   ```csharp
   // For each spline, check if start/end is at a junction
   if (startAnchor.HasValue)
   {
       for (int i = 0; i < sections.Count; i++)
       {
           var distFromStart = sections[i].DistanceAlongSpline;
           var anchorDecay = blendDistanceMeters; // match junction blend distance
           var weight = 0.5f * MathF.Exp(-distFromStart / anchorDecay);
           smoothed[i] = smoothed[i] * (1f - weight) + startAnchor.Value * weight;
       }
   }
   // Similar for end anchor, using distance from spline end
   ```

4. For splines with both endpoints at junctions (common for short connector roads), apply both anchors — the exponential decays from both ends naturally blend in the middle.

5. The anchor elevation is the TERRAIN elevation at the junction center (sampled from heightmap), not a harmonized value (harmonization hasn't run yet). This is the best available estimate.

#### Acceptance Criteria

- On hilly terrain, smoothed endpoint elevation is within 50% of the terrain elevation at the junction (vs several meters divergence currently)
- On flat terrain, anchoring has minimal effect (anchor and smoothed values agree)
- When combined with WI-4, Phase 3 max correction in iteration 1 is reduced by 50%+

#### Verification

1. Generate terrain on hilly area
2. Log endpoint-to-junction terrain elevation difference before and after anchoring
3. **Before:** differences of 2-5m on steep terrain
4. **After:** differences of 0.5-1.5m
5. Combined with WI-4: iteration 1 max correction is much smaller

#### Dependencies

Requires WI-5 (early junction detection). Benefits from WI-4 (iterative refinement further reduces residuals).

---

### WI-7: Auto-Calculate Width-Dependent Parameters

**Root Cause:** Configuration complexity — several parameters depend on road width but are manually configured with fixed defaults

#### Problem

`JunctionDetectionRadiusMeters` (5m), `RoundaboutConnectionRadiusMeters` (10m), and `SmoothingMaskExtensionMeters` (6m) have fixed defaults that don't adapt to road width. A 4m path needs different values than a 20m highway. Users rarely tune these correctly.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [JunctionHarmonizationParameters.cs](BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs) | Properties | Add `AutoCalculateDetectionRadius` toggle + computed properties |
| [RoadSmoothingParameters.cs](BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs) | Properties | Add auto-calculation for `SmoothingMaskExtensionMeters` |
| [NetworkJunctionDetector.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs) | `DetectJunctions()` ~line 220 | Use auto-calculated radius when enabled |
| [TerrainMaterialSettings.razor.cs](BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs) | Junction settings UI | Show computed value read-only with override toggle |

#### Implementation

1. In `JunctionHarmonizationParameters.cs`:
   ```csharp
   /// <summary>
   ///     When true, auto-calculates detection and connection radii from road width.
   ///     The computed values are shown in the UI and can be overridden by disabling this.
   /// </summary>
   public bool AutoCalculateFromRoadWidth { get; set; } = true;

   public float GetEffectiveDetectionRadius(float roadWidthMeters)
       => AutoCalculateFromRoadWidth
           ? MathF.Max(5.0f, roadWidthMeters / 2f + 2.0f)
           : JunctionDetectionRadiusMeters;

   public float GetEffectiveRoundaboutConnectionRadius(float roadWidthMeters)
       => AutoCalculateFromRoadWidth
           ? MathF.Max(10.0f, roadWidthMeters / 2f + 5.0f)
           : RoundaboutConnectionRadiusMeters;
   ```

2. In `RoadSmoothingParameters.cs`:
   ```csharp
   public float GetEffectiveSmoothingMaskExtension()
       => JunctionHarmonizationParameters?.AutoCalculateFromRoadWidth == true
           ? MathF.Max(6.0f, RoadWidthMeters * 0.75f)
           : SmoothingMaskExtensionMeters;
   ```

3. In `NetworkJunctionDetector.cs`, update all places that read `JunctionDetectionRadiusMeters` to call `GetEffectiveDetectionRadius(spline.Parameters.RoadWidthMeters)`.

4. **UI changes** in `TerrainMaterialSettings.razor.cs`:
   - Add a `MudSwitch` toggle: "Auto-calculate from road width"
   - When enabled: show the computed values as read-only `MudTextField` with a "Computed" label
   - When disabled: show editable `MudNumericField` (existing behavior)
   - The toggle maps to `AutoCalculateFromRoadWidth`

#### Auto-Calculation Formulas

| Parameter | Formula | Example 4m road | Example 12m road |
|-----------|---------|-----------------|-------------------|
| `JunctionDetectionRadiusMeters` | `max(5.0, width / 2 + 2.0)` | 5.0m | 8.0m |
| `RoundaboutConnectionRadiusMeters` | `max(10.0, width / 2 + 5.0)` | 10.0m | 11.0m |
| `SmoothingMaskExtensionMeters` | `max(6.0, width * 0.75)` | 6.0m | 9.0m |

#### Acceptance Criteria

- With `AutoCalculateFromRoadWidth = true` (default), values adapt to road width
- With toggle off, manual values take effect (backward compatible)
- Existing presets work unchanged
- UI shows computed values when auto-calculate is on

#### Verification

1. Generate terrain with mixed road widths (4m paths + 12m highways)
2. All junctions detected correctly for both wide and narrow roads
3. Toggle auto-calculate off, set custom values, verify they take effect
4. Compare junction detection with explicit vs auto-calculated radii

#### Dependencies

None (can be done in parallel with WI-1 through WI-6).

---

### WI-8: Junction-Aware IDW Filtering

**Survey Proposal:** 7
**Root Cause:** #3 — IDW elevation mixing at junctions

#### Problem

`InterpolateNearbyCrossSectionsBuffered()` uses inverse-distance-weighted interpolation from ALL nearby cross-sections. At junctions, cross-sections from multiple roads with different elevations coexist. The IDW creates an arbitrary blend that doesn't follow any road's actual surface, producing bumps/dips at junction centers.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| [ElevationMapBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs) | `BuildElevationMapWithOwnership()` ~line 49 | Build spatial index of junction areas at start |
| [ElevationMapBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs) | `InterpolateNearbyCrossSectionsBuffered()` ~line 282 | Filter cross-sections by owner, except within junction areas |

#### Implementation

1. At the start of `BuildElevationMapWithOwnership()`, build a junction area lookup:
   ```csharp
   // Simple radius-based junction areas
   var junctionAreas = network.Junctions
       .Where(j => !j.IsExcluded)
       .Select(j => (
           center: j.Position,
           radius: j.Contributors.Max(c => c.Spline.Parameters.RoadWidthMeters / 2f) + 2f
       ))
       .ToList();
   ```

2. Pass `ownerSplineId` and `junctionAreas` to `InterpolateNearbyCrossSectionsBuffered()`.

3. In the IDW loop, filter contributions:
   ```csharp
   for (var i = 0; i < count; i++)
   {
       var (cs, dist) = searchBuffer[i];

       // Filter: only same-road cross-sections contribute,
       // UNLESS we're within a junction area (where all roads share elevation)
       if (cs.OwnerSplineId != ownerSplineId)
       {
           if (!IsInAnyJunctionArea(worldPos, junctionAreas))
               continue; // Skip cross-road contamination
       }

       // ... existing IDW weight calculation
   }
   ```

4. `IsInAnyJunctionArea()` is a simple distance check. With a spatial hash (50m cells, matching the existing cross-section spatial index), this is O(1) per pixel.

#### Acceptance Criteria

- Blend zone pixels near junctions get elevation only from their own road's cross-sections
- Junction center pixels (within junction area) still get blended elevation from all roads
- No regression on flat terrain (all cross-sections at similar elevation, filtering has no visible effect)

#### Verification

1. Generate terrain with a T-junction where the side road is 2m higher than the main road
2. Measure elevation in the main road's blend zone near the junction
3. **Before:** elevation bump from IDW mixing with side road cross-sections
4. **After:** main road blend zone follows only main road elevation
5. At junction center: elevation matches harmonized value (unchanged)

#### Dependencies

Standalone. Benefits from WI-9 (junction plateau provides explicit junction area geometry).

---

### WI-9: Junction Plateau Area with 2D Elevation Kernel

**Survey Proposal:** 2
**Root Cause:** #3, #4, #7 — IDW mixing, protection mask gaps, no plateau geometry

#### Problem

Junctions are just points where spline endpoints meet. There's no 2D area defined as "the junction surface" where elevation should be flat. The protection mask has triangular gaps at junction boundaries. The junction center elevation is determined entirely by IDW, which mixes nearby cross-sections arbitrarily.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| New file: `JunctionGeometryCalculator.cs` | `BeamNgTerrainPoc/Terrain/Algorithms/` | Compute convex hull junction polygons |
| [NetworkJunction.cs](BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs) | Properties | Add `Polygon` (Vector2[]) property |
| [RoadMaskBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs) | After road core mask | Rasterize junction polygons into protection mask |
| [UnifiedTerrainBlender.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/UnifiedTerrainBlender.cs) | `BlendNetworkWithTerrain()` | Insert junction plateau step before elevation map |
| [ElevationMapBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs) | `BuildElevationMapWithOwnership()` | Respect junction plateau ownership |

#### Implementation

1. **Junction polygon computation** (`JunctionGeometryCalculator`):
   - For each junction with 2+ contributors:
     - Collect the endpoint cross-sections from all contributors
     - Get left and right edge points: `cs.CenterPoint ± cs.NormalDirection * (cs.EffectiveRoadWidth / 2)`
     - Compute convex hull of all edge points (Graham scan or Andrew's monotone chain)
     - Expand hull by `max(contributorWidths) / 4` to fill gaps
     - Store as `Vector2[]` polygon on the `NetworkJunction` object

2. **Rasterize junction polygons** (new method in `RoadMaskBuilder` or a step in `UnifiedTerrainBlender`):
   - After building road core protection mask but before building elevation map
   - For each junction polygon, fill pixels inside with:
     - `protectionMask[y, x] = true`
     - `ownershipMap[y, x]` = special junction ID (e.g., `-1000 - junctionId` to distinguish from spline IDs)
     - `elevationMap[y, x]` = junction's harmonized elevation
   - Junction plateau has effective priority = `junction.MaxPriority + 1` (wins over all road cores)

3. **Elevation map integration**:
   - In `BuildElevationMapWithOwnership()`, pixels with junction ownership get their elevation from the pre-set junction value (skip IDW entirely)
   - For blend zone pixels near a junction plateau, compute distance to the polygon boundary (not to individual cross-sections) for the blend factor

4. **Blend from polygon boundary**:
   - For pixels outside junction polygon but within blend range, use 2D distance from polygon boundary
   - This creates smooth radially symmetric blending around the entire junction area

#### Acceptance Criteria

- Junction center is flat at harmonized elevation, covering the full area where roads overlap
- No protection mask gaps at junction boundaries
- Blend transitions smoothly from junction plateau to terrain in all directions
- Non-junction areas are unaffected

#### Verification

1. Generate terrain with a multi-way junction (3+ roads)
2. Measure elevation within junction polygon: should be uniform (flat)
3. Measure at polygon boundary: smooth transition starts
4. **Before:** bumps/dips at junction center
5. **After:** flat plateau with smooth surrounding blend
6. Export debug image showing junction polygons overlaid on protection mask

#### Dependencies

Requires WI-5 (early junction detection provides junction data for polygon computation).

---

### WI-10: Road Corridor Grouping for Parallel Carriageways

**Survey Proposal:** 3
**Root Cause:** #5 — Blend zone boundary discontinuities for overlapping roads

#### Problem

OSM represents divided highways as two separate ways. The system treats each as an independent road, creating median ridge/valley artifacts, inconsistent banking, and blend zone interference between carriageways.

#### Files to Modify

| File | Location | Change |
|------|----------|--------|
| New file: `RoadCorridorDetector.cs` | `BeamNgTerrainPoc/Terrain/Algorithms/` | Detect and group parallel carriageways |
| [ParameterizedRoadSpline.cs](BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs) | Properties | Add `CorridorGroupId` and `IsCorridorMember` |
| [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) | `SmoothAllRoads()` | Insert Phase 1.7 corridor detection |
| [OptimizedElevationSmoother.cs](BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs) | `CalculateTargetElevations()` | Handle corridor groups (shared elevation profile) |
| [RoadMaskBuilder.cs](BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs) | Protection mask | Shared median ownership for corridors |

#### Implementation

1. **Corridor detection** (`RoadCorridorDetector`):
   ```
   For each pair of same-material, same-priority splines:
     a. Sample points at 20m intervals along both splines
     b. Calculate average perpendicular distance between centerlines
     c. Calculate angular alignment (average dot product of tangent vectors)
     d. If distance < 2 * maxRoadWidth AND alignment > cos(10°): corridor pair
   Use Union-Find to group transitive pairs into corridor groups.
   ```

2. **Corridor elevation profile**:
   - Calculate corridor centerline (average of the two carriageway centerlines, resampled)
   - Apply elevation smoothing to the corridor centerline once
   - Each carriageway derives its `TargetElevation` from the corridor profile + lateral offset (for cross-slope or banking)

3. **Protection mask**:
   - For corridor groups, the median area between carriageways shares ownership (corridor-level, not spline-level)
   - Blend zone extends from the OUTER edges of the combined corridor, not from each carriageway independently

4. **Phase integration**: New Phase 1.7 runs after Phase 1.5, before Phase 2. It modifies the network: grouped splines share a `CorridorGroupId`. Subsequent phases check this ID.

#### Acceptance Criteria

- Dual carriageway highways have smooth median (no ridge/valley)
- Banking is consistent across the full corridor width
- Single-carriageway roads are completely unaffected
- Corridor detection doesn't false-positive on nearby but separate roads

#### Verification

1. Generate terrain with a divided highway from OSM
2. Inspect median area between carriageways
3. **Before:** visible ridge or valley artifact
4. **After:** smooth terrain across corridor
5. Verify isolated roads have no behavioral change

#### Dependencies

Benefits from WI-1 (per-pixel elevation for the widened corridor). Standalone otherwise.

---

## Parameters Summary

### Auto-Calculated (UI: read-only with manual override toggle)

| Parameter | Formula | Becomes Auto In |
|-----------|---------|----------------|
| `JunctionDetectionRadiusMeters` | `max(5.0, RoadWidthMeters / 2 + 2.0)` | WI-7 |
| `RoundaboutConnectionRadiusMeters` | `max(10.0, RoadWidthMeters / 2 + 5.0)` | WI-7 |
| `SmoothingMaskExtensionMeters` | `max(6.0, RoadWidthMeters * 0.75)` | WI-7 |
| `JunctionBlendDistanceMeters` | `max(configured, elevDiff / tan(maxSlope))` | WI-2 (becomes min floor) |

### New Parameters/Enum Values

| Parameter | Default | Purpose | Work Item |
|-----------|---------|---------|-----------|
| `CubicHermiteC1` enum value | New default | C1-continuous junction blend | WI-3 |
| `AutoCalculateFromRoadWidth` | `true` | Toggle for auto-calculated radii | WI-7 |

### Backward Compatibility

- All existing presets work unchanged
- Explicit per-material overrides take precedence over auto-calculated values
- Iterative refinement (WI-4) is transparent (identical output on flat terrain)
- Old `JunctionBlendFunctionType` values (Cosine, Cubic, etc.) remain available

---

## Implementation Order Summary

| Order | WI | Description | Complexity |
|-------|-----|------------|-----------|
| 1 | WI-1 | Per-pixel bilinear road core elevation | Low |
| 2 | WI-2 | Adaptive blend distance | Very Low |
| 3 | WI-3 | C1 Hermite junction blending | Low |
| 4 | WI-4 | Iterative junction refinement | Low-Medium |
| 5 | WI-5 | Early junction detection (prerequisite) | Low |
| 6 | WI-6 | Junction-aware elevation smoothing | Medium |
| 7 | WI-7 | Auto-calculate width-dependent parameters | Low |
| 8 | WI-8 | Junction-aware IDW filtering | Medium |
| 9 | WI-9 | Junction plateau area | High |
| 10 | WI-10 | Road corridor grouping | High |
