# DecalRoad Junction Suppression — Corridor Overlap Approach

**Date**: 2026-03-15
**Status**: Design approved, pending implementation
**Replaces**: Circular exclusion zone system (JunctionInterrupter, JunctionInterruptionRuleBuilder, JunctionInterruptionRule)

## Problem

The current DecalRoad junction interruption uses circular exclusion zones centered on junction positions. This produces crude, inaccurate results:

1. **Edge blends cut with "eraser" effect** — circular cutouts don't follow actual road geometry, leaving ugly gaps at junctions
2. **Both sides suppressed equally** — centerline-based distance checks suppress left and right edge layers identically, even when only one side has an overlapping road
3. **Side detection unreliable** — dot-product-based L/R classification fails on curves, acute angles, and complex junction geometries
4. **Edge blends disabled entirely** — set to `InterruptAtJunctions = false` as workaround because circular zones were too crude

The road mesh (DAE export) forms closed, continuous surfaces at junctions. The junction boundary problem is already solved geometrically — we just need to use that geometry.

## Solution: Per-Node Corridor Overlap Check

Instead of computing exclusion zones from junction positions, check each DecalRoad node's **actual 2D position** against every other road's **surface corridor**. If the node falls inside another road's corridor, suppress it.

This naturally handles side-specific suppression: a left edge blend node won't overlap a road connecting from the right side, so it remains intact. No L/R classification logic needed.

## Approach Choice

Three approaches were evaluated:

| Approach | Description | Verdict |
|----------|-------------|---------|
| **A: Per-Node Corridor Check** | Project each node onto nearby roads' cross-section corridors | **Selected** — uses exact existing geometry, sub-meter precision, natural side handling |
| B: Road Surface Polygon Mask | Build 2D polygons from edge points, point-in-polygon test | Rejected — polygon construction error-prone on curves, concave shapes need triangulation |
| C: Rasterized Bitmap Mask | Paint corridors onto 2D bitmap, sample per node | Rejected — resolution-dependent artifacts at edges, large memory, loses precision where needed most |

## Phase 1: Corridor Overlap Suppression

### Core Rule

> If a DecalRoad node's actual 2D position falls inside any OTHER road's surface corridor, suppress it.

Layers opt into this check via their existing `InterruptAtJunctions` property. Layers with `InterruptAtJunctions = false` skip the check entirely (AI roads, etc.).

### RoadCorridor Data Structure

Built once per spline before DecalRoad generation. Contains the sampled cross-sections and the computed corridor half-width:

```
RoadCorridor:
    SplineId: int
    RoadWidth: float                    // EffectiveMasterSplineWidthMeters
    CorridorHalfWidth: float            // max outer extent of all enabled layers (constant along spline)
    SampledSections: List<CorridorSection>

CorridorSection:
    Center: Vector2                     // cross-section CenterPoint
    Normal: Vector2                     // cross-section NormalDirection
    DistanceAlongSpline: float          // used for interpolation parameter between bracketing sections
```

Note: `CorridorHalfWidth` is a single scalar per corridor (not per-section) because it is computed from the layer set which is constant for the entire spline. No per-section interpolation of half-width is needed.

### Corridor Half-Width Calculation

The corridor half-width is the maximum outer extent of any enabled layer on that road. For each layer in the resolved layer set, compute the outermost position after expansion:

```
layerOuterExtent = |expandedPosition| * 0.5 * roadWidth + nodeWidth / 2
```

Where:
- `roadWidth` = `spline.Parameters.EffectiveMasterSplineWidthMeters`
- `expandedPosition` depends on the expansion mode:
  - **Mirrored** (`IsMirrored`): `|layer.Position|` (both sides are symmetric)
  - **Per-lane** (`IsPerLane`): `max(|boundary|)` across `CalculateLaneBoundaryPositions(laneCount)` — for 4 lanes the outermost boundary is at `|0.5|`
  - **TreadMarks** (`LayerType == TreadMarks`): `max(|center|)` across `CalculateLaneCenterPositions(laneCount)` — for 2 lanes the outermost center is at `|0.5|`
  - **Single placement**: `|layer.Position|`
