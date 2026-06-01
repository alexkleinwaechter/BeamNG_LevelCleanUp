# OSM Dynamic Lane & Direction Data for DecalRoad Generation

**Date:** 2026-03-17
**Status:** Design approved, pending implementation
**Depends on:** `2026-03-12-decalroad-generation-design.md` (DecalRoad generation pipeline)

---

## Goal

Dynamically derive lane count, lane direction, and one-way status from OpenStreetMap (OSM) Overpass data and use it to override DecalRoad AI road properties (`lanesLeft`, `lanesRight`, `oneWay`, `flipDirection`) during generation. Support per-segment lane changes (e.g., overtaking lanes on mountain roads) by splitting lane-dependent DecalRoad layers at lane-change boundaries.

## Problem Statement

The current DecalRoad pipeline has infrastructure for OSM-based lane data but it is non-functional:

1. **OSM tags are lost during pipeline processing.** `ParameterizedRoadSpline.OsmTags` is declared but never populated. Tags are discarded at the `RoadSpline` stage where only `OsmRoadType` (the `highway` tag value) survives.

2. **AI road properties are static.** `lanesLeft`, `lanesRight`, `oneWay`, `flipDirection` come from `DecalRoadLayerDefinition` UI configuration, not from OSM data. Every road of the same type gets identical lane configuration regardless of actual OSM tagging.

3. **Path merging destroys direction information.** `NodeBasedPathConnector` and `RouteRelationAssembler` can reverse point order during EndToEnd/StartToStart merges. OSM's `lanes:forward`/`lanes:backward` are relative to way direction, so reversals invalidate them. No "was-reversed" flag exists.

4. **Per-segment lane variation is lost.** When paths are merged, only path1's metadata survives. A mountain road where one segment has an overtaking lane (`lanes=3`) adjacent to a normal section (`lanes=2`) loses the variation.

## Design Overview

### Core Mechanism: Swap-on-Reversal Lane Segments

Lane/direction data is parsed from OSM tags into an `OsmLaneInfo` record when `PathWithMetadata` is created. Each path carries a `List<LaneSegment>` — one segment per original OSM way. When paths are reversed during merging, the lane info is swapped (`LanesForward` ↔ `LanesBackward`). When paths are merged, segment lists are combined with adjusted indices.

This makes lane data **immune to any number of reversals** — the info always stays aligned with current geometry direction.

At DecalRoad generation time, lane-dependent layers (AI roads, `IsPerLane` markings, center lines) are **split into separate DecalRoad objects** at lane-change boundaries. Lane-independent layers (edge lines, edge blends) render continuously.

### Data Flow

```
OsmFeature.Tags (full OSM tag dictionary)
    ↓ parse OsmLaneInfo from tags (fallback chain)
PathWithMetadata.LaneSegments [seg(0, LaneInfo), seg(48, LaneInfo), ...]
    ↓ survive merges: combine lists, swap on reversal
    ↓ consolidate adjacent identical segments
RoadSpline.LaneSegments (StartPointIndex → StartDistance conversion)
    ↓ copy through
ParameterizedRoadSpline.LaneSegments
    ↓ DecalRoadGenerator reads segments
Split lane-dependent layers at boundaries → separate DecalRoad objects
    ↓ per-segment AI road properties
GeneratedDecalRoad { lanesRight=Forward, lanesLeft=Backward, oneWay, flipDirection }
```

---

## Data Model

### OsmLaneInfo

Parsed lane/direction data stored relative to current geometry direction. The `Reversed()` method produces a copy with forward/backward swapped.

