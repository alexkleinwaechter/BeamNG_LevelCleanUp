# PropagatedMidSplineInfluences — what it is, what we learned, why it must go

- **Date:** 2026-05-31
- **Branch:** `experimental/noblendzones_code_cleanup`
- **Status:** DIAGNOSED + A/B-VALIDATED. Removal agreed (do it as its own commit; see §5 plan).
- **Author context:** found during a debug session on the user's report "main roads still get pulled to
  junction elevations because of side roads" (OSM node 430808759, then node 663313796 on `franco_same_prio`).

---

## 1. One-paragraph summary

`ApplyPropagatedMidSplineInfluences` (the "Step 5b" pass in `UnifiedJunctionProfileBlender`) lets a **short
terminating side road** that can't fit its own blend ramp **reach into the middle of a through/main road and
drag that main road's surface up or down** toward the side road's junction elevation. That is the exact
opposite of the no-blend rule ("the main road keeps its honest profile; side roads bend to meet it, never the
reverse"). It is a leftover from the old blend-zone design, it is **not gated** by the blend flags (so it runs
even on the no-blend path), and the job it once did is now handled correctly by affine leveling. Disabling it
visibly improved short-stub junctions. **Remove it.**

## 2. What it was originally built to solve (the old blend-zone world)

In the blend-zone design, every road that ends at a junction gets a **ramp zone** — a stretch near the
junction over which its elevation eases to meet the junction. A road that needs, say, ~20 m to ramp smoothly
but is only 13 m long **runs out of road**: the ramp can't finish, leaving a kink at the short road's far end.

`PropagateConstraintsThroughShortSplines` was the fix: detect short roads (length < `flatZone +
0.5·blendDistance`), and push the *leftover* ramp through the junction at the short road's far end into the
neighbours, so the transition continues on a road that has room.

- If a neighbour also **ends** at that far junction → it gets a normal propagated endpoint constraint
  (`[PROPAGATE]` / `[PROPAGATE-BLEND]`). This is fine — that road ends there, it is allowed to bend.
- If a neighbour **passes through** (a continuous/main road) → you cannot give a "ramp to your endpoint"
  constraint to a road that has no endpoint there. So instead it collects **mid-spline elevation influences**
  that nudge the through road's cross-sections near the crossing toward the short road's junction Z
  (`[PROPAGATE-CONTINUOUS]`), stored in `_propagatedMidSplineInfluences` and applied by Step 5b.

The continuous-road nudge is the harmful part. The endpoint-neighbour branch is a separate concern (and on the
no-blend path it is inert — see §4).

## 3. What it actually does when enabled (plain speech)

When enabled, **a short side road that couldn't absorb its own transition tugs the main road's surface toward
the side road's junction height.** The main road — which should be the authority at the junction — gets bent by
the little stub. This is precisely the user-reported symptom.

## 4. Mechanism in code (verified by reading)

| Step | Location | Role |
|---|---|---|
| Collect | `UnifiedJunctionProfileBlender.PropagateConstraintsThroughShortSplines` — the `continuousNeighbors` loop (~line 1295) | For each short spline, for each **continuous** neighbour at the far junction, calls `CollectInfluencesFromCrossing` with a temp junction at the propagated elevation → fills `_propagatedMidSplineInfluences`. Logs `[PROPAGATE-CONTINUOUS]`. |
| Build taper | `ApplyUnifiedProfiles` ~line 94 | Phase A.5: builds `_splineClaimedZones` (only when `EnablePropagationOverlapTaper`) to taper the nudge inside contested anchor zones. Exists *only* to soften Step 5b. |
| Apply | `ApplyUnifiedProfiles` Step 5b ~line 197 → `ApplyPropagatedMidSplineInfluences` (~line 807) | Weighted-average nudge of each listed through-road cross-section toward the propagated elevation. **Writes `cs.TargetElevation`.** |

Key facts:
- **Not gated by the blend flags.** Steps 1.9/2/3 blend ramps (`BlendSplineProfile*`, `FinalSnap`) are all
  skipped on the no-blend path, but Step 5b runs unconditionally — so it is one of the few legacy mechanisms
  still actively moving roads on the no-blend path.
- `_propagatedMidSplineInfluences` is populated **only** by the `[PROPAGATE-CONTINUOUS]` branch. The endpoint
  propagation uses a separate local dict (`propagated`). So removing the continuous nudge does not touch
  endpoint propagation.
