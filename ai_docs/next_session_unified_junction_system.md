# Next Session: Unified Junction Elevation System (continued)

## Quick Context
- Branch: `research_rubberband_idea`
- Feature flag: `JunctionHarmonizationParameters.UseUnifiedJunctionSystem` (default: true)
- Set `UseUnifiedJunctionSystem = false` to use legacy system (still works)

## Read These Files First
- `ai_docs/junction_bump_investigation_2026-02-28.md` — original investigation
- `ai_docs/rubberband_elevation_architecture.md` — original rubberband architecture
- This file — current session state

## What Was Built (2026-02-28 evening session)

### New Files
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` — constraint data model (elevation, slope, bankAngle, FlatZoneDistance)
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — core blender class

### Modified Files
- `UnifiedRoadSmoother.cs` — wires unified system with feature flag, skips Phase 3.5/3.6 when active
- `JunctionHarmonizationParameters.cs` — added UseUnifiedJunctionSystem flag
- `BankedTerrainHelper.cs` — removed HasJunctionConstraint branches (simplified)
- `CrossSectionConverter.cs` — removed ConstrainedEdgeElevation preference
- `MasterSplineExporter.cs` — removed HasJunctionConstraint check

### Architecture
The unified blender replaces 4 overlapping legacy systems with ONE Hermite-based system:
1. Compute junction constraints: (elevation, slope, bankAngle) per junction+road pair
2. Two-pass Hermite blend: primary roads first, then terminating roads (so T-junction constraints use actual post-blend primary elevations)
3. Derive edges from blended (TargetElevation, BankAngle)
4. Endpoint tapering, IDW weight modifiers, MidSplineCrossing influences

## Current State: What Works and What Doesn't

### What Works Well
- **Two-pass Hermite**: Primary roads get blended first, then T-junction constraints are recomputed from actual primary elevations. This ensures correct constraint values.
- **Simultaneous elevation + banking blend**: One curve for both → edges automatically smooth, no separate edge constraint system.
- **Flat zone concept**: The Hermite parameter `t` stays at 0 within the primary road width, ensuring full correction in the overlap zone.
- **No Phase 3.5/3.6**: Entire banking finalization and edge constraint propagation eliminated.
- **Feature flag rollback**: `UseUnifiedJunctionSystem=false` restores legacy behavior.

### What Doesn't Work Yet (THE KEY PROBLEM)

**The Hermite correction approach cannot match a spatially varying surface.**

The Hermite adds a CONSTANT delta (computed at the junction endpoint center) to the natural terrain-following elevation:
```
newElev = naturalElev[i] + delta * h00(t)
```

Within the flat zone (primary road width), `h00=1.0`, so:
```
newElev = naturalElev[i] + delta
```

But the primary road surface at each cross-section position is DIFFERENT due to:
- **Longitudinal slope**: The primary road goes uphill/downhill, so surface elevation changes along the terminating road's path through the overlap
- **Banking**: The primary road is tilted, so surface elevation varies laterally

The delta was computed at ONE point (the junction CS center). Adding this same delta to the natural elevation at 2m away gives the wrong value if the primary surface at 2m has a different elevation.

**Result**: The overlap zone has incorrect elevation → visible step at the road edge.

### What Was Tried and Why It Failed

1. **Hermite-only (no flat zone)**: The Hermite starts decaying immediately from the junction center, so the overlap zone cross-sections don't match the primary surface. → Visible step at road edge.

2. **Overlap snap + blend distance**: Surface-following projection within the overlap, with blend distance decay beyond. → Creates bumps at the blend distance boundary (same problem as old Phase 3.6).

3. **Overlap snap (no blend distance)**: Surface-following projection only within primary road width. → "break" bug (wrong iteration direction for end-of-spline junctions) + even after fix, the snap-to-Hermite handoff at the primary edge creates a discontinuity because the snap value ≠ Hermite value there.

4. **Two-pass + flat zone (current)**: Hermite with flat zone to keep correction at 100% within overlap. → The constant delta doesn't match the spatially varying primary surface.

## THE CORRECT APPROACH (not yet implemented)

The solution requires BOTH systems working together correctly:

### Step 1: Hermite blend with flat zone (existing, handles smooth transition)
- Applies correction across entire road
- Flat zone keeps h00=1.0 within primary road width
- Provides smooth C1-continuous transition beyond the road edge
- Handles the long-range slope from junction to natural profile

### Step 2: Surface-following projection in overlap zone (needs to be brought back)
- AFTER the Hermite blend
- ONLY within primary road width
- Projects each CS onto the NEAREST primary road CS (using post-Hermite-blend primary values)
- Sets TargetElevation and BankAngle to EXACTLY match the primary surface
- This handles the spatial variation (slope + banking) that the constant-delta Hermite can't

### Why the handoff works THIS time
At the primary road edge (the boundary between Step 2 snap zone and Step 1 Hermite zone):
- The snap gives: exact primary surface elevation at the edge position
- The Hermite gives: naturalElev + delta (approximately the primary surface, but not exact due to slope)
- The difference should be small (a few cm for typical road slopes)
- The Hermite's zero-slope property (h00'(0)=0) means it's flat at the boundary → the small difference appears as a tiny offset, not a kink

**The key insight**: the snap handles PRECISION (exact surface matching in the overlap), the Hermite handles SMOOTHNESS (no kinks beyond the overlap). A tiny offset at the boundary is better than a kink.

### To make the handoff even better
Instead of using a constant delta for the Hermite, compute the delta AT THE EDGE of the primary road (not at the center). This way the Hermite value at the flat zone boundary exactly matches the snap value:
```
edgeDelta = primarySurfaceAtEdge - naturalElevAtEdge
```
Then the snap zone sets exact values, and the Hermite starts from the correct edge value → seamless handoff.

This requires computing the constraint not from the junction CS center but from the position where the terminating road exits the primary road body. The FlatZoneDistance tells us where that is.

## Implementation Notes

### Wiring in UnifiedRoadSmoother.cs (around line 350)
```
1. Capture originalElevations BEFORE HarmonizeNetwork runs
2. Run HarmonizeNetwork (for junction detection only)
3. Restore originals
4. Run UnifiedJunctionProfileBlender.ApplyUnifiedProfiles()
5. Skip Phase 3.5 and 3.6
```

### The overlap snap was removed — bring it back
The `SnapOverlapZonesToPrimarySurface` method was deleted. It needs to be recreated as Step 3 in ApplyUnifiedProfiles, running AFTER the Hermite blend (Step 2) and BEFORE edge derivation (Step 4).

Key: use `continue` not `break` in the loop (junction can be at either end of spline).

### Real-world junction design parameters (from user)
- Max gradient near junction: 3% desirable, 5% absolute
- Cross slope at junction: 1.5-2%
- Max superelevation at junction: 4% desirable
- Side road should be ≤4% gradient for 30m from main road edge
- No crest curves at intersections
- Minimum 30-60m of controlled gradient before stop line

## Key Files Reference
- `UnifiedJunctionProfileBlender.cs` — main blender (Steps 1-7)
- `JunctionEndpointConstraint.cs` — constraint data model
- `UnifiedRoadSmoother.cs` — pipeline wiring (lines ~320-420)
- `JunctionSurfaceCalculator.cs` — primary surface projection (reusable)
- `BankedTerrainHelper.cs` — terrain blending consumer (simplified)
- `NetworkJunctionHarmonizer.cs` — junction detection + legacy system
- `JunctionHarmonizationParameters.cs` — feature flag + blend parameters

## Test Preset
`D:\Temp\Test_Cleanup\__preset_france_italy\theTerrain_terrainPreset.json`