```csharp
public class OsmLaneInfo
{
    // === USED NOW ===
    public int TotalLanes { get; set; }
    public int LanesForward { get; set; }   // Lanes in current geometry direction
    public int LanesBackward { get; set; }  // Lanes against current geometry direction
    public int LanesBothWays { get; set; }  // Center/shared lanes (e.g., turn lane)
    public bool IsOneWay { get; set; }

    // === STORED FOR FUTURE USE ===
    public string? TurnLanesForward { get; set; }   // e.g., "left|through|right"
    public string? TurnLanesBackward { get; set; }
    public string? MaxSpeed { get; set; }
    public string? Surface { get; set; }            // paved/unpaved/gravel
    public string? BusLanes { get; set; }
    public string? HgvLanes { get; set; }
    public string? Access { get; set; }

    /// <summary>
    /// Returns a copy with forward/backward properties swapped.
    /// Called when geometry direction is reversed during path merging.
    /// </summary>
    public OsmLaneInfo Reversed() => new OsmLaneInfo
    {
        TotalLanes = TotalLanes,
        LanesForward = LanesBackward,
        LanesBackward = LanesForward,
        LanesBothWays = LanesBothWays,
        IsOneWay = IsOneWay,
        TurnLanesForward = TurnLanesBackward,
        TurnLanesBackward = TurnLanesForward,
        MaxSpeed = MaxSpeed,
        Surface = Surface,
        BusLanes = BusLanes,
        HgvLanes = HgvLanes,
        Access = Access
    };
}
```

### OsmLaneInfo Parsing — Fallback Chain

Static factory method `OsmLaneInfo.TryParse(Dictionary<string,string> tags)` returns `OsmLaneInfo?`:

| Priority | Available Tags | Resolution |
|----------|---------------|------------|
| 1 | `lanes:forward` + `lanes:backward` | Use directly |
| 2 | `oneway=yes` + `lanes` | All lanes forward, 0 backward |
| 3 | `oneway=-1` + `lanes` | 0 forward, all lanes backward |
| 4 | `lanes:forward` + `lanes` | backward = lanes - forward |
| 5 | `lanes:backward` + `lanes` | forward = lanes - backward |
| 6 | `lanes` only (two-way) | Even split; odd extra lane goes to forward |
| 7 | No lane tags | Return null (use `DecalRoadLayerSet.DefaultLaneCount` at generation time) |

Additional tags parsed into future-use fields regardless of lane resolution:
- `turn:lanes` / `turn:lanes:forward` / `turn:lanes:backward`
- `maxspeed`, `surface`, `bus:lanes`, `hgv:lanes`, `access`

### LaneSegment

Marks a position along a path where lane configuration changes.

```csharp
public class LaneSegment
{
    public int StartPointIndex { get; set; }    // Index into Points array (PathWithMetadata phase)
    public float StartDistance { get; set; }     // Meters along spline (after spline creation)
    public OsmLaneInfo LaneInfo { get; set; }
}
```

### LaneSegment List Operations

Static helper `LaneSegmentOps`:

```csharp
/// <summary>
/// Reverses a segment list for when the underlying point array is reversed.
/// Reverses list order, swaps each LaneInfo, recalculates StartPointIndex.
/// </summary>
public static List<LaneSegment> ReverseSegments(
    List<LaneSegment> segments, int totalPointCount)

/// <summary>
/// Combines two segment lists during path merge.
/// Offsets path2's indices by pointOffset, then consolidates
/// adjacent segments with identical lane configs.
/// </summary>
public static List<LaneSegment> MergeSegments(
    List<LaneSegment> segments1,
    List<LaneSegment> segments2,
    int pointOffset)

/// <summary>
/// Removes adjacent segments that have identical lane configuration
/// (same TotalLanes, LanesForward, LanesBackward, IsOneWay).
/// </summary>
public static List<LaneSegment> Consolidate(List<LaneSegment> segments)
```

---

## Pipeline Integration

### 1. PathWithMetadata — New Property

```csharp
public List<LaneSegment> LaneSegments { get; set; } = [];
```

Populated in `OsmGeometryProcessor.ConvertLinesToSplines()` when creating PathWithMetadata from OsmFeature. If `OsmLaneInfo.TryParse(feature.Tags)` returns non-null, create `[new LaneSegment { StartPointIndex = 0, LaneInfo = parsed }]`.

### 2. Merge & Reversal Integration Points

