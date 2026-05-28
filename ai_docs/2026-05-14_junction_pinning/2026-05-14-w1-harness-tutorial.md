# W1 Validation Harness — Reading & Interpretation Guide

**Date:** 2026-05-14
**Audience:** Anyone diffing a post-Phase-1.9 run against the Step 0 baseline, or debugging junction-pinning behaviour.
**Producer:** `BeamNgTerrainPoc.Terrain.Services.JunctionPinningValidationExporter` (Tasks A1–A5)
**Gating:** Files are written whenever any road material has `JunctionHarmonizationParameters.ExportJunctionDebugImage = true`. They land next to the existing `unified_junction_harmonization_debug.png` inside `<level>/MT_TerrainGeneration/`.

---

## 1. What you get per terrain-generation run

Four new files plus one new log line, all next to `unified_junction_harmonization_debug.png`:

| File | Type | Per-row unit | One row per |
|---|---|---|---|
| `delta_three_band.png` | image | pixel | heightmap pixel |
| `junction_residuals.csv` | CSV | junction | non-excluded `NetworkJunction` |
| `w_test_summary.csv` | CSV | contributor | terminating contributor (`IsEndpoint`) at a junction whose `HarmonizedElevation` is non-NaN |
| `quadratic_growth.csv` | CSV | contributor | same selection as `w_test_summary.csv` |
| Log line `W1 validation: n=…, pinResMean=…, …` | text | run | one per terrain-generation pass |

