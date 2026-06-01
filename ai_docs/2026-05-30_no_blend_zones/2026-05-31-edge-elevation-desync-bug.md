# Edge/centerline elevation desync — through roads paint high, shoulders read low

- **Date:** 2026-05-31
- **Branch:** `experimental/noblendzones_code_cleanup`
- **Status:** FIXED (2026-05-31). Preferred fix from §6 implemented + TDD-guarded; 326 tests green.
  See §8 for the implementation record.
- **Severity:** real bug, **larger** than the propagation nudge — it is the dominant part of the
  through-road float at the junctions investigated this session (the raised dirt-shoulder / berm look).

---

## 1. One-paragraph summary

A cross-section stores three elevations: the centerline `TargetElevation` and the two edge elevations
`LeftEdgeElevation` / `RightEdgeElevation` (≈ `TargetElevation ± halfWidth·sin(bank)`). Several no-blend passes
move `TargetElevation` but **never recompute the edges**, and the only pass that re-derives *all* edges (Step 4
of `ApplyUnifiedProfiles`) runs *inside* the iteration loop — **before** the post-loop affine passes that move
the centerline again. The painted road **core** uses the centerline (so the road surface sits at the moved-up
Z), while the **embankment/shoulder blend and polygon-corner logic read the stale, lower edges** — so the road
floats while its own shoulder is computed ~1–1.6 m below it. That mismatch is a visible artifact independent of
which pass moved the centerline.

## 2. The two elevation representations

- **Centerline:** `UnifiedCrossSection.TargetElevation`.
- **Edges:** `LeftEdgeElevation` / `RightEdgeElevation`, set as `TargetElevation ± halfWidth·sin(bank)` by:
  - `BankedElevationCalculator` (`:46-47`), via Phase 2.5 `ApplyBankingPreCalculation`;
  - `ApplyUnifiedProfiles` **Step 4** (`UnifiedJunctionProfileBlender.cs:173-183`), every iteration;
  - §4 `MatchTerminatingBankingToThroughSurface` (`UnifiedRoadSmoother.cs:1566-1568`) — **terminating CSes only**;
  - the connector ramp `EaseConnectorGradeToThroughSurface` (`:1712-1713`) — **connector CSes only**.

For a normal banked road the edges straddle the centerline by a few cm. When they are **far** from the
centerline (e.g. 1.6 m below, with bank ≈ 2°), they are **stale** — computed from an older, lower centerline.

## 3. Who paints from what

- **Road core surface (Phase 4):** `RoadMaskBuilder.cs:402` → `BankedTerrainHelper.GetBankedElevationForPixel`
  → `GetBankedElevationInSegment` (`:163`) = `Lerp(cs1.TargetElevation, cs2.TargetElevation) + lateral·sin(bank)`.
  **Uses the centerline.** So the painted road rises with `TargetElevation`.
- **Edge / shoulder / embankment + polygon corners:** `BankedTerrainHelper.GetEdgeElevation` (`:78-81`),
  `GetSegmentCornerElevation` (`:104`), and `JunctionSurfaceCalculator` (`:429-432`) prefer the **stored edge
  elevations** when not NaN. **Uses the stale edges.**

Net: centerline-painted core at the raised Z, shoulder blend at the stale low Z → a step at the road edge =
the raised-shoulder/berm look.

## 4. Which passes desync it (verified)

| Pass | Writes `TargetElevation`? | Updates edges? | Runs when |
|---|---|---|---|
| Phase 2.5 banking (`ApplyBankingPreCalculation`) | no | yes (derives) | **iteration 0 only** |
| `ApplyUnifiedProfiles` Step 4 | no | yes (all CSes) | every iteration, inside the loop |
| Phase-2 affine `ApplyAffineLeveling` (`UnifiedRoadSmoother.cs:1746-1747`) | **yes** | **no** | every iteration |
| §3 `RetargetTerminatingRoadsToSettledThrough` → `ApplyAffineLeveling` | **yes** | **no** | **post-loop** |
| §4 banking match | yes (center kept; bank+edges) | yes | post-loop, **terminating only** |
| ramp | yes (connector center) | yes | post-loop, **connector only** |

The killer ordering: the **last** edge re-derivation a *through* road's mid-junction cross-section gets is the
final iteration's Step 4. **After** that, §3 re-applies affine to roads that terminate elsewhere (a whole-road
tilt that also moves their *through* junctions), and §4/ramp only refresh edges on *terminating/connector*
cross-sections — never on a through road's mid-spline junction CS. So that CS keeps Step-4 edges while its
centerline drifts.

## 5. Evidence

