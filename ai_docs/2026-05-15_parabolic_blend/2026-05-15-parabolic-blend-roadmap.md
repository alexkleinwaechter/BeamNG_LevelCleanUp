# Parabolic Junction Blend — Roadmap

> **Purpose:** Single source of truth for the parabolic-blend program (Phase A, A.5, B) and adjacent open threads on the junction-elevation / banking / mesh-solver pipeline. When a follow-up lands, update the **Status** column and add a one-line **Result** note. When a new follow-up surfaces, add a row here before opening a separate plan.
>
> **Maintenance:** Update this doc whenever a phase moves between Queued / In flight / Complete, or when a new investigation thread is opened. Keep it short — full plans live in their own files (`2026-05-XX-…-phase-X-plan.md`).

---

## Status overview

| # | Item | Status | Plan / link |
|---|---|---|---|
| A | Parabolic profile substitution in single-end blend zones | ✅ Complete (default-on `1638ae2`) | [phase-a-plan](2026-05-15-parabolic-blend-phase-a-plan.md) |
| A.8 | Painted-road-width protection in `RoadMaskBuilder` (two-pass rasterizer) | 🚧 In flight (runs **before** A.5) | [phase-a8-plan](2026-05-15-parabolic-blend-phase-a8-plan.md) |
| A.5 | Step 5b propagation/overlap taper (j126 cliff) | ⏳ Queued behind A.8 | [phase-a5-plan](2026-05-15-parabolic-blend-phase-a5-plan.md) |
| A.6 | Bank-angle parabolic path | ⏳ Queued, conditional | This doc §A.6 |
| A.7 | j126 cliff residual — Phase-4 IDW investigation | 🔬 Investigation, conditional on A.5+A.8 result | This doc §A.7 |
| B.1 | AASHTO K-value cap on blend distance | ✅ Complete (default-on) | [phase-b-plan](2026-05-25-parabolic-blend-phase-b-plan.md) |
| B.2 | Short-connector compositional blend | ✅ Complete (default-on) | [phase-b-plan](2026-05-25-parabolic-blend-phase-b-plan.md) |
| B.3 | 4-constraint cubic at blend-zone end | ❌ Rejected on visual review (ramp/bump artefact); follow-up: stretch L instead of curve harder | [phase-b-plan §Validation outcomes](2026-05-25-parabolic-blend-phase-b-plan.md) |
| B.4 | Dead-end terrain-slope match | ✅ Complete (default-on) | [phase-b-plan](2026-05-25-parabolic-blend-phase-b-plan.md) |
| C | Stretch-L blend distance to match natural slope (B.3 follow-up: extend length, don't curve harder) | ✅ Complete (v1.1 with mid-spline-crossing-aware ceiling, 380/380 tests green) | [phase-c-plan §Phase C](2026-05-25-parabolic-blend-phase-b-plan.md) |
| D | Symmetric bank blend (parabolic + compositional paths) | ✅ Complete (2026-05-28) | [phase-d-design](2026-05-28-phase-d-symmetric-bank-blend-design.md), [phase-d-plan](2026-05-28-phase-d-symmetric-bank-blend-plan.md) |
| X1 | `JunctionBankingAdapter` overwrites CG profiles (Phase 3.5) | 🔬 Investigation | [memory/junction_elevation_debugging.md](../../../C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/junction_elevation_debugging.md) |
| X2 | Generalize seam blending (Nguyen seamless / seam-line) beyond propagation | 🔬 Investigation | [memory/surface_model_junction_overlap.md](../../../C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/surface_model_junction_overlap.md) |
| X3 | Connected-road mesh solver — terrain-road elevation gap | 🔬 Investigation | [memory/mesh_solver_tuning_status.md](../../../C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/mesh_solver_tuning_status.md) |
| X4 | Dead-end spike regression in `FinalSnapTJunctionEndpoints` | 🔬 Investigation | [ai_docs/dead_end_spike_investigation_2026-03-06.md](../dead_end_spike_investigation_2026-03-06.md) |

Status legend: ✅ Complete · 🚧 In flight · ⏳ Queued · 🔬 Investigation (no plan yet) · 🔒 Blocked

---

## Parabolic-blend phases

### Phase A — single-end parabolic substitution ✅

**Outcome on franco_same_prio:** j125 spline 64 `w` collapsed 7.16σ → 2.75σ; j126 spline 64 `w` unchanged at 9.07σ. W1 redBandPixels +4.3 % (within ≤5 % gate). See `examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio/`.

**Why j126 didn't move:** two compounding overrides downstream of the blender hide A's improved profile:
1. The propagation overlay (Step 5b in `UnifiedJunctionProfileBlender`) overrides the parabolic-blended end zone with j102's propagated mid-spline influence. → addressed by **Phase A.5**.
2. The rasterizer (`RoadMaskBuilder.BuildCombinedMaskWithElevation`) stamps each spline's corridor (`surface + smoothingMargin + edgeProtectionBuffer`, ≈ +8 m wider than the painted road) widest-first. At terminating-road junctions the wider primary claims pixels inside the terminating road's actual painted surface, so the terminating road's `cs.TargetElevation` ramp never reaches the heightmap. → addressed by **Phase A.8**.

A.8 runs first because the rasterizer override is likely the dominant cause.

### Phase A.8 — Painted-road-width protection 🚧

**Why this is A.8 not A.6**: numbering follows discovery order, not topological order. A.6/A.7 were defined as *conditional* follow-ups to A.5. A.8 was discovered after the user observed that the rasterizer's combined corridor (`SmoothingCorridorMargin` + `RoadEdgeProtectionBufferMeters` ≈ +8 m on defaults) destroys terminating-road spline ramps at junctions. A.8 runs *before* A.5 because the rasterizer override is likely the dominant cause of the j126 cliff — A.5's improved `cs.TargetElevation` is invisible if the mask builder stomps it.

**Goal:** Two-pass rasterization in `RoadMaskBuilder.BuildCombinedMaskWithElevation`. Pass 1 stamps each spline's *surface* polygon (no smoothing margin, no edge buffer), widest-surface-first. Pass 2 extends with the corridor + edge buffer, but only into pixels not yet claimed by Pass 1. Result: every spline's painted-surface pixels are guaranteed to carry that spline's own banking-aware elevation, even when a wider adjacent spline's corridor overlaps geometrically.

**Why this matters**: Defaults are `SmoothingCorridorMargin = 2.0 m` + `RoadEdgeProtectionBufferMeters = 2.0 m` → +4 m per side → polygon is 8 m wider than the painted road. At a T-junction between a 7 m terminating road and a 14 m primary, the primary's 22 m corridor entirely covers the terminating road's 7 m surface. Widest-first ordering claims those pixels for the primary; the terminating road's ramp elevation never reaches the mask.

**Pass criteria** (re-using franco_same_prio):

| Criterion | Target |
|---|---|
| j126 spline 64 `w` | < 6σ (intermediate goal — A.5 brings it to <3σ on top) |
| j126 quadratic_growth | no sign flip at d=60 (the current legacy cliff signature) |
| Visual inspection — terminating road centerline elevation matches `cs.TargetElevation` at junction approach | yes, in `delta_three_band.png` |
| W1 `redBandPixels` | ≤ parabolic_a + 5 % (no global regression) |

If A.8 alone passes the < 3σ target on j126, A.5 may be reduced in scope (A.6/A.7 still tracked separately). If A.8 only partially fixes the cliff, A.5 runs on top.

### Phase A.5 — Step 5b propagation/overlap taper ⏳

**Goal:** Per-influence smoothstep weight taper that drops to 0 at any *directly-anchored* junction's anchor node whose blend zone the propagated influence overlaps. Terrain-grade-free; geometry only. Composes with parabolic.

**Pass criteria:**

| Criterion | Target |
|---|---|
| j126 spline 64 `w` | < 3σ (from 9.07σ) |
| j126 quadratic_growth | monotone descent, no sign flip at 5/15/30/60 m |
| j126 `residual_max_minus_min` | ≤ 1.5 m (no regression vs 1.414 m) |
| j125 spline 64 `w` (regression gate) | < 3σ (Phase A win preserved) |
| W1 `redBandPixels` | ≤ parabolic_a + 5 % |

If criteria met → flip `EnablePropagationOverlapTaper = true` (Task 7) and move A.5 → ✅. If not met → open A.7.

### Phase A.6 — Bank-angle parabolic path ⏳

**Trigger:** Open *only if* bank artefacts surface in A.5 validation (visible banking discontinuity at junction-126-style endpoints in `delta_three_band.png` or BeamNG.drive in-game inspection).

**Scope:** `BlendSplineProfileParabolic` currently falls through to legacy h00-weighted handling for `cs.BankAngleRadians`. Implement a parallel parabolic substitution for bank angle, anchored at junction `bankAngleRadians` with `bank_slope = 0` at the junction.

**Why deferred:** Phase A's hypothesis (Phase A plan §7) was that bank-angle overshoot is not the primary cliff source. Confirm or refute against parabolic_a5 artefacts before committing engineering time.

### Phase A.7 — j126 cliff Phase-4 IDW investigation 🔬

**Trigger:** Open *only if* A.5 validation shows j126 `w` still > 3σ after Step 5b is tamed.

**Hypothesis** (from baseline README): the cliff is not caused by spline 64's centerline elevation. j126 has `n_contributors = 2` (one continuous + one terminating) and the continuous-road contributor carries an elevation Z that the Phase-4 IDW heightmap rasterization propagates into the heightmap around the junction independent of what the unified blender wrote to `cs.TargetElevation`.

**Diagnostic plan (no code, just measurement):**

1. Inspect `delta_three_band.png` at j126 — does the red blob match the spline-64 centerline (→ blender bug) or a junction-wide circular footprint (→ rasterization bug)?
2. Log `network.Junctions[126].Contributors` elevations post-blend. If the continuous contributor's CS elevation near j126 is close to 166 m while spline 64's CS near j126 is at 158.95 m, the cliff is between two roads at the same junction, not within spline 64. That's a Phase-4 IDW weighting question.
3. If confirmed → open a separate plan `2026-05-XX-phase4-idw-multi-contributor-plan.md`. Scope is the rasterization stage, not the blender.

### Phase B — completed (2026-05-25)

Validation matrix on franco_same_prio (5 runs with diagnostics on; snapshots in
`examples_for_ai/baseline_phase19/phase_b{1,2,3,4}_only_franco_same_prio/` and
`phase_b_all_franco_same_prio/`).

Headline metrics (baseline = `phase19_on_a82`; pinResSigma budget ≤ 0.169+0.05; redBandPixels budget ≤ 197 110 + 5%):

| Run | Flag(s) | pinResSigma | pinResMaxAbs | redBandPixels | wTestOutliers |
|---|---|---|---|---|---|
| baseline | none | 0.169 | 1.944 | 197 110 | 110 |
| R1 | B.1 only | 0.169 | 1.946 | 206 396 (+4.7%) | 119 |
| R2 | B.2 only | 0.169 | 1.946 | 206 278 (+4.6%) | 119 |
| R3 | B.3 only | 0.168 | 1.948 | 201 925 (+2.4%) | 97 |
| R4 | B.4 only | 0.168 | **1.936** | 208 110 (+5.6%) | 115 |
| R5 | B.1+B.2+B.4 | 0.168 | 1.938 | 209 404 (+6.2%) | 116 |

**Default-on flips (Task 11):**
- **B.1 (AASHTO K-cap):** ✅ default-on. Cap fires mostly on residential streets with steep terrain (K_sag=4 / K_crest=3 give L_cap ≈ 42m/32m at 6° slope); doesn't extend or distort terrain-faithful behaviour.
- **B.2 (short-connector compositional):** ✅ default-on. 27 short connectors on franco re-routed from legacy h00 fall-through to per-end parabolic compositional blend; anchor exactness preserved; no metric regression.
- **B.3 (cubic C1):** ❌ rejected on visual review. W1 metrics improved (best `redBandPixels` and `wTestOutliers` of any singleton) but the cubic adds a visible ramp/bump near the seam — the curve has to bend extra inside [0,L] to satisfy both `z(L)` and `z'(L)` when the natural slope past d=L disagrees with the chord grade. Flag stays in code as opt-in. Follow-up sketch in plan §Validation outcomes: extend blend length along the road to ease into the natural grade, rather than curving harder within a fixed L. See [memory/feedback_b3_cubic_rejected](../../../C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/feedback_b3_cubic_rejected.md).
- **B.4 (dead-end terrain-slope match):** ✅ default-on. `redBandPixels` is 0.6% over the 5% strict budget but `pinResMaxAbs` is the best of all runs (1.936m vs 1.944m baseline); visual review confirms the "flat platform → ramp" artefact at sloped dead ends is gone. Step 6 `ApplyEndpointTapering` is now bypassed when this flag is on; removal of the legacy taper code deferred to a follow-up cycle.

**Obsolete code now eligible for removal (follow-up, not blocking):**
- `ApplyEndpointTapering` method body + `EnableEndpointTaper` / `EndpointTaperDistanceMeters` parameters — dead under default-on B.4. Removal kept out of the Phase B scope so the legacy path stays available for one validation cycle.

---

## Adjacent open threads

These predate the parabolic-blend program and remain unresolved. They may interact with A.5/A.7/B in non-obvious ways — flagged here so they don't get re-discovered as "new" bugs in the next session.

### X1 — `JunctionBankingAdapter` overwrites CG profiles 🔬

**Symptom:** After the unified CG solver produces C1/C2 smooth elevation profiles at junctions, Phase 3.5 (`JunctionBankingAdapter`) applies quintic smoothstep ramps that overwrite `TargetElevation` for banking-coupled cross-sections, partially undoing the solver's work. Documented in `memory/junction_elevation_debugging.md` as "Remaining lead" after the v3 RAA-inspired fix landed.

**Why not in A.5:** A.5 changes Step 5b (propagation application). `JunctionBankingAdapter` runs separately, downstream. Order of operations: blender → banking adapter → rasterization. Each can corrupt the previous. Investigate independently when the dominant residual moves to banking-coupled CSes.

### X2 — Generalize seam blending beyond propagation 🔬

**Background:** A.5 applies a *narrow* application of Nguyen-style seam blending (drop weight to 0 at contested anchor) to propagated mid-spline influences only. Memory `surface_model_junction_overlap.md` notes the general principle applies to overlapping trajectory zones at junctions everywhere — currently first-writer-wins in several places.

**Why deferred:** Scope. A.5 fixes one symptom. The general refactor of seam handling is a bigger architectural change; surface it only if multiple symptoms accumulate that all trace back to first-writer-wins.

### X3 — Connected-road mesh solver: terrain-road elevation gap 🔬

**Symptom:** Even with the unified blender's elevation correct, the connected-road mesh solver's output has visible artifacts where road meshes don't sit cleanly on terrain. Memory `mesh_solver_tuning_status.md` notes the alpha bug is fixed but the gap remains.

**Why not in A.5/B:** Mesh rendering, not heightmap generation. Different code path. May become invisible once the heightmap matches the spline's actual elevation, but is currently a separate bug worth its own thread.

### X4 — Dead-end spike regression in `FinalSnapTJunctionEndpoints` 🔬

**Symptom:** `FinalSnapTJunctionEndpoints` (kept indefinitely per Phase 1.9 spec §7.1) corrupts cross-sections near dead ends via unbounded surface extrapolation from distant `MidSplineCrossing → TJunction` junctions. Previously masked by `NetworkJunctionHarmonizer` Steps 4-7 side effects, surfaced after commit `d02fba8` cleaned those up.

**Why not in A.5/B:** Spec explicitly forbids modifying `FinalSnapTJunctionEndpoints`. Fix likely lives elsewhere — bounding the extrapolation reach, or making `MidSplineCrossing` contributors invisible to dead-end snapping. Investigation doc: `ai_docs/dead_end_spike_investigation_2026-03-06.md`.

---

## Triggering convention

For conditional follow-ups (A.6, A.7), the trigger is in the predecessor's Task 6 validation result. The exit row in that plan's pass-criteria table determines whether the conditional follow-up moves from ⏳ → 🚧.

When opening any deferred item:

1. Update the **Status overview** row.
2. Write a plan doc with the established filename pattern `YYYY-MM-DD-<topic>-plan.md`.
3. Add a link in the row's "Plan / link" cell.
4. If the plan introduces a feature flag, default it to false until validation completes, mirroring the A → A.5 pattern.

When closing an item:

1. Update **Status** to ✅ Complete.
2. Add a one-line **Result** note in the item's section above (delta vs baseline, link to validation snapshot).
3. Leave the section in place — closed sections are still load-bearing context for the next-session-cold-reader.
