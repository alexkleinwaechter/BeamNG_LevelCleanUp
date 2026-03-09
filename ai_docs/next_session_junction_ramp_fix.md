# Next Session: Junction Ramp Problem — T-Junction Flat Zone Fix

## Quick Context

- Branch: `research/junction_improvements`
- Feature flag: `JunctionHarmonizationParameters.UseUnifiedJunctionSystem` (default: true)
- Test preset: `D:\Temp\Test_Cleanup\__preset_france_italy\theTerrain_terrainPreset.json`

## What Changed Since the Original Document

The original problem document (`ai_docs/junction_ramp_problem_2026-03-05.md`) describes attempts that failed because the Hermite correction spread across the **entire road length**, causing roads to dig deep into terrain or float above it.

**That problem is now fixed.** `BlendSplineProfile` in `UnifiedJunctionProfileBlender.cs` was rewritten to use **localized per-end Hermite delta correction** with a configurable `BlendDistanceMeters` (default 30m, adaptive for steep terrain). Each junction's influence is now confined to its blend zone — beyond it, the road follows the terrain-following profile from Phase 2 with no correction.

Key changes already made:
- `JunctionEndpointConstraint` now has `BlendDistanceMeters` property (set per constraint using adaptive logic)
- `BlendSplineProfile` computes independent h00 weights per end, each within its own `(flatZone + blendDistance)` zone
- `CalculateAdaptiveBlendDistance` added to `UnifiedJunctionProfileBlender` (extends blend for large elevation gaps)
- All three constraint computation methods (`ComputeTJunctionConstraints`, `ComputeMultiWayConstraints`, `ComputeEndpointConstraints`) now set `BlendDistanceMeters`

**What this means for the junction ramp problem**: The failed "direct interpolation" and "full-road Hermite" approaches from the original document are no longer relevant — they were caused by the full-road spread, which is now fixed. The remaining problem is specifically about the **flat zone** at T-junctions.

## The Remaining Problem: Constant Delta in the Flat Zone

Within the flat zone (primary road half-width, typically 3-5m from the junction center), the code applies:

```
newElev = naturalElev[i] + (constraintElev_center - naturalElev[endpoint])
```

This is a **constant delta** computed at one point (the junction center). But the primary road surface **varies** within this zone due to:
1. **Longitudinal slope** — the primary road goes uphill/downhill
2. **Banking** — the primary road has lateral tilt

Result: visible step at road edges where the terminating road exits the primary road surface.

## Current Code State

### `BlendSplineProfile` (UnifiedJunctionProfileBlender.cs, ~line 396)
- Uses localized h00 per end with `BlendDistanceMeters`
- Within flat zone: `startH00 = 1.0` (full delta correction)
- The flat zone delta is the problem — it doesn't track the varying primary surface

### `ComputeTJunctionConstraints` (UnifiedJunctionProfileBlender.cs, ~line 213)
- Projects terminating road edges onto primary surface using `JunctionSurfaceCalculator.GetPrimarySurfaceElevation`
- Computes centerline elevation = average of edge projections
- Computes bank angle from edge elevation difference
- Sets `FlatZoneDistance = primaryCS.EffectiveRoadWidth / 2f`
- Sets `BlendDistanceMeters` using adaptive logic

### `JunctionSurfaceCalculator.cs`
- `GetPrimarySurfaceElevation(worldPos, primaryCS, primarySlope)` — projects a point onto the primary road's surface plane (banking + longitudinal slope)
- `GetPrimarySurfaceElevationClamped` — same but clamps offsets to prevent extrapolation
- These work correctly for individual point projections

## The Core Problem to Solve

**How to make the terminating road's elevation within the flat zone exactly match the spatially varying primary road surface, with a smooth transition to the localized Hermite delta correction beyond the flat zone.**

### Requirements
1. Within the overlap zone (flat zone = primary road half-width): the terminating road must **exactly match** the primary road surface at each cross-section position (not just a constant delta)
2. At the flat zone boundary (road edge): seamless handoff to the Hermite delta correction — no step, no kink
3. Beyond the flat zone: the existing localized Hermite delta correction handles the transition back to terrain-following (this already works)

### Why This Is Easier Now

With the localized blend distance fix, the Hermite delta correction at the flat zone boundary is small (the correction has only decayed slightly from its full value). The residual error between "snap to primary surface" and "constant delta" at the road edge is typically small:
- The delta is computed at the junction center
- The snap projects onto the primary surface at the road edge
- These positions are separated by `primaryRoadHalfWidth` (~3-5m)
- On a 3% slope over 5m = 0.15m difference
- On a 6% slope over 5m = 0.30m difference

So the discontinuity at the boundary is small and a short blend margin (2-5m) should absorb it.

## Recommended Approach: Two-Stage Correction (Approach C from original doc)

Now that the Hermite is localized, Approach C becomes viable:

**Stage 1 (existing)**: Localized Hermite delta correction — already implemented, handles blend zone correctly.

**Stage 2 (new)**: Flat zone snap + boundary blend:
1. For each T-junction terminating road, walk cross-sections from junction center outward
2. **Within flat zone** (dist < flatZoneDistance): snap each CS to the primary road surface using `JunctionSurfaceCalculator.GetPrimarySurfaceElevation` with the nearest primary CS
3. **Within blend margin** (flatZoneDistance < dist < flatZoneDistance + blendMargin): interpolate between snap value and Hermite delta value using quintic smoothstep
4. **Beyond blend margin**: Hermite delta correction unchanged

The blend margin can be adaptive: `max(2m, residualError * 10)` where `residualError` = difference between snap and Hermite at the flat zone boundary.

### Implementation Notes

- Stage 2 runs **after** `BlendSplineProfile` (as a post-processing step within `ApplyUnifiedProfiles`)
- Needs access to primary road cross-sections (the two-pass architecture already processes primary roads first in Pass 1)
- `PrimarySplineId` needs to be stored on `JunctionEndpointConstraint` so Stage 2 knows which spline to project onto (was tried in original doc's Attempt 1, removed after revert — needs re-adding)
- `FindNearestPrimaryCS` can be used to find the closest primary CS for projection

## Key Files to Read

1. `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — main blender, current `BlendSplineProfile` with localized Hermite
2. `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` — constraint record (has BlendDistanceMeters, FlatZoneDistance)
3. `BeamNgTerrainPoc/Terrain/Algorithms/JunctionSurfaceCalculator.cs` — surface projection utilities
4. `ai_docs/junction_ramp_problem_2026-03-05.md` — original investigation (for historical context of failed attempts)

## What NOT to Do (Lessons from Failed Attempts)

1. **Don't use direct interpolation** (`constraintElev * h00 + naturalElev * (1-h00)`) — it doesn't follow terrain, even with localized blend distance
2. **Don't compute the delta at the edge position instead of center** — the delta should still be at the center for the Hermite; the snap handles the flat zone separately
3. **Don't replace the entire BlendSplineProfile** — it's working correctly now for the transition zone; only the flat zone needs fixing
