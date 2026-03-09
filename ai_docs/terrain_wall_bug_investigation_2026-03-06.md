# Terrain Wall Bug Investigation

**Date**: 2026-03-06
**Branch**: `research_rubberband_idea`
**Status**: SOLVED — commit d02fba8

---

## Problem Description

Massive terrain walls/trenches appear between road endpoints across open terrain where NO road spline exists. The walls look like straight lines of raised or lowered terrain connecting distant points. Some endpoints are unnaturally elevated or deeply depressed. The bug was introduced during development of the Hermite C1 junction blending and rubberband profile systems on this branch.

### Key Observation from User
**The walls are NOT along roads.** They appear in open terrain between endpoints of different splines. There is no spline running along the wall. This rules out cross-section elevation errors as the sole cause — something in Phase 4 terrain blending is creating terrain modifications between distant unconnected points.

---

## Approaches Tried (All Failed)

### 1. Option B: Topology-Aware IDW Filtering
**File**: `ElevationMapBuilder.cs`
**Idea**: Build a junction adjacency map and filter `InterpolateNearbyCrossSectionsBuffered` to only include cross-sections from splines that share a junction with the dominant owner.
**Result**: Changed the wall pattern but didn't eliminate it.
**Conclusion**: Cross-spline IDW contamination is not the sole root cause.

### 2. Option A: Single-Spline Interpolation for All Roads
**File**: `ElevationMapBuilder.cs`
**Idea**: Force all roads (including OSM) to use `InterpolateFromSingleSplineBuffered` instead of multi-spline IDW. This completely prevents cross-spline elevation mixing.
**Result**: Walls persisted unchanged.
**Conclusion**: The multi-spline IDW interpolation is NOT the cause. The bug is independent of which interpolation method is used.

### 3. Per-Spline Half-Width in Distance Check
**File**: `ElevationMapBuilder.cs`, line 173
**Idea**: Replace `maxRoadHalfWidth` (global max across all splines) with per-spline `ownerHalfWidth` when checking if a pixel is within a road's influence zone. The global max allowed narrow roads to claim pixels far beyond their actual influence zone.
**Result**: Did not fix the walls.
**Conclusion**: The influence zone distance check is not the primary issue, or the walls are created by a different mechanism.

### 4. Diagnostic Logging for Extreme Elevations
**File**: `UnifiedRoadSmoother.cs`
**Idea**: Log any cross-section where `|TargetElevation - originalElevation| > 10m` after the unified blender runs.
**Result**: Rolled back before testing (user moved on to other approaches).

---

## Pipeline Analysis

### Phase 3 Architecture (Current Branch)

The unified system path (`UseUnifiedJunctionSystem = true`) runs:

1. **Capture originalElevations** — snapshot of terrain-following profile from Phase 2
2. **HarmonizeNetwork()** — runs junction detection + rubberband profiles (modifies TargetElevation)
3. **Restore originalElevations** — undoes rubberband modifications
4. **ApplyUnifiedProfiles()** — Hermite C1 blending (the replacement system)
5. **FinalSnapTJunctionEndpoints()** — post-iteration correction

The rubberband results are discarded (step 3 undoes them). The harmonizer's side effects that persist: junction detection/classification, HarmonizedElevation, plateau polygons, IDW weight modifiers.

### Phase 4 Architecture

1. **BuildCombinedRoadCoreMask** — rasterizes cross-section lines into binary mask
2. **BuildRoadCoreProtectionMaskWithOwnership** — fills quads between consecutive cross-sections per spline
3. **RasterizeJunctionPlateaus** — fills gap pixels at junctions
4. **ComputeDistanceField (EDT)** — Euclidean distance from combined mask
5. **BuildElevationMapWithOwnership** — assigns ownership + elevation to pixels near roads
6. **ApplyProtectedBlending** — blends terrain toward road elevations in blend zones

### Key Files

| File | Role |
|------|------|
| `UnifiedRoadSmoother.cs` | Pipeline orchestrator, Phase 2-3 iteration loop |
| `UnifiedJunctionProfileBlender.cs` | Hermite C1 blending (new system) |
| `NetworkJunctionHarmonizer.cs` | Rubberband profiles, junction detection, endpoint tapering |
| `ElevationMapBuilder.cs` | Phase 4 elevation map with ownership |
| `ProtectedBlendingProcessor.cs` | Phase 4 terrain blending |
| `RoadMaskBuilder.cs` | Phase 4 road core mask rasterization |
| `JunctionPlateauBuilder.cs` | Phase 4 junction gap filling |
| `JunctionSurfaceCalculator.cs` | Surface elevation projection for T-junctions |

---

## Remaining Hypotheses (Untested)