- **`_generated_terrain` J#312 (node 430808759):** through 195 centerline **198.28**, edges **196.50 / 196.81**
  (≈196.66) → **+1.6 m** desync, bank only +1.8° (can't explain it). At 195's *terminating* junctions the edges
  are consistent — because §4/ramp refresh them there.
- **`franco_same_prio` J#201 (node 663313796):** through 100 centerline **100.45**, edges **98.71 / 98.96**
  (≈98.83) → **+1.6 m** desync, bank +2.0°. The honest edges sit right on the terrain dip (98.66); the
  centerline floats +1.79 m above terrain. This residual persisted **after** the propagation nudge was skipped,
  proving it is the larger, separate defect.

## 6. Proposed fix (discuss before implementing)

**Preferred — a single final edge re-derivation pass.** After the post-loop passes run (affine §3 → §4 banking
→ ramp), and **before** Phase 4 / the `[NO-BLEND DIAG]` dump, re-derive every cross-section's edges from its
*current* `(TargetElevation, BankAngleRadians)`, mirroring Step 4:
```csharp
foreach (var cs in network.CrossSections) {
    if (float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended) continue;
    var d = (cs.EffectiveRoadWidth / 2f) * MathF.Sin(cs.BankAngleRadians);
    cs.LeftEdgeElevation  = cs.TargetElevation - d;
    cs.RightEdgeElevation = cs.TargetElevation + d;
}
```
- Runs **last**, so it captures all centerline moves (affine, §3) and all banking (§4 sets `BankAngleRadians`
  symmetrically, so symmetric ± re-derivation preserves §4's twist — verify against
  `BankingMatchToThroughSurfaceTests`).
- **Skip `cs.IsRoundaboutBlended`** (their edges are authoritative from Phase 2.6).
- Watch any cross-section that intentionally carries asymmetric/constrained edges that are NOT `±sin(bank)`
  about the centerline; if any such consumer exists on the no-blend path, it must be excluded. (Audit:
  `JunctionSurfaceCalculator.ApplyEdgeConstraints` writes `Constrained*EdgeElevation`, noted as orphaned in the
  followup §4 — confirm it isn't feeding `LeftEdgeElevation`/`RightEdgeElevation` here.)

**Alternative — fix at the source:** make `ApplyAffineLeveling` (and therefore §3) recompute edges for every
cross-section it moves. Cleaner conceptually, but affine is called per spline and would re-derive edges from a
possibly-not-yet-final bank; the single final pass is simpler and provably last-writer.

**TDD:** add a network-level test asserting that after the full smooth, every non-roundabout CS has
`|edge − (TargetElevation ± halfW·sin(bank))| < ε`. Then the J#201/J#312 desync becomes a guarded invariant.

## 7. Relationship to other items

- This is **distinct** from `PropagatedMidSplineInfluences` (that nudge moved the *centerline*; this bug is the
  centerline-vs-edge *consistency*). Both were present at J#201; removing the nudge left this one.
- It interacts with §2 (absolute depth): even an honest centerline float would still need consistent edges so
  the shoulder doesn't read low. Fixing the desync makes §2 diagnosis cleaner (the `delta` and the painted
  surface will agree).

## 8. Implementation record (2026-05-31)

Implemented the **preferred** fix from §6 — a single final edge re-derivation pass.

- **New method** `UnifiedRoadSmoother.ReconcileEdgeElevationsToCenterline(network)`
  (`UnifiedRoadSmoother.cs`, just after `EaseConnectorGradeToThroughSurface`): re-derives
  `LeftEdgeElevation`/`RightEdgeElevation = TargetElevation ± (EffectiveRoadWidth/2)·sin(BankAngleRadians)`
  for every cross-section, **skipping** `float.IsNaN(TargetElevation)` and `IsRoundaboutBlended`. Returns the
  count re-derived. `internal static` so it is unit-testable like the other §3/§4/ramp passes.
- **Wired** into the post-loop sequence in the main smooth, **after** §3 retarget → §4 banking → ramp and
  **before** the `[NO-BLEND DIAG]` dump / Phase 4 — so it captures every centerline move and every banking
  change. Logs `[NO-BLEND] edge reconcile: re-derived N …` when N>0.
- **Idempotency verified by code inspection:** §4 (`:1566-1568`) and the ramp (`:1711-1713`) already write
  edges symmetrically from `BankAngleRadians`, so the final pass reproduces their values exactly — it only
  *changes* the through-road mid-junction CSes whose centerline §3 moved without refreshing edges.
- **Constrained edges untouched:** the pass writes only `Left/RightEdgeElevation`; the separate
  `Constrained*EdgeElevation` fields (audit point in §6) are not on this path.
- **TDD guard:** `BeamNgTerrainPoc.Tests/Junction/EdgeElevationReconciliationTests.cs` — 4 tests:
  stale edges re-derived to straddle the centerline (the J#201/J#312 ~1.6 m case), already-consistent edges
  idempotent, roundabout-blended skipped, NaN-centerline skipped. **Full suite: 326 green** (was 322).
- **Not yet visually validated** — next = user render of `franco_same_prio` (J#201/node 663313796) and
  `_generated_terrain` (J#312/node 430808759); expect the shoulder to read at the centerline Z (desync ≈ 0)
  with the `[NO-BLEND DIAG]` dump showing edges straddling the centerline. The remaining honest centerline
  float is §2 (absolute depth), now cleanly separable.
