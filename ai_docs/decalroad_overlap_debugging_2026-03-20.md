# DecalRoad Overlap Post-Processor — Debugging Notes

## Current State (2026-03-20)

Branch: `fix/roundabout-decalroads`

The post-processor approach (generate all roads uninterrupted, then detect/resolve overlaps) is implemented and partially working:
- **Roundabouts**: Working correctly — edge blends/lines interrupted at connecting roads
- **T-junctions**: Edge blends interrupted correctly (after `PreserveContinuity` fix)
- **Some roads**: Edge lines and lane markings completely missing or stopping too early

## What Works

1. `SurfaceFootprintIndex` — spatial hash grid of surface road segments
2. `DecalRoadOverlapPostProcessor` — classifies, masks, splits
3. `PreserveContinuity` — DirectionDivider exemption at T-junctions
4. Roundabout closed-loop splitting

## The Open Bug

**Symptom**: Some roads lose their entire EdgeLine or LaneMarking. The marking just stops mid-road, or is completely absent. Other roads nearby are fine.

**What was tried and failed**:
- Junction proximity gate (25m radius) — reverted, didn't fix the issue and added wrong complexity

## Key Behavioral Difference: Old vs New Code

### Old code (`BuildSegmentsWithCorridorCheck`)
1. Used `CheckWithJunctionFilter` — only tested nodes near junction influence zones
2. Corridor-based: checked against abstract corridor geometry (centerlines + half-widths)
3. Splitting happened during generation (per-layer, inside `GenerateForLayerRange`)
4. Chunking happened inside `GenerateForLayerRange` (before any post-processing)

### New code (`DecalRoadOverlapPostProcessor`)
1. Checks EVERY node against ALL surface roads everywhere (no junction proximity filter)
2. Footprint-based: checks against actual generated DecalRoad nodes
3. Splitting happens after generation (post-processing all roads at once)
4. Chunking happens after post-processing

## Investigation Leads

### Lead 1: Surface footprint width may be too generous
Surface roads with `IsLaneWidth = true` have width = `roadWidth / laneCount`. For a 2-lane 7m road, that's 3.5m. Footprint half-width = `3.5/2 + 0.5m margin = 2.25m`. For `IsTrackWidth = true`, it's `7/2 + 0.5 = 4.0m`.

An edge line at position ±1.0 is `0.5 * roadWidth = 3.5m` from center. At a perpendicular junction, only nodes within ~2.25m of the junction point should overlap. But at shallow angles, overlap extends further.

**Check**: Is the 0.5m margin in `SurfaceFootprintIndex` too large? Should it be reduced or removed?

### Lead 2: Short road segments between two junctions
If a road has two junctions close together, both junction overlap zones suppress nodes. The remaining non-overlapping run between them might have < 3 nodes, causing the entire segment to be discarded.

**Check**: Add diagnostic logging to `SplitOpenRoad` to report when fragments are discarded due to `< 3 nodes`.

### Lead 3: Footprint includes ALL surface roads, not just junction neighbors
The old code only checked against corridors that contributed to nearby junctions. The new footprint includes ALL surface roads. Two parallel roads (not at a junction) would suppress each other's edge lines.

**Check**: Add diagnostic output listing which SplineId is causing each suppression. If a road's edge line is being suppressed by a non-neighboring road, this confirms the issue.

### Lead 4: ComputeFilteredRanges pre-splitting creates short DecalRoads
The `CurveConstraint = ReplaceInCurve` mode splits a layer into multiple `GenerationSegment` objects (straight segments with main material, curve segments with replacement material). Each generates a separate `GeneratedDecalRoad`. If one of these is short (near a junction), the post-processor's 3-node minimum might discard it entirely.

**Check**: Before the post-processor runs, log each `GeneratedDecalRoad`'s node count and SplineId. Look for roads with very few nodes.

## Recommended Debugging Approach

1. **Add diagnostic output** to `DecalRoadOverlapPostProcessor.Process()`:
   - For each interruptable road: log `Name`, `SplineId`, node count
   - For each node marked overlapping: log position, overlapping SplineId
   - For each discarded fragment (< 3 nodes): log the discard
   - For each road that returns empty (completely removed): log warning

2. **Generate terrain with the problem area** and examine the diagnostic output

3. **Identify which specific roads are being falsely suppressed and by which surface roads**

4. **Then decide on the fix**: either scope the footprint to junction-adjacent splines, reduce the margin, or handle the "short segment between junctions" case differently

## Files Involved

| File | Role |
|------|------|
| `Services/DecalRoad/DecalRoadOverlapPostProcessor.cs` | Main post-processor — the bug is here |
| `Services/DecalRoad/SurfaceFootprintIndex.cs` | Spatial hash grid — margin/width may be too generous |
| `Services/DecalRoad/DecalRoadGenerator.cs` | Calls post-processor, provides continuity lookup |
| `Models/DecalRoad/GeneratedDecalRoad.cs` | Metadata fields (SplineId, InterruptAtJunctions, etc.) |

## What NOT to Change

- `PreserveContinuity` logic — this is correct and working
- `SplitClosedLoopRoad` — roundabout splitting works
- `BuildContinuityLookup` — junction continuity detection is correct
- Chunking in `Generate()` — moved correctly to after post-processing
