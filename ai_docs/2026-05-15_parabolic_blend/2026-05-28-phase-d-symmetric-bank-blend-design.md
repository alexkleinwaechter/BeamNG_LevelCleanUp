# Phase D — Symmetric bank blend in parabolic junction profile

**Date:** 2026-05-28
**Branch:** `feature/parabolic_blend_phase_c_wip`
**Status:** Design accepted in session; implementation plan to follow.
**Predecessor:** [2026-05-26 Phase C handoff + banking follow-up](2026-05-26-phase-c-handoff-and-banking-followup.md)

---

## Problem

Phase A introduced `BlendSplineProfileParabolic` as the new default elevation-blend
path inside `UnifiedJunctionProfileBlender` (`EnableParabolicJunctionBlend = true`).
That path writes `cs.TargetElevation` but **never** writes `cs.BankAngleRadians`.
Step 4 of `ApplyUnifiedProfiles`
([UnifiedJunctionProfileBlender.cs:273-282](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L273-L282))
derives edges from `cs.TargetElevation ± halfWidth × sin(cs.BankAngleRadians)`, so
the terminating road's edges through the blend zone use the road's natural
curvature-driven bank instead of the bank ramp that should match the primary's
surface at the junction. Visible artefact on franco_same_prio: a cross-slope wedge
where a connecting road meets a higher-priority primary road.

The legacy `BlendSplineProfile` h00 path
([line 2005-2009](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L2005-L2009))
already does bank+elevation symmetrically via Hermite h00 weights — that path is
correct. This design ports the same symmetry into the parabolic path.

Full root-cause evidence: handoff doc above and 2026-05-28 chat transcript.

## Goal

Through the blend zone of a terminating road, ramp `cs.BankAngleRadians` continuously
from the natural per-CS bank at d=L to the junction constraint's bank at d=0, with
C1 continuity at both ends. After this change, the terminating road's edges sit
flush with the primary road's surface at the junction anchor, and a vehicle crossing
the merge experiences no perceptible bump in the lateral profile.

## Architecture

Two methods in `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`
gain a parallel bank-correction computation:

1. **`BlendSplineProfileParabolic`**
   ([line 1127](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L1127)).
   The existing per-CS loop at
   [line 1328-1382](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L1328-L1382)
   gains a bank computation alongside the elevation write.

2. **`BlendShortConnectorCompositional`**
   ([line 1489](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L1489)).
   The compositional helper already accepts `originalElevations`; it also needs
   `originalBankAngles` plumbed through. Bank is composed using the same
   `OverlapTaper` weights already used for elevation.

The two call sites in `ApplyUnifiedProfiles`
([line 162-168](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L162-L168)
and
[line 226-232](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L226-L232))
thread `originalBankAngles` into the parabolic call — it is already in scope at
those sites because `ApplyUnifiedProfiles` receives it from `UnifiedRoadSmoother`.

Step 4 ([line 273-282](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L273-L282))
is unchanged. It already reads `cs.BankAngleRadians`, so the new written values
propagate into `LeftEdgeElevation` / `RightEdgeElevation` for free.

## Algorithm

Hermite h00 (`2t³ − 3t² + 1`) on the bank delta, mirroring legacy
[line 2005-2009](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L2005-L2009).

```
For each CS in the blend zone:

  naturalBank   = originalBankAngles[cs.Index]
  startBankΔ    = startConstraint.BankAngleRadians - originalBankAngles[firstCS.Index]
  endBankΔ      = endConstraint.BankAngleRadians   - originalBankAngles[lastCS.Index]

  startH00(d)   = 2t³ − 3t² + 1   where t = d / startBlendDist           (0 outside start zone)
  endH00(d')    = 2t³ − 3t² + 1   where t = d' / endBlendDist            (d' = roadLen − d; 0 outside end zone)

  newBank       = naturalBank + startBankΔ × startH00 + endBankΔ × endH00
  cs.BankAngleRadians = newBank
```

Properties:

- At d=0: `newBank = constraintBank` exactly (h00 = 1, decayed natural cancels).
- At d=L: `newBank = naturalBank` at that CS (h00 = 0).
- In between: C1 continuous at both ends (h00 has zero derivative at 0 and 1).
- Outside the blend zone: `cs.BankAngleRadians` is left untouched.

For the compositional path, each end computes its own `(naturalBank + delta × h00)`,
then the two are weighted with `OverlapTaper.Compute(...)` (same weights as elevation):

```
bankFromStart = naturalBank + startBankΔ × startH00(d)
bankFromEnd   = naturalBank + endBankΔ   × endH00(d')
wStart        = OverlapTaper.Compute(distFromEnd, endBlendDist)
wEnd          = OverlapTaper.Compute(d, startBlendDist)
newBank       = (bankFromStart × wStart + bankFromEnd × wEnd) / (wStart + wEnd)
```

## Phase C interaction (stretched-L)

