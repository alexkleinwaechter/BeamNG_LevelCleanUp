# Phase A.5 — propagation/overlap taper (next-session handoff)

You are picking up Phase A.5 of the parabolic-junction-blend work. You did NOT
participate in Phase A, A.8, or A.8.1; read the context inline below before
touching code.

## Repository state

- **Working directory:** d:\Source\beamng_mapping_pro
- **Current branch:** experimental/parabolic_junction_blend
- **HEAD:** `976d1f6` ("test: two-pass rasterizer protects terminating-road surface (Phase A.8)")
- **Test count to preserve:** 267/267 green
- **Uncommitted local edit:** `EnableSurfaceWidthProtection = true` in
  `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`
  (the A.8 validation toggle — leave it as-is; A.5 stacks on A.8).
- **Commits since plan docs landed (newest → oldest):**
  - `976d1f6` — test: two-pass rasterizer protects terminating-road surface (Phase A.8)
  - `5c6fee7` — refactor: extract RasterizeSplinePolygons helper, add two-pass dispatch (Phase A.8)
  - `5327ca1` — docs: correct SurfaceWidth cref to RoadSmoothingParameters.RoadWidthMeters
  - `bb73e95` — feat: add SurfaceWidth field to UnifiedCrossSection (Phase A.8 scaffold)
  - `38aec82` — feat: add EnableSurfaceWidthProtection flag (Phase A.8 scaffold)
  - `9f9f4dd` — docs: Phase A.5 + A.8 plans and parabolic-blend roadmap

A.8.1 (IDW junction-gap-fill) was tried in the previous session and
hard-reset out of history because it produced no measurable improvement
on the centerline metrics. The reset was deliberate — do not try to
reconstruct A.8.1 unless asked.

## Required reading, in order

1. **The A.5 plan itself** —
   [ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a5-plan.md](2026-05-15-parabolic-blend-phase-a5-plan.md).
   This is the seven-task plan you'll execute. **Note:** the "Roadmap context"
   header in the plan correctly states A.5 runs AFTER A.8, and Task 6's
   validation steps already point at the A.8 snapshot as the comparison
   baseline (NOT parabolic_a). Read and treat as authoritative.
2. **The roadmap** —
   [2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md).
   Status table + conditional follow-ups (A.6/A.7) + adjacent threads (X1-X4).
   Note: the A.8.1 row referenced in some earlier docs was reset out of the
   tree; the current roadmap does not mention it.
3. **The franco_same_prio snapshot README** —
   `examples_for_ai/baseline_phase19/README.md` (gitignored; lives locally).
   Contains the parabolic_a baseline AND the `surface_protection_a8_franco_same_prio`
   snapshot section. Both are the baselines you'll compare A.5 against.
4. **Original Phase A.5 handoff (historical context)** —
   [2026-05-15-parabolic-blend-phase-a5-handoff.md](2026-05-15-parabolic-blend-phase-a5-handoff.md).
   The handoff that originally scoped A.5 — read it for the j126/spline-64
   diagnosis but note: its prescribed validation baseline is parabolic_a, which
   is OUTDATED. Use the A.8 snapshot instead per the updated A.5 plan.