All 8 merge methods across both assembler classes need lane segment propagation. After the merged point list is constructed and before the new PathWithMetadata is returned, combine lane segments using `LaneSegmentOps.MergeSegments()`. For methods that reverse path2, first call `LaneSegmentOps.ReverseSegments()` on path2's segments before merging.

**RouteRelationAssembler** (4 methods):

1. **TryEndToStart()** — merged points: `[path1, path2]`. No reversal. `MergeSegments(path1.segs, path2.segs, path1.Count-1)`
2. **TryEndToEnd()** — merged points: `[path1, reversed(path2)]`. Reverse path2's segments first. `MergeSegments(path1.segs, ReverseSegments(path2.segs), path1.Count-1)`
3. **TryStartToEnd()** — merged points: `[path2, path1]`. No reversal. **path2 first:** `MergeSegments(path2.segs, path1.segs, path2.Count-1)`
4. **TryStartToStart()** — merged points: `[reversed(path2), path1]`. Reverse path2's segments. **path2 first:** `MergeSegments(ReverseSegments(path2.segs), path1.segs, path2.Count-1)`

**NodeBasedPathConnector** (4 methods):

5. **MergeEndToStart()** — merged points: `[path1, path2]`. No reversal. `MergeSegments(path1.segs, path2.segs, path1.Count-1)`
6. **MergeEndToEnd()** — merged points: `[path1, reversed(path2)]`. Reverse path2's segments first. `MergeSegments(path1.segs, ReverseSegments(path2.segs), path1.Count-1)`
7. **MergeStartToEnd()** — merged points: `[path2, path1]`. No reversal. **path2 first:** `MergeSegments(path2.segs, path1.segs, path2.Count-1)`
8. **MergeStartToStart()** — merged points: `[reversed(path2), path1]`. Reverse path2's segments. **path2 first:** `MergeSegments(ReverseSegments(path2.segs), path1.segs, path2.Count-1)`

After every merge, `LaneSegmentOps.Consolidate()` removes redundant boundaries where adjacent segments have identical lane configs.

**ClonePath() methods** in both `RouteRelationAssembler` and `NodeBasedPathConnector` must deep-copy the `LaneSegments` list when cloning a PathWithMetadata.

### 3. NodeBasedPathConnector.IsOneway() Fix

Currently only handles `oneway=yes/true/1`. Must also handle `oneway=-1` to align with RouteRelationAssembler.

**Subtlety with `oneway=-1`:** A `oneway=-1` way has traffic flowing AGAINST the digitized direction. The `IsOneway()` guard prevents reversing one-way paths during merging (to avoid creating segments going against traffic). For `oneway=-1`, the geometry already goes against traffic, so reversing it would actually **correct** the direction. Therefore, the merge guard should **not** block reversals of `oneway=-1` paths. The fix: `IsOneway()` returns true only for `yes/true/1` (forward one-way). The `oneway=-1` case is handled purely through `OsmLaneInfo` parsing (LanesForward=0, LanesBackward=totalLanes), and the swap-on-reversal mechanism keeps it correct regardless of whether the path gets reversed.

### 4. RoadSpline — New Property

```csharp
public List<LaneSegment>? LaneSegments { get; set; }
```

Populated in `OsmGeometryProcessor.ConvertLinesToSplines()` after RoadSpline is created from PathWithMetadata. At this point, convert `StartPointIndex` to `StartDistance`:

**Conversion strategy:** Compute `StartDistance` in PathWithMetadata point space (cumulative Euclidean distance between consecutive points up to `StartPointIndex`) before passing to RoadSpline. This is done while the original point array is still available. The distance is in meters (points are already in meter coordinates). After spline interpolation resamples the control points, `StartDistance` remains valid as an arc-length position along the spline because the spline preserves the overall path geometry. When the DecalRoadGenerator samples cross-sections, it can match each cross-section's distance-along-spline to the appropriate lane segment.

**Index recalculation on reversal:** When `ReverseSegments()` is called on a segment list with `totalPointCount = N`:

```
Given segments sorted ascending: [S0, S1, S2, ...]
Each segment S_i spans from StartPointIndex to (S_{i+1}.StartPointIndex - 1),
except the last segment which spans to N-1.

After reversal, original segment S_i (which ended at endIdx) becomes:
  new StartPointIndex = N - 1 - endIdx

where endIdx = S_{i+1}.StartPointIndex - 1  (or N-1 for last segment)

Example: N=100, segments [0, 48, 93]
  Seg0: 0..47   → reversed start = 100-1-47 = 52
  Seg1: 48..92  → reversed start = 100-1-92 = 7
  Seg2: 93..99  → reversed start = 100-1-99 = 0
  Reversed & sorted: [0(was Seg2), 7(was Seg1), 52(was Seg0)]
  Each LaneInfo is also .Reversed()
```

### 5. ParameterizedRoadSpline — New Property, Deprecate OsmTags

```csharp
public List<LaneSegment>? LaneSegments { get; init; }
```

Populated in `UnifiedRoadNetworkBuilder.BuildNetwork()` from `RoadSpline.LaneSegments`.

The existing `OsmTags` property (never populated, always null) is removed or marked obsolete.

### 6. DecalRoadGenerator — Lane-Aware Generation

**Modified `GenerateForSpline()` flow:**

1. **Resolve lane segments.** If `spline.LaneSegments` is non-empty, use them. Otherwise create a single virtual segment spanning the full spline using `DecalRoadLayerSet.DefaultLaneCount` with even split.

2. **Per-layer routing.** For each expanded layer:
   - **Lane-dependent** (`IsPerLane`, `LayerType == AIRoad`, `LayerType == CenterLine`): partition cross-sections by lane-change boundaries, generate separately per partition.
   - **Lane-independent** (all others): generate continuously across full spline, ignore lane segments.

3. **Lane-dependent generation.** For each sub-range of cross-sections within a lane segment:
   - `IsPerLane` layers: expand with segment-specific lane count (different number of lane boundary markings per section)
   - AI road layers: set properties from segment's `OsmLaneInfo`
   - Center line layers: split at boundaries (center line behavior may differ between 2-lane and 3-lane sections)

4. **AI road property derivation** per segment:

   | OsmLaneInfo State | lanesRight | lanesLeft | oneWay | flipDirection |
   |---|---|---|---|---|
   | LanesForward=2, LanesBackward=2 | 2 | 2 | false | false |
   | LanesForward=3, LanesBackward=2 | 3 | 2 | false | false |
   | LanesForward=3, LanesBackward=0, IsOneWay | 3 | 0 | true | false |
   | LanesForward=0, LanesBackward=2, IsOneWay | 0 | 2 | true | true |

5. **`GetLaneCount()` replacement.** The current method (always falling back to defaults) is replaced by reading from the current `LaneSegment.LaneInfo`. Fallback to `DefaultLaneCount` only when `LaneSegments` is null/empty.

6. **`LanesBothWays` handling.** BeamNG's AI road model has no concept of shared center lanes. When `LanesBothWays > 0`, the center lane(s) are added to `lanesForward` for AI road purposes (matching the fallback chain's "odd extra to forward" convention). For visual lane markings, `LanesBothWays` is ignored in the initial implementation — the `IsPerLane` expansion uses `TotalLanes - 1` boundaries regardless of direction. Future work could render center turn lanes with a different material.

### 7. RoadCorridorBuilder — Lane-Aware Corridor Width

`RoadCorridorBuilder.GetLaneCount()` (in `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`) has an identical pattern to `DecalRoadGenerator.GetLaneCount()` — it reads `spline.OsmTags` (always null) and falls back to defaults. This must also be updated to use `LaneSegments`.

For corridor width computation, use the **maximum** lane count across all segments of a spline. Corridor overlap is a spatial check, so using the widest section ensures no visual layers are clipped in wider segments.

### 8. Roundabout Pipeline

`ConvertLinesToSplinesWithRoundabouts()` is the primary entry point (not just `ConvertLinesToSplines()`). The roundabout pipeline creates separate closed-loop splines for roundabout rings via `RoundaboutDetector` and trims connecting roads via `ConnectingRoadTrimmer`.

