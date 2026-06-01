# Phase C handoff + new banking/cross-slope follow-up

**Date:** 2026-05-26
**Branch:** `feature/parabolic_blend_phase_c_wip`
**Status:** Phase C v1.1 in working tree (uncommitted), awaiting franco validation. New cross-slope artefact class raised during today's session — investigation deferred to next session.

---

## Today's progress — Phase C third clamp (longitudinal)

**v1.1 landed in the working tree (uncommitted):** the mid-spline-crossing-aware ceiling from the Phase C TODO list.

**Files modified:**
- [UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs)
  - Field `_midSplineCrossingDistancesBySpline` (per-spline ascending list of distFromStart for non-own-anchor contributors). Built alongside `_splineClaimedZones` when `EnableBlendDistanceStretchToMatchSlope` is on.
  - Builder `BuildMidSplineCrossingDistances` — scans `network.Junctions[*].Contributors` once, filters `!IsEndpoint`, groups by `splineId`, sorts ascending.
  - `BlendSplineProfileParabolic` signature gained `IReadOnlyList<float>? otherJunctionDistancesOnSpline = null` and `float midCrossingSafetyMarginMeters = 2.0f`.
  - Two helpers `NearestStartSideMidCrossingCeiling` / `NearestEndSideMidCrossingCeiling` — return `+∞` when no other-junction CS sits beyond currentL, so the existing `MathF.Min` chain absorbs the new clamp without branching.
  - Both stretch blocks (start- and end-side) call the appropriate helper before the `stretched > currentL + 0.01f` extend check.
  - Both call sites in `ApplyUnifiedProfiles` thread `_midSplineCrossingDistancesBySpline?.GetValueOrDefault(splineId)`.
  - Field cleared at end of `ApplyUnifiedProfiles`.
- [PhaseCStretchLBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseCStretchLBlendTests.cs) — 3 new tests appended.

**Semantics (option b per plan):** a MidSplineCrossing already inside the unstretched zone does NOT shorten stretch — the parabola was already overwriting it pre-stretch; stretching can't make that worse. The clamp only blocks the stretch from running into a NEW MidSplineCrossing that sits beyond currentL.

**Test status:** 380/380 green (was 377 before; +3 new). New tests:
- `StretchOn_MidSplineCrossingAtD35_ClampsStretchAtD33` — direct franco OSM 282534720 regression guard. Junction-20 geometry with synthetic mid-crossing at d=35, expected stretch clamped to d=33.
- `StretchOn_MidSplineCrossingInsideCurrentL_DoesNotShortenStretch` — mid-crossing at d=20 (inside currentL=30), stretch still proceeds to ~40m.
- `StretchOn_NullOtherJunctions_BehavesIdenticallyToBaselineStretch` — byte-identical regression guard for null list.

**Awaiting franco_same_prio validation** with `EnableBlendDistanceStretchToMatchSlope=true`:
1. Junction 20 (OSM 948007001) — kink fix should remain (Phase C v1 already proved this).
2. Node 282534720 (Impasse André Derain ↔ Rue Salvador Dalí) — step discontinuities should be gone after the third clamp.

**User feedback after a first visual look:** "not a bad first try" — but raised a new, separate artefact class (see below).

**Commit decision parked.** Options on resume:
- Layer a new commit on top of `33d8c88`: `feat: add MidSplineCrossing-aware ceiling to stretch-L (Phase C follow-up)`.
- Squash with `33d8c88` via interactive rebase into the single commit message the plan suggests. NOTE: `33d8c88` is already on `origin/feature/parabolic_blend_phase_c_wip` — squash rewrites published history, only do if comfortable.

---

## New issue raised today — bank/cross-slope mismatch at junctions

**Status (updated 2026-05-28):** Resolved by Phase D — see
[2026-05-28-phase-d-symmetric-bank-blend-design.md](2026-05-28-phase-d-symmetric-bank-blend-design.md)
and [2026-05-28-phase-d-symmetric-bank-blend-plan.md](2026-05-28-phase-d-symmetric-bank-blend-plan.md).
Tasks 1-11 complete; commits f9c8e6d..1a71b92. 386/386 tests green. Awaiting
franco visual validation (Task 12, user-driven).

---

