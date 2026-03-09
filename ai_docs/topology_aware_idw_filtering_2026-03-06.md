# Fix: Topology-Aware IDW Filtering in Terrain Blending (Option B)

**Date**: 2026-03-06
**Branch**: `research_rubberband_idea`
**File Modified**: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs`

## Context

Phase 4 terrain blending creates massive terrain walls between unconnected road endpoints. The root cause is in `ElevationMapBuilder.InterpolateNearbyCrossSectionsBuffered` — for OSM roads, it IDW-weights cross-sections from ALL splines within a global `maxSearchRadius`. When two unrelated road endpoints are geographically close but share no junction, the blending mixes their elevation profiles, creating phantom ramps/walls in the terrain.

The fix: filter the IDW interpolation to only include cross-sections from splines that are **directly connected** (share a junction) to the dominant owner spline at each pixel.

## Root Cause Analysis

1. **Global search radius** (`maxSearchRadius`): Computed as `max(allSplines.RoadWidth/2) + max(allSplines.TerrainAffectedRange)`. One wide highway with large blend range causes every pixel to search a huge radius.
2. **OSM IDW mixes all splines**: `InterpolateNearbyCrossSectionsBuffered` finds ALL cross-sections within the global radius from ANY spline and blends them via inverse-distance weighting.
3. **Endpoint tapering amplifies the problem**: Dead-end endpoints get tapered toward local terrain elevation. Two unrelated endpoints at different terrain heights get very different elevations.
4. **The wall forms**: A terrain pixel between two unrelated road endpoints picks up IDW-weighted elevations from both, creating a ramp/wall in the terrain.

## Alternatives Considered

- **Option A (single-spline for all)**: Make OSM roads use same single-spline interpolation as PNG roads. Simplest but loses smooth multi-spline blending at real junctions.
- **Option C (per-spline search radius)**: Replace global `maxSearchRadius` with per-spline radii. Still allows contamination when unrelated roads run parallel within each other's blend range.
- **Option B (topology-aware, chosen)**: Keep multi-spline IDW but filter to topologically connected splines. Most correct — preserves junction blending, prevents phantom connections.

## Implementation

### New Method: `BuildDirectJunctionAdjacency`

Builds `Dictionary<int splineId, HashSet<int> connectedSplineIds>` from `network.Junctions`. For each junction, all contributor splines are mutually connected. Each spline always includes itself.

**Direct connectivity only** — NOT transitive. If A-B share junction 1 and B-C share junction 2, A's set is `{A,B}` not `{A,B,C}`. Transitive closure would connect everything in dense networks, defeating the purpose.

### Call Site Change

In `BuildElevationMapWithOwnership`, the adjacency map is built once before the parallel pixel loop. At each OSM pixel, the nearest spline's adjacency set is passed to the interpolation method:

```csharp
var allowedSplines = junctionAdjacency.GetValueOrDefault(nearestSplineId);
InterpolateNearbyCrossSectionsBuffered(worldPos, spatialIndex, maxSearchRadius, searchBuffer, allowedSplines);
```

### Interpolation Filter

Both `InterpolateNearbyCrossSectionsBuffered` and `InterpolateNearbyCrossSections` gained an optional `HashSet<int>? allowedSplines = null` parameter. When non-null, cross-sections from splines not in the set are skipped:

```csharp
if (allowedSplines != null && !allowedSplines.Contains(cs.OwnerSplineId))
    continue;
```

### What Does NOT Change

- `InterpolateFromSingleSplineBuffered` / `InterpolateFromSingleSpline` — already single-spline filtered
- PNG road path — already uses single-spline interpolation
- `CrossSectionSpatialIndex` — no changes, filtering is at the consumer level
- Dominant owner logic inside interpolation — still runs but only over filtered set

## Edge Cases

| Case | Behavior |
|------|----------|
| Isolated dead-end road (no junctions) | Adjacency = `{self}` only. Becomes single-spline interpolation. Correct. |
| Roundabout | Ring + connecting roads share junction. All in each other's sets. Blending preserved. |
| MidSplineCrossing | Both crossing roads in each other's sets. Blending preserved. |
| Pixel between two unconnected roads | Only nearest road's connected set contributes. No phantom wall. |

## Verification

1. Build: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
2. Run terrain generation on a map with the wall artifacts
3. Verify: walls between unconnected endpoints are gone
4. Verify: junction areas (T-junctions, roundabouts, crossroads) still blend smoothly
5. Check log output for adjacency stats (max neighbors should be small, typically 2-4)
