# Banking Junction Blending Investigation (2026-03-09)

## Problem Statement

When the primary road has banking enabled (~10° superelevation), terminating roads at T-junctions don't match the primary road's **banked edge elevation**. Without banking, the junction blending works perfectly — the terminating road meets the primary road edge with correct elevation and smooth slope. With banking, there's a visible cliff (red area in debug images).

## Root Cause Analysis

### The Geometry Problem

At a T-junction, the OSM junction node is at the **primary road's centerline**. The terminating road's endpoint CS is placed at this shared node position. For a banked primary road (e.g., 10° on a 9m-wide road):

- Primary centerline elevation: 214.3m
- Primary south edge elevation: 214.3 - sin(10°)×4.5 = 213.5m (0.8m lower)
- Terminating road endpoint: at 214.3m (centerline position, lateralOffset=0)

The terminating road needs to connect at the **edge** (213.5m), not the centerline (214.3m).

### The Analytical Delta (Existing System)

The Hermite blending uses an "analytical delta" in the flat zone that projects each CS onto the primary surface:

```
primarySurfaceElev = constraint.Elevation
    + slope × longitudinalOffset
    + sin(PrimaryBankAngleRadians) × lateralOffset
```

At the endpoint (lateralOffset=0): gives **centerline** elevation (correct for that position, wrong for the intended connection).

At the flat zone boundary (lateralOffset ≈ -primaryHalfWidth): gives **edge** elevation (correct!).

So the analytical delta **already works correctly** for all CSes except the endpoint. The endpoint is inside the primary road core (owned by primary road in Phase 4), so its value seems irrelevant for terrain... BUT:

### The Real Visibility Issue

