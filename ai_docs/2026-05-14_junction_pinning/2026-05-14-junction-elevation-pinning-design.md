# Junction Elevation Pinning — Design Spec

**Date:** 2026-05-14
**Working branch:** `experimental/pin_junction_non_mesh`
**Background:** [2026-05-14-old-pipeline-junction-pinning-investigation.md](2026-05-14-old-pipeline-junction-pinning-investigation.md)
**Status:** Approved design. Ready for implementation plan.

---

## 1. Purpose

Adopt the analytical-pipeline rule — *"Junction Z is fixed before road smoothing runs and stays fixed for the rest of the pipeline"* — in the develop-style pipeline used by `experimental/pin_junction_non_mesh`. Roads that **end** at a junction (terminating contributors) are pinned to that Z and ramp into it. Roads that **pass through** a junction (continuous contributors at T-junctions, ring at a roundabout, mid-spline crossing) are exempt and slope across the pinned point untouched.

The continuous-road exemption is what distinguishes this attempt from the earlier multi-road anchoring experiment that produced ditch artefacts at junctions.

## 2. Scope

### In scope

- New phase between Phase 1.8 (junction detection) and Phase 2 (network smoothing): **Phase 1.9 — Junction Elevation Pinning**.
- Pinning for `Endpoint`, `TJunction`, `YJunction`, `CrossRoads`, `Complex`.
- Pinning writes both `HarmonizedElevation` **and** the terminating contributor's longitudinal `Slope` at the junction node — point + tangent, per Nguyen 2014 §6.1.
- Three downstream consumer touchpoints made aware of the pinned value.
- Three independently-toggleable feature flags:
  - `EnablePhase19JunctionPinning` — primary pinning behaviour (the whole feature).
  - `EnableHermiteGradeSkip` — AASHTO ≤ 0.5 % grade-skip rule on Hermite ramps (W2).
  - `EnableMaxGradeClamp` — AASHTO class-dependent max-grade clamp on Hermite ramp samples (W3).
- New validation-harness exporter (W1, Paper 4 metrics): per-junction Z-residual CSV, three-band heatmap PNG, `w`-test summary log.

### Out of scope (deferred — see §7 for trigger conditions)

- `MidSplineCrossing` — explicitly **not pinned**. The existing `ApplyMidSplineCrossingInfluences` step continues to handle these.
- Phase 2.6 (roundabouts) ordering change. Roundabout junctions are **not pinned** in this design; they remain harmonized after Phase 2 as today.
- Iteration-loop reduction from 3 → 1.
- Parabolic vertical ramps (Hermite retained).
- **Class-aware default `BlendDistanceMeters` (W4)** — listed in §7 with a concrete OSM-class-to-distance table proposal covering all OSM highway types (minimum 30 m).
- `JunctionBankingAdapter` audit (class may not exist on develop).
- `FinalSnapTJunctionEndpoints` removal — **explicitly NOT planned.** See §7.1 below for the corrected understanding: this step is a load-bearing 3D-surface-matching pass, not iteration-loop residue.
- Four substantial follow-ups from the literature read (F1–F4 in §7): C² continuity, hyper-polyline pre-merge, soft pin via point duplication, angular gate.

### Novelty notes (literature context, N1+N2)

- **N1 — Through-road exemption is novel relative to most prior art.** Wang 2011 (Paper 0), Wang & Shen ("GIS Data Based ..."), and Wang et al. ("Large-scale 3D Road Networks") all treat every junction node as a hard pin via "level assignment" or flat-polygon-at-mean-Z — none distinguish "passes through" vs "ends at". The closest match is Nguyen, Desbenoit, Daniel 2016 ("Realistic urban road network modelling"), whose hyper-polyline + seam-line/seamless taxonomy is structurally similar. Nguyen 2014 §6.1 sanctions point+tangent pinning of a terminator but does not have a through-road concept. Treat our continuous-road exemption as an **untested extension** — Step 2's R8 ditch-regression check is the first empirical validation of it.
- **N2 — `BlendDistanceMeters` terminology.** In our pipeline this is the **ramp length** along the terminating road from the junction node to where the natural Phase-2 profile takes over. In Wang 2011 Paper 1, the symbol `Ldis = 30 m` is the **radius of the flat polygon at the junction** — a different quantity. The numeric coincidence is just a coincidence. Reviewers should not conflate the two.

## 3. Architecture

### 3.1 New class

`BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs` — pure function over `(RoadNetwork network, HeightMap heightMap, double metersPerPixel, JunctionHarmonizationParameters parameters)`. Walks `network.Junctions`, sets `junction.HarmonizedElevation` and the terminating contributors' constraint slope. No side effects beyond those mutations.

### 3.2 Feature flags

Add to `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`:

```csharp
// W1 — primary pinning feature. Everything in §3.4 / §4 / §5 is gated on this.
public bool EnablePhase19JunctionPinning { get; set; } = false;

// W2 — AASHTO §4.1.5 grade-skip rule (Wang 2011 / Paper 0).
// When the natural Phase-2 grade and the pinned-junction grade differ by
// <= GradeSkipThreshold (default 0.5 %), skip the Hermite ramp on this leg
// entirely — the seam is invisible and the ramp adds noise.
public bool EnableHermiteGradeSkip { get; set; } = false;
public float GradeSkipThresholdPercent { get; set; } = 0.5f;

// W3 — AASHTO §4.1.5 class-dependent max-grade clamp (Wang 2011 / Paper 0).
// After Hermite ramp samples are placed, clamp any segment grade that exceeds
// the class-keyed maximum (freeway 3 %, rolling 5 %, mountainous 7–9 %).
// Belt-and-braces over R7 (slope kink in steep terrain).
public bool EnableMaxGradeClamp { get; set; } = false;
```

All three default off. `EnablePhase19JunctionPinning` flips to true (and the conditional branches at the four touchpoints get removed) only after Steps 1-3 pass on both validation maps (Step 4). `EnableHermiteGradeSkip` and `EnableMaxGradeClamp` stay as permanent toggles even after Step 4 — they are cheap belt-and-braces controls, not the main feature.

### 3.3 Integration call site

`BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.RunUnified(...)`, between Phase 1.8 and Phase 2:

```csharp
// Phase 1.8 — junction detection (existing)
DetectJunctions(network);

// Phase 1.9 — junction elevation pinning (NEW, flag-gated)
if (parameters.EnablePhase19JunctionPinning)
{
    JunctionElevationPinner.PinNetwork(network, heightMap, metersPerPixel, parameters);
}

// Phase 2 — network elevation smoothing (existing)
CalculateNetworkElevations(...);
```

### 3.4 Consumer touchpoints

Three places downstream of Phase 1.9 must respect pinned values when the flag is on. Code archaeology done during spec self-review revealed that the `useHarmonizedElevation` plumbing already exists for iteration 2+ refinement — the changes below are smaller than the investigation doc estimated.

| # | File | Method | Change when flag on |
|---|------|--------|---------------------|
| C1a | `UnifiedRoadSmoother.cs` | Call site at L760 | Change `BuildEndpointAnchorLookup(network, heightMap, metersPerPixel, reSmoothFromExisting)` so that `useHarmonizedElevation` is also `true` on iteration 1 (i.e. `reSmoothFromExisting || EnablePhase19JunctionPinning`). |
| C1b | `UnifiedRoadSmoother.cs` | `BuildEndpointAnchorLookup` (L901-981) | Make the `if (junction.Type != JunctionType.Endpoint) continue;` line at L924 conditional: skip it (allow non-Endpoint junctions through) when the flag is on. The existing L949 `if (!contributor.IsEndpoint) continue;` already filters to terminating contributors — no further change needed. The existing L928 branch (`useHarmonizedElevation && !float.IsNaN(...)`) already routes the anchor through `junction.HarmonizedElevation`. |
| C2 | `NetworkJunctionHarmonizer.cs` | `ComputeJunctionElevations` (L207) | Inside the `foreach (var junction in junctions)` loop, before the `switch (junction.Type)`, add: `if (!float.IsNaN(junction.HarmonizedElevation) && !junction.IsExcluded) continue;`. Pinned junctions skip the per-type handler entirely; iterative-refinement behaviour for unpinned junctions is unchanged. |
| C3 | `UnifiedJunctionProfileBlender.cs` | All assignments to `junction.HarmonizedElevation` (current lines: 407, 611, 784, 919, 971, 1400) | Wrap each assignment with `if (float.IsNaN(junction.HarmonizedElevation)) { junction.HarmonizedElevation = …; }` (or equivalent guard). When pinned by Phase 1.9, the blender's existing local-calc value is still used for the constraint it builds — but it must not overwrite the pin. Keep the existing `edgeCenterElev` consistency check (assert `edgeCenterElev ≈ HarmonizedElevation`, log on divergence — see R7 in §6). |

All four touchpoints branch only on the flag (C1a/C1b) or on the pinned-state NaN check (C2/C3); when the flag is off, no junction is pinned, every guard falls through, and behaviour is bit-identical to today.

## 4. Pin computation per junction type

For each junction visited by `JunctionElevationPinner.PinNetwork`:

| Junction type | `HarmonizedElevation` | Slope written | Terminating contributors anchored |
|---------------|----------------------|---------------|-----------------------------------|
| `Endpoint` | Terrain sample at junction position (current WI-6 behaviour, but written to `HarmonizedElevation` so consumers have one path) | 0 | The one endpoint |
| `TJunction` | Bilinear heightmap sample at junction XY (= what the continuous road would smooth to at that arc-length) | Continuous road's local longitudinal slope at the junction node (compute via the same logic as `UnifiedJunctionProfileBlender.CalculateSlopeAtIndex`) | Terminating roads only |
| `YJunction`, `CrossRoads`, `Complex` | See selector below | See selector | All contributors |
| `MidSplineCrossing` | **Skipped — `HarmonizedElevation` stays `NaN`** | n/a | None |
| `Roundabout` | **Skipped in this design** (Phase 2.6 still handles these) | n/a | None |
| `Continuation` (degree-2 OSM boundary) | **Skipped** — already a no-op in the blender per existing logic | n/a | None |

### 4.1 Multi-way selector (Y / X / CrossRoads / Complex)

```
let p1 = highest contributor.MaxPriority
let p2 = second-highest contributor.MaxPriority

if (p1 - p2) >= 1:
    # Sequential priority snap — Nguyen 2014 §6.1
    HarmonizedElevation = terrain sample at highest-priority contributor's centerline at junction
    Slope (for the highest-priority terminating contributor) = its local slope at junction
    Other contributors: pinned to HarmonizedElevation, slope = 0
else:
    # Width × priority-weighted average — analytical-pipeline B.5
    let w_i = contributor.Width * contributor.MaxPriority
    HarmonizedElevation = Σ(w_i * terrain_sample_i) / Σ(w_i)
    All contributors: pinned to HarmonizedElevation, slope = 0
```

**Code comment near the selector** (verbatim):

> If the selector produces visible artefacts at near-equal-priority junctions (e.g. all-Y at a residential CrossRoads where weighting drags Z toward an outlier), switch to **always sequential**: pick the longest-or-highest-priority contributor, pin to its terrain sample + slope, and let the others adapt. See `ai_docs/2026-05-14_junction_pinning/`.

### 4.2 Why the continuous-road exemption

The previous multi-road anchoring attempt anchored every contributor at every junction, including the through-road at a T-junction. Pulling the through-road's smoothed profile toward a single terrain sample at the junction created a kink (the "ditch artefact" referenced in `UnifiedRoadSmoother.cs:838-841`). With the exemption, the through-road's smoothing is untouched; only roads that already get a Phase-3 constraint get an anchor — just applied **before** smoothing instead of as a post-correction.

## 5. Implementation steps & visual tests

### Step 0 — Baseline capture + validation harness exporter

**Code surface (small)**
- Add a new exporter `JunctionPinningValidationExporter` in `BeamNgTerrainPoc/Terrain/Services/` that emits the W1 (Paper 4) metrics for every terrain run, gated on `ExportJunctionDebugImage` (existing flag). On every Phase 4 completion:
  1. **Per-junction Z-residual CSV** — for each junction, columns: `junction_id, type, position_x, position_y, pinned_z, terrain_z, max_contributor_z, min_contributor_z, mean_contributor_z, residual_pinned_minus_terrain, residual_max_minus_min, n_contributors`.
  2. **Three-band heatmap PNG** of the heightmap delta (modified − original): green pixels where `|Δ| < 0.2 m`, yellow `< 0.5 m`, red `≥ 0.5 m`. Same thresholds as Oude Elberink & Vosselman 2007 Fig 9.
  3. **`w`-test summary log** — at each pinned junction, sample the terminating road's tangent angle at the junction node and at `BlendDistanceMeters * 1.05` (just past the ramp's far end). Compute `w = |Δtangent_angle_deg| / σ_predicted`, where `σ_predicted = max(0.1°, expected slope change for class)`. Log mean, σ, max, and count of `|w| > 3`. A model that holds C¹ should give standard-normal-like distribution: 68 % `< 1`, 92 % `< 2`, < 1 % `> 3`.
  4. **±d quadratic-growth check** — at each Y/T junction, sample heightmap delta at `±{5, 15, 30, 60} m` along each leg. Log a row per leg with all four samples. Residual should grow ≤ quadratically (`Δy(d) ≈ 1e-4 · d²`, so d=30 → 9 cm; d=60 → 36 cm). Linear or step growth indicates a failing blend.
  5. **Aggregate stats per run** — single-line summary at end of log: `n_junctions, pin_residual_mean, pin_residual_sigma, pin_residual_max_abs, w_test_outliers_gt_3, red_band_pixel_count`. These are the regression-gate numbers tracked over commits.

**Baseline run**
With the exporter in place and `EnablePhase19JunctionPinning = false`, run terrain generation on `franco_same_prio` and one crossroads map of your choosing. Save into a `baseline/` folder:
- `unified_junction_harmonization_debug.png` (auto-emitted; controlled by `JunctionHarmonizationParameters.ExportJunctionDebugImage`)
- Generated `theTerrain.ter` (or a heightmap PNG export)
- `junction_residuals.csv`, `delta_three_band.png`, terrain-generation log

These are the reference artefacts every later step diffs against.

### Step 1 — Phase 1.9 skeleton + T-junctions only

