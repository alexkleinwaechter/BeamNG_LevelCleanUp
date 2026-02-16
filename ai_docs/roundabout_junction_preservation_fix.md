# Roundabout Junction Preservation Fix

**Date:** 2026-02-15

## Problem

Connecting roads (e.g. `highway=service`) to roundabouts did not have smooth elevation transitions. The terrain showed a visible slope discontinuity where the road meets the roundabout ring.

## Root Cause

The unified road smoothing pipeline has a phase ordering bug where roundabout junction data is lost between phases:

### Pipeline Phase Ordering

1. **Phase 2.6 (Roundabout Elevation Harmonization)**
   - `NetworkJunctionDetector.DetectRoundaboutJunctions()` creates `NetworkJunction` objects with `JunctionType.Roundabout` and adds them to `network.Junctions`
   - `RoundaboutElevationHarmonizer.BlendConnectingRoads()` smoothly blends each connecting road's elevation toward the roundabout ring elevation
   - Each roundabout junction is marked `IsExcluded = true` so Phase 3 won't double-process it

2. **Phase 3 (General Junction Harmonization)**
   - `DetectJunctions()` / `DetectJunctionsWithOsm()` is called
   - **BUG:** `NetworkJunctionDetector.DetectJunctions()` at line 107 calls `network.Junctions.Clear()`, wiping out ALL roundabout junctions from Phase 2.6
   - The connecting road endpoints near the roundabout are re-detected as regular `JunctionType.Endpoint` junctions WITHOUT the `IsExcluded` flag
   - `ApplyEndpointTapering()` then tapers those endpoints toward raw terrain elevation, **destroying the smooth roundabout blend**

### Affected Code Locations

- `NetworkJunctionDetector.DetectJunctions()` — `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs:107`
- `NetworkJunctionDetector.DetectJunctionsWithOsm()` — same file, line 228 (also clears via calling `DetectJunctions`)
- `UnifiedRoadSmoother.SmoothAllRoads()` — `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` Phase 3 block

## Fix

Modified `UnifiedRoadSmoother.SmoothAllRoads()` to preserve roundabout junctions across Phase 3 detection:

1. **Before Phase 3 detection:** Save all `JunctionType.Roundabout` junctions from `network.Junctions`
2. **After Phase 3 detection:** Call new `RestoreRoundaboutJunctions()` method which:
   - Removes any newly-detected regular junctions that overlap with roundabout junction positions (within 15m radius) to prevent duplicate processing
   - Re-adds the saved roundabout junctions with their `IsExcluded` flag intact
   - Re-assigns sequential junction IDs

### New Method

`RestoreRoundaboutJunctions(UnifiedRoadNetwork network, List<NetworkJunction> roundaboutJunctions)` — added to `UnifiedRoadSmoother.cs`

### Files Changed

- `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

## Why This Affects All Connecting Roads

This bug affects **every** road connecting to a roundabout, not just service roads. The roundabout harmonizer's blend is applied in Phase 2.6 but then overwritten by Phase 3's endpoint tapering for ALL connecting road endpoints near the ring. The service road is most visually obvious because it's typically lower-priority (priority 45) and may have a larger terrain-to-ring elevation difference.

## Cross-Material Consideration

The fix works for cross-material roundabout connections (e.g., roundabout ring on `highway=secondary` material, connecting road on `highway=service` material). The `DetectRoundaboutJunctions` in Phase 2.6 iterates over ALL network splines regardless of material, so connections are detected even across material boundaries. The fix preserves these cross-material roundabout junctions.

## Key Pipeline Phases Reference

| Phase | Component | Purpose |
|-------|-----------|---------|
| 1 | `UnifiedRoadNetworkBuilder` | Build unified network from all materials |
| 1.5 | `IdentifyRoundaboutSplines` | Mark `IsRoundabout = true` (before banking) |
| 2 | `CalculateNetworkElevations` | Per-spline terrain sampling |
| 2.3 | `StructureElevationIntegrator` | Bridge/tunnel profiles |
| 2.5 | `BankingOrchestrator` | Pre-calculate banking (roundabouts excluded) |
| 2.6 | `RoundaboutElevationHarmonizer` | Uniform ring elevation + connecting road blending |
| 3 | `NetworkJunctionHarmonizer` | General junction harmonization (roundabout junctions excluded) |
| 3.5 | `BankingOrchestrator` | Finalize banking after harmonization |
| 4 | `UnifiedTerrainBlender` | Apply terrain blending |
| 5 | `MaterialPainter` | Paint material layers |
