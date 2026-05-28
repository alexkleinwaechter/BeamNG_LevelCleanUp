# Pinning Junction Elevations in the Old Pipeline — Investigation

**Date:** 2026-05-14
**Author target:** future implementation session
**Source branch read via:** `git show develop:...` (also checked-out at `C:\temp\beamng_mapping_pro_development_branch`)
**Working branch:** `feature/analytical_first_good_version`
**Status:** Investigation only. No code changes proposed in this document — only an integration design and risk inventory.

---

## 1. Goal

Take the proven `develop`-branch pipeline (no analytical surface model, no mesh solver) and add the rule we adopted in the analytical pipeline:

> **Junction Z is fixed before road smoothing runs, and stays fixed for the rest of the pipeline. Roads adapt to the pinned junction Z. Roads that pass *through* a junction (continuous roads at T-junctions, roundabout rings, mid-spline crossings) are exempt from the anchor — they slope across the pinned point so their own profile stays smooth.**

The user's expectation is that combining the develop pipeline's terrain-blending quality with the analytical pipeline's "fixed junction" discipline will give more natural elevations and smoother junctions than either current option alone.

---

## 2. Pipeline as it stands on `develop`

Reference: [git show develop:BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs)

```
Phase 1     Build unified road network
Phase 1.5   Identify roundabout splines
Phase 1.8   Detect junctions (topology only, no elevation)
            ┌──────────────────────────────────────────────────────────┐
            │ Iterative refinement loop (max 3 iterations, 0.01 m thr.)│
            │                                                          │
Phase 2     │   CalculateNetworkElevations                             │
            │     - per-spline Box / Butterworth smoothing             │
            │     - WI-6 endpoint anchoring (Endpoint junctions only)  │
Phase 2.3   │   (iter 0 only) Bridge / tunnel structure profiles       │
Phase 2.5   │   (iter 0 only) Banking pre-calculation                  │
Phase 2.6   │   (iter 0 only) Roundabout elevation harmonization       │
Phase 3     │   NetworkJunctionHarmonizer.HarmonizeNetwork()           │
            │     → computes HarmonizedElevation per junction          │
            │     → modifies TargetElevation (then RESTORES original)  │
            │   UnifiedJunctionProfileBlender.ApplyUnifiedProfiles()   │
            │     → Hermite blend with junction constraints            │
            │   convergence check on max elevation correction          │
            └──────────────────────────────────────────────────────────┘
FinalSnap   FinalSnapTJunctionEndpoints (post-loop alignment)
Phase 4     Terrain blending (single-pass EDT)
Phase 5     Material painting
```

Three things matter for this investigation:

1. **Junctions are detected before smoothing.** Topology is known from Phase 1.8.
2. **`HarmonizedElevation` is computed *inside* the iterative loop**, after roads are already smoothed. It is then used as a constraint, but the loop is needed because the smoothed roads change what the right junction Z is.
3. **A weak form of pre-pinning already exists** — but only for dead ends.

---

## 3. The pre-pinning that already exists (WI-6)

[UnifiedRoadSmoother.cs:818-893](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L818) builds endpoint anchors before Phase 2 smoothing runs. Each anchor carries a terrain elevation sampled at the junction center and a decay distance. [OptimizedElevationSmoother.ApplyEndpointAnchoring](BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs) then biases the smoothed profile toward that anchor using exponential decay from the endpoint.

Critically, [UnifiedRoadSmoother.cs:838-841](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L838) gates this hard:

```csharp
// Only anchor isolated endpoints (dead-end roads) toward terrain.
// Multi-road junctions are handled by the rubberband blend envelope in Phase 3,
// which smoothly interpolates between junction elevations and terrain-following.
// Anchoring at multi-road junctions was the root cause of the "ditch" artifact.
if (junction.Type != JunctionType.Endpoint) continue;
```

This is the entire reason the develop pipeline "has no pinned junctions" in the user's words. Pre-anchoring was tried for multi-road junctions and produced ditches. It was disabled.