### A. Junction Plateau Polygons Creating Large Terrain Areas
`JunctionPlateauBuilder.ComputeJunctionPolygons` creates convex hull polygons from road edge points at junctions and fills them with the harmonized elevation. If a junction has contributors with large `ExpandHull` distances, or if the convex hull spans a large area, it could create flat terrain patches that look like walls when they have different elevations than surrounding terrain.
**To test**: Disable junction plateau rasterization (step 2.5 in UnifiedTerrainBlender) and see if walls disappear.

### B. EDT Distance Field Propagating Artifacts
The EDT computes minimum distance from road core pixels. If the combined road core mask has unexpected pixel patterns (e.g., from rounding errors in cross-section rasterization), the distance field could create artifacts that affect the blend zone shape.
**To test**: Export the combined road core mask and distance field as debug images and inspect for unexpected patterns.

### C. ProtectedBlendingProcessor Ownership Boundary Issue
The elevation map assigns ownership based on "nearest cross-section wins." The boundary between two ownership regions creates a sharp elevation transition. The blending processor then creates different embankments on each side of this boundary.
**To test**: Export the ownership map as a debug image and check if ownership boundaries align with the visible walls.

### D. Something in Phase 2 Creating Bad Cross-Section Positions
If cross-sections have wrong `CenterPoint` coordinates (e.g., placed far from the actual road), the road mask and spatial index would include pixels in unexpected locations.
**To test**: Export cross-section positions and verify they're all along actual roads.

### E. Unbounded Slope Extrapolation in GetPrimarySurfaceElevation
`JunctionSurfaceCalculator.GetPrimarySurfaceElevation` (line 74) uses unbounded `longitudinalOffset * primarySlope`. A clamped version exists (`GetPrimarySurfaceElevationClamped`) but is not used in the main constraint computation paths. This could cause extreme constraint elevations at T-junctions.
**Status**: Analysis showed the call sites pass positions close to the junction (within flat zone), so the offset should be small. But not definitively ruled out.

### F. Debug Image Export
The pipeline has debug image export capabilities. Running with debug images enabled would show:
- Road core mask (which pixels are road)
- Distance field (blend zone shape)
- Ownership map (which road owns which terrain pixel)
- Elevation map (what elevation each pixel targets)
This would immediately reveal WHERE in the pipeline the walls first appear.

---

## What We Know For Certain

1. The bug is NOT in cross-spline IDW interpolation (Option A test proved this)
2. The bug was introduced on this branch (not present on develop)
3. The walls appear in terrain between road endpoints, not along roads
4. The walls persist regardless of interpolation strategy in ElevationMapBuilder
5. The develop branch does NOT have rubberband profiles — they were added on this branch
6. Both rubberband AND Hermite C1 systems exist on this branch; rubberband results are discarded but the harmonizer's junction detection/classification side effects persist

## Resolution (commit d02fba8)

### Root Cause
When `UseUnifiedJunctionSystem=true`, `NetworkJunctionHarmonizer.HarmonizeNetwork()` was running Steps 4-7 (constraint propagation, endpoint tapering, junction plateau polygons, IDW weight modifiers) even though the unified system replaces all of these. The side effects from these steps persisted into Phase 4 terrain blending:

1. **Junction plateau polygons (WI-9)** — computed with pre-correction (wrong) harmonized elevations, then rasterized as flat protected patches in Phase 4, creating elevated/depressed terrain patches
2. **IDW weight modifiers (WI-8)** — double-applied (harmonizer computed first, unified blender used `min()` so harmonizer values persisted as floor)
3. **Constraint propagation (WI-3/4)** — modified TargetElevation values that were wiped by the elevation restore, but the stats/side effects leaked

### Fix (3 changes)
1. Added `skipPropagation` parameter to `HarmonizeNetwork()` — when `true`, skips Steps 4-7 (only Steps 1-3 run: detection, classification, elevation computation)
2. Reset `JunctionIdwWeightModifier = 1.0f` for all cross-sections after harmonizer runs, before unified blender
3. Skip junction plateau rasterization (`RasterizeJunctionPlateaus`) in Phase 4 when unified system is active

### Remaining Issue — INTRODUCED by this fix
Dead-end spikes at isolated endpoints. Initially thought to be pre-existing from WI-3 era, but confirmed to be **introduced by commit d02fba8** (this wall fix). The `skipPropagation=true` change removed the harmonizer's Steps 4-7, which previously masked a bug in `FinalSnapTJunctionEndpoints` that corrupts cross-sections near dead ends via unbounded surface extrapolation from distant MidSplineCrossing→TJunction converted junctions.
See: `dead_end_spike_investigation_2026-03-06.md`
