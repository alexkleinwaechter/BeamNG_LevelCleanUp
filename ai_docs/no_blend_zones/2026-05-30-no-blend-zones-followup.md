# No-Blend-Zones — follow-up debug session plan

- **Date opened:** 2026-05-30
- **Branch:** `experimental/switch_off_blend_zones`
- **Predecessor:** `2026-05-30-no-blend-zones-investigation-and-plan.md` (root-cause investigation, the
  `ThroughRoad` fix, A/B history). Read that first.
- **Status of the main fix:** the junction **ramp is fixed** and validated visually ("way better") on
  `franco_same_prio` at node 282534762. This document lists the *remaining* issues to work, in priority order,
  each as its own systematic-debugging pass (instrument → data → fix). **Do not bundle them.**

---

## 0. Current state (what's done)

- Root cause of the ramp: the through/main road sits below terrain (≈half honest wide low-pass over convex
  terrain, ≈half a one-directional **affine drag**), and the side road + raw-terrain center-pin then ramp down
  to it. See predecessor §9c–§9e.
- Fix shipped this session (TDD, 399 tests green): **`AffineJunctionTargetMode.ThroughRoad`** — "junction
  follows the main road." Terminating roads target the through road's Z; the through road is never targeted;
  `junction.HarmonizedElevation` is set to that Z so the `RoadMaskBuilder` center-fill matches.
  - `Terrain/Algorithms/ThroughRoadJunctionElevation.cs` (+ `ThroughRoadJunctionElevationTests`, 7 tests)
  - `UnifiedRoadSmoother.BuildEndpointTargetLookup` (ThroughRoad branch + HarmonizedElevation write)
  - TEMP hardcode `TerrainMaterialSettings.razor.cs` ~1147: `AffineJunctionTargetMode.ThroughRoad`
- **Result at J#78:** road-vs-road step 3.31 m → 0.59 m; center fill 156.02 (raw terrain) → 152.35 (matches
  roads). Ramp gone.

## 1. Flag-architecture cleanup — fold `EnableAffineJunctionLeveling` into the blend-off path

**Decision (2026-05-30):** all new functionality is gated by `blendOff && EnableAffineJunctionLeveling`, read
in exactly one place (`UnifiedRoadSmoother.cs:1065,1078`). The flag is a **burden**:
- On the blend-off path, affine is the *only* thing that makes roads meet. `blendOff + affine off` produces
  **no junction correction at all** — i.e. the original ramp/float. That "off" state is broken, not useful.
- The blend flags already select the path; affine should be *implied* by blend-off, not a third orthogonal
  toggle. It currently rides on the C# default (`= true`) + the TEMP hardcode, not the preset DTO — so it's
  effectively always-on already.

**Action:**
- Remove `EnableAffineJunctionLeveling`; make affine leveling the implicit blend-off junction-meeting
  mechanism (the dispatch becomes: parabolic → hermite → else affine).
- Keep `AffineJunctionTargetMode`; once `ThroughRoad` is validated across more cases, collapse to it (or keep
  Consensus/RawTerrain only for the A/B record).
- Until removed: do not expose to users/presets; document `blendOff + affine off` as "no junction handling —
  diagnostic only."

## 2. Through road still affine-dragged + iteration compounding (largest residual)

**FRESH DATA post-§3+§4 (log 175134, 2026-05-30) — 21 suspect through-roads, worst offenders:**

| spline | len | dPoint mean (total cut) | dRaw mean (honest low-pass) | **dCorr mean (affine drag)** | dCorr min |
|---|---|---|---|---|---|
| 55 | 965 m | −3.23 | −1.79 | **−1.43** | ≈ −5.50 |
| 42 | 513 m | −2.93 | −1.48 | **−1.45** | ≈ −4.20 |
| 105 | 157 m | −1.66 | −0.93 | **−0.72** | −1.25 |
| 66 | 18 m | −1.74 | −0.03 | **−1.71** | −2.68 |

`dRaw` = the honest wide-low-pass cut over convex terrain (KEEP — user wants this). `dCorr` = the affine
correction (the §2 target — drive toward ~0 on through-carrying roads). §3+§4 did NOT touch `dCorr` (they fix
junction *meeting*, not absolute depth): the long through roads (55, 42) still carry ~−1.4 m mean affine drag,
locally −4 to −5.5 m. **spline 105 (`dCorr −0.72`/min −1.25) is the through road under the screenshot's
embankment walls.** spline 66 (18 m, through at BOTH ends → all drag, no honest cut) is the J#128 short-through
edge case from §3.

**Stale (pre-§3, log 144047):** spline 55 `dPoint −5.48`, `dRaw −4.24`, `dCorr −1.24` — superseded by the table
above; §3 + the user's side-road window=101 change already lifted the cut from ~5.5 m → ~3.2 m.

