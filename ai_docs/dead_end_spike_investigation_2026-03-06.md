# Dead-End Spike Investigation

**Date**: 2026-03-06
**Branch**: `research_rubberband_idea`
**Status**: UNSOLVED — root cause identified but fix attempt failed
**Related**: `terrain_wall_bug_investigation_2026-03-06.md` (walls fixed in commit d02fba8)

---

## Problem Description

After fixing the terrain wall bug (commit d02fba8), terrain spikes **were introduced** at some road dead ends (isolated endpoints). The wall fix added `skipPropagation=true` to `HarmonizeNetwork()`, which skips Steps 4-7 (constraint propagation, endpoint tapering, plateau polygons, IDW modifiers). Previously, the harmonizer's Step 5 (endpoint tapering) modified TargetElevation near dead ends, and even though those values were restored by the elevation-restore loop, the harmonizer's Step 4 (constraint propagation) also ran and its side effects partially masked the `FinalSnapTJunctionEndpoints` corruption. With Steps 4-7 skipped, the corruption from `FinalSnapTJunctionEndpoints` is no longer masked and produces visible spikes.

The spikes are massive vertical terrain columns (28m+ above terrain) at the tip of roads that have a T-junction or MidSplineCrossing at one end and a dead end at the other.

### Known Spike Locations (BeamNG coords)
- Spike 1: (238.16, 476.06, 106.04) — terrain should be ~78m
- Spike 2: (-99.49, 1735.43, 105.76) — terrain should be ~73m

---

## Root Cause: FinalSnapTJunctionEndpoints Corrupts Remote Cross-Sections

### The Mechanism

`FinalSnapTJunctionEndpoints` (in `UnifiedJunctionProfileBlender.cs:1222`) runs after the iteration loop to correct T-junction terminating road endpoints to match the current primary surface. It processes all junctions of type `TJunction` and `Roundabout`.

**The problem chain for Spline 20 (Spike 1):**

1. Spline 20 has a **dead end** at position (2286, 2524) with terrain ≈ 78m
2. Spline 20 also participates in **Junction #598** (a MidSplineCrossing that was converted to a TJunction) at position (2317, 3196) with primary elevation ≈ 169m
3. `FinalSnapTJunctionEndpoints` processes Junction #598, finds Spline 20 as a terminating road
4. It calls `CalculateDistancesFromEndpoint(termSections, isStart)` — but `isStart` points to the dead-end tip (CS[0]), not the junction end
5. Cross-sections near the dead end (d=0-17m from CS[0]) have small distances and fall within the snap extent
6. The surface elevation is computed as: `centerElev + primarySlope * dot(offset, primaryTangent)` where centerElev ≈ 169m
7. With the offset pointing 700m away from the junction, the extrapolation produces: `169 + (-0.065 * 700) ≈ 123m`
8. Cross-sections near the dead end get `TargetElevation` set to ~120m (instead of ~78m terrain)

### Diagnostic Evidence (from log)

**Before FinalSnapTJunctionEndpoints** (logged in ApplyUnifiedProfiles):
```
CS[0] d=0.0m: origElev=88.03m terrain=78.10m TargetElev=78.10m  ← CORRECT (tapered to terrain)
CS[4] d=2.0m: origElev=90.41m terrain=79.09m TargetElev=79.86m  ← CORRECT
```

**After FinalSnapTJunctionEndpoints** (final state in game):
```
CS[12] d=6.0m: TargetElev=94.89m  delta=+14.30m above terrain  ← CORRUPTED
CS[20] d=10.0m: TargetElev=118.62m delta=+37.54m above terrain ← CORRUPTED
CS[23] d=11.5m: TargetElev=120.84m delta=+39.58m above terrain ← PEAK SPIKE
```

The unified blender correctly tapers the dead end to terrain. Then `FinalSnapTJunctionEndpoints` overwrites those values with extrapolated primary surface elevations from a junction 700m away.

### Why This Happens

MidSplineCrossing→TJunction conversion (in the harmonizer's junction detection) creates a junction in the middle of a spline. After conversion, the spline appears as "terminating" at the crossing point. But the `JunctionContributor.IsSplineStart` flag may point to the wrong end of the spline (the dead end instead of the crossing), causing `CalculateDistancesFromEndpoint` to measure from the dead end.

---

## Approaches Tried (All Failed or Reverted)

### 1. Disable ComputeEndpointConstraints for Dead Ends
**Idea**: Skip the Hermite correction for dead ends, rely only on ApplyEndpointTapering.
**Result**: No effect on spikes — the spikes come from FinalSnapTJunctionEndpoints, not from endpoint constraints.

### 2. Disable IDW Weight Modifiers Entirely
**Idea**: Set all JunctionIdwWeightModifier to 1.0 (disable WI-8).
**Result**: No effect on spikes — IDW modifiers affect Phase 4 blend zones, not the TargetElevation corruption.

### 3. Restore IDW Modifiers Near Dead Ends (Step 7b)
**Idea**: After computing IDW modifiers, restore them to 1.0 near dead-end endpoints to prevent suppression from T-junctions propagating to dead-end tips.
**Result**: No effect on spikes — same reason as above.

### 4. Guard FinalSnapTJunctionEndpoints with Distance Check
**Idea**: Skip snap if `terminatingCS.CenterPoint` is >50m from `junction.Position`.
**Result**: Reverted — approach was correct but the fix had issues and the user decided to revert all spike-related changes.

---

## Key Files

| File | Role |
|------|------|
| `UnifiedJunctionProfileBlender.cs` | Contains `FinalSnapTJunctionEndpoints` (line 1222) — the corrupting method |
| `UnifiedJunctionProfileBlender.cs` | Contains `ApplyUnifiedProfiles` — correctly handles dead ends |
| `UnifiedRoadSmoother.cs` | Calls `FinalSnapTJunctionEndpoints` at line 464 (after iteration loop) |
| `NetworkJunctionDetector.cs` | MidSplineCrossing→TJunction conversion logic |

---

## Recommended Next Steps

### Option A: Fix FinalSnapTJunctionEndpoints (Most Targeted)
Add a guard that verifies the terminating CS is physically near the junction before snapping. The attempted fix (approach 4) was on the right track but needs refinement:
- Check distance from `terminatingCS.CenterPoint` to `junction.Position`
- Also check that `endpointCS` (the reference point for offset calculations) is near the junction
- May need to find the ACTUAL cross-section closest to the junction instead of using the spline endpoint

### Option B: Fix MidSplineCrossing→TJunction Conversion
Ensure that when a MidSplineCrossing is converted to a TJunction, the `IsSplineStart` flag correctly indicates which end of the spline touches the junction. This would fix the root cause (wrong distance measurement direction).

### Option C: Limit Surface Extrapolation
In `FinalSnapTJunctionEndpoints`, clamp the `offset` magnitude used in surface elevation computation to prevent unbounded extrapolation. E.g., `max_offset = flatZone + blendDist`.

### Option D: Skip FinalSnapTJunctionEndpoints for Converted Junctions
Track which junctions were converted from MidSplineCrossing and skip them in the final snap. The BlendSplineProfile already handled them correctly.

---

## Diagnostic Logging (Remove Before Commit)

Diagnostic logging was added to `ApplyUnifiedProfiles` in `UnifiedJunctionProfileBlender.cs` (search for `[SPIKE-DIAG]`). This should be removed when committing final fixes. The logging dumps:
- Junction position and harmonized elevation
- Constraint values (elevation, slope, bank, blend distance)
- Cross-section profile from endpoint: origElev, terrain, TargetElev, delta, idwMod