- `nodeWidth` is resolved per layer:
  - `IsTrackWidth` → `roadWidth`
  - `IsLaneWidth` → `roadWidth / laneCount`
  - Otherwise → `layer.Width`

The corridor half-width is `max(layerOuterExtent)` across all enabled layers. The `JunctionExclusionMarginMeters` from settings is added on top as configurable tolerance.

**Example** — Primary road with `masterWidth = 7m`:
- EdgeLine: `|1.0| * 0.5 * 7 + 0.25/2 = 3.625m`
- EdgeBlend1: `|1.1| * 0.5 * 7 + 1.0/2 = 4.35m`
- EdgeBlend2: `|1.25| * 0.5 * 7 + 2.0/2 = 5.375m`
- **corridorHalfWidth = 5.375m**

### Corridor Overlap Check Algorithm

For a point P being checked against road A's corridor:

1. **Find closest section**: Find the section S_k whose center is closest to P (linear scan, or spatial index for large corridors).

2. **Check bracketing pairs**: Check the pair (S_{k-1}, S_k) and (S_k, S_{k+1}) to find which pair longitudinally brackets P. Bracketing test: compute `t` by projecting P onto the segment between the two section centers. If `0 ≤ t ≤ 1`, the pair brackets P.

3. **Interpolate center and normal**: Using the bracketing pair and parameter `t`, linearly interpolate: `center_t = lerp(S_i.Center, S_{i+1}.Center, t)` and `normal_t = normalize(lerp(S_i.Normal, S_{i+1}.Normal, t))`.

4. **Project laterally**: `lateralDist = dot(P - center_t, normal_t)`

5. **Check**: If `|lateralDist| < corridorHalfWidth` → P is inside road A's corridor.

**Endpoint handling**: The corridor extends from its first section to its last section. Points projecting before S_0 or after S_N are outside the corridor. This is intentional — corridor suppression at spline endpoints is handled by the other spline's corridor (the connecting road's corridor will cover the junction area from its own perspective).

**Short splines (2-3 sections)**: With only 2 sections, there is exactly one bracketing pair. This is sufficient — the corridor check still works correctly for short stub roads.

**Curved roads**: Using closest-section lookup avoids the tangent-projection ambiguity that arises on curves where consecutive sections have different tangent directions.

### Performance: Junction Proximity Filter

Full corridor checks for every node against every road would be O(nodes × roads × sections). Optimization:

1. **Pre-compute junction influence zones**: For each junction, compute a bounding radius = `max(contributing corridorHalfWidths) + margin`. Store junction positions and radii.

2. **Per-node quick reject**: Before checking corridors, test if the node is within any junction's influence zone. Nodes far from all junctions are guaranteed to not overlap other roads (on straight segments away from junctions, roads don't cross).

3. **Only check relevant corridors**: At each junction, only the contributing splines' corridors need checking. Don't check road A against road Z on the other side of the map.

This reduces the check from O(N × M) to O(junction_nodes × junction_roads), which is negligible.

### Generation Order Change

**Current order** (per spline):
1. Resolve layer set → expand layers → sample nodes → interrupt with rules

**New order** (two-pass):

**Pass 1 — Build corridors** (before any DecalRoad generation):
1. For each non-bridge/non-tunnel spline:
   a. Resolve layer set via cascade
   b. Compute `roadWidth` = `EffectiveMasterSplineWidthMeters`
   c. Determine `laneCount` from OSM tags or layer set default
   d. Compute `corridorHalfWidth` = max layer outer extent
   e. Sub-sample cross-sections at `nodeSpacingMeters`
   f. Store as `RoadCorridor` in `Dictionary<int, RoadCorridor>`