Two coupled causes:
- **(a) The through road is still affine-leveled at its OWN endpoints** (`dCorr` constant −1.24). The fix
  changed what *other* roads target, but spline 55 itself still tilts to its endpoint targets, dragging its
  middle (and the mid-junction) down.
- **(b) Iteration compounding.** `ThroughRoad` makes each junction follow the through road, which follows
  *its* endpoints' through roads — with no upward restoring force. The re-smooth-from-existing loop sinks the
  chain further each pass (Consensus sank slower because it averaged in the on-terrain terminating roads).

**Candidate fixes (discuss, don't pre-pick):**
- Do **not** affine-level a road at junctions where it is the *through* road; only tilt genuine terminating
  stubs — and only **once** (final iteration), not re-applied per re-smooth pass.
- Or: cap the cumulative affine correction, or re-derive targets from the *first-pass* low-pass each iteration
  rather than from the previous (already-corrected) values.
- Goal: drive `dCorr`→~0 on through-carrying roads, pulling `dPoint` from ~−3.2 m back toward the honest `dRaw`
  ~−1.8 m (the irreducible wide-low-pass-over-convex cut the user wants kept).

**Mechanism (code, 2026-05-30):** `ApplyAffineLeveling` (`UnifiedRoadSmoother.cs:1148`, helper `:1622`) tilts
**every** blend-off spline to its two endpoint targets via `AffineJunctionLeveler.Apply` — *unconditionally*,
including a road that is THROUGH at a mid-junction. That uniform offset+tilt IS `dCorr`, and it runs inside the
3-iteration re-smooth loop (`:253`, `maxIterations=3`; `reSmoothFromExisting` on iters 2–3) so it compounds.
Targets come from `BuildEndpointTargetLookup` (`:1135`), rebuilt each iteration from the *previous* (already
affine-corrected) `TargetElevation` — that re-derivation from corrected values is candidate-fix-3's lever.
NOTE: `AffineJunctionLeveler` is a *linear* tilt — it preserves curvature exactly (proven in §3), so skipping
it on through roads CANNOT kink them; it only changes their absolute depth. That removes the only fear about
candidate-fix-1.

**Instrument:** the `[NO-BLEND PROFILE]` dump already separates `dRaw` (low-pass) from `dCorr` (correction).
Add per-iteration logging if compounding needs to be seen pass-by-pass.

**ADDED 2026-05-30 (build+22 no-blend tests green, NOT a fix — Phase-1 evidence only):** per-iteration
logging now lands. **Why it was needed:** the end-of-run dump reads `_preCorrectionElevations`, which is
re-snapshotted *every* iteration (`CalculateNetworkElevations:1125`), so the dumped `dRaw`/`dCorr` only reflect
**iteration 3** — `dCorr` = the *last* single-pass correction, `dRaw` = the iter-3 *re-smoothed* low-pass that
has ALREADY absorbed iters 1–2's affine drag. The single-snapshot dump therefore **cannot distinguish (a) from
(b)** — compounding hides inside its "dRaw". New code (`UnifiedRoadSmoother.cs`, all TEMP `[NO-BLEND]`):
`_iterationSnapshots` field captures `(preAffine, postAffine)` per pass; the PROFILE dump now prints, per
suspect through road, one line per iteration:
`[NO-BLEND PROFILE]   iter=k/3 spline=… dRawIt(baseline-terr mean/min)=… dCorrIt(thisPass mean/min)=…`.
**Read it:** `dRawIt` drifting steadily more negative pass-over-pass ⇒ **compounding (b)** (the affine drag
folds into the next pass's low-pass baseline; iter-1 `dRawIt` is the truly honest cut). `dRawIt` stable +
`dCorrIt` the only nonzero term ⇒ **single-pass own-endpoint tilt (a)**. This line picks the candidate fix.

**VERDICT (log 180858, 2026-05-30): cause (b) compounding, conclusively — AND the table's "honest dRaw" premise
is FALSIFIED.** Per-iteration baselines (`dRawIt` = pre-affine low-pass vs terrain):

| spline | iter1 | iter2 | iter3 | honest cut? |
|---|---|---|---|---|
| 55 | **+0.01** | −1.15 | −1.79 | ~0 — entire −3.23 is affine drag |
| 42 | **+0.00** | −0.63 | −1.48 | ~0 |
| 105 | **+0.15** | −0.65 | −0.93 | ~0 |
| 66 | +0.33 | +0.41 | −0.03 | ~0 (short; baseline can't hold drag) |

Iter-1 baseline ≈ 0 on every through road: they sit **flush** with terrain at the honest low-pass. There is
**no wide-low-pass cut to keep** — the whole −3.2/−2.9/−1.7 m is affine drag, mostly hidden by compounding (the
final-dump "dRaw −1.79" is just iter-3's polluted baseline). **Mechanism (verified at J#107/J#108):** spline 55
is `THROUGH` at J#78/85/86/124/126/161 (never targeted there — the `IsEndpoint` filter already skips it), and
is targeted ONLY at its two genuine endpoints J#107 (snaps to through spline 43, −0.6 m) and J#108 (snaps to
through spline 42, which is **itself** sunk −3.09 m). So it's a **propagation cascade**: every road faithfully
follows its neighbor's endpoint Z, the neighbor follows its own, and the 3-pass re-smooth deepens the whole
coupled chain each pass. `AffineJunctionLeveler` is a pure `(e0+e1)/2`-mean endpoint-error tilt
(`correction(t)=e0+(e1−e0)·t`); the drag is entirely driven by how far the endpoint **targets** sit below the
road, and those targets are rebuilt from sinking neighbors every pass.

**FIX CHOSEN (user, 2026-05-30): "decide-once, reuse"** (over the literal "only final pass" — same on-the-ground
result, but cleanly unit-testable and lower-risk; the "skip through roads" lever was already in effect via the
`IsEndpoint` filter, so it was a no-op). Compute each junction's affine target ONCE from the honest first-pass
low-pass and reuse it every iteration, so no road ever chases its own (or a neighbor's) affine-sunk value →
the cascade can't compound; the whole coupled network stays at its honest-flush Z.

**IMPLEMENTED 2026-05-30 (TDD, 410 tests green — 2 new), then RENDERED, then DISCARDED — no meaningful gain.**
- The fix: `ThroughRoadJunctionElevation.Compute(junction, referenceElevations)` overload reading each
  contributor's Z from a `(splineId,localIndex)→Z` honest first-pass snapshot instead of live
  `TargetElevation`; `UnifiedRoadSmoother._honestReferenceElevations` captured once on iteration 0 and threaded
  into `BuildEndpointTargetLookup`'s `ThroughRoad` branch. Tests `ThroughRoadJunctionElevationTests` (+2).

**RENDER RESULT (log 184833) — decide-once did NOT fix the trench; reverted to `7835ef3`:**
- It *did* do what it was designed to: stop the **target-chasing**. The convergence loop dropped 3→2 iterations
  (targets stabilized) and the late-pass corrections shrank hard (spline 55 iter2 `dCorrIt` −0.64→**−0.25**;
  spline 42 −0.86→−0.28). So mechanism (a) (targets rebuilt from drifted values) is real and was removed.
- **But the roads stayed deep:** spline 55 `dPoint` −3.23→**−2.88** (~0.35 m), spline 42 −2.93→−2.61, spline 105
  −1.66→−1.53. Still a 2–3 m trench. The baseline **still marched within the 2 passes** (spline 55 `dRawIt`
  +0.01→−1.15).
- **Why it didn't work (the real §2 driver, now understood):** the dominant sink is NOT the inter-pass chasing —
  it is the **very first honest-target application** (spline 55 iter1 `dCorrIt` = −1.17, identical pre/post-fix).
  That −1.17 is geometry, not compounding: spline 55 must drop its J#108 endpoint to meet through-road spline 42,
  and **spline 42 itself sits ~2.75 m below terrain there** (J#108 render: spline 55 term roadZ 98.40 == spline 42
  through roadZ 98.36, `dZ +0.04` flush — they MEET correctly, just ~2.8 m under terrain @101.1). Affine then
  spreads that −1.17 endpoint drop over the whole 965 m body (its job — no local ramp). spline 42 is deep for the
  same reason recursively → a **coupled downward cascade with no upward restoring force**. Freezing the targets
  (decide-once) freezes them at the *already-cascade-sunk* iteration-0 values, so the network still settles deep.
- **Conclusion / lesson:** §2 is NOT fixable by changing *when/how the affine target is computed* — we tried both
  stabilizing the target source (decide-once) and §3 (settled retarget). The residual is the **fundamental
  no-blend conflict** (see memory "ARCHITECTURAL CONFLICT"): {roads meet at junctions} + {endpoint error spread
  over the whole body, no ramp} + {junction meeting-heights set by the wide low-pass over convex terrain} forces
  a collective sink. Junctions are flush (`[NO-BLEND OWN]` all 110 pairs `|dZ|<0.13`); the whole road network just
  sits in a shared cut below the convex terrain. Closing it would require either pulling the junction
  meeting-heights up toward terrain (= terrain-following/grade influence on through roads — conflicts with the
  no-curvature goal and the user's no-clamp stance) or accepting the cut. **Left for a future design call with the
  user — do not re-attempt target-source tweaks.**

**STATUS: reverted to `7835ef3` (working tree clean except this doc). NEXT SESSION = re-debug §4 (banking/twist),
per the user.** §2 parked with the understanding above.

**Confirmed at node 282534707 / J#126 (log 152336, 2026-05-30):** spline 55 is the THROUGH road here and
`[NO-BLEND PROFILE] spline=55 … dCorr(final-lowpass)=-1.24/-1.94/-0.50`. At the J#126 crossing the through road
sank from **155.57 (snapshot) → 154.83 (final) = −0.74 m**. This is the same spline-55 `dCorr −1.24` residual
the data above already flagged — and it is now tied to a *visible* symptom (see §3/§4). **This is the lever for
the 282534707 screenshot: if the through road doesn't sink after the junction Z is snapshotted, both the step
(§4) and the bump (§3) close without any snap.**

## 3. Junction pinned to the through road's **pre-sink** Z → step **and** bump (root cause of the 282534707 screenshot)

`BuildEndpointTargetLookup` (`UnifiedRoadSmoother.cs:1298`, called at `:1108`) runs **once, before** affine
leveling and the re-smooth iteration. In `ThroughRoad` mode it snapshots, from the through road's *current*
(= pre-affine) Z:
- `junction.HarmonizedElevation = ThroughRoadJunctionElevation.Compute(junction)` — drives the Phase-4
  junction-fill disk (`RoadMaskBuilder.cs:217`);
- the terminating road's affine **target** (same value) — drives where the side road tilts to.

Then §2 sinks the through road, and **neither value is recomputed**. Measured at J#126:

| quantity | snapshot (pre-affine) | final (post-affine) | stale gap |
|---|---|---|---|
| through road (spline 55) junction Z | 155.57 | **154.83** | −0.74 |
| `HarmonizedElevation` (fill disk) | 155.57 | 155.57 (never updated) | **+0.74 above through** → bump inside through-road width |
| terminating target (spline 64 roadZ) | 155.57 | 155.57 | **+0.73 above through** → step at the seam |

So the **bump inside the through-road surface** and the **step at the seam** are the *same* stale-snapshot
defect (not §5 mask-bleed — `maskWinner=through(ok)` at J#126; not primarily §4's snap).

**Action (ordering fix — closes step + bump together):** recompute the terminating affine target **and**
`HarmonizedElevation` from the through road's **post-affine** Z. Chicken-and-egg (the target is used to level
the terminating road, but the through road also levels): resolve by **two-pass affine** — (1) affine-level the
through roads, (2) recompute junction targets from their settled Z, (3) affine-level the terminating roads to
those. **Or** make §2's "never affine-level a through road" hold, so the single snapshot stays valid (then this
is moot). Decide jointly with §2 — they share the lever. Banking wedge (`dBank`) is left to §4.

**IMPLEMENTED 2026-05-30 (TDD, 403 tests green) — chosen approach: post-loop re-target (not §2).** User
preferred this over §2 ("don't affine-level through roads") after I proved the §2 fear unfounded (affine is a
*linear* tilt — `AffineJunctionLeveler` preserves curvature exactly, so neither §2 nor §3 can kink the through
road; the real difference is only the through road's *absolute* depth, which §3 leaves at −1.24 m — that's §2's
separate concern, judged visually next).
- `UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network)` — **ITERATED** Pass1/Pass2 (max 8
  passes, converge @ 0.01 m). Pass 1: recompute each eligible junction's `ThroughRoadJunctionElevation.Compute`
  from CURRENT Z → write `HarmonizedElevation` (closes bump) + collect terminating targets; Pass 2: re-apply
  `AffineJunctionLeveler` to terminating splines only (closes step).
- **Why iterate (found via log 154838):** a road that is THROUGH at junction X is often TERMINATING at its own
  endpoint Y. Pass 2 re-levels it at Y, tilting its body and moving X's crossing *after* X was pinned this pass.
  Single pass left a residual (real log: `dZ +0.73 → +0.29`; the through road sank a further 0.29 m when
  re-leveled at its ends). Iterating to convergence tracks the settling through road so the seam stays flush.
- Wired post-iteration-loop in `SmoothAllRoads`, gated `affineThroughActive`, **before** the `[NO-BLEND DIAG]`
  dump so the dump reflects the fix. Logs `[NO-BLEND] §3 retarget: re-leveled N …`.
- Tests: `RetargetTerminatingToSettledThroughTests` (5) — bump close, step close, through-never-moved,
  roundabout-skip, **chained through+terminating iterates until flush** (the log-154838 hard case).
- **Log 154838 (single-pass) result at J#126:** `harmonized 155.57→154.83` (bump gone ✓), spline 64 endpoint
  `155.57→154.83` (✓), but `dZ +0.73→+0.29` (through sank again). Iteration added after this → re-render to
  confirm `dZ≈0`.
- **VALIDATED (log 155825, iteration build + user lowered side-road smoothing window to 101):**
  - J#126/node 282534707: `harmonized=157.21 == spline-55 through roadZ=157.21`, **`dZ=+0.00`** (was
    +0.73→+0.29→0). Step + bump both closed.
  - **Network-wide:** all 130 `[NO-BLEND OWN]` pairs have `|dZ|<0.3` (essentially all `+0.00`); §3 iteration
    converged everywhere. Only residual `dZ`: J#128 `through=66 (len 18) term=65` = −0.17 (tiny 18 m through
    road terminating at both ends — near-converged, not chased).
  - **Absolute depth (§2) much improved as a side effect:** through-road `delta` vs terrain at J#126
    −4.45 m → **−1.79 m**. The window=101 change + less compounding lifted the cut. §2 may be satisfied —
    judge visually.
- **§4/§5 COLLAPSED INTO ONE ISSUE (post-§3):** now that `dZ≈0` everywhere, §5 (mask ownership) is **moot for
  elevation** — when both roads meet at the same centerline Z, who owns the overlap pixel writes the same Z.
  The 17 `maskWinner=TERMINATING(bug?)` flags all have `dZ=+0.00`. What remains is `dBank` (±3–4° at some
  junctions; +2.1° at J#126): terminating roads stay flatter than the banked through road → a cross-slope
  wedge across the width. §5 now only matters *through* §4 (it picks whose banking is painted in the overlap).
  **So the only remaining seam defect is the banking/cross-slope mismatch (§4), and §5's fix is "the through
  road should own the overlap so ITS banking is painted."**

## 4. Terminating road must match the target road's surface — slope **and** banking, over a zone

> **Scope narrowed by the 152336 log (2026-05-30):** at the reported junction the *Z step* is actually §3's
> stale-snapshot defect (the through road sank after the target was pinned), **not** a failure of the snap.
> Once §2/§3 land, §4 owns only the **banking / cross-slope wedge** residual — measured `dBank=+2.1°` at J#126
> (terminating flat `+0.1°` vs through banked `−2.0°`), `dSlope≈0`. So §4 ≈ "match the through road's banking
> across the terminating road's width," with longitudinal slope a non-issue at this junction.

**Reported (2026-05-30, screenshot at OSM node [282534707](https://www.openstreetmap.org/node/282534707),
`franco_same_prio`):** a *sloped* terminating road still leaves a visible step / elevation mismatch where it
meets the through road. The affine `ThroughRoad` fix only pins the terminating road's **centerline Z at the
single junction node** — it does **not** adopt the through road's longitudinal slope, nor its banking /
superelevation across the road width, nor blend over a zone. So a terminating road that approaches at any grade
mismatches just inside the node.

**Root cause (found this session):** the no-blend path **disables the old snap-to-surface mechanism**. That
mechanism is `UnifiedJunctionProfileBlender.FinalSnapTJunctionEndpoints` (`UnifiedRoadSmoother.cs:449`), and it
is skipped for blend-off splines at `UnifiedJunctionProfileBlender.cs:2536-2538`:

```csharp
if (termParams is { EnableParabolicJunctionBlend: false, EnableHermiteJunctionBlend: false })
    continue;   // terminating road no longer snapped to the through-road surface
```

On `develop`, `FinalSnap` did exactly what the screenshot now lacks: it projected the terminating road's
**left/right edges** onto the *current* through-road surface (`GetPrimarySurfaceElevation`), matching the
through road's **longitudinal slope and banking across the full width**, and applied it over a **snap zone**
(flat zone + transition + Hermite-decayed blend), not at a single point. The affine replacement is a strictly
weaker, single-point version of this.

**Two candidate fix directions (document both, don't pre-pick — decide in a debugging pass with logs):**
- **(A) Restore FinalSnap for the blend-off path.** Re-enable the edge-projection snap (slope + banking, over
  the zone) for blend-off splines — `develop` already proved it works. Risk: it was disabled *because* it
  re-creates a junction ramp on the blend-off path (see the skip comment) and is itself a blend-zone mechanism;
  would need to be reconciled with affine `ThroughRoad` so the two don't fight or double-count. Also watch the
  known `FinalSnap` regression (dead-end spikes via wrong `IsSplineStart` reference — see memory
  `terrain_wall_bug`).
- **(B) Extend affine leveling to do the snap natively.** Keep affine `ThroughRoad` but, instead of pinning a
  single centerline Z, fit the terminating endpoint cross-sections to the through-road **surface plane** (slope
  + bank) and ramp that over a short zone — replacing `FinalSnap`'s role without re-introducing a separate
  blend mechanism. Risk: more new code; must not re-introduce the wide low-pass drag §2 is fighting.

**CHOSEN (2026-05-30, after §3 shipped + log 155825): a NEW standalone post-loop method (≈ direction B, but its
own method, not folded into §3).** Direction A is dead — `FinalSnap` explicitly *skips* banked primaries
(`UnifiedJunctionProfileBlender.cs:2611` `if (hasPrimaryBanking) continue;`), deferring them to
`BlendSplineProfile`, which is off on the blend-off path. So nothing warps the terminating road's banking →
the twist. **Root cause = a GAP, not fighting code** (verified: `NetworkJunctionHarmonizer.ComputeTJunctionElevation`
*does* run on blend-off and calls `JunctionSurfaceCalculator.ApplyEdgeConstraints`, but that only sets the
orphaned `Constrained*EdgeElevation` fields — which the renderer never reads — plus a `TargetElevation` that §3
overwrites; it never touches `BankAngleRadians`. Clean that slop later per §7, it is not the cause).

Design — `UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network)`:
- Slot: in `SmoothAllRoads`, immediately AFTER the §3 `RetargetTerminatingRoadsToSettledThrough` call, inside
  the same `if (affineThroughActive)` block → runs last; nothing re-flattens before Phase-4 paints from
  `BankAngleRadians`.
- Per T-junction (skip excluded/Roundabout/MidSplineCrossing/Continuation/Endpoint): `primary` = highest-prio
  CONTINUOUS contributor (skip if none — fork/Y has no through surface); `primarySlope =
  JunctionSurfaceCalculator.CalculateLocalSlope(throughSections, junctionIdx)`.
- Per TERMINATING contributor: `runoff = terminatingCS.SurfaceWidth × BankingRunoffSurfaceWidthMultiplier`
  (new param, default 3f, not-for-UI). Walk the terminating road's CSes inward from the junction endpoint up to
  `runoff` m; `weight = smoothstep(1 − dist/runoff)` (1 at junction → 0 at zone end); `(L,R,C) =
  JunctionSurfaceCalculator.CalculateFullSurfaceFollowingConstraintsClamped(cs, primaryCS, primarySlope, weight,
  primaryHalfWidth)`; write `TargetElevation=C`, `LeftEdgeElevation=L`, `RightEdgeElevation=R`,
  `BankAngleRadians=asin(clamp((R−L)/width,−1,1))`.
- Geometry (perpendicular T): the projection makes the terminating *bank* pick up the through *longitudinal
  slope* (its edges spread along the through tangent) and its *centerline* near the junction pick up the through
  *bank* (it moves along the through normal). Clamping bounds banking/slope to the through half-width (anti-spike).
- Interaction: projects onto the SAME through surface §3 pinned, so `dZ` stays ≈0 and now bank-matches; at
  weight 0 returns natural → smooth handoff to §3's affine result.
- TDD: (1) bank-from-slope (through sloped 0.1, flat → terminating endpoint bank ≈ asin(0.1)); (2) runoff decay
  (zone-end CS unchanged, endpoint matched); (3) no-through junction skipped; (4) multiplier respected.
- **Validate at node 282534733 (way 169265618)** — the screenshot twist.

**IMPLEMENTED + COMMITTED `7835ef3` (2026-05-30 17:36; TDD, 408 tests green — §3+§4 9 tests re-verified green
on that commit). STILL NOT visually validated.** Test 1
(geometry) was driven RED→GREEN alone first to confirm `asin(slope)` before the rest — passed exactly.
- `UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network)` — as designed above; wired after the
  §3 retarget in the `if (affineThroughActive)` block, before the `[NO-BLEND DIAG]` dump. Logs `[NO-BLEND] §4
  banking match: warped N cross-section(s)`.
- Param `BankingRunoffSurfaceWidthMultiplier` (default 3f) on `JunctionHarmonizationParameters`.
- Tests: `BankingMatchToThroughSurfaceTests` (4).
- **Verification is the log line + VISUAL at 282534733 — NOT the `dBank` metric.** `dBank` (term bank − through
  bank) stays non-zero by design: for a perpendicular T the terminating *bank* matches the through *slope* and
  the terminating *centerline* matches the through *bank* (orthogonal axes), so equal bank angles are not the
  goal. Don't chase `dBank→0`.
- **VALIDATED + DONE (2026-05-30, log 175134).** §4 ran (`warped 3424 cross-section(s)`). At node 282534733 /
  way 169265618 (J#84 TJunction) the two seam cross-sections are flush in BOTH Z and banking: spline 43 end
  `roadZ=136.58 bank=−5.4°` vs spline 105 start `roadZ=136.58 bank=−5.4°`, edges coincident ~1cm (`dZ=0`,
  banking matched). Network-wide `[NO-BLEND OWN]` dZ all ≈0. User visual sign-off "looks and feels okay."
  Committed `7835ef3`; no runoff tuning needed. **Next: §2** (spline 105 THROUGH −2.01m below terrain, dCorr
  mean −0.72 / min −1.25 → the background embankment walls in the screenshot).

**Instrument (added 2026-05-30, build+399 tests green):**
- `[NO-BLEND DIAG]` contributor lines now also carry `slope=` (longitudinal grade m/m, central-difference),
  `bank=`(deg), `Ledge=`/`Redge=` (left/right edge Z), `len=`(spline length). Compare the THROUGH row vs each
  ENDPOINT row at the same junction.
- `[NO-BLEND OWN]` line per through×terminating pair: `dZ` (centerline mismatch), `dSlope`, `dBank`. **All
  three ≈0 ⇒ terminating sits flush; large `dZ`/`dSlope` ⇒ the §4 step.** (See §5 for the `maskWinner` field
  on the same line.)
- `[NO-BLEND T-SNAP] SKIPPED FinalSnap: spline=… junction=#… ` fires once per blend-off terminating road —
  **confirms the develop-era snap is being skipped** (direction-A premise). Grep count = how many junctions
  lost the snap.
- Files: `UnifiedRoadSmoother.cs` (DIAG/OWN dump + `ComputeLongitudinalSlope` helper),
  `UnifiedJunctionProfileBlender.cs:~2536` (T-SNAP skip log). All under TEMP `[NO-BLEND]` markers — remove per §7.

## 5. Terminating road bleeds into the through-road **surface width** → bumpy footprint

**Reported (same screenshot):** inside the through road's own paved width, near the junction, the surface is
**bumpy** — the terminating road is writing elevation into pixels the through road should own. The through road
must own its footprint flush; a terminating contributor must not paint bumps inside it. **Not allowed.**

This is a **mask-ownership** issue, separate from §4's profile match. The intended guard is
`EnableSurfacePriorityOverride` (default **true**, `JunctionHarmonizationParameters.cs:156`): in Pass 1 of
`RoadMaskBuilder.RasterizeSplinePolygons`, the higher-priority spline wins a contested pixel via
`ContestedPixelResolver.CompareForOverlap` (`ContestedPixelResolver.cs:73`). **But on `franco_same_prio` the
two roads have equal `Priority`** (the preset name is literal), so the cascade falls through to
`TotalLengthMeters`, then `SplineId` — which can let the *terminating* road claim part of the through-road core
and stamp its (floating) elevation there, producing the bump.

**Leads to check (in a debugging pass):**
- Confirm at this junction which spline actually wins the contested pixels (log the resolver outcome for the
  overlap), and whether the tiebreak is going the wrong way under equal priority.
- Consider a junction-aware ownership rule: the **through/continuous** road should win its surface-width pixels
  over a *terminating* contributor regardless of length/ID (a fourth cascade tier, or seed Pass 1 from the
  through road first). Cross-reference memory `surface_model_junction_overlap` (overlap needs seamless/seam-line
  blending, not first-writer-wins) and the Pass-2 `useSurfaceWidthOnly: false` first-writer path
  (`RoadMaskBuilder.cs:180,203`).
- Even with correct ownership, the bump may also be the terminating road's *floating* elevation leaking via
  Phase-4 embankment — so this interacts with §2/§4 (a snapped terminating surface has nothing bumpy to leak).

**Instrument (added 2026-05-30):** the `[NO-BLEND OWN]` line (see §4) ends with the **analytic** ownership
prediction — `prio th=/te=`, `len th=/te=`, `decidedBy=priority|length|splineId`, and
`maskWinner=through(ok)|TERMINATING(bug?)|tie`. This is the *pure* `ContestedPixelResolver.CompareForOverlap`
cascade (geometric + deterministic), so it predicts who wins contested surface pixels without per-pixel
logging. **If `maskWinner=TERMINATING(bug?)` at this junction, §5 is confirmed and `decidedBy` tells you which
tier mis-decided** (expect `length` or `splineId` under equal priority). *Only if* the prediction says
`through(ok)` but bumps persist do we escalate to the heavier per-pixel / owner-band dump inside
`RoadMaskBuilder` (post-Pass-2, sample `splineOwner[,]` over the through road's surface band near the junction).

## 6. `EnableEndpointTerrainSlopeMatch` interaction + other quirks

`EnableEndpointTerrainSlopeMatch` (default true) drives dead-end terrain-slope matching and skips Step 6
endpoint tapering. Verify it doesn't fight the affine/ThroughRoad path (e.g. at dead-ends, or where a spline is
both a dead-end and a junction contributor). Plus the other cases the user will point at — capture each here as
encountered.

## 7. Cleanup checklist (after the above are resolved)

- Remove the three TEMP diagnostics in `UnifiedRoadSmoother.SmoothAllRoads` / `CalculateNetworkElevations`:
  `[NO-BLEND DIAG]` junction dump, `[NO-BLEND PROFILE]` suspect-road dump, and the `_preCorrectionElevations`
  snapshot field + its population.
- Remove the TEMP hardcode in `TerrainMaterialSettings.razor.cs`; wire the chosen mode to UI + preset
  `JunctionHarmonizationSettings` DTO.
- Remove `EnableAffineJunctionLeveling` per §1; collapse `AffineJunctionTargetMode` if appropriate.
- Carry forward the obsolete-flag removals already noted in the predecessor §9 backlog
  (`EnableHermiteGradeSkip`, `EnableMaxGradeClamp`, etc.).
- Confirm `JunctionBlendDistanceMeters` no longer affects elevation; document remaining (IDW/roundabout) uses.

## Validation map / handles

`franco_same_prio` preset. Primary junction: OSM node **282534762** (J#78, T-junction; through=spline 55
ways 25900767/62/66/64/767132536, side=spline 40). Second handle (§4/§5 sloped-terminating + width-bleed):
OSM node **282534707** (J#126; through=spline 55, terminating=spline 64). Grep logs for `[NO-BLEND DIAG]` /
`[NO-BLEND OWN]` / `[NO-BLEND PROFILE]` / `[NO-BLEND T-SNAP]` / `[NO-BLEND] §3 retarget`.
The user runs generation (WinForms+Blazor GUI) and shares logs/screenshots; the agent cannot run it.

---

## HANDOFF PROMPT (paste to resume if context is lost)

> Continuing the no-blend-zones work on branch `experimental/switch_off_blend_zones`. Read
> `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md` first (esp. §2, §3, §4, §5).
>
> **State:** §3 (junction Z) COMMITTED (`9a0c4da`) + visually validated (log 155825: `dZ≈0` network-wide,
> cut shallower). §4 (banking match) IMPLEMENTED, **408 tests green, NOT committed, NOT visually validated.**
> - §3 = `UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough` (iterated Pass1/Pass2 post-loop, gated
>   `affineThroughActive`). Tests `RetargetTerminatingToSettledThroughTests` (5).
> - §4 = `UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface`, called right after §3 in the same
>   gated block. Per T-junction it warps each terminating road's near-junction CSes onto the through road's
>   tilted surface plane over a runoff zone (= terminating `SurfaceWidth` × new param
>   `BankingRunoffSurfaceWidthMultiplier`, default 3), weight smoothstep 1→0, via
>   `JunctionSurfaceCalculator.CalculateFullSurfaceFollowingConstraintsClamped`. Reused-not-revived (FinalSnap
>   skips banked primaries; the old constraint code is orphaned slop — see §4/§7). Tests
>   `BankingMatchToThroughSurfaceTests` (4). Logs `[NO-BLEND] §4 banking match: warped N`.
> - Diagnostics `[NO-BLEND DIAG/OWN/T-SNAP]` + `ComputeLongitudinalSlope` live in `UnifiedRoadSmoother.cs`. All
>   TEMP, removable per §7.
>
> **I am waiting on a render WITH §4.** User runs `franco_same_prio` (WinForms GUI — I cannot run it) and gives
> a log path. **Check node 282534733 / way 169265618** (the twist screenshot): is the cross-slope twist where
> the side road meets the banked main road gone? Grep `\[NO-BLEND\] §4 banking match` to confirm it ran.
> **Do NOT use the `dBank` metric** — for a perpendicular T the terminating *bank* matches the through *slope*
> (orthogonal axes), so `dBank` stays non-zero by design. Verification = the log line + the visual. If the
> twist is gone → commit §4 (message like the §3 commit). If runoff looks too abrupt/long, tune
> `BankingRunoffSurfaceWidthMultiplier` (≥3).
>
> Remaining backlog (own passes, don't bundle): §2 through-road absolute depth (~−1.3 m, much improved by §3 +
> the user's side-road smoothing window=101 change — judge visually); §5 mask-bleed is moot for elevation now
> that `dZ≈0`, only matters via §4 banking ownership at short-through junctions (e.g. J#128); §7 cleanup
> (remove TEMP diagnostics + orphaned `ApplyEdgeConstraints` slop + the `EnableAffineJunctionLeveling` flag).
>
> Use systematic-debugging + TDD. The user makes the design calls (no grade clamps; affine = linear tilt, so
> removing/keeping it never kinks the through road — only changes its absolute height; no parameter hell).
