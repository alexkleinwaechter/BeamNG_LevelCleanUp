# No-Blend-Zones: junction ramps from road smoothing — investigation, design & plan

- **Date:** 2026-05-30
- **Branch:** `experimental/switch_off_blend_zones`
- **Status:** diagnosis complete; switches #3 and #2 implemented; **affine A/B implemented** (both target
  modes) behind a TEMP hardcode; awaiting visual A/B on `france_italy_8k`
- **Validation maps:** `france_italy_8k` preset (`d:\temp\TestMappingTools\__preset_france_italy_8k`), plus historical junction 20 / OSM 282534720 / j126

---

## 1. Goal

Generate roads on terrain derived from **real-world elevation data**. We do **not** want artificial
curvature ("blend zones") imposed on roads near junctions. We *do* want roads to meet at junctions
without kinks/bumps, using only honest surface-smoothing helpers. The eventual aim is that the
**junction blend-distance parameters have no effect on road elevation**, because they are hard to set
across roads of very different lengths.

## 2. Symptom

With the Phase-3 blend turned off, roads still show a **ramp / raised embankment** where a road meets a
junction (see screenshot in conversation: a terminating road climbs onto an embankment lip right before
the intersection). Turning off Phase-2 endpoint anchoring (#2) as well did **not** change the ramp.

## 3. The three mechanisms that bend road elevation near junctions

| # | Phase | Mechanism | File |
|---|-------|-----------|------|
| 1 | 1.9 | `JunctionElevationPinner` — sets `junction.HarmonizedElevation = raw terrain @ junction XY`, `IsPinned=true`. Endpoint + TJunction only. | `JunctionElevationPinner.cs` |
| 2 | 2 | Endpoint anchoring (WI-6) — exp-decay pull of road cross-sections toward the pin over `blendDistance`. | `OptimizedElevationSmoother.ApplyEndpointAnchoring` |
| 3 | 3 | `BlendSplineProfile` (Hermite) / `BlendSplineProfileParabolic` — the two ramp algorithms. | `UnifiedJunctionProfileBlender.ApplyUnifiedProfiles` |

## 4. Root cause (verified by code reading)

**The Phase-2 elevation smoother is an unconstrained low-pass filter with zero junction awareness.**

- `OptimizedElevationSmoother.CalculateTargetElevations` samples raw terrain along the road and runs it
  through `BoxFilterPrefixSum` or `ButterworthLowPassFilter(rawElevations, windowSize, order)`. It takes
  **no junction position / pin as input** — it just low-passes terrain.
- `france_italy_8k` config: `smoothingWindowSize=301`, `crossSectionIntervalMeters=0.5`,
  `useButterworthFilter=true`, order 4 → smoothing **radius ≈ 75 m**.
- A 75 m low-pass **cannot represent terrain features sharper than ~75 m**. Where the ground dips/rises
  into a junction, the smoothed road **glides straight over** the local junction elevation at the wrong Z.
- Phase 4 terrain blending + `RoadMaskBuilder` junction-center fill then bridge the gap between the
  gliding road and the real ground → **the visible embankment ramp**.

**Why the pin never fixes it:** the pin (#1) was only ever *applied* to the road by #2 (a weak ≤0.5
post-correction over `blendDistance`) and #3 (the blend). The smoother itself never targets the pin.
With #2 and #3 off, **nothing imposes the junction elevation on the road profile at all.**

**Why disabling #2 changed nothing visible:** #2 was a small nudge on top of a large smoothing error;
removing it leaves the dominant glide-over ramp untouched. (Butterworth also edge-pads its state to the
*raw* endpoint sample — [`OptimizedElevationSmoother.cs:646`] — biasing the endpoint toward raw terrain at
the last cross-section, not toward where the connecting road actually sits.)

### User thesis verdict
> "high `smoothingWindowSize` lets the spline overshoot at junctions; the smoothing target should be the
> pinned junction but it can't reach it."

**Correct in conclusion**, with one refinement: the smoother does not *target the pin and miss* — it
**never targets the pin at all**. It is a free low-pass. The wide window (needed to bridge DEM gaps/voids
for smooth roads) is fundamentally at odds with endpoint fidelity at junctions. Fix = decouple the two.

## 5. Phase 1.9 (`EnablePhase19JunctionPinning`) — current role

**Not obsolete.** It pins `HarmonizedElevation = raw terrain @ junction XY` for Endpoint/TJunction and sets
`IsPinned=true`, which **locks** the value against the harmonizer/blender (`if (!IsPinned)` guards
everywhere). Still consumed by **`RoadMaskBuilder.cs:253`** to fill the junction-center gap pixels at that
elevation, even with #2/#3 off. It is the foundation the **raw-terrain A/B variant** reuses.

**Known weaknesses (cleanup candidates):**
- Coverage gap: multi-way Y/X/Complex + MidSplineCrossing are left NaN by Phase 1.9; they get a
  *consensus weighted-average* from the harmonizer instead.
- **Inconsistency / "AI slop":** Endpoint/T → raw terrain; Y/X/Complex/MidCrossing → consensus average.
  Two different target philosophies for different junction types.

## 6. Changes already made on this branch

1. **`EnableHermiteJunctionBlend`** (new, default `false`) in `JunctionHarmonizationParameters`.
   Dispatch in `ApplyUnifiedProfiles` (both passes) is now:
   - `EnableParabolicJunctionBlend` → parabolic (wins if both true)
   - else `EnableHermiteJunctionBlend` → Hermite
   - else **neither** (no profile curvature).
2. **Gate #2 coupled to the blend flags**: in `UnifiedRoadSmoother.CalculateNetworkElevations`, endpoint
   anchoring is **skipped** for any spline whose params have both blend flags false. This also removes the
   only elevation consumer of `JunctionBlendDistanceMeters`.
3. Both flags default `false` → on this branch the default is "no blend, no anchoring." No JSON preset
   overrides them; 386 terrain tests green.
4. **Affine junction leveling implemented (§7-8):**
   - New flags `EnableAffineJunctionLeveling` (default false) + `AffineJunctionTargetMode` enum
     {`Consensus`, `RawTerrain`} (default Consensus) in `JunctionHarmonizationParameters`.
   - Pure math: `AffineJunctionLeveler.Apply(elev, dist, targetStart?, targetEnd?)` — affine offset+tilt,
     error spread over full length, curvature preserved. 6 unit tests (`AffineJunctionLevelerTests`).
   - Wiring in `UnifiedRoadSmoother.CalculateNetworkElevations`: per spline, when blend-off and affine
     enabled, apply affine leveling (using `BuildEndpointTargetLookup` + `ComputeConsensusElevation`)
     instead of #2 anchoring. Targets built once from pre-correction smoothed elevations.
   - 392 terrain tests green (386 + 6).
   - **TEMP A/B harness:** `EnableAffineJunctionLeveling = true` + mode hardcoded in
     `TerrainMaterialSettings.razor.cs` (~line 1138). Flip `AffineJunctionTargetMode` there and recompile
     to compare Consensus vs RawTerrain. **Must be removed / wired to UI+preset once a winner is chosen.**

## 7. Fix design — affine endpoint constraint (preserve shape, pin the ends)

Keep the heavy low-pass for the road **body** (bridges DEM gaps); add a per-spline **affine correction**
*after* smoothing so the profile passes exactly through a junction **target** at each end, with the error
spread over the **whole spline length** (not a local `blendDistance`), so no local ramp appears.

```
s[]   = smoothed elevations (current Butterworth/Box output)
d[i]  = distance along spline, L = d[n-1]
e0    = target_start - s[0]        (0 if start is a free/dead end)
e1    = target_end   - s[n-1]      (0 if end   is a free/dead end)
correction[i] = e0 + (e1 - e0) * (d[i] / L)     // affine: hits both ends exactly
s[i] += correction[i]
```

- Preserves the smoothed **curvature** exactly (only offset+tilt change).
- Added grade ≈ `e / L` → negligible on long roads; **`smoothingWindowSize` can stay high.**
- One-ended splines: distribute `e0` linearly to 0 at the far end (`correction[i] = e0·(1 − d[i]/L)`).
- Replaces #2 on the blend-off path (alternatives, not stacked).
- Hook site: `UnifiedRoadSmoother.CalculateNetworkElevations`, same per-spline loop as the current #2
  apply (~line 900), using a per-endpoint **target** lookup (sibling to `BuildEndpointAnchorLookup`).

## 8. A/B test — two target modes

Implement both target sources behind a selector so we can compare on the same map:

- **Mode A — Consensus (recommended):** target = priority/width-weighted average of the connecting roads'
  *smoothed* contributor elevations at the junction (computed from a pre-correction snapshot so road
  corrections don't couple). Guarantees all roads at the junction agree; denoised. This is essentially the
  harmonizer's `HarmonizedElevation` for non-pinned junctions, reused as a non-ramping constraint.
- **Mode B — Raw terrain:** target = Phase 1.9 `HarmonizedElevation` (raw bilinear DEM @ junction XY).
  Simplest; reuses existing pin. Risk: noisy / can sit in a DEM void.

### Proposed flags (to be added)
- `EnableAffineJunctionLeveling` (bool, default `false`) — master enable for the new correction.
- `AffineJunctionTargetMode` enum { `Consensus`, `RawTerrain` } (default `Consensus`).

### Tests to add
- Affine correction hits both targets exactly; interior curvature preserved (compare 2nd differences).
- One-ended spline: free end unchanged, junction end hits target.
- Consensus target = weighted average of contributors; both connected roads end at same Z (no step).
- Mode select wiring; defaults; no-op when master flag false.

## 9. Code-cleanup backlog (do NOT bundle into the A/B change)

- **TEMP A/B hardcode** in `TerrainMaterialSettings.razor.cs` (~1138) — remove once a winner is chosen;
  wire the chosen mode (or both flags) to UI + preset `JunctionHarmonizationSettings` DTO if kept.
- `EnableHermiteGradeSkip` + `GradeSkipThresholdPercent` — marked obsolete in source; remove with code.
- `EnableMaxGradeClamp` — marked obsolete; user rejects grade clamping (see memory
  `feedback_no_grade_clamp`). Remove with code.
- Phase 1.9 vs harmonizer target inconsistency (§5) — unify on one target philosophy once A/B picks a winner.
- If Consensus wins and #2/#3 stay off by default: evaluate retiring `ApplyEndpointAnchoring` (#2) and the
  Hermite/parabolic blend code paths, or keep them behind the flags for non-real-terrain workflows.
- `JunctionBlendDistanceMeters` — once affine leveling lands, document that it no longer affects elevation
  (still used by IDW terrain-blend taper + roundabouts).

## 9b. A/B results so far

- **Consensus → still ramps** (tested, screenshot). Root cause of *this* ramp: on real terrain the roads
  share one ground point, but their 75 m-SMOOTHED elevations disagree at the junction because each road
  averaged a different approach. Consensus = weighted middle of those disagreeing values → humps the low
  road up and the high road down to meet → a crown/ramp. Consensus targets a fiction, not the ground.
- **RawTerrain — fixed to sample ground directly** for ALL junction types (was falling back to Consensus
  for Y/X/Complex because Phase 1.9 only pins Endpoint/T — that made earlier RawTerrain logic a no-op on
  forks). Now `SampleTerrainBilinear` at junction center. This targets the real shared connect point.
  **Awaiting visual test.**
- **Falsification plan:** if RawTerrain ALSO ramps, the artefact is NOT the longitudinal target — it is the
  road floating above terrain along the whole approach → Phase-4 terrain-blend embankment. Next step then:
  instrument (dump road TargetElevation vs raw terrain vs target at the problem junction) and investigate
  `UnifiedTerrainBlender` / cross-section side-slopes, NOT another target tweak.

## 9c. ACTUAL root cause: `FinalSnapTJunctionEndpoints` (unconditional override)

After Consensus AND RawTerrain both ramped identically, code-tracing (not guessing) found the real cause:
**`UnifiedJunctionProfileBlender.FinalSnapTJunctionEndpoints`** runs at the very end of `SmoothAllRoads`
(after the whole iteration loop), gated ONLY by `shouldHarmonize` — NOT by the blend flags. For every
T-junction it snaps the terminating road's endpoint + flat zone + blend zone onto the primary road's
surface (`GetPrimarySurfaceElevation`), ramping over `GetEffectiveBlendDistance`. It is itself a blend-zone
ramp, and it silently overwrote EVERY upstream scheme (#2, #3, affine Consensus, affine RawTerrain) — which
is why none of them changed the picture and why "blend distance has no effect" was false.

**Fix:** gate it per terminating spline — skip when both blend flags are false
(`UnifiedJunctionProfileBlender.cs` ~2529, the `GetTerminatingRoads()` loop). 392 tests green.

This means the earlier failures were NOT the road float / not the Phase-2 target. With FinalSnap gated,
affine **Consensus** is expected to work: the terminating road tilts gently (over its full length) to meet
the primary at the junction, with no final re-ramp. TEMP mode set back to Consensus.

**Checked for other unconditional overrides on the no-blend path:** the remaining `TargetElevation =` writes
in the blender are inside the gated blend methods, or `ApplyEndpointTapering` (Step 6, gated by
`EnableEndpointTerrainSlopeMatch`, skipped by default). FinalSnap was the only one. Phase-3.5
`JunctionBankingAdapter` no longer exists as code (comments/docs only).

**RESUME AT:** rebuild app (mode = Consensus, FinalSnap gated), regenerate `france_italy_8k`, check the
T-junction. If a residual *step* (not ramp) remains because the primary road floats, that's the genuine
float question (§4) — only then consider lowering the through road / softening the embankment.

## 9d. Attempt (e) FALSIFIED — instrument-first, question the architecture

**Update:** gating `FinalSnapTJunctionEndpoints` on the no-blend path (§9c) was rebuilt and tested — the
ramp is **visually unchanged**, identical to attempts (a)–(d). That is now **five** root-cause guesses
that each tested green and moved nothing:
(a) Phase-3 blend off · (b) #2 endpoint anchoring off · (c) affine Consensus · (d) affine RawTerrain ·
(e) FinalSnap gated.

Five invariant failures means the ramp is almost certainly **not** the terminating road's longitudinal
elevation target at all — every lever we've pulled lives on that target. Per
`superpowers:systematic-debugging` Phase 4.5 (≥3 failed fixes ⇒ stop fixing, question the architecture)
and Phase 1 step 4 (multi-component system ⇒ instrument to find WHERE it breaks before any further fix):

**STOP guessing. Gather data.** A one-run diagnostic was added in
`UnifiedRoadSmoother.SmoothAllRoads` immediately before Phase 4 (terrain blending), grep tag
`[NO-BLEND DIAG]`. For every non-roundabout junction it logs: JunctionId, Type, world pos, raw-terrain Z
at center, `HarmonizedElevation`, `IsPinned`/`IsExcluded`, and per contributor:
`splineId · role (ENDPOINT/THROUGH) · priority · material · roadZ (CrossSection.TargetElevation) ·
terrainZ@pt · delta = roadZ − terrainZ`. `heightMap` at that point is still the raw input (Phase 4 writes
to `smoothedHeightMap`), so the per-contributor delta is the true road-float.

**The data decides the diagnosis (do not pre-judge):**
- Ramping junction's **Type** — is it actually a T, or a Y/X/Complex/MidCrossing? (FinalSnap & Phase-1.9 pin
  only ever touched Endpoint/T — a fork would explain why every T-targeted lever was a no-op.)
- Contributors **DISAGREE** (different roadZ at one junction) ⇒ a *step* is built (longitudinal-target problem).
- Contributors **AGREE but all delta ≫ 0** ⇒ the whole junction neighborhood, through road included, *floats*
  above terrain ⇒ Phase-4 / `RoadMaskBuilder` builds the embankment. No endpoint target can fix a through
  road that is never an endpoint. → then the fix lives in Phase 4 / float-reduction, NOT another target.

**RESUME AT:** user rebuilds the app (current branch state: blend off, affine Consensus on, FinalSnap gated)
and regenerates `france_italy_8k` ONCE; share the `[NO-BLEND DIAG]` block. Find the junction nearest the
buildings in the screenshots; read its Type and the agree-vs-float pattern. Only then design the fix.

## 9e. Sag decomposed (pre/post-correction profile) → fix implemented

The `[NO-BLEND PROFILE]` dump (snapshots the pure chain low-pass before the affine correction) decomposed
spline 55's sag at node 282534762 (s≈196): `terr=155.78, lowpass=154.57, final=153.52`. The 2.26 m sag is
**two roughly equal, separable causes**:
- **−1.21 m honest low-pass** — the wide filter follows the ±75 m terrain *mean* (the junction sits on local
  convexity). This is the smoothing we keep. Across roads it oscillates ± (splines 8/113/90 have `dRaw≈0`).
- **−1.05 m affine drag** — `dCorr` (final−lowpass) is a near-**constant** offset down the whole spline.
  Affine-leveling spline 55 to *its own* endpoint targets smears a uniform pull-down across the spline, and
  that lands on the **mid-spline through-junction**. Dragged roads (55,42,64,38) all show one-directional
  `dCorr`; honest roads show `dCorr≈0`. **Design flaw: affine treats every spline by its 2 endpoints; for a
  road that is THROUGH at a mid-junction, leveling to its distant endpoints corrupts that junction.**

Also confirmed by the **raw-DEM screenshot**: roads on the unsmoothed DEM meet cleanly → the ramp is purely a
smoothing-pipeline artefact, and this junction's ground is already clean (heavy smoothing is *degrading* it).

**Fix (user chose "kill affine bias + junction follows main road"), implemented via TDD, 399 tests green:**
- New `AffineJunctionTargetMode.ThroughRoad` + pure helper `ThroughRoadJunctionElevation.Compute`
  (`Terrain/Algorithms/`, 7 unit tests): junction Z = average of the THROUGH contributors' low-pass Z;
  fork/Y (no through) = mean of terminating ends; lone dead-end = NaN (no target → road stays on its profile).
- `BuildEndpointTargetLookup` (ThroughRoad branch): terminating roads target the through-road Z (so they tilt
  to meet it); the through road is never in `endpoints`, so it is **not dragged**. Also sets
  `junction.HarmonizedElevation = target` so the `RoadMaskBuilder.cs:253` junction-centre fill matches the
  meeting Z (else a centre bump remains after the profiles agree).
- TEMP hardcode (`TerrainMaterialSettings.razor.cs` ~1147) flipped `Consensus → ThroughRoad`.

Expected at J#78: through road stays ~154.5 (its low-pass), side road tilts to 154.5, centre fill 154.5 → no
step/ramp; residual is only the honest ~1.2 m smooth cut below point terrain. **Awaiting visual validation on
`franco_same_prio` (node 282534762).** Then: remove the 3 TEMP diagnostics + wire the mode to UI/preset.

## 10. Open questions

- Consensus weighting: priority-only, width-only, or both? (Through road should dominate a T-junction.)
- Should multi-way junctions get the same affine treatment, or stay on the harmonizer path during A/B?
- Convergence loop interaction: apply affine once vs per iteration (re-smooth path).
- Does the `RoadMaskBuilder` junction-center fill need to switch from raw pin → consensus target to match?