**Pass 2 — Generate DecalRoads** (per spline, largely unchanged):
1. For each spline, expand layers and sample nodes as before
2. For each layer with `InterruptAtJunctions = true`:
   - For each laterally-offset node, check against all OTHER splines' corridors (with junction proximity filter)
   - If node is inside any other corridor → suppress
3. Remaining nodes are segmented and chunked as before

### What Gets Replaced

| Old | New |
|-----|-----|
| `JunctionInterruptionRuleBuilder.cs` | **Deleted** — no more rule building |
| `JunctionInterruptionRule.cs` | **Deleted** — no more rule records |
| `JunctionInterrupter.cs` | **Replaced** by `RoadCorridorOverlapChecker.cs` |
| `InterruptionSide` enum | **Deleted** — geometry handles side naturally |
| `DecalRoadSettings.JunctionExclusionMarginMeters` | **Kept** — used as additional margin on corridor half-width for configurable tolerance |

### InterruptAtJunctions Flag Change

With the new system, edge blends SHOULD participate in corridor suppression. The defaults need updating:

| Layer | Old `InterruptAtJunctions` | New `InterruptAtJunctions` |
|-------|---------------------------|---------------------------|
| EdgeLine | `true` | `true` (unchanged) |
| CenterLine | `true` | `true` (unchanged) |
| LaneMarking | `true` | `true` (unchanged) |
| TreadMarks | `true` | `true` (unchanged) |
| EdgeBlend | `false` (workaround) | **`true`** (corridor is precise enough) |
| AIRoad | `false` | `false` (unchanged) |

### Node Segmentation

When nodes are suppressed, the remaining nodes form continuous segments (same as current system). Segments with fewer than `minSegmentNodes` (default 3) are discarded. Each segment becomes a separate DecalRoad with appropriate fade-in/out at cut points.

---

## Phase 2: Layer-Type-Aware Behavior

Phase 1 provides clean geometric suppression. Phase 2 adds per-layer-type intelligence:

### 1. Continuous Road Centerline Preservation

At T-junctions with a clear continuous (primary) road, the primary road's centerline should pass through uninterrupted.

**Detection**: Use existing `NetworkJunction.GetContinuousRoads()` classification.

**Implementation**: When the corridor check finds that a centerline node overlaps another road's corridor, look up which junction caused it. If the current spline is marked as continuous at that junction, skip suppression for `CenterLine` layer type.

Data needed: the corridor check must return not just a bool but an `OverlapResult`:

```
OverlapResult:
    IsOverlapping: bool
    OverlappingSplineId: int?           // which road's corridor was hit
```

Then check: is there a junction where current spline is continuous and the overlapping spline is terminating? If yes, skip suppression for CenterLine layers.

### 2. AI Road and IsLaneWidth Preservation

Already handled: `InterruptAtJunctions = false` for AI roads. Tread marks (`IsLaneWidth = true`) currently have `InterruptAtJunctions = true` — evaluate whether they should keep it. They sit inside the road surface, so they WILL overlap other roads' corridors at junctions. This may be acceptable (tire tracks don't visually appear in junctions anyway) or they could be switched to `false`.

### 3. Future Extension: Junction-Specific Layer Replacement

Instead of simply suppressing edge lines at junctions, a future `JunctionReplacementMaterial` property on `DecalRoadLayerDefinition` could generate replacement DecalRoad segments with an alternative material (e.g., dashed edge line) for the suppressed zones.

The corridor check identifies which nodes are suppressed. A second pass could:
1. Collect suppressed node ranges
2. Generate new DecalRoad objects with the replacement material for those ranges
3. Apply appropriate fade transitions at the replacement boundaries

This is **deferred** — noted as a future extension point, not implemented in Phase 2.

---

## Files Affected

