# Roundabout DecalRoad Edge Blend Suppression — Investigation Notes

**Date:** 2026-03-20
**Branch:** `fix/roundabout-decalroads`
**Status:** UNSOLVED — edge blend interruption on roundabout ring not working

---

## Goal

Suppress (interrupt) roundabout ring DecalRoad layers with `InterruptAtJunctions = true` (edge lines, edge blends) where connecting roads meet the ring. The connecting roads' equivalent layers should also be suppressed where they overlap the ring.

## What Was Successfully Implemented

These changes are committed and working:

1. **`IsClosedLoop` flag on `RoadCorridor`** — marks roundabout ring corridors as closed loops
2. **Closed-loop wrap-around in `RoadCorridorOverlapChecker`** — bridges the gap between last and first corridor sections at the wrap seam
3. **Roundabout-first layer set resolution** — `RoadCorridorBuilder` and `DecalRoadGenerator` resolve `"roundabout"` layer set key before regular OSM type cascade
4. **Ring-wide junction influence zones** — groups all roundabout junctions into one zone covering the entire ring + corridor padding
5. **Continuity lookup exclusion** — roundabout junctions excluded from `BuildContinuityLookup` so ring markings don't get false continuity exemptions
6. **Roundabout default layer set** — `CreateRoundaboutSet` with edge lines, edge blends, tread marks, wear, patches, and one-way AI road (`LanesLeft=0, LanesRight=lanes, OneWay=true`)
7. **One-way AI road override** — `GenerateForLayerRange` forces `OneWay=true` for roundabout splines, uses OSM lane data when available
8. **AI road configuration works correctly** — verified in-game

## The Unsolved Problem

**Roundabout ring edge blends and edge lines are NOT being interrupted where connecting roads meet the ring.**

Looking at in-game screenshots, the connecting roads clearly overlap the roundabout ring at each junction point. Their surfaces cross. But the ring's `InterruptAtJunctions = true` layers continue unbroken through these overlap zones.

## Root Cause Analysis

The suppression pipeline works like this:
1. For each node on a spline's layer with `InterruptAtJunctions = true`
2. `BuildSegmentsWithCorridorCheck` calls `CheckWithJunctionFilter`
3. `CheckWithJunctionFilter` checks if the node is within a junction influence zone
4. If yes, checks the node against contributing corridors via `CheckPointAgainstCorridor`
5. `CheckPointAgainstCorridor` uses bracket checks: project point onto segment between two consecutive corridor sections, check if longitudinally between them (`0 <= t <= 1`) and laterally within `corridorHalfWidth`

**The bracket check fails for roundabout ring nodes against connecting road corridors.** The connecting road corridor sections follow the connecting road's centerline and end at/near the ring junction. Ring edge blend nodes (at lateral position 1.1x half-width from ring center) project to `t > 1.0` (past the last corridor section) or are laterally outside the corridor due to the angle between the ring tangent and the connecting road direction.

## Approaches Tried

### Attempt 1: Proximity-Based Suppression Zones (FAILED)

**Idea:** Create `RoundaboutSuppressionZone` records at each roundabout junction position. For ring nodes within the zone's radius, suppress directly without corridor overlap check.

**Implementation:**
- `BuildRoundaboutSuppressionZones()` in `DecalRoadGenerator.cs`
- For each roundabout junction: zone position = junction position, radius = 2x max connecting road corridor half-width
- In `BuildSegmentsWithCorridorCheck`: if node belongs to ring spline and is within zone radius, suppress

**Result:** "Exactly nothing changed." The zones either had wrong positions, wrong radii, or the junction positions don't align with where the ring nodes actually are. The `NetworkJunction.Position` may not correspond to the geometric overlap area on the ring.

**Code was reverted.**

### Attempt 2: Centerline-Based Corridor Check (FAILED — not yet tested visually but user reported no change)

**Idea:** For roundabout ring splines, check corridor overlap using the cross-section center points (ring centerline) instead of the laterally offset edge blend node positions. The ring centerline should pass through connecting road corridors at junction points even if the offset nodes don't.

**Implementation:**
- In `GenerateForLayerRange`: for `spline.IsRoundabout`, pass `sections.Select(cs => cs.CenterPoint).ToList()` as `centerlineCheckPositions`
- In `BuildSegmentsWithCorridorCheck`: new optional param `IReadOnlyList<Vector2>? centerlineCheckPositions`; when provided, use `centerlineCheckPositions[i]` for the corridor check instead of `offsetNodes[i]`
- The output segments still use `offsetNodes` positions (for correct rendering), only the overlap decision uses centerline

**Result:** User reported it didn't work either. This means even the ring centerline doesn't fall inside connecting road corridors at junction points.

**Code is still in the codebase (not reverted yet).**

## Possible Explanations for Both Failures

1. **Connecting road corridors don't extend far enough**: The connecting road splines may be trimmed/shortened before reaching the ring, so their corridor sections end before the junction point.

2. **Junction influence zone mismatch**: The ring-wide zone position (centroid of all junction positions) may cause the initial proximity filter in `CheckWithJunctionFilter` to fail for ring nodes at the periphery. However, the zone radius should be large enough.

3. **Corridor section sampling gap**: The connecting road may have too few corridor sections near its endpoint, creating a gap between the last section and the actual road surface extent.

4. **NetworkJunction.Position is not at the geometric overlap**: The junction position may be at the ring center point of the connection, not where the connecting road's surface actually overlaps the ring.

## User's Suggested Approach (Not Yet Implemented)

> "Can't we just check where the AI roads not belonging to the roundabout overlap the roundabout spline and cut out the parts of the roundabout decalroads which have junction interruption enabled?"

And vice versa: remove all overlapping DecalRoad nodes which don't belong to the roundabout and have `InterruptAtJunctions = true`.

**Key insight from user:** Use the AI road corridors specifically (which are full track-width `IsTrackWidth = true`) rather than the general corridor, and check overlap against the spline path directly.

## Next Steps to Investigate

1. **Debug logging**: Add diagnostic output showing:
   - Ring node positions at junction areas
   - Connecting road corridor section positions near ring
   - Bracket check `t` values and lateral distances
   - Junction influence zone coverage
   - Whether `CheckWithJunctionFilter` even reaches the corridor check for ring nodes

2. **Visualize corridors**: Export corridor section centers as debug markers to verify they actually overlap the ring in the game world

3. **Check spline endpoint trimming**: Verify that connecting road splines extend all the way to/past the ring, not trimmed short

4. **Try the user's AI road approach**: Instead of checking corridor overlap, directly compute the intersection between the AI road (full track-width) geometry of connecting roads and the roundabout ring spline path. Use this intersection to build suppression ranges.

5. **Consider alternative geometry**: Instead of bracket-checking point-against-corridor, compute the actual distance from a ring node to the connecting road's centerline (nearest-point-on-polyline) and compare against the road width. This avoids bracket `t` clamping issues.

## Files Modified in This Session

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs` | Added `IsClosedLoop` flag |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Set `IsClosedLoop`, roundabout-first layer resolution |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs` | Closed-loop wrap-around, ring-wide influence zones |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Continuity exclusion, roundabout layer set resolution, one-way AI override, centerline check positions (attempt 2, still in code) |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | `CreateRoundaboutSet` with tread marks and one-way AI road |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs` | Closed-loop and roundabout influence zone tests |
