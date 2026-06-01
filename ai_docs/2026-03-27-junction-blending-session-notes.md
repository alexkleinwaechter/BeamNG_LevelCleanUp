# Junction Blending Regression Fix — Session Notes (2026-03-27)

**Branch:** `feature/relation-protected-junction-blocking`
**Commits:** 22 commits (from `6d3d28e` to `7dfebaf`)

---

## What Was Implemented

### Phase C: Eliminate False Junctions (DONE, works well)

Added `JunctionType.Continuation` for degree-2 OSM way boundaries (deflection < 30deg, width ratio < 2:1). These are skipped by the blender — elevation handled by chain-based smoothing. Rendered as gray dots in debug image.

**Files changed:**
- `NetworkJunction.cs` — enum value
- `NetworkJunctionDetector.cs` — `IsDegree2Continuation()` detection + log
- `UnifiedJunctionProfileBlender.cs` — skip in switch
- `NetworkJunctionHarmonizer.cs` — debug rendering + legend

### Phase A: Multi-Way Junction Blending (DONE, works well)

Rewrote `ComputeMultiWayConstraints` with three-tier dominant road detection:
1. **Priority:** strictly higher priority than all others
2. **Width:** >= `DominantRoadWidthRatio` (default 1.5) x average width of others
3. **Length:** >= 3x average length of others (catches same-priority/width cases like D 914 meeting short roundabout entries)

When dominant found → multi-T path (dominant unmodified, terminators edge-anchored).
When no dominant → peer path (priority-weighted average with flat zones + analytical deltas).

Added two-pass deferral for multi-T terminators and short-spline overlap protection (80% threshold).

**Safety toggle:** `EnableMultiWayDominantRoadDetection` (default: true) + `DominantRoadWidthRatio` (default: 1.5) on `JunctionHarmonizationParameters`.

**Files changed:**
- `UnifiedJunctionProfileBlender.cs` — major rewrite of multi-way path
- `JunctionHarmonizationParameters.cs` — toggle + ratio parameters

### Blend Propagation Through Short Segments (DONE, works for roundabouts)

When a spline is too short for its blend zone (`roadLength < flatZone + blendDist * 0.5`), propagates the constraint to neighboring splines at the far junction.

**Three propagation mechanisms:**
1. **Endpoint propagation:** Remaining blend attached to endpoint neighbors (works when merging disabled)
2. **Elevation blending into direct constraints:** When neighbor already has a direct constraint, blend its elevation toward propagated elevation (weight = remainingBlend / totalBlend). This shifts the CrossRoads constraint toward the roundabout elevation.
3. **Continuous-road mid-spline influences:** When far junction has a continuous road (T-junction with merged splines), collect quintic smoothstep influences along the continuous road. This handles the merged-spline case where the main road passes through and has no endpoint.

**Key design:**
- One-hop only (`IsPropagated` guard)
- Direct constraints always win for structure (slope, bank, flat zone)
- `PropagatedThroughSplineId` for diagnostics
- `CapBlendDistanceToRoadLength` removed (superseded)

**Files changed:**
- `UnifiedJunctionProfileBlender.cs` — propagation pass, mid-spline influences
- `JunctionEndpointConstraint.cs` — `IsPropagated`, `PropagatedThroughSplineId`

### Adaptive Blend Distance Cap (DONE)

`CalculateAdaptiveBlendDistance` now caps slope-based extension at 2.5x the configured distance. Without this, steep terrain with 25m+ elevation diffs produced 245m+ blend distances that dominated entire roads.

Example: 50m configured → max 125m adaptive (was unbounded 245m).
Roundabouts unaffected — small elevation diffs mean adaptive barely extends.

### UI Changes (DONE)

Increased blend distance slider max from 100/60 to 200m for both `JunctionBlendDistanceMeters` and `RoundaboutBlendDistanceMeters`.

---

## What Was Tried and Reverted

### Same-Priority T-Junction Continuous Road Nudge (REVERTED — commit `987fb37` → `7dfebaf`)

**Problem:** At T-junctions where all roads have the same priority (residential streets), the continuous road stays completely flat while the terminating road does ALL the elevation work. This creates steep ramps on short side roads.

**Attempted fix:** Apply 30% mid-spline influence on the continuous road toward the terminating road's natural elevation when priorities are equal and elevation diff > 0.5m.

**Result:** Too aggressive — deformed the main road's profile badly. The quintic smoothstep influence creates visible warping of the continuous road. The approach is fundamentally wrong because mid-spline influences are additive with the terrain-following profile, and when multiple T-junctions exist along a road, the influences compound and distort the entire profile.

**Why it failed:** Mid-spline influences work well for localized point corrections (MidSplineCrossing, propagated roundabout constraints) but NOT for systemic elevation adaptation across an entire road. The continuous road needs a constraint-based approach, not an influence-based one.

---

## Open Problems