### New Files
| File | Purpose |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs` | Corridor data model (sections + half-width) |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Builds corridors from network + resolved layer sets |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs` | Per-node overlap check with junction proximity filter |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs` | Unit tests for overlap logic |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorBuilderTests.cs` | Unit tests for corridor construction |

### Modified Files
| File | Changes |
|------|---------|
| `DecalRoadGenerator.cs` | Two-pass architecture: build corridors first, then generate with overlap check instead of rule-based interruption |
| `DecalRoadDefaultLayerSets.cs` | Set `InterruptAtJunctions = true` for EdgeBlend layers |
| `DecalRoadSettings.cs` | `JunctionExclusionMarginMeters` retained as corridor margin |

### Deleted Files
| File | Reason |
|------|--------|
| `JunctionInterruptionRuleBuilder.cs` | Replaced by corridor-based approach |
| `JunctionInterruptionRule.cs` | No longer needed (no rules, no side enum) |
| `JunctionInterrupter.cs` | Replaced by `RoadCorridorOverlapChecker` |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs` | Tests for deleted code |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs` | Tests for deleted code |

---

## Key Design Decisions

1. **Per-node 2D position check, not centerline**: Each laterally-offset node is checked at its actual position. This is WHY side-specific suppression works without L/R logic — a left edge blend at `center - normal * 4m` simply won't be inside a road connecting from the right.

2. **Corridor width from actual layer positions**: No guessing or hardcoded margins. The corridor half-width is computed from the exact same formula used to place the DecalRoad nodes: `|position| * 0.5 * EffectiveMasterSplineWidthMeters + nodeWidth / 2`.

3. **Junction proximity filter for performance**: Only check nodes near junctions. Away from junctions, roads on separate alignments don't overlap (guaranteed by the road network layout).

4. **Edge blends re-enabled for interruption**: The old system was too crude for edge blends, so they were disabled. The corridor approach is precise enough to interrupt them cleanly.

5. **Configurable margin preserved**: `JunctionExclusionMarginMeters` adds tolerance to the corridor half-width, accounting for minor geometry imprecisions or desired visual padding.

---

## Known Limitations

1. **Undetected crossings**: The junction proximity filter assumes roads only overlap near detected junctions. If OSM data has crossing roads not identified as junctions (and not tagged as bridges/tunnels), their corridors won't be checked against each other. This matches the old system's behavior.

2. **Parallel dual carriageways**: Two closely-spaced one-way roads (common OSM dual carriageway pattern) will have overlapping corridors in the gap between them. Inner edge blends will be mutually suppressed. This is likely desirable (no terrain-to-road transition between carriageways), but inner edge lines would also be suppressed. Acceptable for Phase 1; can be addressed later with a "same-direction parallel" detection if needed.

3. **Roundabouts**: Roundabout ring splines are included in the corridor dictionary. Connecting roads' edge layers will be suppressed where they overlap the ring's corridor, which is the correct behavior. The ring's own edge layers will be suppressed where connecting roads' corridors overlap them. This should produce clean results but may need visual tuning of the ring's corridor width.

4. **Custom layer type**: Layers with `LayerType = Custom` follow the default `InterruptAtJunctions = true` behavior and participate in corridor suppression. This is the safe default.

---

## Alternative Approaches (Evaluated, Not Selected)

### B: Road Surface Polygon Mask
Build 2D polygons from left/right edge points of consecutive cross-sections. For each DecalRoad node, run point-in-polygon against all other roads' polygons.

**Why rejected**: Polygon construction on curves produces concave shapes requiring triangulation. More memory. Point-in-polygon slower than corridor projection for long roads. Error-prone edge cases.

### C: Rasterized Bitmap Mask
Rasterize all road corridors onto a 2D bitmap at fixed resolution (e.g., 0.5m/pixel). For each node, sample the bitmap.

**Why rejected**: Resolution-dependent artifacts at road edges. Large memory for big terrains. Loses precision exactly where we need it most (road boundaries).
