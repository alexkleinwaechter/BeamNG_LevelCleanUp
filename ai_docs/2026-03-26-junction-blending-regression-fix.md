# Junction Blending Regression Fix — Design Spec

**Date:** 2026-03-26
**Branch:** `feature/relation-protected-junction-blocking`
**Status:** Design approved, implementation pending
**Prerequisite:** `ai_docs/2026-03-25-segment-based-road-architecture-plan.md` (Part 1 implemented)

---

## Problem

Part 1 of the segment-based road architecture (`disableSplineMerging=true`) introduced a regression: **abrupt elevation steps at junctions** where previously there were smooth transitions.

### Root Cause

With spline merging disabled, every OSM way is its own spline. Nodes that were previously internal to merged splines are now spline endpoints, creating **new junctions that didn't exist before**. These fall into two categories:

1. **Degree-2 continuations** — two splines share a node but it's just an OSM way boundary, not a real intersection. The junction is unnecessary.
2. **Degree-3+ split/merge nodes** — e.g., one-way entry/exit pairs at roundabouts (node 35301284: entry road + exit road + upstream feeder). These ARE real topology but were previously hidden inside merged splines.

### Symptoms

- Terrain cliffs/bumps at split/merge nodes (visible as car-launching ramps)
- Only one of three roads at a junction adapts elevation; others stay at terrain height
- Junction debug image shows `CrossRoads` (red) at nodes that should blend smoothly
- Worst at roundabout entry/exit pairs and dual carriageway merge points

### Why the Current Blender Fails

`ComputeMultiWayConstraints()` computes a priority-weighted average elevation and constrains all roads, but:
- **No flat zone** (`FlatZoneDistance = 0`) — no area of constant elevation at the junction center
- **No edge-anchored constraints** — unlike T-junctions, doesn't match the primary road's surface analytically
- **Constant delta mode** — applies uniform correction without considering road surface geometry
- **Short spline conflict** — 50m entry roads get constraints from BOTH ends (CrossRoads + Roundabout), the two Hermite corrections fight

---

## Junction Type Reference — Current Treatment and Changes

| Type | Degree | Current blender treatment | Changed? |
|------|--------|--------------------------|----------|
| `Endpoint` | 1 (dead end) | Snap to terrain elevation, zero slope/bank, blend inward | No |
| `TJunction` | 2+ (one continuous, others terminate) | Primary road unmodified; terminators get edge-anchored constraints (snap to primary surface with slope/bank match) | No |
| `YJunction` | 2 (all endpoints) | `ComputeMultiWayConstraints`: priority-weighted average elev, no flat zone, constant delta | **Phase A** |
| `CrossRoads` | 3–4 (all endpoints) | `ComputeMultiWayConstraints`: priority-weighted average elev, no flat zone, constant delta | **Phase A** |
| `Complex` | 5+ (all endpoints) | `ComputeMultiWayConstraints`: priority-weighted average elev, no flat zone, constant delta | **Phase A** |
| `MidSplineCrossing` | 2 (both continuous) | Separate handler (`ApplyMidSplineCrossingInfluences`): both roads continue, elevation nudge only | No |
| `Roundabout` | special (ring + connector) | Ring is primary; connecting road gets edge-anchored constraints to ring surface with radial slope match | No |
| **`Continuation` (new)** | 2 (OSM way boundary) | **No constraint — skipped entirely** (elevation handled by chain smoothing) | **Phase C** |

**Key observation:** `YJunction`, `CrossRoads`, and `Complex` all share the same `ComputeMultiWayConstraints` code path. Phase A upgrades this shared path with dominant-road detection (multi-T-junction) and proper flat zones.

---

## Solution: Two-Phase Fix (C then A)

### Phase C: Eliminate False Junctions at Degree-2 Continuations

**Goal:** Don't create junctions at nodes where exactly 2 splines meet and it's clearly the same road continuing. This is a performance optimization and reduces noise in the junction debug image.

**Approach:** In `NetworkJunctionDetector.ClassifyJunctions()`, after the initial classification, identify degree-2 endpoint clusters where both splines are endpoints (no continuous contributor), the deflection angle is small (< 30°), and width ratio is within 2:1. Reclassify these as a new `JunctionType.Continuation` type that the blender skips entirely.

**Why this is safe:** The elevation chain system (`NetworkElevationGraph`) already chains through degree-2 nodes transparently. These nodes get smooth elevation from chain-based smoothing. Adding a junction constraint on top is redundant at best, harmful at worst.

**Edge case — width transitions:** A degree-2 node where road width changes significantly (e.g., 7m → 3.5m taper) still gets classified as `Continuation` if deflection < 30° and width ratio < 2:1. Width transitions are a styling concern, not an elevation concern. If width ratio exceeds 2:1, it falls through to normal classification (likely `YJunction`) and gets blended — this is correct since a dramatic width change often indicates a real junction (e.g., slip road meeting motorway).

**Key design decisions:**
- Reuse the same heuristics as `NetworkElevationGraph.FindBestContinuation()` (deflection < 30°, width ratio < 2:1) for consistency
- New `JunctionType.Continuation` enum value — not `Endpoint` (which means dead-end)
- The blender's switch statement skips `Continuation` junctions (no constraint computation)
- The debug image renders `Continuation` junctions in a distinct color (e.g., dim gray) so they're visible but clearly passive

**Files to modify:**
- `NetworkJunction.cs` — add `Continuation` to `JunctionType` enum
- `NetworkJunctionDetector.cs` — add continuation detection in `ClassifyJunctions()`
- `UnifiedJunctionProfileBlender.cs` — skip `Continuation` in the switch statement
- `NetworkJunctionHarmonizer.cs` — render `Continuation` in debug image