The terminating road's first segment (endpoint CS to next CS) creates a **road core polygon** in the RoadMaskBuilder. Edge pixels of this polygon that extend beyond the primary road's core mask are **visible** and use the terminating road's elevation. Since the endpoint CS has:
- `TargetElevation ≈ centerline` (not edge)
- `BankAngleRadians ≈ 0°` (for perpendicular junction, the terminating road's tilt axis is perpendicular to the primary road's banking axis — it CAN'T tilt to match)

These edge pixels are at ~centerline elevation, while the primary road's edge is 0.8m lower. This creates the visible cliff.

### Key Constraint: Perpendicular Banking Axes

For a perpendicular T-junction:
- Primary road banking: tilts North-South (its normal direction)
- Terminating road banking: tilts East-West (its normal direction)
- The terminating road **cannot** match the primary road's N-S tilt by adjusting its own banking

This is a fundamental geometric limitation. The terminating road's cross-section model (centerline + bankAngle) cannot represent the primary road's banked surface at the junction.

## Approaches Tried

### 1. Edge Correction to constraint.Elevation (FAILED — Double Counting)

**Idea**: Add `lateralComponent × primaryHalfWidth × sin(bankAngle)` to `constraint.Elevation` to shift from centerline to edge.

**Result**: Double-counted with the analytical delta's banking term. At 5m from junction, both the shifted constraint AND the banking term contributed the edge offset, giving 2× the correction (1.74m instead of 0.87m).

**Why**: The analytical delta's `sin(bank) × lateralOffset` already accounts for the lateral position. Adding the same offset to the base elevation doubles it.

### 2. Virtual Lateral Offset Blend (FAILED — Wrong Interior Elevations)

**Idea**: At the endpoint, replace `actualLatOffset=0` with `edgeLateral=-3.8` (the edge position). Blend from edge lateral to actual lateral over the flat zone distance.

**Result**: Gave good slope visually, but pulled ALL flat zone CSes toward edge elevation. At 0.5m from junction, the terminating road was 0.6m below the primary surface (should have been only 0.08m below). Created a visible valley inside the primary road.

**Why**: The blend affected CSes that were only slightly off the primary centerline, pulling them toward the edge elevation instead of following the primary surface.

### 3. Short Virtual Blend (2m only) + Clamp (PARTIAL — Still Mismatched)

**Idea**: Same as #2 but limit the virtual blend to first 2m near endpoint. Add clamp to ±flatZone to prevent banking extrapolation beyond the road edge.

**Result**: Better slope near endpoint, but still didn't match the primary road edge. The 2m blend zone was still too aggressive, pulling CSes away from the correct primary surface.

### 4. Clamp Only (no virtual blend) (PARTIAL — Prevents Extrapolation)

**Idea**: Just clamp `lateralOffset` to `±flatZone` in the analytical delta and handoff delta. No virtual blend.

**Result**: Prevented banking extrapolation beyond the road edge (good), but the endpoint stayed at centerline elevation. The flat zone CSes correctly followed the primary surface. No visible improvement at the junction because the endpoint pixels were still wrong.

### 5. Bake Edge Elevation + Zero PrimaryBankAngleRadians (FAILED — Lost All Slope)

**Idea**: Set `constraint.Elevation = edgeElev` and `PrimaryBankAngleRadians = 0`. The analytical delta becomes slope-only (no banking term). No double-counting since banking is baked into the base elevation.

**Result**: Lost the smooth slope that the analytical delta was providing. The flat zone became a flat platform at edge elevation with only slope variation. The Hermite h00 decay had nothing to work with (handoff delta was just slope-based). Made things worse — no slope adaptation at all.

**Why**: Zeroing PrimaryBankAngleRadians removed the banking-based surface tracking that was working correctly for interior CSes. The handoff delta (which feeds the decay zone) lost the banking gradient information.

## What Works

1. **Without banking**: Junction blending works perfectly. The terminating road meets at the (flat) primary road edge with correct elevation and smooth slope.

2. **The analytical delta**: Correctly computes the primary surface at each CS's actual position. Interior CSes in the flat zone correctly follow the banked primary surface.

3. **The lateral clamp** (±flatZone): Prevents banking extrapolation beyond the primary road edge in the handoff/transition zones. Worth keeping.

4. **The Hermite h00 decay**: Smoothly transitions from the junction constraint to terrain-following.

## What Doesn't Work

1. **Endpoint elevation**: Always at centerline (lateralOffset=0). The analytical delta can't fix this because offset IS zero at the endpoint.

2. **Edge pixel mismatch**: The terminating road's first segment has flat (bankAngle≈0) edge pixels that extend beyond the primary road core, at the wrong elevation.

3. **Any modification to constraint.Elevation**: Either double-counts with the analytical delta or removes the useful banking gradient for interior CSes.

## Possible Next Steps (Not Yet Tried)

### A. Skip Overlap Rasterization (Terrain-Level Fix)
In `RoadMaskBuilder`, don't rasterize the terminating road's core pixels that fall within the primary road's core area. The primary road's banked pixels would fill the overlap instead. The terminating road's first visible pixel would be at the flat zone boundary, where the analytical delta already gives the correct edge elevation.

**Pros**: No elevation math changes, leverages existing priority system
**Cons**: Requires changes to the raster pipeline, may affect EDT distance field

### B. Extend Primary Road Core at Banked Junctions
Expand the primary road's protection/priority zone at junctions to cover the terminating road's overlap pixels. This is conceptually similar to A but works through the priority system.

### C. Trim Terminating Road CSes to Primary Road Edge
Remove or skip terminating road CSes that are inside the primary road's width. The first CS would be at the primary edge, where the analytical delta already gives the correct elevation. This is what the user described: "trim the overlapping part."

**Pros**: Cleanest solution — the terminating road starts where it should (at the edge)
**Cons**: Significant pipeline change — needs to shift the junction endpoint, recalculate flat zone distances, etc.

### D. Per-Pixel Elevation Override at Overlap
In the ElevationMapBuilder, when a pixel is in both the terminating road's core and near the primary road's core, use the primary road's banked surface elevation instead of the terminating road's elevation.

## Key Insight

The fundamental issue is that the **OSM junction node position** (primary centerline) doesn't match the **physical connection point** (primary edge). The elevation pipeline works correctly at each CS's actual position, but the first CS is at the wrong position. Any attempt to shift the elevation at the endpoint creates conflicts with the analytical delta that correctly handles interior positions.

The cleanest fix would operate at the **terrain level** (options A/B/D) rather than the elevation level, ensuring the primary road's banked surface fills the overlap zone regardless of what the terminating road's CSes say.

## Part 1 Changes (Banking Smoothing — Working)

Part 1 changes are independent and working correctly:
- Curvature Gaussian smoothing (activates existing dead code in CurvatureCalculator)
- Bank angle Gaussian smoothing (new SmoothBankAngles method in BankingCalculator)
- Both wired into BankingOrchestrator.ApplyBankingPreCalculation()
- Parameters: CurvatureSmoothingWindow (default 5), BankAngleSmoothingWindow (default 7) in BankingParameters

## Part 2 Changes (Junction-Aware Banking — May Need Revision)

Part 2 activates dead code for junction-aware banking adjustments:
- CalculateJunctionBankingBehaviors() before unified blender
- ApplyJunctionAwareBankingAdjustments() after unified blender
- These work but are independent of the edge elevation problem

## Files Modified

- `UnifiedJunctionProfileBlender.cs` — Multiple attempted fixes to ComputeTJunctionConstraints, BlendSplineProfile analytical delta, FinalSnapTJunctionEndpoints
- `BankingOrchestrator.cs` — Part 2 junction-aware banking wiring
- `BankingCalculator.cs` — Part 1 SmoothBankAngles method
- `BankingParameters.cs` — Part 1 smoothing window parameters
- `UnifiedRoadSmoother.cs` — Part 2 pipeline wiring
- `JunctionSurfaceCalculator.cs` — Not modified (GetPrimarySurfaceElevation already accounts for banking)