**Code surface**
- Add `JunctionElevationPinner` class (§3.1) with handling for `Endpoint` and `TJunction` only. Y/X/CrossRoads/Complex left to Step 2 — they remain unpinned (`HarmonizedElevation = NaN` after Phase 1.9) and continue to go through the existing Phase 3 path until then.
- Add `EnablePhase19JunctionPinning` flag (§3.2), default off.
- Wire the four consumer touchpoints C1a, C1b, C2, C3 (§3.4). C1a is flag-gated. C1b, C2, C3 are NaN-guarded (fall through harmlessly when nothing is pinned).
- Iteration loop kept at 3.

**Visual test (W1 harness)**

1. Build + run on `franco_same_prio` with `EnablePhase19JunctionPinning = false`, `EnableHermiteGradeSkip = false`, `EnableMaxGradeClamp = false`. Diff against Step 0 baseline → behaviour must be bit-identical or sub-mm (validates no-op when off).
2. Same map, `EnablePhase19JunctionPinning = true`, W2/W3 still off. Save new debug image, heightmap, log, `junction_residuals.csv`, `delta_three_band.png`.
3. Compare against baseline using the W1 harness:
   - **Three-band heatmap diff:** at every T-junction, the red-band pixel count in a 60 m × 60 m crop around the junction should *not increase* vs baseline. Yellow may shift; red is the regression gate. (Paper 4 Fig 9 thresholds.)
   - **Per-junction residual CSV:** for T-junctions, `|residual_pinned_minus_terrain|` should be < 5 cm (we're pinning to a terrain sample, so the residual is the bilinear-vs-nearest-neighbour delta). `residual_max_minus_min` across contributors should be < 10 cm (terminating roads should converge to the same Z as the through-road at the node).
   - **`w`-test on terminator tangent:** mean `|w|` close to 0, σ ≤ 1.5, count of `|w| > 3` should be zero for T-junctions in this step (gentle terrain on `franco_same_prio`).
   - **±d quadratic-growth check:** for every terminating leg, the four samples at ±{5, 15, 30, 60} m should fit `Δy ≤ 1e-4 · d² + 5 cm`. Any leg failing this is a candidate Hermite-ramp bug.
   - **Aggregate stats:** `pin_residual_max_abs ≤ 0.20 m`, `w_test_outliers_gt_3 = 0`, `red_band_pixel_count` not greater than baseline.
   - **Through-road regression check:** extract heightmap samples along a through-road centerline → byte-identical or sub-mm to baseline (the through-road must remain untouched per the exemption).
   - **Phase 3 max-correction log:** iteration 1 should drop dramatically (target ≤ 5 cm); iterations 2-3 effectively no-ops.
   - **Debug PNG side-by-side (qualitative):** at every T-junction, the terminating road's legend elevation matches the through-road's elevation at the junction node.
   - **In-game (gold standard):** load the generated map in BeamNG.drive, drive across a T-junction → no bump, no ditch, through-road feels unchanged.
4. **Toggle W2 / W3 sub-tests:**
   - Re-run with `EnableHermiteGradeSkip = true`, others as in (2). Expect a small reduction in `red_band_pixel_count` on flat-terrain T-junctions (where natural and pinned grades are within 0.5 %). Phase 3 max-correction may drop slightly. No regressions on steeper T-junctions.
   - Re-run with `EnableMaxGradeClamp = true`, others as in (2). Expect `w_test_outliers_gt_3` to stay at 0 even if the Hermite naturally produced a kink, because the clamp would have caught it. On `franco_same_prio` (gentle terrain) this should be a no-op; the effect appears in steep-terrain maps (R7 deliberately tested in Step 3).

**Pass criterion:** all checks under (3) pass with `EnablePhase19JunctionPinning = true, EnableHermiteGradeSkip = false, EnableMaxGradeClamp = false`. The (4) W2/W3 sub-tests are characterization runs, not pass-gates — their job is to confirm the flags do what they say, not to fix a Step 1 failure. Step 1 failures should be root-caused in C1a/C1b/C2/C3.

### Step 2 — Multi-way junctions with selector

**Code surface**
- Extend `JunctionElevationPinner` to handle `YJunction`, `CrossRoads`, `Complex` using the selector in §4.1.
- Code comment about always-sequential fallback (§4.1) included verbatim.
- Same `EnablePhase19JunctionPinning` flag — no new flag.

**Visual test (W1 harness — same metrics as Step 1, applied to multi-way junctions)**

1. Run on the crossroads map (your pick) with all three flags off → save baseline (or reuse Step 0 baseline if same map).
2. Same map, `EnablePhase19JunctionPinning = true`, W2/W3 off → save artefacts.
3. Compare against baseline using the W1 harness, focusing on multi-way junctions:
   - **Three-band heatmap diff:** red-band pixel count in 60 m × 60 m crops around every X/Y/CrossRoads junction should *not increase*. **R8 ditch-regression gate** lives here — a new red blob centred on a junction = ditch artefact.
   - **Per-junction residual CSV:** `residual_max_minus_min` across contributors should be ≤ 10 cm at every multi-way junction (all arms agree on Z). The selector chose either sequential or weighted; either way every contributor was pinned to the same value.
   - **`w`-test on terminator tangent:** for *every* contributor at a multi-way junction (selector pins all of them as terminators), `|w| > 3` count should be small (< 5 % of multi-way pins). Higher counts suggest the selector picked the wrong reference contributor.
   - **±d quadratic-growth check:** as in Step 1.
   - **Aggregate stats:** `pin_residual_max_abs ≤ 0.20 m`, `red_band_pixel_count` not greater than baseline, `w_test_outliers_gt_3` < 5 % of total pinned junctions.
   - **Debug PNG side-by-side:** at every multi-way junction, all arms show the same `HarmonizedElevation` in the legend.
   - **Phase 3 max-correction log:** drops similarly to Step 1.
   - **In-game:** drive through the X → no asymmetric tilt; junction feels flat or matches the dominant road's slope.
4. **R8 ditch-regression specific test (Paper 4 §5.4 + §4.3.2):** for any multi-way junction where the three-band heatmap shows a *new* red blob centred on the junction:
   - **Cross-boundary neighbor exclusion fix:** verify the heightmap delta uses only samples whose source road is in the junction's contributor list. Samples from non-contributing nearby roads must be excluded — this is Paper 4's named fix for this exact artefact.
   - Log the junction position, contributor priorities, and selector branch chosen. Consider switching that junction's pin strategy to always-sequential per the §4.1 code comment.
5. **Toggle W2 / W3 sub-tests** as in Step 1 — characterization, not pass-gate.

**Pass criterion:** all (3) checks pass, zero R8 hits in (4). If R8 fires on > 1 % of multi-way junctions, treat as a Step 2 failure and root-cause in §4.1 selector.

### Step 3 — Risk validation pass (no new code unless a risk fires)

Walk through §6 risks using the artefacts from Steps 1-2. Each risk has a defined remediation if it fires.

### Step 4 — Default flag flip (deferred until Steps 1-3 pass cleanly)

Set `EnablePhase19JunctionPinning = true` as the parameter default and remove the conditional branches at the three touchpoints. Until this step, Phase 1.9 is opt-in.

## 6. Risk register (acting subset of investigation §8)

| ID | Risk | What to look for | If observed |
|----|------|------------------|-------------|
| **R4** | Short splines between two pinned junctions degenerate | Heightmap delta on a connector < 20 m between two pinned junctions | Confirm 5805bc0 (linear interp on short splines) handles it; otherwise extend |
| **R7** | C¹ kink at flat-zone / free-profile seam in steep terrain | Heightmap along a terminating road on > 4 % grade — visible slope kink where the Hermite ramp ends; W1 `w`-test outlier (`\|w\| > 3`) at the seam node | First mitigation: enable `EnableMaxGradeClamp` (W3) — AASHTO class-keyed max grade caps the visible kink. If insufficient: R9 mitigation #2 from investigation: cubic Hermite with measured natural-profile slope at the far end (`h00 + h10 · slope_far`). If still insufficient: F1 (quintic Hermite for C² at the seam) — see §7. |
| **R3** | Cross-material junctions disagree on pinned Z | All contributors at a multi-material junction must read the same `HarmonizedElevation` | Add an assertion in `JunctionElevationPinner` that fires and logs the junction id + per-contributor values |
| **R8** | Ditch artefact regression at multi-way | Depression at the junction in heightmap delta | Document repro; fall back to always-sequential for that junction type |
| **R7b** | Pinned Z vs continuous-road actual elevation mismatch | `edgeCenterElev` in C3 diverges from `HarmonizedElevation` by > 10 cm | Keep `edgeCenterElev` as the terminating-road's actual snap target (already proposed in C3); log the divergence so we can see if it accumulates |

Risks that are explicitly **not** addressed in this implementation (per §2 out-of-scope): R2 (`JunctionBankingAdapter`), R5 (Phase 2.6 reorder), R6 (`FinalSnapTJunctionEndpoints`). R1 (banking pre-calc) is expected to be subsumed by the existing edge-anchored constraint logic in `ComputeTJunctionConstraints`; revisit only if Step 1 surfaces edge-elevation drift > a few cm at banked junctions.

## 7. Deferred follow-ups (with trigger condition)

### 7.1 Existing deferrals

| Follow-up | Trigger to revisit |
|-----------|-------------------|
| Phase 2.6 reorder (move roundabout harmonization before Phase 1.9) | Visible roundabout regression after Step 2 |
| Iteration loop 3 → 1 | Step 1/2 logs consistently show iterations 2-3 are no-ops on multiple maps |
| Parabolic vertical ramps for motorway-class roads | R7 fires and W3 clamp + Hermite-mitigation #2 are both insufficient |
| `JunctionBankingAdapter` audit | Banking artefacts observed at pinned junctions |
| ~~Remove `FinalSnapTJunctionEndpoints`~~ — **withdrawn** | See note below |

**Note on `FinalSnapTJunctionEndpoints` (correction to investigation doc R6):** This function — reviewed against `UnifiedJunctionProfileBlender.cs:1703-1930` — does substantially more than the investigation doc credited it with. It is the project's only 3D-surface-matching step for terminating roads:

- Reads the **final post-loop** primary CS, including longitudinal slope at the junction node and the primary's lateral bank.
- For each terminating contributor, projects the road's **left and right edges** onto the primary surface and derives a target centre Z + target bank angle from the edge-pair delta — guaranteeing edge alignment with the primary surface, not just centreline alignment.
- Pass 1 (within `flatZone + transitionDist`) writes per-CS `TargetElevation`, `BankAngleRadians`, `LeftEdgeElevation`, and `RightEdgeElevation` from a full surface formula `Z = centre + slope·Dot(offset, tangent) + sinBank·Dot(offset, normal)`.
- Pass 2 (in the blend zone) propagates the drift measured at the snap-zone boundary with a Hermite `h00` decay so the correction tapers smoothly to zero, preserving the terrain-following transition.
- Has a specific code path for banked-primary cases (line 1808 onward) that deliberately skips the snap to avoid corrupting `BlendSplineProfile`'s edge-anchored values.

**Phase 1.9 pinning does NOT replace any of this.** Phase 1.9 writes a single scalar (`HarmonizedElevation`) per junction. The primary (through) road is exempt from anchoring, so its smoothed profile can still settle slightly across iterations as other parts of the network refine — and the terminating road's per-CS bank/slope/edges were built from iter-0 primary values. `FinalSnapTJunctionEndpoints` aligns the terminating road's whole 3D surface to the *final* primary surface, including lateral and longitudinal slopes at every edge.

Action: **keep `FinalSnapTJunctionEndpoints` indefinitely**, both for T-junctions and Roundabouts. Phase 1.9 may reduce the number of corrections it logs (because the junction point is more stable), but the per-CS edge/bank matching it performs remains necessary. The investigation doc's R6 reasoning ("primary surface no longer changes between iterations") was wrong — the junction *point* doesn't change, but the primary *surface around the junction* still settles.

### 7.2 W4 — Class-aware default `BlendDistanceMeters` (own follow-up step)

**Status:** Strongly motivated by Paper 0 (AASHTO `L_min = 2·V km/h`) and Paper 5 (six-class OSM-aligned taxonomy with AASHTO grounding). Deferred to its own step so Steps 1-3 don't conflate "does pinning work" with "is the blend length right."

**Trigger:** Whenever the user accepts that Steps 1-3 pass on at least one map with a wide road-class mix (motorway + primary + residential in the same generation).

**Proposed implementation: OSM-class lookup table inside `JunctionHarmonizationParameters`.** Minimum value is **30 m** (the current default and Wang 2011 `Ldis`); class-keyed values scale up from there based on AASHTO `L_min ≈ 2·V km/h`. All OSM `highway=*` tags covered, including the long-tail "not mentioned in earlier discussions":

| OSM `highway` value | Typical design speed (km/h) | `BlendDistanceMeters` |
|---------------------|------------------------------|------------------------|
| `motorway`, `motorway_link` | 100-130 | **120 m** |
| `trunk`, `trunk_link` | 80-110 | **100 m** |
| `primary`, `primary_link` | 70-90 | **80 m** |
| `secondary`, `secondary_link` | 50-70 | **60 m** |
| `tertiary`, `tertiary_link` | 40-60 | **45 m** |
| `unclassified` | 30-50 | **30 m** (minimum) |
| `residential` | 30-50 | **30 m** (minimum) |
| `living_street` | 20-30 | **30 m** (minimum, floor) |
| `service` | 10-30 | **30 m** (minimum, floor) |
| `road` (generic / unknown) | unknown | **30 m** (minimum, floor) |
| `track` | 20-40 | **30 m** (minimum, floor) |
| `busway`, `bus_guideway` | 50-70 | **60 m** (same as secondary) |
| `raceway` | n/a (purpose-built) | **60 m** (treat as secondary) |
| Anything not in the table | — | **30 m** (minimum, floor) |

Notes:
- **30 m is a hard floor.** Any class shorter than that may produce a visible step at the seam when natural grade differs from pinned grade. Even `service` and `track` keep the 30 m floor.
- **Footpaths, cycleways, pedestrian, path, steps, bridleway, corridor** — not in this table because they are not part of the road network in this pipeline. If they ever are, the floor still applies.
- **Per-spline override:** the existing `JunctionHarmonizationParameters.BlendDistanceMeters` field stays as a per-spline scalar; the class table populates the default at OSM-import time and the user can still override.
- **Short-spline interaction:** the 40 % cap from commit `b52f454` and the linear-interp fallback from `5805bc0` continue to apply on top of the class default. If a `motorway_link` is only 50 m long, its 120 m default gets capped to 20 m (= 50 m × 0.4) by the existing logic.
- **Validation:** re-run the W1 harness on a multi-class map; expect `red_band_pixel_count` to drop for primary/motorway segments (longer ramps, gentler grades) and stay flat for residential.

### 7.3 Tier-2 substantial follow-ups from literature read (F1-F4)

Documented for future sessions. Each has a specific trigger condition; none are implemented now.

| ID | Follow-up | Source | Trigger to revisit |
|----|-----------|--------|--------------------|
| **F1** | **C² (slope-difference) continuity at junction nodes via quintic Hermite or a global LS pass** — kills the flat-zone seam kink completely (we currently target C¹ only). Chen et al. 2007 pins point + tangent + tangent-rate via global least-squares. | Chen, Lo, Shao, Teo 2007 (Paper 3) §4 | R7 fires AND W3 clamp + Hermite-mitigation #2 are insufficient. Last-resort mitigation before parabolic ramps. |
| **F2** | **Hyper-polyline pre-merge + seam-line/seamless decision rule** — pre-merge same-name/importance/width/lanes/type chains into G¹ chains *before* Phase 1.9, then use Nguyen 2016's seam-line rule (equal-class-and-width pair → continuous through, any unequal → highest priority continuous, others terminate) instead of our priority selector. Structurally cleaner; touches the network builder, not just Phase 1.9. | Nguyen, Desbenoit, Daniel 2016 (Paper 2) §2, §4 | §4.1 selector visibly fails on near-equal-priority junctions AND switching to always-sequential is unsatisfactory. |
| **F3** | **Soft pin via point duplication in the LS objective** — duplicate the pinned sample point `k` times as soft constraints in the elevation smoother (LM weighted least squares) instead of hard Hermite ramp. Avoids C¹ kink without changing the curve shape. Substantial change to the smoother architecture. | Nguyen, Desbenoit, Daniel 2016 (Paper 2) §4 LSGA extension | R7 fires AND F1 quintic Hermite is insufficient OR we want to remove Hermite ramps entirely. |
| **F4** | **Angular gate for near-straight T-junctions** — if all incoming roads are within X° of a single line (e.g. T-junction where the through-road is nearly straight), skip pinning entirely; the through-road already provides correct Z naturally. Requires a defensible threshold (probably 170-180°). | XINGJIANG YU 2019 (OSM-Unity thesis) §3.4.4 | W1 `w`-test shows systematic outliers at near-straight T-junctions where the pin is unnecessary. |

### 7.4 N1 — Through-road exemption (continuous-road exemption is novel)

See §2 "Novelty notes". The continuous-road exemption is the structural mechanism that prevents the original ditch artefact (Wang-style anchoring of every contributor). Step 2's R8 check is the empirical validation. If R8 fires on multiple maps, the exemption rule may need refinement — possible refinements documented under F2 (seam-line decision rule) and F4 (angular gate).

## 8. Files that change

| File | Change | Lines (approx, today) |
|------|--------|-----------------------|
| `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs` | **New** class with `PinNetwork(...)` entry point | n/a |
| `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs` | **New** — W1 (Paper 4) validation harness: per-junction residual CSV, three-band heatmap PNG, `w`-test summary log, ±d quadratic-growth check rows, aggregate stats line | n/a |
| `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` | Add `EnablePhase19JunctionPinning` + `EnableHermiteGradeSkip` + `GradeSkipThresholdPercent` + `EnableMaxGradeClamp` | ~12 added |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | Phase 1.9 call site (between L1.8 `DetectJunctions` and L760 anchor build); call to `JunctionPinningValidationExporter` after Phase 4 (gated on existing `ExportJunctionDebugImage`); C1a (update `BuildEndpointAnchorLookup` call argument); C1b (gate the L924 early-out on the flag) | ~15 added near L760 + new call site at end of pipeline + ~3 changed at L924 |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs` | C2 — early-`continue` inside `ComputeJunctionElevations` foreach when `HarmonizedElevation` is non-NaN | ~3 added near L215 |
| `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` | C3 — guard the 6 existing writes to `junction.HarmonizedElevation` (lines 407, 611, 784, 919, 971, 1400) with `if (float.IsNaN(...))` so a Phase 1.9 pin is preserved; W2 grade-skip check at ramp-build entry (skip the Hermite if `\|natural_grade - pinned_grade\| ≤ GradeSkipThresholdPercent` and `EnableHermiteGradeSkip`); W3 max-grade clamp after Hermite samples are placed (gated on `EnableMaxGradeClamp`, AASHTO class-keyed table) | ~25 changed/added (12 for C3 + ~6 for W2 + ~7 for W3) |
| `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` | **No change** — `ApplyEndpointAnchoring` already accepts arbitrary anchor elevations | 0 |

## 9. Why this is safe to try

- **Three independent feature flags**, all default off:
  - `EnablePhase19JunctionPinning` (the main feature) — when off, no pin is ever set; the NaN-guarded consumer changes (C1b, C2, C3) are no-ops on every junction.
  - `EnableHermiteGradeSkip` (W2) — when off, every ramp is built normally; when on, only ramps with `|Δgrade| ≤ 0.5 %` are skipped.
  - `EnableMaxGradeClamp` (W3) — when off, ramp samples are not clamped; when on, only samples exceeding the class max grade are clamped.
- **Two new files + roughly 50 lines of net change** in existing files (up from ~30 in the original spec — the increase is the W1 exporter, W2 grade-skip, and W3 clamp logic).
- **No data-model or persisted-format changes.**
- **No UI surface changes.** The three flags are internal until Step 4 (only `EnablePhase19JunctionPinning` ever becomes a default-true public setting; W2/W3 stay as advanced toggles).
- **The "ditch artefact" failure mode is structurally prevented** by the terminating-only anchoring rule (existing L949 filter `if (!contributor.IsEndpoint) continue;` in `BuildEndpointAnchorLookup`), not by the flag.
- **W1 validation harness is independent of the pinning feature.** It runs on every terrain generation (gated on the existing `ExportJunctionDebugImage`), so Step 0's baseline capture and Steps 1-2's comparisons use the same code path. Disagreements between baseline and post-feature runs are real, not artefacts of a different measurement procedure.

## 10. References

### Project documents
- [2026-05-14-old-pipeline-junction-pinning-investigation.md](2026-05-14-old-pipeline-junction-pinning-investigation.md) — full design rationale, civil-engineering references, complete risk inventory.

### Source files touched
- [BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs](../../BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs)
- [BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs)
- [BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs)
- [BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs)
- [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs)

### Literature (read in full during spec self-review — see [examples_for_ai/internetsources/markdown/](../../examples_for_ai/internetsources/markdown/))
- **Nguyen, Desbenoit, Daniel 2014** — "Realistic road path reconstruction from GIS data" (`0_paper1124-final`). Source for §6.1 point+tangent pinning rule, G¹/G² hierarchy, sequential priority handling, class-aware blend distances.
- **Wang 2011** — "Automatic High-Fidelity 3D Road Network Modeling" (`0_Automatic_High-Fidelity_3D_Road_Network_Modeling`). PhD dissertation. Source for **W2** (AASHTO §4.1.5 0.5 % grade-skip rule) and **W3** (AASHTO class-keyed max-grade table 3-9 %).
- **Wang et al.** — "Large-scale 3D Road Networks" (`1_Automatic_Generation_of_Large-scale_3D_Road_Networ`). Source for flat-polygon-at-mean-Z junction model; confirms our sequential-priority branch matches established practice. **N2** terminology origin (`Ldis = 30 m`).
- **Nguyen, Desbenoit, Daniel 2016** — "Realistic urban road network modelling" (`2_Realistic_urban_road_network_modelling_from_GIS_data`). Source for **F2** hyper-polyline + seam-line/seamless decision rule and **F3** soft-pin-via-point-duplication trick.
- **Chen, Lo, Shao, Teo 2007** — "Automatic reconstruction of 3D road models" (`3_Automatic_reconstruction_of_3D_road_models_by_usin`). Source for **F1** C² continuity at junction nodes via quintic Hermite / global LS.
- **Oude Elberink & Vosselman 2007** — "Quality analysis of 3D road reconstruction" (`4_quality-analysis-of-3d-road-reconstruction-4e9e5jncjq`). Source for **W1** validation harness: three-band heatmap (0.2/0.5 m thresholds, §5.3 Fig 9), `w`-test (§4.3), ±d quadratic-growth model (§3.3), cross-boundary neighbor exclusion (§5.4).
- **Wang & Shen** — "GIS Data Based Automatic High-Fidelity 3D Road Network Modeling" (`5_GIS_Data_Based_Automatic_High-Fidelity_3D_Road_Network_Modeling`). Class taxonomy + AASHTO grounding for **W4** OSM class table.
- **XINGJIANG YU 2019** — "OSM-Based Automatic Road Network Geometry Generation in Unity" (`OSM-Based_Automatic_Road_Unity`). Source for **F4** angular gate heuristic.
- **TU Delft graduation plan 2024** (`6_5841089_P2_product`). No usable curve math; sketches OSM tag-based pinning rules (bridge/tunnel/embankment skip). Cross-checked, no spec impact.
