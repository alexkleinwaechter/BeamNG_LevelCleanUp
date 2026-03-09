# T-Junction Flat Zone Fix — Session Log

**Date**: 2026-03-06
**Branch**: `research_rubberband_idea`

## Problem

Within the flat zone of T-junctions (where the terminating road overlaps the primary road, typically 3-5m), `BlendSplineProfile` applied a **constant elevation delta**:

```
delta = constraintElev_center - naturalElev[endpoint]
newElev[i] = naturalElev[i] + delta * h00
```

But the primary road surface **varies** within this zone due to longitudinal slope. On a 3% slope over 5m = 0.15m error; on 6% slope = 0.30m. This caused visible steps at road edges where the terminating road exits the primary road surface.

## Failed Approaches (Attempts 1-4: Post-Processing)

### Attempt 1: Surface projection + short blend margin (2-5m)
- `ApplyFlatZoneSurfaceSnap` snapped flat zone CSes to primary surface via `FindNearestPrimaryCS` + `GetPrimarySurfaceElevationClamped`
- Blend margin of 2-5m between snap and Hermite
- **Result**: Bump at flat zone boundary (transition too abrupt), false banking from surface projection

### Attempt 2: Extend blend to full BlendDistanceMeters (30m)
- Same surface projection approach but blended over the full 30m Hermite distance
- **Result**: Destroyed smooth Hermite profile entirely — bumps along the whole road

### Attempt 3: Residual decay
- Snap only in flat zone, then measure residual (gap between snap and Hermite at boundary), decay it over 5-15m
- **Result**: Still a bump at the flat zone boundary because snap and Hermite have different slopes at the boundary

### Attempt 4: Approach zone with surface projection (15m ramp)
- Gradual blend from Hermite toward snap in a 15m approach zone outside the flat zone
- **Result**: `FindNearestPrimaryCS` introduces noise — discrete CS lookups jump between different primary CSes, creating crumpled bumps

### Key Lesson
All post-processing approaches that use `FindNearestPrimaryCS` + surface projection are fundamentally noisy. The discrete CS lookup and planar projection at distance produce artifacts that no amount of blending can smooth out. **Post-processing removed entirely.**

## Failed Approaches (Attempts 5-7: Analytical Inside Hermite)

### Attempt 5: Additive slope correction to constant delta
Added `slope * dot(offset, tangent)` to the constant delta:
```
adjustedDelta = constantDelta + primarySlope * dot(csOffset, primaryTangent)
```
- **Result**: Wrong because the delta is relative to `naturalElev[endpoint]`, not `naturalElev[i]`. If the terrain slopes differently from the primary road, the baseline drifts. Made no visible difference at most junctions, worse at junctions with large elevation changes.

### Attempt 6: Per-CS absolute delta (everywhere in h00 zone)
Fixed to compute the full per-CS delta:
```
primarySurfaceElev = constraint.Elevation + slope * dot(offset, tangent)
adjDelta = primarySurfaceElev - naturalElev[i]
```
Applied across the entire h00 zone (flat zone + blend distance).
- **Result**: Within flat zone: exact primary surface match (correct!). Beyond flat zone: the linear extrapolation of `primarySurfaceElev` diverges from terrain — at 15-30m from junction the extrapolated surface is wildly wrong, causing the road to float above or dig below terrain even with h00 decay.

### Attempt 7: Per-CS delta in flat zone only + H10 slope basis
- Per-CS delta limited to flat zone only (where linear approximation is accurate)
- Beyond flat zone: constant delta * h00 (original behavior)
- Added Hermite h10 slope basis (`t³ - 2t² + t`) for slope matching at flat zone edge
- `correction = delta * h00 + slopeDelta * blendDist * h10`
- **Result**: h10 creates a visible HUMP peaking at ~10m from flat zone edge. The factor `slopeDelta * blendDist * h10_peak` = `0.05 * 30 * 0.148 = 0.22m` for a 5% slope difference. For steep junctions (10%+), the hump reaches 0.5m+. **Worse than the original problem.** h10 removed.

## Current State (end of session)

**What remains in code:**
- `PrimaryTangentDirection` (Vector2?) on `JunctionEndpointConstraint` — set by `ComputeTJunctionConstraints`
- Per-CS delta in `BlendSplineProfile`, **limited to flat zone only** (`dist <= flatZone`)
- Beyond flat zone: constant delta * h00 (original behavior, unchanged)
- No post-processing (all `ApplyFlatZoneSurfaceSnap`, `FindNearestPrimaryCS` removed)
- No h10 slope basis (removed)

**What works:**
- Flat zone snap within BlendSplineProfile (per-CS delta gives exact primary surface)
- Smooth h00 Hermite ramp beyond the flat zone

**What still doesn't work:**
- The snap in the flat zone appears to NOT be matching the primary surface correctly at some junctions (road endpoint floats above primary road — see last screenshot)
- Possible root cause: the constraint elevation itself may be wrong (computed in `ComputeTJunctionConstraints` from `primaryCS.TargetElevation` after Pass 1)
- The slope discontinuity at the flat zone boundary remains (terrain slope vs primary road slope)

## Next Session Plan

### Priority 1: Fix the snap — road endpoint must match primary surface
The road endpoint is floating above the primary road at some junctions. This means either:
1. The **constraint elevation** is wrong — `ComputeTJunctionConstraints` reads `primaryCS.TargetElevation` after Pass 1, but this value may not represent the actual primary surface at the junction
2. The **per-CS delta** is computed incorrectly — check the analytical formula at the endpoint (offset=0, should give `constraintElev - naturalElev`)
3. Something else **overwrites** the elevation after `BlendSplineProfile` — check `ApplyMidSplineCrossingInfluences`, `ApplyEndpointTapering`, `ComputeJunctionIdwWeightModifiers`

**Debugging approach:**
- Add logging in `BlendSplineProfile` for T-junction endpoints: print constraintElev, naturalElev, adjDelta, h00, newElev
- Add logging in `ComputeTJunctionConstraints`: print primaryCS.TargetElevation, primarySlope, computed centerElev
- Compare with the actual primary road surface visible in the 3D export
- Check if the snap worked in the original post-processing approach (Attempt 1) — if it did, the constraint elevation is likely correct and the issue is in how the analytical delta is applied

### Priority 2: Smooth ramp from flat zone edge to blend distance
Once the snap works correctly, the transition beyond the flat zone needs work:
- h00-only Hermite arrives at the junction with terrain slope, not primary road slope
- h10 approach failed due to large humps (blendDist * slopeDelta too large)
- Consider: shorter effective blend for slope correction only, or different basis function
- Consider: the snap approach (Attempt 1) actually worked for the flat zone — maybe a hybrid of analytical snap + short-distance surface projection for the ramp

## Key Files
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — `BlendSplineProfile`, `ComputeTJunctionConstraints`, `ApplyUnifiedProfiles`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` — constraint record with `PrimaryTangentDirection`
- `BeamNgTerrainPoc/Terrain/Algorithms/JunctionSurfaceCalculator.cs` — `GetPrimarySurfaceElevation`/`Clamped`