### Phase A: Fix Multi-Way Blending for Peer-to-Peer Junctions

**Goal:** Make `ComputeMultiWayConstraints()` produce smooth elevation transitions at real degree-3+ junctions where all roads are equal peers (no "primary" road).

**Approach:** Upgrade `ComputeMultiWayConstraints()` to use the same edge-anchored constraint pattern that `ComputeTJunctionConstraints()` uses, adapted for the peer-to-peer case.

#### Step A.1: Detect dominant road at multi-way junctions

Before computing constraints, check if the junction has a **dominant road** — one that is clearly the main through-road while the others are branches. This is the common case at one-way pair split/merge nodes: the two-lane D 914 (7m) continues while narrow entry/exit roads (3.5m each) branch off.

**Detection heuristic** (reuse T-junction patterns):
1. Sort contributors by `road width × priority` (descending)
2. The widest/highest-priority road is the **candidate dominant**
3. Confirm dominance: candidate width ≥ 1.5× the average width of other contributors, OR candidate priority is strictly higher than all others
4. If confirmed: treat as **multi-T-junction** (one dominant, N terminators)
5. If no dominant: treat as **true peer junction** (all roads negotiate equally)

This splits `ComputeMultiWayConstraints` into two sub-paths.

#### Step A.2a: Multi-T-junction path (dominant road detected)

Reuse `ComputeTJunctionConstraints` logic directly:
- **Dominant road gets NO constraint** — passes through unmodified (same as T-junction continuous road)
- **Each terminating road gets edge-anchored constraints** — snap to the dominant road's surface at the exit point, inheriting slope and computing proper bank angle
- `FlatZoneDistance` = dominant road's half-width (same as T-junction)
- `PrimaryTangentDirection` = dominant road's tangent (enables spatially-varying analytical delta)

This is the fix for the roundabout entry/exit case: the main D 914 stays at its natural elevation, and both the narrow entry and exit roads blend smoothly down/up to its surface.

#### Step A.2b: True peer junction path (no dominant road)

For junctions where all roads are similar width/priority (e.g., three residential streets meeting):
- Compute **priority-weighted average elevation** (existing logic, correct for this case):
  ```
  harmonizedElev = SUM(road[i].elevation × road[i].priority) / SUM(road[i].priority)
  ```
- Compute shared junction slope by priority-weighted average of contributor slopes
- Set `FlatZoneDistance` to the maximum half-width of all contributor roads
- Per-road constraints use `PrimaryTangentDirection` set to the average slope direction, enabling the spatially-varying analytical delta mode in `BlendSplineProfile`. This is the key upgrade from the current constant-delta mode.
- All roads blend to the shared virtual surface

#### Step A.3: Adaptive blend distance with short-spline protection

Use `GetEffectiveBlendDistance()` (already exists) but add **overlap protection** for short splines:
- When a spline has constraints from both ends and their blend zones would overlap (cover > 80% of the spline), reduce both blend distances proportionally to leave a gap in the middle where the natural profile dominates
- Cap each constraint's effective blend distance at `(splineLength - flatZone) / 2`
- This prevents the "fighting Hermite corrections" problem on short entry/exit roads

**Key design decisions:**
- The dominant-road detection makes multi-way junctions behave like T-junctions in the common case. This reuses proven, working code rather than inventing new algorithms.
- True peer junctions (Step A.2b) are rarer and get the upgraded average-based blending with flat zones and analytical deltas.
- Short-spline protection applies to BOTH sub-paths — any spline squeezed between two junctions gets proportionally reduced blend distances.
- Banking: all non-dominant contributors are flattened to `BankAngleRadians = 0` at the junction (same as current behavior). Banking ramps back in through the blend zone.

**Files to modify:**
- `UnifiedJunctionProfileBlender.cs` — split `ComputeMultiWayConstraints()` into dominant-road detection + two sub-paths (~50→~120 lines), add short-spline overlap detection in `BlendSplineProfile()`
- `JunctionHarmonizationParameters.cs` — possibly add `MultiWayDominantRoadEnabled` toggle (default true) for A/B testing
- `JunctionHarmonizationParameters.cs` — add `DominantRoadWidthRatio` (default 1.5) for tuning the dominance threshold

**Files NOT modified:**
- `ComputeTJunctionConstraints()` — unchanged, already works well
- `ComputeRoundaboutConstraints()` — unchanged, already works well
- `NetworkElevationGraph.cs` — unchanged, elevation chains unaffected
- `NetworkJunctionDetector.cs` — unchanged (Phase C already handled)

---

## Verification

### Phase C verification
- Generate terrain with `disableSplineMerging=true`
- Junction debug image should show fewer colored dots (degree-2 nodes now gray/absent)
- No elevation changes at former degree-2 nodes
- Junction breakdown log should show `N Continuation` type

### Phase A verification
- Same terrain generation
- Roundabout entry/exit split/merge nodes: smooth elevation transition, no cliff
- All three roads at a junction adapt elevation (not just one)
- Compare junction debug image: CrossRoads junctions should show minimal elevation change (gray, not red/blue)
- Drive test: no car-launching bumps at junctions

### Regression checks
- T-junctions: unchanged behavior (separate code path)
- Roundabout ring junctions: unchanged behavior (separate code path)
- Existing elevation chain smoothing: unaffected

---

## Estimated Effort

| Phase | Effort | Risk |
|-------|--------|------|
| C: Continuation detection | 3-4 hours | Low — additive change, skip logic |
| A: Multi-way blending fix | 8-12 hours | Medium — core algorithm change, needs careful testing |
| **Total** | **11-16 hours** | |

Phase C can be implemented and verified independently before starting Phase A.