CSVs are written with `CultureInfo.InvariantCulture` — decimal `.` always, regardless of OS locale. The log line uses current culture (on a DE-locale Windows box you'll see commas there — cosmetic).

---

## 2. The aggregate log line — your 30-second triage

Search the `MT_TerrainGeneration/logs/Log_TerrainGen_*_Info.txt` for `W1 validation:`. You'll get one line per run, shape:

```
[21:10:28.173] [6,259s] [DETAIL] W1 validation: n=215, pinResMean=-0,058m, pinResSigma=1,207m, pinResMaxAbs=4,918m, wTestOutliers=102, redBandPixels=288042
```

| Field | Meaning | Step 1 pass criterion |
|---|---|---|
| `n` | Junctions visited for residual stats (non-NaN pinned_z AND non-NaN terrain_z) | Should match baseline ±1 |
| `pinResMean` | Mean of `pinned_z − terrain_z` across all `n` junctions | Within ~0.1 m of baseline; should NOT drift systematically |
| `pinResSigma` | Population σ of the same residuals | Smaller is tighter convergence; ≤ baseline is good |
| `pinResMaxAbs` | Worst single junction's `|pinned_z − terrain_z|` | **≤ 0.20 m** when Phase 1.9 is on (we pin TO the terrain sample) |
| `wTestOutliers` | Contributors with `|w| > 3` (significant tangent kink across ramp) | **0** on gentle-terrain maps; < 5 % of terminating-contributor count on steep maps |
| `redBandPixels` | Pixels in `delta_three_band.png` classified red (`|Δ| ≥ 0.5 m`) | **Not greater than baseline** — Step-1 R8 gate |

**Baseline values for reference** (from our two captures; Phase 1.9 still off):

```
franco_same_prio: n=215,  pinResMaxAbs=4.918m, wTestOutliers=102,  redBandPixels=288042
bled:             n=1084, pinResMaxAbs=5.684m, wTestOutliers=595,  redBandPixels=1656936
```

Note `pinResMaxAbs` is 5 m in baseline — that's the legacy pipeline's harmonized Z being far from the terrain sample. Phase 1.9 should drive this number down to **bilinear-sample noise level** (~0.05 m), because Phase 1.9 *defines* `HarmonizedElevation` as the bilinear sample for the pinned types. If Phase 1.9 is on and `pinResMaxAbs > 0.20 m`, something downstream is overwriting the pin (a missing NaN guard at C3 or a forgotten consumer).

---

## 3. `delta_three_band.png` — the visual diff

### Pixel encoding

The image is the same dimensions as the heightmap (e.g. 2048×2048). For every pixel:

| Colour | Condition |
|---|---|
| **Green** `(40, 180, 60)` | `|modified − original| < 0.20 m` |
| **Yellow** `(230, 200, 30)` | `0.20 ≤ |Δ| < 0.50 m` |
| **Red** `(220, 40, 40)` | `|Δ| ≥ 0.50 m` |
| **Black** `(0, 0, 0)` | either heightmap value is NaN |

"Modified" is the post-Phase-4 (terrain blended) heightmap. "Original" is the raw input heightmap before road carving. So this image shows **how much terrain the pipeline moved**, classified into three severity bands.

Thresholds come from Oude Elberink & Vosselman 2007 Fig 9 (standard road-reconstruction quality bands).

### What "good" looks like

- Roads visible as thin yellow/green ribbons against a sea of black (NaN outside heightmap bounds) or unmodified terrain.
- Junctions are slightly thicker spots on the ribbons — same intensity as the adjacent road.
- No red blobs.

### What "bad" looks like

| Pattern | Likely cause |
|---|---|
| Big red blob centred on a junction | **R8 ditch artefact** — pinning pulled all contributors to one Z that is far from the surrounding terrain. Step 2 gate. |
| Long red ribbon along a terminating road | **R7 slope kink** — Hermite ramp can't reconcile pinned junction Z with natural Phase-2 grade. Try `EnableMaxGradeClamp` (W3). |
| Red along a through-road through a T-junction | **Continuous-road exemption broken** — the through-road was anchored despite being continuous. Look at C1b. |
| Wholesale red shift across entire heightmap | Pipeline bug — possibly heightmap units / coord-frame mismatch. Not Phase-1.9-related; investigate orthogonally. |
| Black pixels where they shouldn't be | NaN propagation. Compare against `unified_smoothed_heightmap_with_outlines.png`. |

### Diff workflow

1. Open `baseline/<map>/delta_three_band.png` and the post-Phase-1.9 version side-by-side in any image viewer (IrfanView, GIMP, Photoshop layers).
2. The pixel count regression gate is **`redBandPixels` from the log** — eyeballing is qualitative, the number is the gate.
3. If new red appears, screenshot the region and note the world coords (the image is pixel-aligned to world via `metersPerPixel`).

---

## 4. `junction_residuals.csv` — one row per junction

### Columns

```
junction_id, type, position_x, position_y, pinned_z, terrain_z,
max_contributor_z, min_contributor_z, mean_contributor_z,
residual_pinned_minus_terrain, residual_max_minus_min, n_contributors, osm_node_id
```

| Column | Meaning |
|---|---|
| `junction_id` | Stable ID inside this run; not stable across runs |
| `type` | Enum name: `Endpoint`, `TJunction`, `YJunction`, `CrossRoads`, `Complex`, `MidSplineCrossing`, `Roundabout`, `Continuation` |
| `position_x/y` | World coordinates (m) |
| `pinned_z` | `junction.HarmonizedElevation`. With Phase 1.9 OFF, this is set by the legacy harmonizer (or NaN). With Phase 1.9 ON, this is the value Phase 1.9 wrote. |
| `terrain_z` | Bilinear sample of the **original** heightmap at the junction's XY |
| `max/min/mean_contributor_z` | Stats across `j.Contributors[i].CrossSection.TargetElevation` (post-smoothing) |
| `residual_pinned_minus_terrain` | `pinned_z − terrain_z`. With Phase 1.9 ON for Endpoint/T/Y/X/Complex, this should be ~0 (the pin is a terrain sample). |
| `residual_max_minus_min` | Spread of contributor Z at the junction — measures how well the contributors agreed. |
| `n_contributors` | Count of contributors at the junction. |
| `osm_node_id` | OSM node ID at the junction (`long`), taken from the first terminating contributor's endpoint. Empty if the map had no OSM origin or the junction has no terminating contributors (e.g. `MidSplineCrossing`). Plug into `https://www.openstreetmap.org/node/<id>` to inspect the live OSM node. |

### Key questions to ask

**Q1 — Did Phase 1.9 actually pin?**
Filter rows where `type IN (Endpoint, TJunction, YJunction, CrossRoads, Complex)`. Look at `residual_pinned_minus_terrain`. With Phase 1.9 ON:
- All values should be very small (`< 0.001 m` in flat-heightmap test cases; in real runs limited by bilinear-sample noise vs nearest-neighbour terrain_z).
- If you see large values here, the pinner didn't run on this type (Task B2 didn't reach it) or a downstream consumer overwrote it (Task B7 C3 guards missed a write site).