5. **Code touchpoints** — `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`:
   - L243-272 — Step 5b applies propagated mid-spline influences as a post-overlay.
     This is what A.5 changes.
   - L2256-2349 — `PropagateConstraintsThroughShortSplines` populates
     `_propagatedMidSplineInfluences`.
   - L1609-1653 — `CollectInfluencesFromCrossing`. Per-CS quintic-smoothstep
     weighting. Do NOT modify (it's shared with Step 5).

## What changed since the previous A.5 handoff was written

The original A.5 handoff was drafted on 2026-05-15, before Phase A.8 existed.
Between then and now (2026-05-25):

- **Phase A.8 landed.** Two-pass rasterization in `RoadMaskBuilder` protects
  each spline's painted-surface pixels from a wider adjacent spline's corridor
  stamp. Validated on franco_same_prio: TJunction `residual_max_minus_min`
  worst-case dropped from 3.229 m to 1.091 m (3× tighter contributor
  agreement); W1 `pinResSigma` dropped from 1.377 m to 0.718 m (2× tighter
  convergence).
- **A.8 also surfaced an unexpected effect at j126 / spline 64:** the W1
  `w` value rose from 9.07σ to 17.39σ. This is **NOT** A.8 making the road
  worse. It is A.8 *exposing* the parabolic blend's actual shape on
  spline 64's centerline. PA's legacy widest-first rasterizer was claiming
  spline 64's centerline pixels with the wider primary road's (flatter,
  terrain-following) elevation; A.8 correctly stamps spline 64's own
  `cs.TargetElevation` (the parabolic-blend value) on those pixels. The
  kink at the ramp boundary was always present in `cs.TargetElevation` —
  PA was hiding it.

**A.5's job is therefore even more direct than the original handoff
described:** smooth the parabolic profile that produces the kink at the
ramp boundary, by tapering propagated mid-spline influences (Step 5b)
in the blend zone of any directly-anchored junction.

## What the original A.5 handoff still gets right

- The mechanism it diagnosed (j102's propagated mid-spline influence on
  spline 64 via short spline 52 overlapping j126's blend zone) is still
  the right hypothesis. See A.5 plan §"Why a taper, not a hard masking".
- The taper's mathematical contract (smoothstep on `d_Y / L_Y`) is the
  right shape.
- The hard constraints listed are still binding (no grade clamp, don't
  touch `FinalSnapTJunctionEndpoints`, etc.).
- The seven-task structure is still valid.

## What's different now

- **Validation baseline:** Compare A.5 against
  `surface_protection_a8_franco_same_prio` (the A.8 snapshot), NOT
  `parabolic_a_franco_same_prio`. The A.5 plan's Task 6 has been updated
  for this — read it as the authoritative comparison procedure.
- **Pass criteria interpretation:** j126's "starting point" is 17.39σ
  (A.8 baseline), not 9.07σ (parabolic_a baseline). A.5 should bring
  j126's `w` substantially below 17.39σ — ideally below 6σ
  (intermediate target) or below 3σ (full target). j125 should return
  toward < 3σ (it regressed from 2.75σ to 5.01σ when A.8 exposed the
  parabolic profile there too).
- **What "success" looks like at the heightmap level:** A.5 smooths
  `cs.TargetElevation`. A.8 then renders that smoothed profile faithfully
  into the heightmap. The W1 tangent-kink metric measures the smoothed
  profile directly — no rasterizer masking effect to discount.

## Mission

Execute Phase A.5 per
[the existing plan](2026-05-15-parabolic-blend-phase-a5-plan.md), seven
tasks. The plan is TDD-scaffolded with bisectable commits. The plan's
internal pass criteria and rollback paths are already coherent with the
A.8 base.

## Hard constraints (unchanged from original handoff)

- **Terrain-faithful.** No max-grade clamp. The taper must be derived
  from blend-zone geometry, not terrain-grade rules. The user has
  explicitly rejected `EnableMaxGradeClamp`; see
  `memory/feedback_no_grade_clamp.md`.
- **Do not touch `FinalSnapTJunctionEndpoints`** in
  `UnifiedJunctionProfileBlender.cs` (around L1703-1930). Phase 1.9
  spec §7.1 keeps it indefinitely.
- **Do not touch `CalculateAdaptiveBlendDistance`,
  `JunctionBlendDistanceMeters`, or `RoundaboutBlendDistanceMeters`
  defaults.** Those are Phase B work, tracked as a separate roadmap row.
- **`EnableParabolicJunctionBlend` stays `true`** by default; the taper
  composes with parabolic, doesn't replace it.
- **`EnableSurfaceWidthProtection` stays `true`** (currently uncommitted
  in the user's working tree); A.5 stacks on A.8.
- **Do not modify `BlendSplineProfile` or `BlendSplineProfileParabolic`.**
  The A.5 change is in propagation construction or Step 5b application,
  not in the per-spline blender.

## Execution discipline

1. The A.5 plan is already written and reviewed in the previous session.
   You do NOT need to re-write it. Read it; if you find genuine errors
   that need correction (e.g., line numbers shifted, signatures changed),
   propose a correction commit before starting Task 1.
2. Use **`superpowers:subagent-driven-development`** to execute the plan
   task-by-task. Dispatch one implementer subagent per task, then spec
   reviewer, then code quality reviewer, then mark complete.
3. Task 6 is user-driven (BeamNG.drive regen). Pause and hand back; the
   user runs the regen, you snapshot and analyze.

## Validation pass criteria (A.5 specifically)

Re-run franco_same_prio with all three flags on:
- `EnableParabolicJunctionBlend = true`
- `EnableSurfaceWidthProtection = true`  (A.8)
- `EnablePropagationOverlapTaper = true`  (A.5, after Task 1)

Snapshot to `examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio/`.

Compare against `surface_protection_a8_franco_same_prio/` (the A.8 baseline).

Pass criteria for j126 / spline 64:
- `w` < A.8's 17.39σ; ideally < 6σ.
- `quadratic_growth`: monotone shape, no sign flip between adjacent d
  markers.
- `residual_max_minus_min`: stay ≤ A.8's 1.091 m (must not regress the
  contributor-convergence win).

Regression gate:
- **j125 spline 64 `w`** < 3σ (A.8 took it to 5.01σ; A.5 should restore
  toward < 3σ).
- W1 aggregate `redBandPixels`: ≤ A.8's 390 248 + 5 %.

## First message back to the user

1. Confirm you've read this handoff, the A.5 plan, and the franco_same_prio
   snapshot README (specifically the
   `surface_protection_a8_franco_same_prio` section).
2. Verify branch state: `git log --oneline -3` should show `976d1f6` at
   HEAD.
3. Verify test count: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
   → expect 267 green.
4. Verify the local toggle is still present:
   `grep "EnableSurfaceWidthProtection" BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`
   → expect default = true.
5. Confirm understanding of the hard constraints.
6. Confirm understanding that A.8 is not a "drift" but an *exposure*:
   the parabolic blend's actual shape on spline 64's centerline is now
   visible because A.8 stops the primary's corridor from overwriting it.
   A.5's job is to smooth that visible shape.
7. Dispatch the first implementer subagent for Task 1 of the A.5 plan
   (the flag scaffold). Same TDD discipline as A.8 used —
   pause for user only at Task 6 (validation snapshot).
