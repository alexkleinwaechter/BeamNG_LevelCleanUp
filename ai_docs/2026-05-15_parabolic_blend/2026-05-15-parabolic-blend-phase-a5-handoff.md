# Phase A.5 — propagation/overlap taper

You are picking up Phase A.5 of the parabolic-junction-blend work. You did NOT
participate in Phase A; read the context inline below before touching code.

## Repository state

- **Working directory:** d:\Source\beamng_mapping_pro
- **Current branch:** experimental/parabolic_junction_blend, HEAD = `1638ae2`
- **Test count to preserve:** 264/264 green
- **Phase A commits (newest → oldest):**
  - `1638ae2` — flag default true after Phase A validation
  - `49da44f` — junction-126 synthetic regression test
  - `41a8091` — dispatcher wires EnableParabolicJunctionBlend
  - `33032e5` — BlendSplineProfileParabolic method + tests
  - `e66663e` — ParabolicJunctionProfile.Sample helper
  - `bac7fa7` — flag scaffold
  - `80dca32` — Phase A plan doc

## Required reading, in order

1. `ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a-plan.md`
   — Phase A context (read, do NOT re-execute).
2. `examples_for_ai/baseline_phase19/README.md` § "parabolic_a_franco_same_prio"
   — the Phase A validation snapshot and the j126 failure analysis. Gitignored
   but present in the working tree.
3. `ai_docs/2026-05-14_junction_pinning/2026-05-14-phase19-visual-debug-handoff.md`
   — W1 harness conventions (CSV columns, w-test, quadratic_growth, snapshot
   workflow at section §10).
4. `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`:
   - **L243-272** — `ApplyUnifiedProfiles` Step 5b applies propagated mid-spline
     influences as a post-overlay (the source of the bumpiness this phase fixes).
   - **~L2256-2349** — `PropagateConstraintsThroughShortSplines` builds the
     `_propagatedMidSplineInfluences` dictionary.
   - **~L2330** — `CollectInfluencesFromCrossing` populates per-CS influence
     entries (likely the right injection point for taper logic).

## What Phase A landed and what it left unsolved

Phase A replaced the cubic-Hermite-weighted additive delta in
`BlendSplineProfile` with a direct parabolic substitution inside single-end
blend zones (`BlendSplineProfileParabolic`, `UnifiedJunctionProfileBlender.cs`
around L1006). End-to-end on franco_same_prio:

| | flagsoff | parabolic_a |
|---|---|---|
| j125 / spline 64 START — w | 7.16σ | **2.75σ** ✅ |
| j126 / spline 64 END — w | 9.09σ | **9.07σ** ❌ |
| j126 quadratic_growth | sign flip at d=60 | sign flip at d=60 |
| j126 residual_max_minus_min | 1.413 m | 1.414 m |
| W1 redBandPixels | 287 742 | 300 248 (+4.3 %) |

j125 is a clean win. j126 is structurally invisible to Phase A.

## Root cause of the j126 bump (concrete data from the run)

Spline 64 (length 311.7 m) terminates at:
- **j125** (T-Junction, start): edge Z = 184.40 m, slope = −8.4 %, blendDist = 100 m
- **j126** (T-Junction, end): edge Z = 158.98 m, slope = +0.11 % (≈0), blendDist = 100 m

A third anchor exists, applied as an overlay AFTER the blender:
- **j102** (Endpoint at coords 1012.32, 589.87, Z = 166.54 m, ~66 m straight-line
  from j126) terminates on short Spline 52 (length 27.6 m). Spline 52 can't fit
  j102's 100 m blend, so the propagation system emits a mid-spline influence on
  Spline 64:

  ```
  [PROPAGATE-CONTINUOUS] Constraint from Junction #102 through short Spline 52
     (len=27.6m) → continuous Spline 64
     (mid-spline influence, blend=72.4m, targetElev=166.54m)
  ```

The 72.4 m influence range falls inside spline 64's last ~80 m — directly
overlapping j126's 100 m end blend zone. Step 5b overwrites `cs.TargetElevation`
in that overlap region:

```csharp
// UnifiedJunctionProfileBlender.cs:256-262
var weightedElev = influences.Sum(inf => inf.elevation * inf.weight) / totalWeight;
var influence = MathF.Min(totalWeight, 1.0f);
var newElev = weightedElev * influence + cs.TargetElevation * (1f - influence);
cs.TargetElevation = newElev;
```

Result: parabolic (or legacy) sets the end zone to ~159 m, Step 5b drags it
toward 166.54 m, creating a 5–7 m cliff visible as the j126 sign-flip
signature in quadratic_growth.

j125 doesn't have a comparable propagated influence in its blend zone, which is
why parabolic shines there.

## Mission — option 3a "overlap taper"

When a propagated mid-spline influence's range intersects ANOTHER junction's
blend zone, weight-taper the influence toward zero at the boundary so it cannot
fight a directly-anchored junction constraint.

**Suggested approach (the plan should refine this):**
1. Build a per-spline map of "occupied blend zones" — for each CS index, record
   which junctions claim it via their own blend distance.
2. In `CollectInfluencesFromCrossing` (or wherever the propagated weights are
   assigned), attenuate weight by a smooth taper based on distance to the
   nearest contested junction's blend boundary. Taper = 1.0 outside other
   junctions' zones, falls to 0 at the contested junction's anchor node.
3. Re-validate franco_same_prio. The j125 win must be preserved; j126 must
   improve.

## Hard constraints (carried from Phase A)

- **Terrain-faithful.** No max-grade clamp, no max-slope ceiling. Taper must be
  derived from blend-zone geometry, not terrain-grade rules. The user has
  explicitly rejected `EnableMaxGradeClamp` and similar; see memory file
  `feedback_no_grade_clamp.md`.
- **Do not touch `FinalSnapTJunctionEndpoints`** in
  `UnifiedJunctionProfileBlender.cs` (~L1703-1930). Phase 1.9 spec §7.1 keeps
  it indefinitely.
- **Do not touch `CalculateAdaptiveBlendDistance`, `JunctionBlendDistanceMeters`,
  or `RoundaboutBlendDistanceMeters` defaults.** Those are Phase B (AASHTO
  K-value cap), tracked as a separate plan.
- **`EnableParabolicJunctionBlend` stays `true` by default.** The taper composes
  with parabolic; it does not replace it.
- **Do not modify `BlendSplineProfile` or `BlendSplineProfileParabolic`.** The
  Phase A.5 change is in propagation construction or Step 5b application, not
  in the per-spline blender.

## Execution discipline

1. First use `superpowers:writing-plans` to draft
   `ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a5-plan.md`
   — TDD-scaffolded tasks, each commit bisectable. Pause for user review of the
   plan before any code.
2. Once the plan is approved, execute via `superpowers:subagent-driven-development`.
3. Validation (Task N of the plan) is user-driven: the user runs BeamNG.drive,
   the agent snapshots and analyzes. You cannot run terrain generation.

## Validation pass criteria

Re-run franco_same_prio with Phase A.5 + parabolic both on. Snapshot to
`examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio/`. Compare
against:
- `parabolic_a_franco_same_prio/` (current Phase A baseline)
- `step1_franco_same_prio/` (Phase 1.9-only baseline)
- `repro_flagsoff_20260515/` (all-off baseline)

Pass criteria for j126 spline 64:
- `quadratic_growth`: monotone descent, no sign flip between 5/15/30/60 m markers
- `w_test_summary`: w < 3σ (currently 9.07σ)
- `residual_max_minus_min`: ≤ 1.5 m (must not regress; currently 1.414 m)
- W1 aggregate `redBandPixels`: ≤ parabolic_a baseline (300 248) + 5 %
- **j125 spline 64 must stay at w < 3σ** (regression-gate; currently 2.75σ)

## First message back to the user

1. Confirm you've read this handoff, the Phase A plan, and the README snapshot.
2. Verify branch state: `git log --oneline -3` should show `1638ae2` at or near
   HEAD. (If the user has merged Phase A to develop, ask which branch they want.)
3. Verify test count: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
   → expect 264 green.
4. Confirm understanding of the hard constraints.
5. Start drafting the Phase A.5 plan using `superpowers:writing-plans`. Pause
   for user review before touching code.