**Roundabout rings:** Parse lane info from the roundabout's OSM tags like any other road. Roundabouts typically have `oneway=yes` and `lanes=1` or `lanes=2`. The parsed `OsmLaneInfo` applies to the entire ring (single segment). Since roundabouts are not merged with other paths, no merge complications arise.

**Connecting roads:** These are trimmed by `ConnectingRoadTrimmer`, which may modify point arrays. `LaneSegments` must survive trimming — since trimming removes points from one end, the surviving segment's `StartPointIndex` may need adjustment if points are removed from the start. The implementer must check `ConnectingRoadTrimmer` to verify whether it trims from start or end and adjust segment indices accordingly.

### 9. Existing OsmTags Cleanup

- Remove `DecalRoadGenerator.GetLaneCount()` (replaced by segment-based resolution)
- Remove `ParameterizedRoadSpline.OsmTags` (replaced by `LaneSegments`)
- The `OsmFeature.Lanes` computed property can remain as a convenience

---

## BeamNG Property Mapping Reference

From BeamNG Lua analysis (`vehicle/ai.lua`, `ge/extensions/util/decalRoadsEditor.lua`):

- `lanesLeft` / `lanesRight`: lane counts relative to DecalRoad node-order direction
- `lanesRight` lanes flow **forward** (in node order), `lanesLeft` flow **backward**
- `flipDirection`: reverses direction interpretation — when true, left/right swap meaning
- `oneWay`: all traffic flows in one direction
- `autoLanes = true`: let BeamNG compute lane string from lanesLeft/lanesRight
- `autoJunction = true`: automatic junction detection at DecalRoad intersections
- `useSubdivisions = true`: subdivision handling for smoother AI paths
- `drivability`: 0-1 float, AI road weight for pathfinding (1.0 = fully drivable)

Internally, BeamNG uses a lane string where `+` = forward lane, `-` = backward lane (e.g., `"--+++"` = 2 backward + 3 forward). The `autoLanes` flag generates this from lanesLeft/lanesRight.

Convention: **right-hand traffic** (continental European). BeamNG's per-level LHD/RHD setting is handled by the game engine.

---

## Driving Convention

Right-hand traffic is assumed. BeamNG handles LHD/RHD switching at the game level via a level setting — our pipeline does not need to account for it.

Our mapping is purely directional:
- `lanesRight` = `LanesForward` (lanes flowing in DecalRoad node-order direction)
- `lanesLeft` = `LanesBackward` (lanes flowing against)

---

## Deferred Features (Documented, Not Implemented)

### Minimum Segment Length Filter

A configurable `MinLaneChangeSegmentMeters` property on `DecalRoadSettings`. When set > 0, lane-change segments shorter than this threshold are absorbed into the longer neighboring segment.

```
Example with MinLaneChangeSegmentMeters = 30:
[2 lanes, 500m] → [3 lanes, 15m] → [2 lanes, 800m]
                   ↑ too short, absorbed
Result: [2 lanes, 1315m] — no split
```

Default: 0 (strict, trust OSM data). Users can increase for noisy datasets. Not implemented in initial version — documented here for future reference.

### Future-Use OsmLaneInfo Fields

Parsed and stored but not used in generation:

- **TurnLanesForward/Backward** (`turn:lanes`, `turn:lanes:forward`, `turn:lanes:backward`): Could drive turn-lane-specific materials at intersections, or different marking patterns for left-turn-only lanes.
- **MaxSpeed** (`maxspeed`): Could influence AI road speed limits or road classification.
- **Surface** (`surface`): Could influence material selection — use gravel DecalRoad textures for `surface=unpaved`.
- **BusLanes/HgvLanes** (`bus:lanes`, `hgv:lanes`): Could mark restricted lanes with different materials or affect per-lane drivability. **Caveat:** These are per-lane strings (e.g., `designated||`) that have directional content. The current `Reversed()` method stores them as-is without reversing per-lane order. When these fields are eventually used, `Reversed()` must also reverse the `|`-separated lane order within the string.
- **Access** (`access`, `vehicle`, `motor_vehicle`): Could affect drivability value on AI roads.