### 1. Same-Priority T-Junction Elevation Sharing

**The problem:** When residential streets of equal priority meet at T-junctions, the continuous road (the one that happens to pass through the junction node) gets NO constraint and stays at its natural terrain-following elevation. The terminating road does all the elevation work, creating steep ramps (e.g., 25m drop over 231m on Rue Joan Miró).

**Root cause:** The T-junction model assumes the continuous road is "dominant" and should not be modified. This is correct for highway/residential (different priority) but wrong for residential/residential (same priority).

**The parameter sensitivity problem:** With 50m blend distance, the side road has too much elevation work to do, creating visible warping. With 30m, it looks better but the junction area is compressed. There's no single "sweet spot" that works for all junctions on a big map because the elevation diffs vary wildly.

**Possible approaches (not yet implemented):**
- **A: Constraint-based continuous road adaptation.** Instead of mid-spline influences, create a real endpoint-like constraint on the continuous road at the T-junction point. This would require splitting the constraint system to support mid-spline constraints (not just endpoints).
- **B: Reclassify same-priority T-junctions.** When all contributors have the same priority, don't classify as T-junction at all — use the peer path instead, which gives all roads equal treatment. Risk: the continuous road might get an inappropriate flat-zone constraint.
- **C: Reduce the terminating road's constraint elevation diff.** Instead of snapping to the continuous road's exact surface elevation, snap to a weighted average between the continuous road and the terminator's natural elevation. This splits the elevation gap so neither road has to do all the work.
- **D: Adaptive blend distance based on elevation difference.** Instead of a fixed configured distance, scale blend distance per-constraint based on the actual elevation gap. Small gaps get short blends, large gaps get long blends. This would make the parameter less sensitive.

### 2. Dual-Parameter Confusion

`JunctionBlendDistanceMeters` and `RoundaboutBlendDistanceMeters` both default to 50m and serve the same purpose. The propagation system makes the distinction even less meaningful since constraints flow between junction types. Consider unifying into a single parameter.

---

## Log Markers for Debugging

| Log marker | Meaning |
|------------|---------|
| `[PROPAGATE]` | Constraint propagated to endpoint neighbor (no existing constraint) |
| `[PROPAGATE-BLEND]` | Propagated elevation blended into existing direct constraint |
| `[PROPAGATE-CONTINUOUS]` | Propagated to continuous road via mid-spline influences |
| `[OVERLAP-PROTECT]` | Blend distances reduced because both ends' zones exceed 80% of road |
| `[T-TRANSITION]` | Transition zone setup for T-junction analytical delta |
| `[T-SNAP BLEND]` | Endpoint cross-section values after blending |
| `[T-SAME-PRIO-NUDGE]` | (REVERTED) Continuous road nudged at same-priority T-junction |
| `[ROAD-LENGTH-CAP]` | (REMOVED) Blend distance capped to 40% of road length |
| `Multi-T Junction #N` | Junction with detected dominant road |
| `Peer Junction #N` | Junction with all-equal contributors |
| `Blend propagation: N short spline(s)` | Summary of propagation pass |
| `Applied N propagated mid-spline influences` | Summary of continuous-road influences |

---

## Commit History

```
6d3d28e feat: add Continuation junction type for degree-2 OSM way boundaries
bd7d448 feat: detect degree-2 continuations as Continuation junction type
14a4021 feat: skip Continuation junctions in profile blender
bc011d6 feat: render Continuation junctions as gray dots in debug image
8fb293e fix: add explicit Continuation case in harmonizer elevation switch and legend
46990ad fix: make Continuation junction dots more visible in debug image
30d0b0a feat: add EnableMultiWayDominantRoadDetection toggle and DominantRoadWidthRatio
45adc5e feat: rewrite ComputeMultiWayConstraints with dominant road detection
6fd9f14 feat: defer multi-T-junction terminators to pass 2
11df40e feat: add short-spline overlap protection to BlendSplineProfile
2756a6c fix: add logging to peer junction constraint path
b52f454 feat: cap blend distance to 40% of road length at all constraint sites
b7365bb feat: add IsPropagated and PropagatedThroughSplineId to JunctionEndpointConstraint
43d0573 feat: propagate junction constraints through short splines
54778ee refactor: remove CapBlendDistanceToRoadLength, superseded by propagation
ff8050f fix: use HashSet for accurate short-spline count in propagation log
a344f51 feat: blend propagated elevation into existing direct constraints
1931679 feat: add length-based dominant road detection at multi-way junctions
6366050 feat: increase blend distance slider max to 200m and add propagation plan doc
bd740e9 feat: propagate constraints onto continuous roads via mid-spline influences
81dc40c feat: cap adaptive blend distance at 2.5x configured value
987fb37 feat: nudge continuous road at same-priority T-junctions (REVERTED)
7dfebaf Revert "feat: nudge continuous road at same-priority T-junctions"
```
