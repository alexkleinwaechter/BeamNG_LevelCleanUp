# Handoff — apply the no-blend technique to roundabout connecting roads

- **Date:** 2026-05-30
- **Branch:** `experimental/switch_off_blend_zones`
- **Predecessors (read first):**
  - `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md` — the no-blend technique, §1–§7 (source of truth).
  - `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-investigation-and-plan.md` — root-cause history.
  - Memory: `no_blend_zones_affine_leveling`, `blend_propagation_architecture`,
    `surface_model_junction_overlap`, and the "Roundabout Junction Preservation Bug (2026-02-15)" learning.

---

## HANDOFF PROMPT (paste to start the session)

> Continuing the no-blend-zones work on branch `experimental/switch_off_blend_zones`. The technique for T/Y
> junctions is built and committed; now extend it to **roundabouts**. Read
> `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md` first, then this file.
>
> **Goal:** roundabout **connecting roads** (the entry/exit stubs) should meet the roundabout ring and the
> ongoing through roads with the **same no-blend technique** we use elsewhere — the connector follows the
> settled surface (affine *linear* tilt, curvature preserved), banking is matched at the seam, and **no
> hermite/parabolic blend-zone elevation curve** is introduced. No grade clamps (user rejects them).
>
> **The pain point to stress:** entry/exit connectors are often **very short (~20 m)** and sit between the
> roundabout ring (fixed ~uniform Z) on one side and a **much longer ongoing spline** on the other. The short
> connector has to reconcile two strong, different constraints over a tiny length → today this produces
> **bumpy / unsmooth** elevation. A correct fix must stay smooth across the short connector AND not push a
> bump back into the long neighbor (constraints propagate through short segments — see
> memory `blend_propagation_architecture`).
>
> Use systematic-debugging (instrument → data → fix) + TDD. I run generation in the WinForms+Blazor GUI and
> share logs/screenshots; you cannot run it. Validate visually on a roundabout in `franco_same_prio` (or
> whichever preset has a clean roundabout — confirm which).

---

## Why this is a real gap (what's true today)

The no-blend pipeline is **gated `affineThroughActive`** (`blendOff && EnableAffineJunctionLeveling`, mode
`AffineJunctionTargetMode.ThroughRoad`) and runs as post-loop passes in `UnifiedRoadSmoother.SmoothAllRoads`:

1. **Affine ThroughRoad targeting** — `BuildEndpointTargetLookup` (ThroughRoad branch) +
   `ThroughRoadJunctionElevation.Compute`: terminating roads target the through road's Z; the through road is
   never targeted. Applied via `AffineJunctionLeveler.Apply` (a **linear** offset+tilt — preserves curvature,
   so it can never kink a road; it only changes absolute depth).
2. **§3 `RetargetTerminatingRoadsToSettledThrough`** — iterated Pass1/Pass2: recompute each junction's Z from
   the *settled* through road, write `HarmonizedElevation` (closes the junction-fill bump), re-level
   terminating endpoints onto it (closes the seam step).