**Q2 — Do contributors agree at multi-way junctions?**
Filter `type IN (YJunction, CrossRoads, Complex)` and sort `residual_max_minus_min` descending. Step 2 gate: **≤ 0.10 m** at every multi-way junction. If any row exceeds that, R3 (cross-material disagreement) or R8 (ditch) is firing.

**Q3 — Which junctions saw the biggest move?**
Sort by `ABS(residual_pinned_minus_terrain) DESC`. The top 10 rows are the riskiest pins. Spot-check those in `delta_three_band.png` at `(position_x, position_y)`.

### Interpreting the franco_same_prio baseline

Header excerpt:
```
0,TJunction,160.18,961.21,194.742,195.218,195.155,194.742,194.948,-0.476,0.413,2
1,TJunction,179.08,848.68,197.767,197.834,197.890,197.767,197.829,-0.067,0.123,2
```

- Junction 0: legacy harmonizer picked `194.742` (the contributor min); terrain at this XY is `195.218`. Residual `-0.476 m` — the harmonizer is half a meter below the terrain bilinear sample. `residual_max_minus_min` of `0.413` means contributors disagreed by 41 cm even in the legacy pipeline. Junction 0 in `delta_three_band.png` near (160, 961) is the spot to inspect.
- Junction 1: cleaner — legacy harmonizer matches terrain within 7 cm; contributors agree within 12 cm.

After Phase 1.9 is on, junction 0's `residual_pinned_minus_terrain` should be near zero (because Phase 1.9 pins to the terrain bilinear sample for T-junctions). The 41 cm contributor disagreement is **not** Phase-1.9's job to fix — that's the contributors' smoothed profiles disagreeing, and the per-contributor `ApplyEndpointAnchoring` ramps them to the pin.

---

## 5. `w_test_summary.csv` — tangent-kink detector

### Columns

```
junction_id, spline_id, is_start, tangent_at_node_deg, tangent_past_ramp_deg,
delta_deg, sigma_predicted_deg, w, osm_node_id
```

| Column | Meaning |
|---|---|
| `spline_id` | The terminating spline at this junction |
| `is_start` | `True` if this contributor is at the start of its spline, `False` if at the end |
| `tangent_at_node_deg` | Terrain tangent angle (atan2 of dz/dx) sampled along the centerline at the junction node |
| `tangent_past_ramp_deg` | Same, sampled `blend_distance × 1.05` past the node (just beyond where the Hermite ramp ends) |
| `delta_deg` | `tangent_past_ramp_deg − tangent_at_node_deg` (signed) |
| `sigma_predicted_deg` | Class-keyed noise σ: `2°` for motorway/trunk/links, `1°` otherwise |
| `w` | `|delta_deg| / sigma_predicted_deg`. Paper 4 §4.3 statistic. |
| `osm_node_id` | OSM node ID of this contributor's *endpoint* (start if `is_start=True`, end otherwise). Empty if no OSM data. URL: `https://www.openstreetmap.org/node/<id>`. |

### Reading `w`

`w` is a normalized tangent change. Under the null hypothesis "the road profile is C¹ across the ramp," `w` should be standard-normal-ish:

| `w` band | Meaning |
|---|---|
| `0 ≤ w < 1` | Within 1σ — expected noise |
| `1 ≤ w < 2` | Within 2σ — borderline normal |
| `2 ≤ w < 3` | 2-3σ — suspicious but possibly noise |
| `w ≥ 3` | **3σ outlier — real C¹ kink across the ramp.** This is the `wTestOutliers` count in the log. |

### Pass criteria

- Step 1, gentle-terrain maps (`franco_same_prio`): `wTestOutliers = 0`.
- Step 1, steep-terrain maps: `wTestOutliers < 5 %` of terminating-contributor count.
- Per individual row: if `w > 3` and Phase 1.9 is on, check whether `EnableMaxGradeClamp` (W3) was on — that's R7 mitigation.

### Interpreting the franco_same_prio baseline

```
0,0,True,-5.54,2.24,7.77,1.00,7.77
```

Junction 0, spline 0, `is_start=True`, sigma=1° (not motorway). Tangent angle at the node is -5.54° (downhill into junction); past the ramp it's +2.24° (uphill again). Delta = 7.77°; `w = 7.77` — a **massive** outlier. The terrain at this junction has a fold that the legacy pipeline didn't smooth out. Phase 1.9 won't necessarily fix this single junction (it's a terrain feature, not a junction artefact), but the *count* of such outliers should drop because Phase 1.9 makes the ramp grade better-matched to natural Phase-2 grade.

### When `w` reads 0 and shouldn't

`SampleTangentAngleDeg` returns 0 if the spline is shorter than `blend_distance × 1.05` (clamped to spline ends, both samples land at the same point, `dx < 0.01`). On short connectors this is a known degradation — see plan §6 R4. If you see a suspicious cluster of `delta=0, w=0` rows, cross-reference the spline lengths.

---

## 6. `quadratic_growth.csv` — Hermite-ramp shape diagnostic

### Columns

```
junction_id, spline_id, is_start, delta_5m, delta_15m, delta_30m, delta_60m, osm_node_id
```

Last column is the contributor's endpoint OSM node ID, same semantics as `w_test_summary.csv`.

For each terminating contributor: heightmap `modified − original` sampled at 5, 15, 30, 60 m along the leg from the junction node toward the spline's other end. Distance is signed by `is_start` (start contributors march forward along the spline; end contributors march backward).

### The "quadratic growth" model

A healthy Hermite ramp produces an elevation delta that grows ~quadratically with distance: `Δy(d) ≈ 1e-4 · d²`. So expected:

| Distance | Δ predicted | Δ allowed |
|---|---|---|
| 5 m | ~0.0025 m | < 0.05 m |
| 15 m | ~0.023 m | < 0.10 m |
| 30 m | ~0.09 m | < 0.15 m |
| 60 m | ~0.36 m | < 0.50 m |

If the delta **plateaus or jumps**, the Hermite ramp didn't taper smoothly:

| Pattern | Diagnosis |
|---|---|
| `delta_5m` is already large and `delta_15..60` are small | Step jump at the junction node — pin Z is wildly off |
| All four deltas similar | Linear ramp, not Hermite — short-spline fallback may have fired |
| `delta_60m` >> `delta_30m` | The natural Phase-2 profile diverges from terrain *outside* the ramp — not a junction bug, a smoothing-window problem |
| All four near zero | Ramp is invisible — either no pin was set or the natural grade already matched (W2 grade-skip could legitimately produce this) |

### Interpreting the franco_same_prio baseline

```
0,0,True,0.034,-0.285,-0.107,-0.055
```

Junction 0, spline 0: at 5 m the terrain was lifted 3.4 cm; at 15 m it was dropped 28.5 cm; at 30 m dropped 10.7 cm; at 60 m dropped 5.5 cm. **The sign flip between 5 and 15 m suggests the legacy harmonizer is forcing the road through an unrealistic point** — exactly what Phase 1.9 should smooth out. Expected post-Phase-1.9 pattern on this row: monotonic, all four numbers within ±0.1 m, and growth shape closer to the quadratic prediction.

### When `quadratic_growth` says nothing useful

