# Phase 1.9 Visual-Debug Investigation — Handoff for AI Test Partner

**Date:** 2026-05-14
**Branch:** `experimental/pin_junction_non_mesh`
**Audience:** A fresh Claude / AI session that did **not** participate in the Phase A / Phase B implementation. The user will hand you OSM node IDs corresponding to visual problems they saw in BeamNG.drive; your job is to investigate using the W1 harness data and form a hypothesis.

---

## 1. Mission in one paragraph

Phase 1.9 junction elevation pinning is implemented and running on this branch with `EnablePhase19JunctionPinning = true` (uncommitted local toggle in [JunctionHarmonizationParameters.cs:35](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs#L35)). Numerical aggregates show **strong macro improvement** (σ collapsed 5×, mean ≈ 0, 71 % of pinned junctions sit within 5 cm of terrain on franco) **but** a tail of pinned-type junctions still has residuals up to ~0.5 m on franco and ~1.1 m on bled, plus some `w`-test outliers and a small `redBandPixels` regression. The user is doing in-game / debug-PNG visual checks; when they spot a real problem they will hand you the OSM node ID. **You investigate, you don't fix code without alignment.** Investigation = look up junction in the W1 CSVs, classify against the failure-mode taxonomy in §7, propose a hypothesis and a targeted next test.

---

## 2. Required reading (in order)

1. **Design spec** — `ai_docs/2026-05-14_junction_pinning/2026-05-14-junction-elevation-pinning-design.md`. Especially §2 (scope: what gets pinned today vs deferred), §3.4 (consumer touchpoints C1a/C1b/C2/C3), §4 (per-type pin computation), §6 (risk register R3/R4/R7/R7b/R8), §7.1 (`FinalSnapTJunctionEndpoints` is kept indefinitely — do NOT touch it).
2. **W1 harness tutorial** — `ai_docs/2026-05-14_junction_pinning/2026-05-14-w1-harness-tutorial.md`. Tells you what every column in the CSVs means and how to interpret `w`, `delta_three_band.png` bands, quadratic-growth rows.
3. **Implementation plan** — `ai_docs/2026-05-14_junction_pinning/2026-05-14-junction-elevation-pinning-plan.md`. Tasks B1-B9 reference. Phase C (multi-way pinning via selector) and Phase D (risk validation) are NOT implemented yet.
4. **Pinner source** — [BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs). One file, ~120 lines. Read it.
5. **The four consumer touchpoints** — quick scan:
   - [UnifiedRoadSmoother.cs](../../BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) — Phase 1.9 call site (around L215), C1a (around L778), C1b (around L924 + L944).
   - [NetworkJunctionHarmonizer.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs) — C2 per-handler `!IsPinned` guards inside `ComputeEndpointElevation`, `ComputeTJunctionElevation`. Note: `ComputeMidSplineCrossingElevation` and `ComputeMultiWayJunctionElevation` are **deliberately unguarded** because Phase 1.9 doesn't pin those types today.
   - [UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) — C3 `!IsPinned` guards around six writes (L408, L614, L789, L926, L980; plus the one at L1395 which uses the pre-existing `float.IsNaN` outer guard).
6. **Baseline README** — `examples_for_ai/baseline_phase19/README.md` (unversioned). Aggregate stats cheat-sheet.

You don't need to read the investigation doc (`2026-05-14-old-pipeline-junction-pinning-investigation.md`) unless a hypothesis specifically points at the old multi-road anchoring failure mode (R8 ditch).

---

## 3. Current code / flag state

| Item | Value |
|---|---|
| HEAD commit | `ac2874f` (Phase B complete + B8/B9 review fixes) |
| Branch | `experimental/pin_junction_non_mesh` |
| `EnablePhase19JunctionPinning` | **`true`** (uncommitted in working copy) |
| `EnableHermiteGradeSkip` | `false` |
| `GradeSkipThresholdPercent` | `0.5` |
| `EnableMaxGradeClamp` | `false` |
| Test suite | 252/252 green at HEAD with flag on |
| `FinalSnapTJunctionEndpoints` | unchanged, kept indefinitely per spec §7.1 |

To verify flag state at the start of any investigation:

```
grep -A1 "EnablePhase19JunctionPinning" BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs | grep "= "
```

If the user later says "I reverted the flag" or "I changed W2 / W3", re-check before reasoning about a run.

---

## 4. Where the data lives

Both baseline (flags off) and step1 (Phase 1.9 on) artefacts are in `examples_for_ai/baseline_phase19/` (gitignored, unversioned). Layout:

```
examples_for_ai/baseline_phase19/
├── README.md                                  ← aggregate stats + regression-gate cheatsheet
├── baseline_franco_same_prio/                 ← flags-off (2026-05-14 21:10:21 capture)
│   ├── delta_three_band.png
│   ├── junction_residuals.csv                 ← 12 cols, NO osm_node_id (predates 05daff7)
│   ├── w_test_summary.csv                     ← 8 cols, no osm_node_id
│   ├── quadratic_growth.csv                   ← 7 cols, no osm_node_id
│   ├── unified_junction_harmonization_debug.png
│   ├── unified_junction_harmonization_debug_legend.png
│   └── terrain_gen_info.log                   ← grep "W1 validation" for aggregate
├── baseline_bled/                             ← same files as franco baseline
├── step1_franco_same_prio/                    ← Phase 1.9 on (2026-05-14 22:27:08 capture)
│   ├── delta_three_band.png
│   ├── junction_residuals.csv                 ← 13 cols, HAS osm_node_id
│   ├── w_test_summary.csv                     ← 9 cols, HAS osm_node_id
│   ├── quadratic_growth.csv                   ← 8 cols, HAS osm_node_id
│   ├── unified_junction_harmonization_debug.png
│   ├── unified_junction_harmonization_debug_legend.png
│   └── terrain_gen_info.log
└── step1_bled/                                ← same files as franco step1
```

User has explicitly asked to focus on **franco** for now. Bled artefacts are available for cross-check if a hypothesis needs to be confirmed on a second map.

---

## 5. CSV column reference

### `step1_*/junction_residuals.csv` (13 columns)

```
junction_id, type, position_x, position_y, pinned_z, terrain_z,
max_contributor_z, min_contributor_z, mean_contributor_z,
residual_pinned_minus_terrain, residual_max_minus_min, n_contributors,
osm_node_id
```

- `junction_id` — stable within a run (sequential after detect/merge), **deterministic across re-runs** of the same map / same code, so `junction_id` joins step1 ↔ baseline correctly.
- `type` — `Endpoint | TJunction | YJunction | CrossRoads | Complex | MidSplineCrossing | Roundabout | Continuation`. **Phase 1.9 today pins ONLY `Endpoint` and `TJunction`.** Others fall through to the legacy harmonizer.
- `pinned_z` — value of `junction.HarmonizedElevation` *as written by Phase 1.9 (for pinned types)* or *by the legacy harmonizer (for unpinned types)*.
- `terrain_z` — **nearest-neighbour** sample of the **original** heightmap at `(position_x, position_y)`. The pinner uses **bilinear** sampling, so on a sloped pixel `pinned_z ≠ terrain_z` even when the pin is perfectly preserved end-to-end.
- `residual_pinned_minus_terrain` = `pinned_z − terrain_z`. The W1 aggregate `pinResMaxAbs` is `max(abs(residual_pinned_minus_terrain))` across all rows with both columns non-NaN.
- `residual_max_minus_min` — spread of contributor `TargetElevation` at the junction (post-smoothing). Step 2 gate (≤ 10 cm at multi-way junctions) — not Step 1's concern.
- `osm_node_id` — **for terminating contributors only.** Empty cell (`,,`) means the junction has no terminating contributor with an OSM node ID (e.g. PNG-pipeline junction, or `MidSplineCrossing` whose contributors are all continuous).

### `step1_*/w_test_summary.csv` (9 columns)

```
junction_id, spline_id, is_start, tangent_at_node_deg, tangent_past_ramp_deg,
delta_deg, sigma_predicted_deg, w, osm_node_id
```

- One row per *terminating contributor* (`IsEndpoint = true`) of every pinned junction (`HarmonizedElevation` non-NaN).
- `w = abs(delta_deg) / sigma_predicted_deg`. `w > 3` ≈ 3σ outlier = real C¹ kink across the ramp.
- `sigma_predicted_deg` is class-keyed: 2.0° for motorway / trunk / *_link, 1.0° otherwise.

### `step1_*/quadratic_growth.csv` (8 columns)

```
junction_id, spline_id, is_start, delta_5m, delta_15m, delta_30m, delta_60m, osm_node_id
```

- Heightmap delta (modified − original) along the contributor's centerline at 5, 15, 30, 60 m from the junction node.
- A healthy Hermite ramp grows roughly quadratically: `Δy(d) ≈ 1e-4 · d²` → at 60 m, ~36 cm.
- Sign flips between 5 m and 15 m = ramp forcing the road through an unrealistic point (R7 / kink).

---

## 6. Workflow for one investigation

The user reports a visual problem and gives you an **OSM node ID** (and probably a screenshot / region note).

### Step 1 — locate the junction by OSM node ID

```
grep ",<OSM_ID>$" examples_for_ai/baseline_phase19/step1_franco_same_prio/junction_residuals.csv
```

Multiple rows can match if a node appears in multiple junctions (unusual but possible).

If the grep returns empty:
- The junction may have **no terminating contributor with that OSM node ID**. The exporter only writes osm_node_id for the first terminating contributor it finds. If all contributors are continuous (e.g. `MidSplineCrossing`), `osm_node_id` is empty for that junction.
- Ask the user for the world coordinates instead, then filter by `position_x` / `position_y` (tolerance ~5 m).

### Step 2 — extract the matching rows from all three CSVs

For the `junction_id` you found above:

```
J=<junction_id>
echo "=== residuals ==="
grep -E "^${J}," examples_for_ai/baseline_phase19/step1_franco_same_prio/junction_residuals.csv
echo "=== w-test (all contributors) ==="
grep -E "^${J}," examples_for_ai/baseline_phase19/step1_franco_same_prio/w_test_summary.csv
echo "=== quadratic growth (all contributors) ==="
grep -E "^${J}," examples_for_ai/baseline_phase19/step1_franco_same_prio/quadratic_growth.csv
```

Also pull the **baseline** row by `junction_id` to compare what changed:

```
grep -E "^${J}," examples_for_ai/baseline_phase19/baseline_franco_same_prio/junction_residuals.csv
```

### Step 3 — classify against §7 failure modes (below). Write a short report:

- Junction id, type, position, n_contributors, osm_node_id.
- Pinned vs unpinned type (decides whether Phase 1.9 is even involved).
- Step1 vs baseline `pinned_z` and `residual_pinned_minus_terrain` deltas.
- All `w` and quadratic-growth rows for this junction's contributors.
- One paragraph: what failure mode this looks like and why.
- A proposed next test (a flag toggle, a specific value to print, a code path to instrument).

**Don't propose a code fix yet.** First hypothesis, then alignment with the user, then code.

---

## 7. Failure-mode taxonomy (checklist)

Run this checklist on every junction the user hands you. For each: yes / no / need more data.

### F1. Multi-way unpinned by design (most common false alarm)

- Junction `type` ∈ {`YJunction`, `CrossRoads`, `Complex`, `MidSplineCrossing`, `Roundabout`, `Continuation`}.
- Phase 1.9 does NOT pin these today (design §4). `pinned_z` is whatever the **legacy harmonizer** produced.
- **Verdict if yes:** Not a Phase 1.9 bug. This is what Phase C / C1 will address. Tell the user, move on. Unless they specifically want C1 work to start.

### F2. C3 miss — downstream consumer overwrote the pin

- Junction `type` ∈ {`Endpoint`, `TJunction`}.
- `pinned_z` differs substantially from `bilinear(original_heightmap, position_x, position_y)`.
  - You can approximate by reading 4 neighbouring pixels from the heightmap. Lacking that, compare to the step1 `terrain_z` (nearest-neighbour); a difference of more than `0.5 × max_slope_per_pixel` is suspicious.
- Cross-check: does the `junction_id` row's `pinned_z` match the **baseline** row's `pinned_z`? If yes → Phase 1.9 ran but a consumer wrote the legacy value over it. If no → pin survived but is just wrong, look at F3.
- **Verdict if yes:** Real bug. Find the consumer. Common suspects:
  - `UnifiedJunctionProfileBlender.cs` — one of the six writes is missing the `!IsPinned` guard, or executes inside a code path that the guard doesn't cover.
  - A site outside the four touchpoints that writes `HarmonizedElevation` (less likely — B6/B7 catalogued the writers, but a new write may have landed since `ac2874f`).
  - The roundabout harmonizer (`Phase 2.6`) — not in scope but writes `HarmonizedElevation` for `Roundabout` junctions only. Should NOT touch `Endpoint` or `TJunction`.

### F3. Cliff-face sampling artefact (bilinear vs nearest)

- Junction `type` ∈ {`Endpoint`, `TJunction`}.
- `pinned_z` ≠ `terrain_z` but the surrounding terrain has a steep slope across the junction position (visible in `unified_junction_harmonization_debug.png` as a sharp Z gradient).
- Sample 4 neighbouring pixels at the junction position from `original` heightmap; if they span > 1 m in elevation, bilinear-vs-nearest difference up to `0.5 × span` is mathematically possible.
- **Verdict if yes:** Not a bug. The pin is correctly tracking terrain; the exporter's nearest-neighbour `terrain_z` is just a noisy proxy. If this dominates the aggregate stats, the exporter could be updated to use bilinear `terrain_z` — log as a follow-up, don't change the pinner.

### F4. Hermite ramp kink (R7)

- `w_test_summary.csv` row for this contributor has `|w| > 3`.
- `quadratic_growth.csv` row shows non-monotonic `delta_5m … delta_60m` (sign flip).
- Likely on a steep-grade leg (> 4 %).
- **Verdict if yes:** R7 fired. Mitigation #1 = enable `EnableMaxGradeClamp` (W3) and re-run. Mitigation #2 = quintic Hermite (F1 in spec §7.3). Tell the user before flipping W3 — it changes Hermite samples globally, not just at this junction.

### F5. Short-spline degeneracy (R4)

- `SampleTangentAngleDeg` returns 0° when the spline's total length is shorter than `BlendDistanceMeters × 1.05`. Look at `w_test_summary.csv`: rows where `tangent_at_node_deg = tangent_past_ramp_deg = 0.00` and `w = 0` are suspect.
- Cross-check spline total length: tedious without running code; ask the user for the spline length if needed.
- **Verdict if yes:** Known degraded measurement, not a bug in the pin. Pin can still be correct.

### F6. R8 ditch artefact (multi-way only)

- New red blob centred on the junction in `delta_three_band.png` step1 vs baseline.
- Only triggers for *pinned* multi-way junctions, which today is **none**. If you see this on franco / bled with current code, it's coming from somewhere else (e.g. the existing T-junction edge-constraint logic getting confused by the pinned Z).
- **Verdict if yes:** Document for Phase C / C1. May indicate the §4.1 selector will need always-sequential fallback at that junction.

### F7. Excluded junction unexpectedly visible

- `IsExcluded = true` in code; pinner skips it; harmonizer NaN-sets it. The CSV exporter also filters on `IsExcluded` and excludes these rows. If the user reports a visual problem at a position that grep finds no row for, the junction may be excluded. Confirm by reading the network state at runtime (requires a logged dump or unit-test repro).

### F8. The osm_node_id mismatch case

- The junction in step1 step1 CSV has `osm_node_id = X` but the user gave a different ID for the same visual location. Possible causes:
  - The OSM ID in the CSV is from the **first terminating contributor** the exporter visits; another contributor of the same junction may have a different ID.
  - The visual position straddles two adjacent junctions that the detector merged or split differently than the user expects.
- **Verdict:** Ask the user to confirm by clicking around the OSM node in `https://www.openstreetmap.org/node/<id>` to verify it's the right end of the right way.

---

## 8. What's already known (the macro)

Quoting from the B10 Step 1 analysis the user has:

| Metric | franco baseline | franco step1 | Verdict |
|---|---|---|---|
| pinResMean | −0.058 m | −0.008 m | ✅ Closer to zero |
| pinResSigma | 1.207 m | **0.250 m** | ✅ **5× tighter** |
| pinResMaxAbs | 4.918 m | 2.337 m | ❌ Above 0.20 target — dominated by MidSpline (2.34 m max) which is unpinned today |
| wTestOutliers | 102 | 109 | ❌ +7 (noise on 215 junctions) |
| redBandPixels | 288 042 | 326 928 | ❌ +13.5 % regression — likely unpinned multi-way blob-shape change |

**Pinned-type histogram on franco** (Endpoint + TJunction, 206 junctions):

| `|residual|` | count | % |
|---|---|---|
| < 5 cm | 147 | 71 % |
| 5–10 cm | 39 | 19 % |
| 10–20 cm | 14 | 7 % |
| 20–50 cm | 6 | 3 % |
| ≥ 50 cm | 0 | 0 % |

Worst franco offenders for **pinned** types:
- Endpoint with max `|residual|` = **0.261 m** (junction 194, lookup details to come).
- TJunction with max `|residual|` = **0.485 m** (junction 57, lookup details to come).

Worst franco offender overall (but unpinned, so F1):
- MidSplineCrossing with `|residual|` = **2.337 m** (junction 216).

When user gives you an OSM node ID, your first job is to find which `junction_id` it maps to and whether it falls into the "above-0.10 m pinned" bucket (real concerning tail) or somewhere else.

---

## 9. Boundaries — don't do these

- **Don't run terrain generation.** The user runs it in BeamNG.drive (Windows desktop app). Your environment can't.
- **Don't touch `FinalSnapTJunctionEndpoints`** in `UnifiedJunctionProfileBlender.cs` (around L1703-1930). Spec §7.1 explicitly keeps it indefinitely.
- **Don't flip the production default of `EnablePhase19JunctionPinning` to `false`** without the user telling you. It's currently `true` (uncommitted) to support the visual-debug session. You may *suggest* flipping it for a comparison run, but the user does the flip and the rerun.
- **Don't start Phase C / C1 (multi-way pinning) code.** That's a separate phase pending Step 1 sign-off.
- **Don't write code fixes from a hypothesis.** First report the hypothesis, then align with the user, then code if approved.
- **Don't add defensive checks for impossible cases** — see CLAUDE.md project rules.
- **Don't add `osm_node_id` column to the baseline CSVs.** The baseline data is pre-`05daff7` capture and is intentionally frozen for diff purposes. If you need OSM lookup on a baseline row, match by `junction_id` to the step1 row and read `osm_node_id` from there.

---

## 10. If a new W1 run is needed

The user re-runs terrain generation in BeamNG.drive. After each run:

1. New artefacts overwrite `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\<map>\MT_TerrainGeneration\`.
2. Ask the user to confirm the run is done and to share the `MT_TerrainGeneration/logs/Log_TerrainGen_*_Info.txt` timestamp.
3. Copy the new run into `examples_for_ai/baseline_phase19/<labelled_subfolder>/`. Suggested label scheme:
   - `step1_franco_same_prio_run<N>/` for repeated step1 runs.
   - `step1_franco_same_prio_w3on/` when W3 is enabled mid-investigation.
   - `step1_franco_same_prio_<flag>_<value>/` for any other targeted toggle.
4. Update `examples_for_ai/baseline_phase19/README.md` with the new aggregate line and a one-line note on what was different.

To toggle W2 or W3 mid-investigation:

```
# in BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs

public bool EnableHermiteGradeSkip { get; set; } = true;   // was false
# or
public bool EnableMaxGradeClamp { get; set; } = true;      // was false
```

The user rebuilds (Visual Studio in Release mode) and reruns. Then you copy the new artefacts.

---

## 11. Quick reference

| Thing | Value |
|---|---|
| Branch | `experimental/pin_junction_non_mesh` |
| Phase B HEAD | `ac2874f` |
| Plan doc | `ai_docs/2026-05-14_junction_pinning/2026-05-14-junction-elevation-pinning-plan.md` |
| Design spec | `ai_docs/2026-05-14_junction_pinning/2026-05-14-junction-elevation-pinning-design.md` |
| W1 tutorial | `ai_docs/2026-05-14_junction_pinning/2026-05-14-w1-harness-tutorial.md` |
| Pinner | `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs` |
| Consumer touchpoints | `UnifiedRoadSmoother.cs` (C1a/C1b), `NetworkJunctionHarmonizer.cs` (C2 in per-handler guards), `UnifiedJunctionProfileBlender.cs` (C3, six guarded writes) |
| Pinned types today | `Endpoint`, `TJunction` |
| Unpinned types | `YJunction`, `CrossRoads`, `Complex`, `MidSplineCrossing`, `Roundabout`, `Continuation` |
| User's environment | Windows 11, BeamNG.drive, app produces artefacts in `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\<map>\MT_TerrainGeneration\` |
| Test command | `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj` (252/252 at HEAD) |
| Build (sandboxed) | `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true` (PowerShell needs `-p:`, not `/p:`) |

---

## 12. First message to the user

When the user opens a new session and asks you to investigate the first OSM node, your first message should:

1. Confirm you've read this handoff doc.
2. Confirm the current flag state (grep `JunctionHarmonizationParameters.cs`).
3. Verify the step1 data is still in place (`ls examples_for_ai/baseline_phase19/step1_franco_same_prio/`).
4. Ask the user for the OSM node ID and any rough world coordinates / screenshot context.
5. Tell them: "I will *not* run terrain generation; you do that. I will *not* propose a code change until we agree on the hypothesis."

Then run the §6 workflow on the first ID and produce the §6 Step 3 report. Stop and wait for the user's response.