**Observation (user screenshot, franco_same_prio):** the connecting road meeting a higher-priority primary road shows a visible cross-slope wedge at the merge point. The primary road has real banking (dashboard widgets read 4.5° / 0.8°); the connecting road carries its own, different bank. The two surfaces don't lie flush at the junction — both are smooth individually, but their left-edge / right-edge elevations don't line up where they meet.

**What the user wants (paraphrased and confirmed):** within the connecting road's blend zone, the cross-slope (bank angle, i.e. dz between left and right edges) should ramp from the connecting road's natural bank to the primary road's bank at the junction anchor. Mirror of how longitudinal elevation/slope already blends, but on the lateral axis.

**Why this is orthogonal to Phase C stretch-L:** Phase C handles longitudinal slope at d=L (where the blend meets the natural profile). This new issue is lateral slope at d=0 (where the blend meets the junction anchor). Independent axes of the same blend zone.

### Three candidate root causes (decide before designing)

The unified blender already claims to handle bank — so the artefact must come from one of:

- **(a) Wrong target at the constraint.** `JunctionEndpointConstraint.BankAngleRadians` for the connecting road might be set from its OWN natural bank, not from the primary's bank at the junction. If so, the blender ramps the connecting road back to its natural bank → no match by construction.
- **(b) `MaintainBanking` locks the natural bank in the blend zone.** `ApplyUnifiedProfiles` pass-2 marks terminating-road CSes inside the blend zone as `MaintainBanking` to keep `JunctionBankingAdapter` from overwriting them. That guard might also be preventing the unified blender's own bank-blend from taking effect.
- **(c) Bank is blended only for edge-elevation derivation, not written back.** Step 4 derives edge elevations from `TargetElevation ± halfWidth × sin(BankAngleRadians)`. If no upstream step writes the blended bank back onto the CS's `BankAngleRadians`, the field still carries the natural value.

### First task on resume — INVESTIGATE before designing

Read in order, looking for who reads and writes `BankAngleRadians` and `JunctionEndpointConstraint.BankAngleRadians` for a terminating road:

1. `UnifiedJunctionProfileBlender.ComputeAllJunctionConstraints` — does it set the terminating road's `BankAngleRadians` from the primary road, or from the terminating road's own natural value?
2. `UnifiedJunctionProfileBlender.BlendSplineProfileParabolic` — does it touch `BankAngleRadians` at all, or only `TargetElevation`? (Suspect: only elevation.)
3. `BlendSplineProfile` (legacy h00 path) — does it blend bank? Compare bank handling between legacy and parabolic paths.
4. `JunctionBankingAdapter` — find via Grep. What does it do, and where in the pipeline does it run relative to `ApplyUnifiedProfiles`?
5. `JunctionBankingBehavior.MaintainBanking` — every site that reads it. The pass-2 comment is the suspect; verify.

Once root cause is pinned, **ask user before designing the fix.** This could be a 1-line constraint-source change or a multi-phase rewrite — don't presume scope.

### Test framing for when work resumes

Mirror the Phase B/C TDD pattern: synthetic terminating-road geometry with the primary's bank specified at the junction constraint, run `BlendSplineProfileParabolic` (or whatever path needs the fix), assert `BankAngleRadians` at d=0 equals primary's bank and ramps smoothly to natural bank at d=L. Add a regression guard for the artefact-producing case.

---

## Linked artefacts

- Phase C plan + §Phase C v1 notes: [2026-05-25-parabolic-blend-phase-b-plan.md](2026-05-25-parabolic-blend-phase-b-plan.md)
- Roadmap (Phase C is row C, 🚧 In flight): [2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md)
- Memory: `phase_c_stretch_l_midspline_blindspot.md` (updated today to reflect v1.1 status), `phase_c_banking_followup.md` (new, captures the cross-slope issue)
- Related earlier banking work (for context, not directly applicable): `mesh_solver_tuning_status.md`, `surface_model_junction_overlap.md`

---

## Quick resume snippet

```
You are on feature/parabolic_blend_phase_c_wip. Phase C v1.1 (third clamp) is in
working tree, uncommitted, 380/380 tests green. Awaiting franco_same_prio visual
re-validation on junction 20 + node 282534720. Then commit, then look at the
cross-slope/bank mismatch issue described in this doc — start with the
investigation order above; do NOT design until root cause is pinned.
```
