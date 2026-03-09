# Junction Ramp Problem — Session 2026-03-05

## Branch: `research_rubberband_idea`

## Starting State (before this session's changes)

The unified junction system (`UnifiedJunctionProfileBlender`) had a two-pass Hermite approach:

1. **Pass 1**: Hermite blend all non-T-junction-terminating splines (primary roads get correct elevation first)
2. **Pass 2**: Recompute T-junction constraints from post-blend primary elevations, then Hermite blend terminating roads
3. **Step 4**: Derive edge elevations from (TargetElevation, BankAngle)

The Hermite used **delta correction**: `newElev = naturalElev[i] + delta * h00(t)` where `delta = constraintElev - naturalElev[endpoint]`.

A **FlatZoneDistance** concept kept the correction at 100% within the primary road's half-width (the overlap zone).

### Known Problem at Session Start

The constant delta doesn't match the spatially varying primary surface. Within the flat zone, `naturalElev[i] + delta` produces wrong values because:
- The primary road surface changes due to **longitudinal slope** (going uphill/downhill)
- The primary road surface changes due to **banking** (lateral tilt)

The delta was computed at ONE point (junction center). Adding this same delta at other positions gives incorrect results if the primary surface at those positions has a different elevation.

**Result**: Visible step at road edges at T-junctions (red overlap artifact in 2D top-down view).

## What Was Tried in This Session

### Attempt 1: Overlap Snap Step (after Hermite)

Added `SnapOverlapZonesToPrimarySurface` as Step 3 between Pass 2 Hermite and edge derivation:

- For each T-junction terminating road, walk cross-sections from the junction endpoint outward
- Within `FlatZoneDistance` (primary half-width): snap each CS to the exact primary surface using `JunctionSurfaceCalculator.GetPrimarySurfaceElevationClamped` with the nearest primary CS
- Within a 3m blend margin beyond: quintic smoothstep from snap value to Hermite value
- Used `FindNearestPrimaryCS` (O(n) linear scan) for each terminating CS to find closest primary road CS

Also added `PrimarySplineId` and `PrimarySlope` fields to `JunctionEndpointConstraint` so the snap step could look up the primary road's cross-sections.

**Result**: The overlap zone matched the primary surface, but the TRANSITION from the road's natural elevation to the junction was not smooth. The Hermite beyond the snap zone started from the wrong elevation because the constant delta was computed at the junction center, not the edge.

### Attempt 2: Direct Interpolation + Edge-Based Constraint

Two changes to fix the transition:

**A. Direct interpolation in `BlendSplineProfile`**

Changed from delta correction to direct interpolation for T-junctions (detected by `FlatZoneDistance > 0`):

```
OLD (delta): newElev = naturalElev[i] + (constraintElev - naturalElev[endpoint]) * h00(t)
NEW (direct): newElev = constraintElev * h00(t) + naturalElev[i] * (1 - h00(t))
```

The direct interpolation creates a clean ramp between the junction elevation and the natural terrain-following elevation. Since `h00 + h01 = 1`, this is a pure weighted average. When only one constraint exists: `constraintElev * h00 + naturalElev * (1 - h00)`.

At junction (t=0): `constraintElev` (correct).
Far away (t=1): `naturalElev` (correct).
In between: smooth ramp (no terrain undulations near junction).

The idea: delta correction preserves terrain undulations (good far from junctions), while direct interpolation ignores them (good near junctions where you want a clean ramp).

**B. Edge-based constraint computation in `ComputeTJunctionConstraints`**

Changed from computing the constraint at the junction CENTER to computing it at the ROAD EDGE (flat zone boundary):

- Find the terminating road CS closest to `primaryHalfWidth` distance from the junction endpoint
- Project that CS onto the primary surface
- Use that as the Hermite's target elevation (instead of the junction center elevation)

The idea: the Hermite's target should match what the snap produces at the flat zone boundary, so the handoff is seamless.

**Result**: **CATASTROPHIC FAILURE**. Two problems:

1. **Roads dig meters deep into terrain** (blue marking in screenshot). The direct interpolation approach with `constraintElev * h00 + naturalElev * (1-h00)` doesn't follow terrain AT ALL near the junction — it interpolates between a constant target and the natural elevation. On sloped terrain where the junction elevation differs significantly from the natural elevation along the road, this creates deep cuts or tall walls as the road ramps to the junction level over its full length.