- `CollectInfluencesFromCrossing` is **shared** with the legitimate Step 5 (`ApplyMidSplineCrossingInfluences`,
  real X-crossings where both roads continue). **Keep it.**

## 5. Evidence — two reproductions + the A/B

### 5a. `_generated_terrain`, node 430808759 (J#312), log `…130207`
Through road **195** (prio 5001, 625 m) rides ~1.0–1.4 m below terrain at four of its five junctions (honest
cut, edges consistent). At J#312 — where a **21 m** side road (spline 158) connects — its centerline is spiked
to **198.28** (+0.55 above terrain). Log:
```
[PROPAGATE-CONTINUOUS] Constraint from Junction #313 through short Spline 158 (len=21.2m)
   → continuous Spline 195 (mid-spline influence, blend=28.8m, targetElev=198.28m)
```
J#313 is the side road's free dead-end; terrain there = 198.28. So the side road's far-end terrain was dragged
onto the main road. It only fires at the short-side-road junction, not at J#73 (3622 m) or J#92 (586 m) — which
is exactly why 195 is honest there.

### 5b. `franco_same_prio`, node 663313796 (J#201) — the clean A/B handle
T-junction, **equal priority** (both 5001), short stub **106** (16 m) into main road **100** (431 m).

| quantity | nudge **ON** (baseline) | nudge **OFF** (skipped) | Δ |
|---|---|---|---|
| through 100 centerline `roadZ` | 101.07 | **100.45** | **−0.62** |
| through 100 `delta` vs terrain | +2.41 | +1.79 | −0.62 |
| through 100 honest edges | ~99.90 | ~98.83 | — |
| harmonized | 101.07 | 100.45 | −0.62 |

**Verdict:** the nudge is real (it lifted the main road **0.62 m**, ~25 % of the float here), so "it does
nothing" is wrong — but it is a **minor** contributor; the dominant +1.6–1.8 m float is a *separate* defect (the
edge-desync / §2 affine cascade — see `2026-05-31-edge-elevation-desync-bug.md`). User confirmed short stubs
"way better" with it skipped.

## 6. Why remove rather than keep-gated

- It **contradicts the no-blend invariant** (never move the through road). Keeping it as a flag is "parameter
  hell" the user rejects.
- Its original job (extend a transition smoothly past a too-short road) is now done by **affine leveling** —
  a linear tilt over the whole road, no local ramp — which already handles short roads without tugging
  neighbours.
- It is unconditional legacy code on the no-blend path; leaving it invites exactly this class of regression.

## 7. Removal plan (surgical — do as one commit, TDD where tests exist)

**Remove (the continuous-road nudge + its dead support):**
1. `UnifiedJunctionProfileBlender`:
   - the `SkipPropagatedMidSplineInfluences` test const,
   - the `_propagatedMidSplineInfluences` field + the Step 5b apply block,
   - the `ApplyPropagatedMidSplineInfluences` method,
   - the `continuousNeighbors` collection branch inside `PropagateConstraintsThroughShortSplines`
     (the `[PROPAGATE-CONTINUOUS]` loop + the `if (farJunction.Type == Roundabout) continue;` guard that
     only precedes it; keep `continuousNeighbors`'s sibling `endpointNeighbors` branch),
   - the `_splineClaimedZones` field + the Phase A.5 build at ~line 94.
2. `JunctionHarmonizationParameters`: remove `EnablePropagationOverlapTaper` (grep showed no preset/DTO/UI use —
   confirm before deleting).
3. Delete `SplineClaimedZones.cs` (used only by the removed taper — confirm with rg).
4. Delete tests that exist only for the removed feature: `PropagationOverlapTaperTests.cs`,
   `SplineClaimedZonesTests.cs`, `SplineClaimedZonesNestedGuardTests.cs`.

**Keep (do NOT touch):**
- `ApplyMidSplineCrossingInfluences` (Step 5 — real mid-spline crossings).
- `CollectInfluencesFromCrossing` (shared with Step 5).
- The endpoint-neighbour propagation branch (`propagated` dict, `[PROPAGATE]`/`[PROPAGATE-BLEND]`) — separate,
  and inert on the no-blend path because it feeds the gated-off `BlendSplineProfile`. (Flag it in the
  "leftover hunt" doc for a later, separate decision.)

**Verify:** build + full terrain test suite green; regenerate `franco_same_prio`, confirm `[PROPAGATE-CONTINUOUS]`
and `[NO-BLEND TEST] SKIPPED` no longer appear and J#201 through-road stays ~100.45 (not 101.07).