Just like `w_test_summary.csv`, this exporter samples positions out to 60 m along the leg. On splines shorter than 60 m, the 60 m position clamps to the spline start (when `is_start=False`) or to `0` via `MathF.Max(0f, totalLen - 60f)`. That produces a `delta_60m` that's actually sampled at the spline end, not at 60 m past the node. Watch for this on residential/service roads where most splines are < 60 m.

---

## 7. Suggested triage order

When comparing a new run against baseline, work top-down:

1. **Log line.** Compare `wTestOutliers` and `redBandPixels` to baseline. If both went down or stayed flat → strong signal. If either went up → drill into the relevant artefact.
2. **`delta_three_band.png`** side-by-side. New red blobs = R8. New red ribbons = R7.
3. **`junction_residuals.csv`** sorted by `ABS(residual_pinned_minus_terrain) DESC`. With Phase 1.9 on, the top of this list should all be ≤ 0.20 m for the pinned types. If a row is large, that pin didn't take effect.
4. **`w_test_summary.csv`** filtered to `w > 3`. Each surviving row is a tangent kink. Cross-reference position to `delta_three_band.png`.
5. **`quadratic_growth.csv`** for the suspect contributors. Read the four deltas — does the shape match the quadratic prediction?

---

## 8. Excel / spreadsheet quick recipes

All four CSVs are `,`-separated with `.` decimal. They open clean in Excel / LibreOffice / Numbers on any locale.

- **Phase-1.9 pin coverage check:**
  ```
  =COUNTIFS(B:B, "TJunction", ABS(J:J), "<0.001") / COUNTIF(B:B, "TJunction")
  ```
  Should be ~1.0 when Phase 1.9 is on (every TJunction has near-zero `residual_pinned_minus_terrain`). The exact numerator may vary by 1-2 due to NaN handling at boundary junctions.

- **Multi-way agreement:**
  ```
  =MAX(IF((B:B="CrossRoads")+(B:B="YJunction")+(B:B="Complex"), K:K))   {Ctrl+Shift+Enter}
  ```
  → max `residual_max_minus_min` across multi-way junctions. Step 2 gate: ≤ 0.10 m.

- **w-test outlier rate:**
  ```
  =COUNTIF(H:H, ">3") / COUNTA(H:H)
  ```
  on `w_test_summary.csv`. Step 1 gate: 0 on gentle maps, < 0.05 on steep maps.

---

## 9. Cross-references

- **Paper 4 (Oude Elberink & Vosselman 2007)** — primary source for the 0.2/0.5 m heatmap bands (Fig 9), the `w`-test definition (§4.3), and the quadratic-growth model (§3.3).
- **Paper 0 (Wang 2011)** — AASHTO §4.1.5 0.5 % grade-skip rule (W2) and class-keyed max-grade table (W3); used in interpretation of `w_test_summary.csv` outliers under steep terrain.
- **Spec** — [2026-05-14-junction-elevation-pinning-design.md](2026-05-14-junction-elevation-pinning-design.md), §5 Step 1/2/3 pass criteria.
- **Implementer code** — [BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs](../../BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs)
- **Captured baselines** — [baseline/README.md](baseline/README.md) for franco_same_prio and bled aggregate numbers.

---

## 10. Known limitations

- `SampleTangentAngleDeg` returns 0° on splines shorter than `blend_distance × 1.05`. Affects short connectors; not a bug, but the row's `w` is meaningless. Plan §6 R4 acknowledges this.
- `quadratic_growth.csv` `delta_60m` is sampled at spline start (not 60 m past node) for splines shorter than 60 m. Same caveat.
- The aggregate log line is on `TerrainCreationLogger.Detail` — current culture. On DE-locale you'll see `pinResMean=-0,058m`. The CSVs are invariant culture. Cosmetic.
- `pinResMaxAbs` measures `|pinned_z − terrain_z|`, not "worst pin error". For an unpinned junction in Phase 1.9 (e.g. `MidSplineCrossing`), `pinned_z` may already be NaN — those don't enter the aggregate.