2. **Still no smooth ramp at junctions** (red marking in screenshot). The fundamental problem remains: the transition from terrain-following to junction-matching is not smooth.

### Why Direct Interpolation Failed

The direct interpolation `constraintElev * h00 + naturalElev * (1-h00)` operates over the ENTIRE road length (the Hermite basis h00 decays from 1 to 0 across the full effective length). For a 200m road with a junction at one end, the ramp extends all 200m. If the junction is 2m above the road's natural level at its endpoint, the road is lifted by `2 * h00(t)` at every point. Near the far end h00 is tiny, but near the junction (even outside the overlap zone), the road is lifted significantly — way above the surrounding terrain.

The delta correction doesn't have this problem because it adds a delta ON TOP of the terrain-following profile. The road still follows terrain undulations; it's just shifted up/down. The issue with delta correction is only within the flat zone where the constant delta doesn't match the varying primary surface.

### Why the Overlap Snap Alone Wasn't Enough

The snap correctly handles the flat zone (within the primary road width). The problem is the TRANSITION beyond the flat zone. The Hermite with delta correction provides a smooth transition far from the junction, but at the flat zone boundary there's a discontinuity because:

- Snap gives: `primarySurfaceAt(edge)` (exact, varies with slope/banking)
- Hermite gives: `naturalElev[edge] + (constraintElev_center - naturalElev[endpoint])` (approximate, constant delta)

These differ by: the terrain variation from endpoint to edge + the primary surface variation from center to edge. On sloped terrain, this can be several cm to tens of cm — enough to see.

## The Core Problem to Solve

**How to create a smooth, realistic ramp from a terminating road's natural terrain-following elevation to the primary road's surface at the road edge of a T-junction.**

Requirements:
1. Within the overlap zone (primary road width): the terminating road must EXACTLY match the primary road surface (slope + banking)
2. At the primary road edge: seamless transition — no step, no kink
3. Beyond the road edge: the terminating road should smoothly approach its natural terrain-following elevation over a reasonable distance (30-60m per real road design standards)
4. The transition can dig into or rise above the surrounding terrain (terraforming) in the junction blending area
5. The approach should work on sloped terrain where junction elevation differs significantly from the surrounding terrain
6. The junction blending solution must work well together with the max slope parameter for roads if enabled.

### The Fundamental Tension

- **Delta correction** follows terrain but can't match a varying surface in the flat zone
- **Direct interpolation** can match the target but doesn't follow terrain, causing cuts/fills

### Possible Approaches to Explore

**A. Local Hermite with limited blend distance**

Instead of blending across the entire road length, use a LIMITED blend distance (e.g., 30-60m from the road edge). Within this distance, use a Hermite that transitions from the snap value at the edge to the natural elevation at the blend boundary. Beyond this distance, the road is purely terrain-following based on somoothing parameters with little terraforming which is needed to get smooth roads.

This avoids the "digging into terrain" problem because the correction is localized. The delta at the boundary is small (natural terrain at 30m from junction is close to natural terrain at the boundary).

Key: the blend distance must be long enough for a smooth ramp but short enough to not distort the road far from the junction.

**B. Re-smoothing with pinned endpoints**

After computing junction elevations (Phase 3), pin the cross-sections at the flat zone boundary to the correct primary surface elevation, then RE-RUN the elevation smoother (Phase 2) for just those splines. The smoother naturally creates smooth profiles that converge at the pinned points.

This leverages the iterative loop that already exists (up to 3 iterations of Phase 2 + Phase 3). The WI-6 endpoint anchoring mechanism (`ApplyEndpointAnchoring`) could be extended to support junction anchors with weight=1.0 at the pinned point and exponential decay along the road.

Caution: the existing code explicitly avoids anchoring at multi-road junctions because it previously caused "ditch" artifacts. Would need careful implementation.

**C. Two-stage correction**

Stage 1 (delta correction): Apply the existing Hermite delta correction across the full road. This preserves terrain following but has the wrong constant delta in the flat zone.

Stage 2 (local snap + local ramp): Within and near the flat zone, override with:
- Flat zone: exact primary surface snap (already implemented)
- Ramp zone (edge to edge + blendDistance): compute the delta between snap value at the edge and Hermite value at the edge. Distribute this delta using a LOCAL correction that decays to zero at `edge + blendDistance`. Use quintic smoothstep or similar.