When `EnableBlendDistanceStretchToMatchSlope` extends `startBlendDist` /
`endBlendDist`, the bank zone extends with the elevation zone — both reads
operate on the post-stretch `*BlendDist` variables. Rationale: keeping the two
zone boundaries coincident prevents an artefact at d ∈ (originalL, stretchedL)
where the centerline is still ramping but the bank has already returned to
natural. No new code path needed; the formula picks up whatever the in-scope
`startBlendDist` / `endBlendDist` is at write time.

## Phase B.3 interaction — TBD

The Phase B.3 cubic path (`EnableBlendZoneEndC1` + `CubicJunctionProfile.Sample`)
**is suspected broken** (user observation, 2026-05-28). This design **does not
adapt bank to cubic** — bank stays Hermite h00 regardless of whether elevation
went parabolic or cubic. Two reasons:

- h00 already gives zero-slope at d=L by construction, which is the same end-
  condition the cubic was designed to enforce for elevation.
- The cubic-for-bank analogue would require a `PrimaryBankSlope` field on
  `JunctionEndpointConstraint` (mirror of `mJunction` for elevation). That field
  does not exist today and adding it is out of scope for Phase D.

**Action:** the Phase B.3 cubic path is to be debugged in a separate, later
session. Phase D introduces no dependency on it.

## Flag

New field on `JunctionHarmonizationParameters`:

```csharp
/// <summary>
///     Phase D — when true, BlendSplineProfileParabolic and
///     BlendShortConnectorCompositional write Hermite-h00-blended
///     BankAngleRadians through the blend zone, mirroring the legacy h00 path's
///     symmetric (elevation, bank) behavior. When false (escape hatch only),
///     the parabolic paths leave BankAngleRadians at its pre-blend (natural)
///     value — the historical pre-fix behavior which produces cross-slope
///     wedge artefacts at primary-vs-terminating bank mismatches.
/// </summary>
public bool EnableParabolicBankBlend { get; set; } = true;
```

Default **true** — the parabolic path's missing bank-write is a bug, not a new
feature, and current default behavior is wrong. The flag exists only as a
regression escape hatch.

## Tests

New file `BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs`, TDD-first
(write tests, watch them fail with the current parabolic path, then implement
until green). Pattern mirrors `PhaseCStretchLBlendTests`.

| # | Test                                                            | Asserts                                                                                                       |
|---|-----------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| 1 | `Parabolic_BankAtConstraintEnd_EqualsConstraintBankAngle`       | start-only constraint; bank at d=0 == constraintBank; bank at d=L == naturalBank at L; monotone in between.   |
| 2 | `Parabolic_BankAtBothEnds_MatchesBothConstraints`               | constraints at both ends, normal-length spline; both endpoints match constraints exactly.                     |
| 3 | `ShortConnectorCompositional_BankCompositionAtBothAnchors`      | short spline (overlap region); each anchor's bank matches its constraint within tolerance.                    |
| 4 | `StretchL_BankZoneExtendsWithElevationZone`                     | enable stretch-L; assert no kink in bank derivative at originalL; bank reaches natural only at stretchedL.    |
| 5 | `FlagOff_ParabolicPathLeavesBankUntouched`                      | regression guard: `EnableParabolicBankBlend = false` reproduces current (buggy) behavior byte-identically.    |
| 6 | `Franco_Junction20_EdgesFlushWithPrimary`                       | synthetic geometry from franco junction 20 (primary 4.5° bank, terminating 0.8° natural); assert terminating-road edge elevations within 0.05m of primary's edges at d=0. |

## Risks / what to audit when implementing

- **Existing tests that asserted `BankAngleRadians` unchanged after parabolic
  blend** will start failing — those asserted the bug, not the contract.
  Audit `PhaseAJunctionBlendTests`, `PhaseBStretchLBlendTests`,
  `PhaseCStretchLBlendTests`, and any compositional test for assertions
  about bank being equal to its pre-blend value.
- **`FinalSnapTJunctionEndpoints`** still snaps the tip CS bank at
  [line 2545](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L2545).
  After Phase D, the blend already arrives at constraintBank at d=0, so the
  final-snap write will be a no-op (or near no-op) for those CSes. Not a
  correctness risk, but worth confirming no double-correction warning fires.
- **Compositional path's `originalBankAngles` plumbing.** The helper does not
  currently take this parameter. Adding it requires updating the two call
  sites inside `BlendSplineProfileParabolic`'s two-end-overlap branch
  ([line 1162-1174](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L1162-L1174))
  to thread `originalBankAngles` through.

## Out of scope (Phase D follow-ups)

- **`PrimaryBankSlope` field on `JunctionEndpointConstraint`.** Would enable
  a cubic bank profile that matches the primary's bank gradient at the
  junction in addition to its value. Defer until visual evidence demands it.
- **Phase B.3 cubic path debugging.** Suspected broken, parked.
- **Banking pipeline rework** (`BankingOrchestrator` + `BankingCalculator`).
  Phase D does not touch Phase 2.5 — bank is still computed naturally there
  and overwritten only inside the blend zone in Phase 3.

## Linked artefacts

- Predecessor handoff: [2026-05-26 Phase C handoff + banking follow-up](2026-05-26-phase-c-handoff-and-banking-followup.md)
- Roadmap: [2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md)
- Memory: `phase_c_banking_followup.md`