**Why ditches happened (reconstructed from code, not from a written incident note):** if the anchored endpoint belongs to a road that *continues through* the junction (a primary road at a T-junction, the ring at a roundabout, a mid-spline crossing road), pulling the smoothed profile toward the terrain sample at the junction creates a kink in an otherwise straight-sloping road. The terrain sample at the junction can be meters away from where the smooth profile wanted to be. Result: a divot at the junction even though the road geometry has no reason to change.

**The missing ingredient is the continuous-road exemption.** The current code does not distinguish "this endpoint is a contributor at a junction" from "this endpoint is at a road that ends at the junction". With the exemption, the original failure mode disappears: through-roads are never anchored at the junction node, so the kink can't form.

---

## 4. The model already supports the exemption

[NetworkJunction.cs](BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs) already carries every distinction we need:

- `JunctionContributor.IsContinuous => !IsEndpoint` — true when the spline passes through the junction (no endpoint at this node), false when the spline ends at this node.
- `NetworkJunction.GetContinuousRoads()` and `GetTerminatingRoads()` already split contributors into the two groups.
- `NetworkJunction.HarmonizedElevation` already exists as the canonical pinned-Z field — it just isn't currently set until Phase 3.
- `JunctionType` distinguishes `TJunction`, `YJunction`, `CrossRoads`, `Complex`, `MidSplineCrossing`, `Roundabout`, `Endpoint`.
- [JunctionEndpointConstraint.cs](BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs) already has `Elevation`, `Slope`, `BankAngleRadians`, `FlatZoneDistance`, `BlendDistanceMeters` — the full set for a fixed-constraint formulation.

No new model surface is needed. The change is *when* and *for which contributors* `HarmonizedElevation` is set, plus a small extension to the anchor lookup.

---

## 5. Proposed design — "Phase 1.9: Junction Elevation Pinning"

Insert a single new phase between detection (1.8) and smoothing (2). Nothing else moves.

### 5.1 Inputs

- `network.Junctions` (populated by Phase 1.8)
- `heightMap`, `metersPerPixel`
- Per-spline priorities, widths, parameters
- Roundabout info already collected by Phase 1.5

### 5.2 What it computes

For every junction except `MidSplineCrossing`:

| Junction type    | Pin value (`HarmonizedElevation`)                                                | Anchored contributors |
|------------------|----------------------------------------------------------------------------------|-----------------------|
| `Endpoint`       | terrain sample at junction position                                              | the one endpoint      |
| `TJunction`      | terrain sample along the **continuous** road's centerline at the junction node¹ | terminating roads only |
| `Roundabout`     | ring elevation from Phase 1.5's roundabout pre-pass (or terrain along ring)      | connecting roads only |
| `YJunction`      | priority-weighted average of contributors' terrain samples²                      | all contributors      |
| `CrossRoads`     | same as Y                                                                        | all contributors      |
| `Complex`        | same as Y                                                                        | all contributors      |
| `MidSplineCrossing` | **skip — do not pin**                                                         | none                  |

¹ The continuous road exists at this stage, but its smoothed profile does not. Use a terrain sample at the junction center along the continuous road's centerline. This is the same value the road would smooth to in Phase 2 (modulo Box/Butterworth filtering, which mostly preserves it for a single sample point). The continuous road is left free, so even a slightly-off pin doesn't kink it.

² Use the same weighting Stage B.5 uses in the analytical pipeline: width × priority, with a slope-outlier guard. See [RoadAxisProfiler.cs ApplyJunctionHarmonization](BeamNgTerrainPoc/Terrain/Algorithms/SurfaceModel/RoadAxisProfiler.cs) as the reference.

### 5.3 What it writes