This way the global Hermite handles long-range profile adjustment, and the local correction handles the precision at the junction boundary. The local correction only needs to fix the small residual error at the snap-Hermite boundary, not the full elevation difference.

**D. Surface-following within the flat zone only**

Keep the Hermite delta correction for everything BEYOND the flat zone. For cross-sections WITHIN the flat zone, project each CS individually onto the nearest primary road CS's surface (the snap step). At the boundary, the snap and Hermite should nearly agree because:

- The delta correction is based on the junction center constraint
- The snap is based on the actual primary surface at each CS
- At the road edge, these are close (separated by ~primary half-width)

The residual error at the boundary is typically small (< 5cm for typical slopes). A 2-3m blend margin can absorb this. The key is getting the blend margin right and making sure the Hermite's delta is reasonable.

This is essentially what Attempt 1 did. The remaining issue was that the 3m blend margin wasn't always enough and the Hermite started from slightly wrong values. Could be improved by:
- Computing the delta at the EDGE position (not center) — this was part of Attempt 2 and is correct in isolation
- BUT keeping delta correction (not switching to direct interpolation)
- Increasing the blend margin adaptively based on the actual residual error at the boundary

## Key Files Reference

- `UnifiedJunctionProfileBlender.cs` — main blender, Steps 1-7, contains `BlendSplineProfile`, `ComputeTJunctionConstraints`, `SnapOverlapZonesToPrimarySurface`
- `JunctionEndpointConstraint.cs` — constraint record with Elevation, Slope, BankAngle, FlatZoneDistance, PrimarySplineId, PrimarySlope
- `JunctionSurfaceCalculator.cs` — `GetPrimarySurfaceElevation`, `GetPrimarySurfaceElevationClamped`, surface projection utilities
- `UnifiedRoadSmoother.cs` — pipeline wiring, Phase 2+3 iteration loop, `BuildEndpointAnchorLookup`
- `OptimizedElevationSmoother.cs` — Phase 2 elevation calculation, WI-6 endpoint anchoring
- `NetworkJunctionHarmonizer.cs` — legacy harmonizer (junction detection, elevation negotiation)
- `BankingOrchestrator.cs` — two-phase banking (pre/post harmonization)

## Pipeline Order (relevant phases)

1. **Phase 1.8**: Junction detection (topology only, before elevation)
2. **Phase 2**: Elevation smoothing (`OptimizedElevationSmoother` — terrain sampling + filtering)
3. **Phase 2.5**: Banking pre-calculation (curvature → bank angles → edge elevations)
4. **Phase 3**: Junction harmonization + unified profile blending
   - Captures `originalElevations` and `originalBankAngles`
   - Runs harmonizer (for junction detection/classification)
   - Restores originals (discards harmonizer's rubberband)
   - Runs `UnifiedJunctionProfileBlender.ApplyUnifiedProfiles`:
     - Step 1: Compute constraints
     - Step 2 (Pass 1): Hermite blend primary roads
     - Step 2 (Pass 2): Recompute T-junction constraints from post-blend primary, Hermite blend terminating roads
     - Step 3: Snap overlap zones (if implemented)
     - Step 4: Derive edge elevations
     - Steps 5-7: Mid-spline crossings, endpoint tapering, IDW modifiers
5. **Phase 4**: Terrain blending (burns road elevations into heightmap)

Phases 2-3 iterate up to 3 times for convergence.

## Real-World Junction Design Standards (for reference)

- Max gradient near junction: 3% desirable, 5% absolute
- Cross slope at junction: 1.5-2%
- Max superelevation at junction: 4% desirable
- Side road should be ≤4% gradient for 30m from main road edge
- No crest curves at intersections
- Minimum 30-60m of controlled gradient before stop line

## Test Preset

`D:\Temp\Test_Cleanup\__preset_france_italy\theTerrain_terrainPreset.json`

## State After Revert

The user will revert the changes from this session. After revert, the code is back to commit `3828f0a`:
- Two-pass Hermite with delta correction
- FlatZoneDistance concept (but snap step removed)
- No direct interpolation
- No edge-based constraint computation
- No `SnapOverlapZonesToPrimarySurface`
- No `PrimarySplineId`/`PrimarySlope` on constraint
- The original flat zone problem remains (constant delta in overlap zone)

Feature flag `JunctionHarmonizationParameters.UseUnifiedJunctionSystem` (default: true) can toggle back to legacy system.