### Other Deferred Items

- `oneway=alternating/reversible` handling (rare, dynamic direction)
- Per-lane access restrictions affecting individual lane drivability values
- Visual differentiation of turn lanes (different marking material per lane)
- Lane-change taper geometry (gradual widening/narrowing at overtaking sections)

---

## Testing Strategy

All tests in `BeamNgTerrainPoc.Tests/DecalRoad/`:

### OsmLaneInfoTests.cs — Parsing and Reversal

- Parse `lanes=4, lanes:forward=3, lanes:backward=1` → correct values
- Parse `lanes=3, oneway=yes` → 3 forward, 0 backward, IsOneWay=true
- Parse `oneway=-1, lanes=2` → 0 forward, 2 backward, IsOneWay=true
- Parse `lanes=3` (two-way, no directional tags) → 2 forward, 1 backward
- Parse `lanes:forward=2, lanes=3` → backward computed as 1
- Parse `lanes:backward=1, lanes=3` → forward computed as 2
- Parse no lane tags → returns null
- `Reversed()` swaps forward/backward correctly
- `Reversed().Reversed()` returns to original values
- Future-use fields (TurnLanesForward/Backward) also swap on `Reversed()`

### LaneSegmentMergeTests.cs — Merge Operations

- EndToStart merge: combines segment lists with correct index offset
- EndToEnd merge: reverses path2's segments and swaps lane info
- StartToStart merge: reverses correctly
- Adjacent identical segments are consolidated after merge
- Merge of path with empty lane segments + path with lane segments
- Multiple merges in sequence preserve correct segment boundaries
- Segment with different lane count preserved through merge (overtaking lane scenario)

### DecalRoadGeneratorLaneTests.cs — Lane-Aware Generation

- Spline with uniform lane segments → single AI road with correct lanesLeft/lanesRight
- Spline with lane change mid-way → AI road split into separate objects at boundary
- Lane-independent layers (edge lines, edge blends) NOT split at lane boundaries
- IsPerLane layers split and get correct lane count per section
- CenterLine layers split at lane boundaries
- OneWay derivation: LanesForward > 0, LanesBackward == 0 → oneWay=true, flipDirection=false
- FlipDirection derivation: IsOneWay, LanesForward == 0, LanesBackward > 0 → flipDirection=true
- No lane segments → falls back to DecalRoadLayerSet.DefaultLaneCount with even split
- Fallback chain: various incomplete tag combinations produce correct results

---

## Files Affected

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs` | Lane/direction data model with `Reversed()` and `TryParse()` |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegment.cs` | Position + OsmLaneInfo along a path |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs` | Static helpers: ReverseSegments, MergeSegments, Consolidate |
| `BeamNgTerrainPoc.Tests/DecalRoad/OsmLaneInfoTests.cs` | Parsing and reversal tests |
| `BeamNgTerrainPoc.Tests/DecalRoad/LaneSegmentMergeTests.cs` | Merge operation tests |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs` | Lane-aware generation tests |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs` | Add `List<LaneSegment> LaneSegments` property (mutable, `set`) |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Parse OsmLaneInfo when creating PathWithMetadata; convert StartPointIndex→StartDistance when creating RoadSpline |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs` | Lane segment propagation at all 4 merge methods (2 with reversal), update `ClonePath()` to deep-copy LaneSegments |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs` | Lane segment propagation at all 4 merge methods (2 with reversal), update `ClonePath()` to deep-copy LaneSegments, keep `IsOneway()` as-is (no `-1` — see Section 3) |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs` | Add `List<LaneSegment>? LaneSegments` property |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs` | Add `List<LaneSegment>? LaneSegments`, deprecate `OsmTags` |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs` | Propagate LaneSegments to ParameterizedRoadSpline |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Lane-aware generation: segment splitting, AI property derivation, replace GetLaneCount() |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Update GetLaneCount() to use LaneSegments (max across segments for corridor width) |