- `junction.HarmonizedElevation` is set once and treated as immutable for the rest of the pipeline.
- **Junction tangent / slope is pinned as a first-class output**, not a side detail. For each terminating contributor, populate both `JunctionEndpointConstraint.Elevation` *and* `JunctionEndpointConstraint.Slope` (the longitudinal slope the road must have at the junction node). [Nguyen et al. 2014 §6.1](#11-civil-engineering-reference-points-nguyen-et-al-2014) is explicit that joining curves at a junction requires fixing both the point *and* the tangent — pinning Z without slope produces a kink even inside a well-formed flat zone. The fields exist already; the requirement is to make sure Phase 1.9 sets them, not just `Elevation`.
- For each *terminating* contributor at a non-Endpoint junction, register an `EndpointAnchor`. For Endpoint junctions, the existing WI-6 behavior is preserved.
- For each *continuous* contributor, **do not** register an anchor — the continuous road must remain free to slope through the junction.

### 5.4 Phase 2 changes (minimal)

[BuildEndpointAnchorLookup](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L818) loses the `junction.Type != JunctionType.Endpoint` early-out and instead iterates contributors with the rule above. The anchor elevation source becomes `junction.HarmonizedElevation` for non-Endpoint junctions and the existing terrain sample for Endpoint junctions. Everything downstream of `ApplyEndpointAnchoring` is unchanged.

### 5.5 Phase 3 changes (small but important)

[NetworkJunctionHarmonizer.HarmonizeNetwork](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) currently *computes* `HarmonizedElevation` from already-smoothed roads. With pinning, it must respect the pinned value: skip the elevation negotiation step, classify only.

[UnifiedJunctionProfileBlender.ComputeTJunctionConstraints](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) currently writes `junction.HarmonizedElevation = edgeCenterElev` mid-blend. That write must go away — the value is already pinned. The constraint elevation it builds for the terminating road should use the pinned value directly.

The two-pass Hermite pattern stays. With a pinned junction Z and continuous roads exempted, pass 1 (primary/continuous) does nothing that affects junction nodes, and pass 2 (terminating) blends to a known fixed value. The pattern was originally there to make pass 2 see the post-blend primary elevation — that need disappears when the value is pre-pinned, but keeping the structure costs nothing.

### 5.6 Iterative loop becomes a single iteration

The 3-iteration convergence loop ([UnifiedRoadSmoother.cs:222-415](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L222)) exists because junction Z and road Z are mutually dependent today. Pinning breaks that dependency: roads adapt to junctions, junctions never react to roads. One pass is enough.

This is not load-bearing — leaving the loop in place with `maxIterations=1` is the safest first cut. Removing it can come after validation.

### 5.7 Phase 2.6 (roundabouts) needs to move up

Roundabout elevation harmonization currently runs after Phase 2 ([UnifiedRoadSmoother.cs:283-313](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L283)) because it needs the smoothed elevations of connecting roads to pick a ring elevation. With pinning, the ring elevation should be one of the inputs to Phase 1.9, not a result of Phase 2. Two viable approaches:

- **Move Phase 2.6 to Phase 1.9a**, before junction pinning. Compute ring elevations from terrain samples along the ring (not from smoothed connecting roads), then pin each roundabout-junction at the ring elevation at the connection point.
- **Keep Phase 2.6 where it is**, but treat roundabout junctions as un-pinned in Phase 1.9 and let Phase 2.6 + Phase 3 work the way they do today.

The first option is the cleaner match to "pin before smoothing." The second option is the lower-risk first step.

---

## 6. Where each junction type's Z value should come from

This is the part most likely to need iteration once we see real maps. First-cut values:

- **Endpoint**: keep the current behavior — terrain sample at junction position, validated for NaN/extreme values.
- **TJunction**: terrain sample at the junction node along the continuous road's centerline. This is what the continuous road *would* smooth to, before WI-6 anchoring. The terminating road then has to climb/descend to meet it.
- **Roundabout**: ring elevation at the connection point. Coming either from Phase 2.6's existing ring harmonizer (if we move it) or from a fresh terrain sample along the ring.
- **YJunction / CrossRoads / Complex**: two viable strategies — pick per-junction by priority spread:
  - **Sequential priority-first snap (Nguyen 2014 §6.1):** fit/sample the highest-priority contributor first, then pin all lower-priority contributors to that elevation and tangent. This preserves the dominant road's profile cleanly and is the approach civil-engineering literature actually recommends. Use this when priorities are unequal (e.g. `motorway` meets `service`).
  - **Width × priority-weighted average (analytical-pipeline B.5):** weighted mean of all contributors' terrain samples, with an outlier guard that drops contributors deviating >2 m from the dominant target. See the comment block at [RoadAxisProfiler.cs:137-180](BeamNgTerrainPoc/Terrain/Algorithms/SurfaceModel/RoadAxisProfiler.cs#L137). Use this when priorities are equal or nearly so, where the symmetric result is more natural than letting one branch win arbitrarily.
  - A reasonable selector: if the highest-priority contributor's priority exceeds the second-highest by ≥ 1 tier, use sequential snap; otherwise use weighted average. The two strategies converge when contributors are similar, so the selector only matters at very-unequal junctions.
- **MidSplineCrossing**: do not pin. Both roads pass through; the existing `ApplyMidSplineCrossingInfluences` step in `UnifiedJunctionProfileBlender` is the right tool for these.

For T-junctions specifically, the "continuous road's terrain sample" choice is the simplest correct answer when the smoother is a low-pass filter. If the continuous road is on a 6 % grade through the junction, the terrain sample at the junction is what the smoothed profile will hit at that arc-length. The terminating road's terrain sample at the same XY may be meters off (cliff on one side, ditch on the other). Picking the continuous road's sample is the only one that doesn't fight the through-road.

---

## 7. Why this is expected to work where the earlier attempt failed

The previous attempt — anchoring all multi-road junction endpoints to a terrain sample — failed because it anchored *every* contributor including the through-road. Symptom: ditch at the junction, because the through-road got pulled to a single sample point that it would otherwise have flown past.

This proposal anchors only contributors that *end* at the junction. The through-road's smoothing is unchanged from today's no-anchor behavior. The pin therefore can only affect roads that were already going to take a junction constraint in Phase 3. The difference is just *when* the constraint is applied:

- **Today:** road smooths freely → Phase 3 corrects toward a junction Z computed from the already-smoothed road → iteration.
- **Proposed:** road smooths *into* a known junction Z that was set before smoothing ran → Phase 3 sees the road already at the right elevation → minimal correction.

In other words, this proposal moves the constraint from after smoothing to before smoothing, but only for the roads that get a constraint at all.

---

## 8. Risks and unknowns

**R1 — Banking pre-calculation order.** Phase 2.5 ([UnifiedRoadSmoother.cs:275-282](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L275)) computes bank angles and edge elevations after Phase 2. The pinned junction Z is a centerline value; edge elevations are derived from it plus bank. If the bank at the junction approach is steep, the edge elevations can drift several centimeters from the pin. The analytical pipeline solved this with the "edge-anchored constraint with slope" trick already used by `ComputeTJunctionConstraints` ([UnifiedJunctionProfileBlender.cs:267-340](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L267)). Reuse that logic.

**R2 — `JunctionBankingAdapter` overwriting pinned values.** The analytical pipeline memory ([memory/MEMORY.md](C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/junction_elevation_debugging.md)) flags this adapter as a known overwriter of `TargetElevation` near junctions. Verify whether it runs in the develop branch and whether the existing `MaintainBanking` flag covers the affected cross-sections.

**R3 — Cross-material junctions.** Pin elevation must be a single value across materials. Verify that all contributors at a cross-material junction agree on `HarmonizedElevation` and that the terrain blender's protected-edge handling stays consistent.

**R4 — Short splines.** [commit 5805bc0 "use linear interpolation instead of Hermite on short splines"](https://github.com/) is a known special case. A pinned-Z spline of e.g. 12 m between two junctions will spend most of its length inside flat zones from both ends — the Hermite blend has nowhere to live. The existing short-spline path probably already handles this, but it should be validated explicitly with a pinned junction at each end.

**R5 — Phase 2.6 ordering.** Picked up in §5.7. Lower risk if we leave it where it is on the first pass.

**R6 — `FinalSnapTJunctionEndpoints`.** This runs after the iterative loop ([UnifiedRoadSmoother.cs:411-418](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L411)) to align terminating endpoints to the *current* primary surface. With pinning, the primary surface no longer changes between iterations, so the snap should be a no-op. Keep it as a safety net; expect zero corrections in logs.

**R7 — Slope continuity at the continuous road.** Pinning Z but exempting the continuous road means the continuous road's smoothed profile passes through the pinned point only by coincidence (we picked the terrain sample along its centerline, but the Box/Butterworth filter smooths it). There can be a residual mismatch of a few centimeters between the pinned Z and what the through-road actually evaluates to at the junction node. For terrain blending this is fine. For the terminating road's constraint, it could matter — the terminating road thinks it's hitting the through-road's surface at the pin, but the through-road may be 0.1 m off. Mitigation: keep `ComputeTJunctionConstraints`' existing logic of computing `edgeCenterElev` from the actual primary CS rather than from `HarmonizedElevation` directly, then assert `edgeCenterElev ≈ HarmonizedElevation` and log if not.

**R8 — Ditch artifact regression.** The original failure mode must not return. Validate on `franco_same_prio` and at least one other map with mixed junction types before declaring success. Specifically check multi-road junctions where the previous attempt failed (the commit/history search didn't surface a specific repro map — that's an open question, see §10).

**R9 — C¹ continuity at the flat-zone / free-profile seam.** The Hermite h00 basis gives slope = 0 at the flat-zone boundary and decays smoothly to 0 at the end of the blend distance. That is C¹ on both ends *of the Hermite ramp*, but it implicitly assumes the natural Phase-2 profile arrives at the end of the ramp with slope = 0 too. If the natural profile is on a non-zero grade where the ramp ends, the combined curve has a slope kink at that seam — small but visible in steep terrain. Civil-engineering practice ([Nguyen 2014](#11-civil-engineering-reference-points-nguyen-et-al-2014)) uses **parabolic** vertical transitions precisely to avoid this: constant slope change between two slope values, C¹ at both endpoints with matched non-zero slopes. Mitigation options, in increasing complexity:
  1. Accept the residual kink; it is bounded by the natural-profile slope at the ramp boundary and is usually < 1 % grade change.
  2. Use a cubic Hermite with the *measured* natural-profile slope at the ramp's far end as the boundary tangent (replace `h00` with a full `h00 + h10 · slope_far`).
  3. Replace the Hermite ramp with a parabolic vertical curve between the pinned slope at the junction and the natural slope at the far end of the ramp. This is the paper-faithful option for highways.

---

## 9. Suggested validation plan

1. **Baseline capture** on `develop` for two maps: `franco_same_prio` and one with heavy crossroads. Save heightmap, delta map, and junction debug image.
2. Implement Phase 1.9 with the conservative ordering (Phase 2.6 unchanged, loop kept at 3 iterations).
3. Compare:
   - junction debug images side-by-side
   - max elevation correction per Phase 3 iteration — should fall to ≈ 0 in iteration 1 if pinning is working
   - heightmap delta along through-roads near T-junctions — should not show a divot at the pin
   - heightmap delta along terminating roads — should show a single smooth ramp instead of two-stage smoothing+correction
4. Stress cases:
   - very short connector between two pinned junctions (R4)
   - steep grade through a T-junction (R7)
   - cross-material Y-junction (R3)
   - dead-end already covered by current WI-6 — should be unchanged
5. If clean, simplify: drop iteration count to 1, then later move Phase 2.6 up.

---

## 10. Open questions

- **Where is the "ditch artifact" documented?** The comment in [UnifiedRoadSmoother.cs:840](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs#L840) is the only reference I found. A specific repro map and commit would be useful before re-enabling multi-road anchoring. Worth searching `ai_agent_md_files_history_some_outdated/` and `ai_docs/` for the WI-6 history.
- **What sample do we use along a continuous road's centerline?** Nearest CS to the junction position? Linear interpolation between the two flanking CSes? Bilinear heightmap sample at the junction XY? Probably the third — that's what the smoother itself uses as input.
- **Should `IsContinuous` be trusted at cross-material junctions?** A road that is "continuous" in OSM topology may be split into two splines for material reasons. The detector should already handle this via `GetContinuousRoads()`, but worth a sanity check.
- **`JunctionBankingAdapter` presence on develop.** I didn't find a class by that name in the develop tree (only `BankingOrchestrator` and `PriorityAwareJunctionBankingCalculator`). The "overwrites TargetElevation" memory may apply only to an older branch state. Verify before assuming this is a risk on develop.
- **Paper-validated vs not.** [Nguyen et al. 2014](#11-civil-engineering-reference-points-nguyen-et-al-2014) §1 and the conclusion explicitly list complex nodes, roundabouts, and highway on-ramps as future work. The paper validates pinning for the **T-junction / sequential-priority** case. Our proposal goes beyond the paper for Y/X/CrossRoads/Complex/Roundabout — those should be treated as a separate, lower-confidence validation tier in §9.

---

## 11. Civil-engineering reference points (Nguyen et al. 2014)

Direct lessons from [`0_paper1124-final.pdf`](../examples_for_ai/internetsources/0_paper1124-final.pdf) — "Realistic road path reconstruction from GIS data" — that apply to this refactor. The paper fits analytical curves (lines, arcs, clothoids, parabolae) rather than blending heightmaps, but its civil-engineering discipline maps cleanly onto our problem.

### 11.1 Pin the point **and** the tangent — always

Paper §6.1, equation 8: at a junction the joined curves must satisfy
`(x_e, y_e) = (x_s, y_s)` **and** `φ_e = φ_s` — same point, same tangent angle.

For us this means a Phase 1.9 entry for a terminating contributor must produce **both** `Elevation` (the pinned Z) and `Slope` (the pinned dz/ds along the road). [JunctionEndpointConstraint](BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs) already has both fields. Today only the elevation is computed reliably in Phase 1.9-like code; the slope is often left at 0 or derived later. Make slope a Phase-1.9 output.

For T-junctions: slope = continuous road's local longitudinal slope at the junction node, exactly as [`ComputeTJunctionConstraints`](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) already computes via `CalculateSlopeAtIndex` — just compute it earlier and store on the junction.

For Y/X/Complex: slope = 0 is acceptable when contributors are symmetric; for sequential-priority snapping, slope = highest-priority contributor's slope at the junction.

### 11.2 G¹ minimum, G² for highways

Paper §2.1: *"The lowest required level is G¹ so that for every connection point between primitives, the tangents of the two curve segments have a common direction. For highway horizontal curves, G² continuity is required, implying that a transition curve exists between two segments having different curvatures."*

Translation to vertical profiles at junctions:
- For local / urban / suburban roads, the flat-zone → ramp → free-profile chain needs C¹ (matched slope) at every seam. The R9 mitigation #2 (Hermite with measured far-end slope) is sufficient.
- For motorway / trunk roads, the slope should change at constant rate across the ramp, i.e. a **parabolic** vertical curve (R9 mitigation #3). Civil-engineering vertical curves are parabolic by convention — see paper Figure 7-8.

A pragmatic rule: pick the ramp shape from the highest-priority contributor's road class. `Hermite` for everything by default; `parabolic` for motorway/trunk. Both are cheap to implement and use the same `BlendDistanceMeters`.

### 11.3 Sequential priority handling is the paper's actual recommendation

Paper conclusion: *"for a junction, we can fix the starting point from the already created road path with the required tangent."*

The paper processes roads in priority order and snaps each new road's endpoint to whatever the already-processed neighbour produced. That is **stronger** than priority-weighted averaging — it preserves the dominant road's profile exactly rather than dragging it toward less-important contributors.

This is captured in §6's revised multi-way row. The summary: weighted average is the symmetric/diplomatic option; sequential snap is the civil-engineering-correct option. Default to sequential when priorities are unequal.

### 11.4 Blend distance should depend on road class

Paper §2.1: *"the length of each primitive type for each kind of road (urban, suburban, highway...) must fall into a range defined by the civil engineering requirements."*

We already have `BlendDistanceMeters` as a per-spline parameter, configurable via `JunctionHarmonizationParameters`. The paper supports making the *default* depend on road class:
- Urban / residential: short blend (~15–25 m). Stiffer transitions are acceptable and natural.
- Suburban / tertiary: medium (~25–40 m).
- Trunk / primary / motorway: long (~40–80 m). Drivers expect very gentle vertical curves at speed.

If the existing defaults are not already class-aware, this is a low-risk improvement to ship alongside Phase 1.9.

### 11.5 Sample density on long straight segments

Paper §5.4: long straight runs are augmented with K-means-derived interpolated points to avoid the LMA objective being dominated by noisy endpoints.

We have an analogue: very long splines between two pinned junctions need enough cross-sections at the *endpoints* for `ApplyEndpointAnchoring` and the flat-zone definition to behave well. The default CS interval (0.5 m) is dense enough; the risk is at the edges, where a Box-filter window can extend past the spline ends. If we observe pinned-Z values being smoothed away by the Box filter near the junction, the fix is the same as the paper's: either decrease the smoothing window near pinned endpoints, or insert extra anchor weight (the WI-6 exponential decay already does this in spirit).

### 11.6 Horizontal / vertical decomposition is already aligned

Paper Figure 2: input 3D polyline is split into a horizontal polyline (xy) and a vertical polyline (arc-length, z), each fitted independently, then recombined.

Our pipeline already does this — `UnifiedRoadNetworkBuilder` produces the xy spline, and `OptimizedElevationSmoother` produces the vertical profile (TargetElevation per CS). No change needed; just worth noting that the structural decomposition we built on independently matches what civil-engineering literature recommends.

### 11.7 What the paper does **not** address — and our proposal therefore cannot lean on

- **Multi-way intersections** (Y/X/CrossRoads/Complex): listed as future work in the paper's own §1 and conclusion. Our handling of these is a contribution beyond the paper.
- **Roundabouts**: same.
- **Highway on-ramps / merging geometry**: same.
- **Continuous road sloping across a multi-way junction**: the paper has no through-road concept; every road is fit independently with pinned endpoints. Our continuous-road exemption is an extension to fit how heightmap-based blending works, not a paper-validated technique.
- **Terrain blending around the road**: out of scope for the paper.

R-tier in §8 must be assessed on our own evidence for all five categories above.

---

## 12. Files that would change (preview, not for implementation in this doc)

- [BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) — new Phase 1.9 call site; extended `BuildEndpointAnchorLookup`; possibly move Phase 2.6.
- [BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs](BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) — skip elevation negotiation when `HarmonizedElevation` is already pinned.
- [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) — remove the in-blender write to `HarmonizedElevation`; use the pinned value as input.
- New small class for Phase 1.9 (e.g. `JunctionElevationPinner` in `BeamNgTerrainPoc/Terrain/Algorithms/`) — pure function over network + heightmap, no side effects beyond setting `HarmonizedElevation` and returning anchor data.
- [BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs](BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs) — no change expected; the existing `ApplyEndpointAnchoring` already accepts arbitrary anchor elevations.

The change surface is small and the new class is self-contained — the design is mostly "set a field earlier and consume it everywhere else as already coded."

---

## 13. Bottom line

The develop pipeline does not pin junctions, but the *infrastructure* for pinning is already in place — `HarmonizedElevation`, `IsContinuous`, `GetTerminatingRoads`, `ApplyEndpointAnchoring`, `JunctionEndpointConstraint`. The only reason multi-road junctions aren't pinned today is that the original attempt didn't exempt continuous roads and produced ditches.

Adding the continuous-road exemption — taken straight from the analytical pipeline's mesh-solver pin/no-pin rule — closes that gap. The expected effect is exactly what the user described: junction Z chosen once before smoothing, terminating roads smoothly ramping into it, through-roads sloping across it untouched, iterative correction in Phase 3 reduced to a no-op.

The proposal is small, low-risk to try, and reversible. Recommended next step before implementation: confirm the ditch-artifact history (§10) and pick a validation map, then implement Phase 1.9 in the conservative form described in §5 and measure.