3. **§4 `MatchTerminatingBankingToThroughSurface`** — warp each terminating road's near-junction cross-sections
   onto the through surface's **banking/slope** over a runoff zone (`SurfaceWidth ×
   BankingRunoffSurfaceWidthMultiplier`, default 3), smoothstep weight 1→0. **Banking ONLY — it must NOT write
   the centerline** (that was the `f2b4426` fix: writing the projected centerline re-introduced a blend-zone
   Z curve; the painted core surface is `TargetElevation + sin(bank)·lateral`, so only `BankAngleRadians`
   carries the twist).

**Roundabouts are explicitly EXCLUDED from §3 and §4.** Both methods early-`continue` on
`junction.Type is JunctionType.Roundabout or JunctionType.MidSplineCrossing or JunctionType.Continuation`
(see `UnifiedRoadSmoother.cs` — §3 `RetargetTerminatingRoadsToSettledThrough` and §4
`MatchTerminatingBankingToThroughSurface`). So connectors at roundabouts get **none** of the no-blend
treatment — they still go through the legacy roundabout path:

- `RoundaboutElevationHarmonizer` (Phase 2.6) sets the ring's uniform/blended elevation and a smooth transition
  for connecting roads (sets `UnifiedCrossSection.IsRoundaboutBlended` so Phase-3 tapering won't overwrite it).
- `UnifiedRoadSmoother.RestoreRoundaboutJunctions()` saves roundabout junctions before Phase-3
  `DetectJunctions()` (which `Clear()`s them) and restores them afterward, removing overlapping regular
  junctions within ~15 m (see the 2026-02-15 roundabout-preservation learning).

That legacy transition is itself a blend-zone mechanism — exactly the thing this branch removes everywhere
else. On a 20 m connector it's also the most fragile place for it.

> **VERIFY these symbol names at the start of the session** (some are from memory / mid-session reads, not
> re-confirmed): `RoundaboutElevationHarmonizer`, `RestoreRoundaboutJunctions`, the `IsRoundaboutBlended`
> flag, the exact `JunctionType` members, and which junction type the entry/exit *contributor* carries
> (is the connector endpoint an `Endpoint`/`TJunction` contributor against the ring, or a Roundabout
> contributor?). The fix design hinges on this.

---

## Phase 1 — instrument before touching anything

Mirror the existing `[NO-BLEND DIAG/OWN/PROFILE]` diagnostics for roundabouts. For each roundabout and each
connecting road, dump (one run):

- ring Z (and whether it's uniform), connector endpoint roadZ, ongoing-spline roadZ at the junction, and the
  raw terrain Z at each — i.e. **who agrees, who floats** (same logic that cracked the T-junction case).
- connector **length**, number of cross-sections, and its two endpoint constraints (ring side vs ongoing
  side). Flag connectors `< ~30 m`.
- per-cross-section `TargetElevation` along the connector + the ongoing spline near the seam, so a **bump**
  is visible as a non-monotone / kinked profile.
- `BankAngleRadians` and slope at the ring seam and the ongoing seam.

Run once, read it, decide where the bumpiness originates **before** proposing a fix:
- connector endpoints **disagree** (ring Z ≠ ongoing Z) → it's a *targeting* problem (the connector is being
  asked to span a step over 20 m).
- endpoints **agree** but the middle bulges → it's the *transition/blend* (RoundaboutElevationHarmonizer or a
  taper) fighting the affine linear tilt, or two constraints meeting with mismatched slope.
- bump appears in the **long neighbor**, not the connector → constraint propagated back through the short
  segment (`blend_propagation_architecture`).

---

## Phase 2 — likely shape of the fix (decide with data, don't pre-pick)

Treat the **roundabout ring as the "through road"** of the no-blend model:

- The ring has the authoritative settled Z (it's a closed loop at ~uniform or gently-varying elevation).
- A **connecting road is a terminating road** whose ring-side endpoint targets the ring Z, and whose other end
  is the genuine endpoint into the ongoing network.
- So the connector should be **affine-leveled (linear tilt) between {ring Z at the ring seam}** and
  **{settled ongoing-spline Z at the far seam}** — no curve, curvature preserved — then **§4 banking-matched**
  at *both* seams (ring banking on the ring side, ongoing-road banking on the far side), centerline left flush.

Candidate approaches to weigh:
- **(A) Stop excluding roundabouts from §3/§4** and feed roundabout contributors through the same passes, with
  the ring as the priority/through surface. Cleanest if the ring can be expressed as a "through" contributor.
- **(B) A dedicated roundabout pass** that reuses `AffineJunctionLeveler` + the §4 banking helper but knows the
  ring topology (two seams per connector, ring on one side). Lower risk of disturbing the ring's own loop Z.
- Either way, **retire / bypass** the legacy `RoundaboutElevationHarmonizer` transition on the blend-off path
  for connectors (it's the back-door blend zone here), the same way FinalSnap/Hermite were gated off — but
  keep the ring's uniform-Z computation.

**Short-connector stress (the user's headline):**
- A 20 m connector spanning a ring↔ongoing **Z step** *cannot* be smooth without either a steep-but-linear
  ramp or pulling the two ends together. Affine is linear (no bump) but a big step over 20 m = a steep grade —
  acceptable to the user (no clamps) only if the step is small. So the real lever is making the **ends agree**:
  drive the ongoing-spline seam Z toward the ring Z (settled-retarget, §3-style, *iterated* because the
  ongoing spline is long and terminates elsewhere), so the connector tilts over a small residual, not a step.
- Watch **propagation into the long neighbor**: re-leveling the ongoing spline to the ring must not bump *its*
  far junction (this is exactly why §3 iterates to convergence — reuse that pattern).
- Watch the **15 m roundabout-junction-restore radius** vs a 20 m connector — overlapping junction removal may
  be eating the connector's far junction. Confirm the connector still has a valid far-end target.

---

## TDD / tests to add (pure, network-level — no GUI)

Model on `BankingMatchToThroughSurfaceTests` / `RetargetTerminatingToSettledThroughTests`:
1. **Connector spans no step → stays flush** (ring Z == ongoing Z → connector centerline flat, no bump).
2. **Short connector + ring/ongoing step → linear ramp, no overshoot** (centerline monotone between the two
   ends; no mid-connector bulge; curvature preserved).
3. **Iterated settle** — re-leveling the ongoing spline to the ring must not move *its* far junction beyond
   tolerance (convergence), mirroring the §3 chained-case test.
4. **Banking matched at both seams**, centerline untouched (the `f2b4426` invariant: §4 adds bank only).
5. **Ring loop Z preserved** — the fix must not disturb the ring's own elevation.

---

## Validation handles

- Preset: confirm which has a clean roundabout (try `franco_same_prio`; otherwise pick one and record the OSM
  node / way of the roundabout + 2–3 connectors here).
- Grep tags to add: reuse `[NO-BLEND …]` style, e.g. `[NO-BLEND RAB]` for roundabout diagnostics.
- Acceptance = visual: connectors meet the ring and the ongoing roads with **no bump and no blend-zone dip**,
  short 20 m connectors included; the ring stays smooth; no new walls.

## Don't-forget pitfalls

- Affine is a **linear** tilt — it can't kink a road; fear of "ramps" from affine is unfounded (proven for T).
  Curves/bumps come from *centerline writes* (the §4 trap) or from *transition blends*, not from affine.
- No grade clamps (`feedback_no_grade_clamp`).
- The §2 absolute-depth residual (whole network sits in a shared cut under convex terrain) is **parked** — the
  roundabout work is about *meeting smoothly*, not absolute depth. Don't re-open §2 here.
- Cleanup still owed from §7: remove TEMP `[NO-BLEND]` diagnostics, the `EnableAffineJunctionLeveling` flag,
  and the TEMP hardcode in `TerrainMaterialSettings.razor.cs`. Add any new roundabout diagnostics under the
  same TEMP markers so they're removed together.
