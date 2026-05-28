# Relation-Protected Junction Blocking — Attempt 1

**Date:** 2026-03-25
**Branch:** `feature/relation-protected-junction-blocking`
**Status:** Implemented but NOT visually successful — needs deep dive

## Problem

The spline merger (`NodeBasedPathConnector`) uses angle-first greedy matching to connect OSM ways into longer splines. At complex junctions (highway ramps, bridge approaches like the B416/Mosel area in Kattenes), the angle-based scorer picks geometrically plausible but topologically **wrong** connections — ways that look straight but don't actually continue each other in the real road network.

This produces:
- Circular/crossover merges (splines that cross over themselves)
- ~180-degree turns in splines at junction points (visible as tight loops in the red-circled areas)
- Incorrect junction geometry that doesn't match real-world layout

Disabling merging entirely (`disableSplineMerging=true`) produces correct junction geometry — each OSM way follows its real-world path. But merging is needed for smooth road continuity.

## What We Implemented

### Approach: Relation-Protected Junction Blocking

Used OSM route relations (`type=route, route=road`) as hard constraints at junction nodes instead of just scoring bonuses.

**Blocking rules at junction nodes (valence >= 3):**

| Path 1 | Path 2 | Decision |
|--------|--------|----------|
| Has relation | Has relation, shared | ALLOW (relation-mandated) |
| Has relation | Has relation, different | BLOCK |
| Has relation | Orphan (no relation) | BLOCK |
| Orphan | Orphan | ALLOW (angle-based) |

At non-junction nodes (valence < 3): no change, all merges use angle scoring as before.

### Key Changes

1. **`PathWithMetadata.AllWayIds`** — New `HashSet<long>` tracking ALL original OSM way IDs through merges. Previously only `OsmWayId` (first way's ID) was tracked, making relation membership partially invisible after Tier 0 merges combined multiple ways.

2. **`NodeBasedPathConnector.ScoreEndpoint`** — Added ~15 lines of blocking logic using `HasRouteRelation()` and explicit `sharesRelation` boolean flag.

3. **`ShareRouteRelation`** — Changed from checking single `OsmWayId` to checking all `AllWayIds` via `GetPathRelationIds()`.

4. **Both `RouteRelationAssembler` and `NodeBasedPathConnector`** — All merge methods and `ClonePath` now propagate `AllWayIds` via `UnionWayIds()`.

### Files Modified

- `BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs`
- `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs`
- `BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs`
- `BeamNgTerrainPoc.Tests/Osm/NodeBasedPathConnectorTests.cs` (new, 11 tests)

### Test Results

All 11 new tests pass, all 151 total tests pass. The logic is correct in isolation — the blocking rules work as designed in synthetic test scenarios (collinear paths at T-junctions, multi-relation overlap, orphan-vs-relation, etc.).

## Visual Result: NOT Successful

The rendered output still shows:
- **~180-degree turns** in splines at junction points (red-circled areas in screenshot)
- Splines that don't merge where expected
- The `residential` road type appears involved in some of the wrong merges

## Deep Dive Needed

The blocking logic is correct in unit tests but the real-world Kattenes data still produces wrong merges. Possible reasons to investigate:

### 1. The wrong merges may not be at junction nodes (valence >= 3)
The blocking only activates at junction nodes. If the problematic merges happen at valence-2 nodes (only two ways share a node), the blocking won't fire. Need to check: what is the actual valence at the nodes where wrong merges occur?

### 2. The problematic ways may not have route relations
The blocking only protects relation-member ways. If the B416 ramp ways or the residential roads at the junction don't belong to any route relation, they're "orphan" and the blocking doesn't apply. Need to check: which specific OSM way IDs are involved in the wrong merges, and do they have route relations?

### 3. Highway type partitioning may not separate the problematic ways
`NodeBasedPathConnector` partitions by exact highway type (e.g., `secondary` vs `residential`). If the wrong merge is between two `residential` ways, the partition won't help. The selected code (`residential` in `RoadSpline.cs:107`) suggests residential roads are involved.

### 4. The 180-degree turns might be from RouteRelationAssembler (Tier 0), not NodeBasedPathConnector (Tier 1-3)
Tier 0 merges ways within the same route relation. If the relation member ordering is wrong in OSM data, Tier 0 could create bad merges before our blocking even runs.

### 5. Need diagnostic logging
To debug effectively, we need to log:
- Which specific merges are being performed (way IDs, node IDs, scores)
- Which merges are being blocked by relation protection
- The valence of nodes where merges occur
- The route relation membership of each way involved

### Suggested Next Steps

1. **Add diagnostic logging** to `NodeBasedPathConnector` that prints each merge decision with way IDs, node valence, and relation status
2. **Identify the specific OSM ways** producing the 180-degree turns (cross-reference with OSM data for the Kattenes area)
3. **Check if `disableSplineMerging=true` still fixes the visual issue** — confirms the problem is in merging, not downstream
4. **Consider whether the valence threshold (>= 3) is too high** — maybe valence >= 2 blocking at relation boundaries would be more effective
5. **Inspect the Overpass query** to verify route relations are being fetched for this area
