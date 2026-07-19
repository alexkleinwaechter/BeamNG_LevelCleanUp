# Doc 11 — DEM voids + Phase-1.9 junction pinning dams (the two spike engines) — handoff

**Date:** 2026-07-07 · **Status:** HANDOFF for a fresh session — verify fix A, root-cause + fix mechanism B ·
**Branch:** `feature/bridge_embankment_containment` @ `d7c26bc` (741 tests green)
**Read this alone — self-contained.** Follow-up to doc 10 (contiguous-span consolidation, VALIDATED:
spline 14 `structureSegs` 11→1, `[BRIDGE-OVERLAP] spans` 67→44, `maxCut` 80.89→70.26).

---

## 0. The prompt (user, 2026-07-07)

> It looks worse now. [Render: giant needle-wall boxes at the water; roads on top of ~50m spiked
> embankment walls] Spikes at bridge bridge_2101591116 with parallel road underneath (spline 156).

Log: `…\manhattan\MT_TerrainGeneration\logs\Log_TerrainGen_4096_20260707_100409_Info.txt`.
Preset: `d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json`.

**First fact: this run's terrain is (almost certainly) byte-identical to the previous one**
(`…091228`): identical `[DAM-REPORT] totals` (145/60/18345m/4356m), identical spline-287 line
(maxDev +22,63 @s=171), identical `5576 spike fixes`, and **no `Inpainted … DEM void cell(s)` line**
⇒ the regen ran WITHOUT the `d7c26bc` binary (the 10:04 run started while the app had been running
through the previous session — rebuild happened for `4a99bc4` but likely not for `d7c26bc`), OR the
fix found nothing (see §2 caveat). "Worse" = the same defects viewed at a worse spot (Manhattan
Bridge area, spline 51), not a regression from the fixes.

## 1. Where the investigation stands — TWO independent spike engines identified

The interchange needle walls had three stacked causes. #1 is fixed+validated (doc 10). The remaining
two are:

### Engine A — DEM voids become pits (fix COMMITTED `d7c26bc`, NOT YET VERIFIED)

- A LiDAR bare-earth DTM has **no ground return under solid structures**. Park Row under the Police
  Plaza deck (user aerial confirmed; OSM way 25564592 `tunnel=yes layer=-1` inside spline 287) is
  NODATA in the source.
- `GeoTiffReader` turned nodata into pits at the **global minimum** two ways: (1) declared nodata →
  `minElevation` in `ConvertToHeightmap`; (2) **neither call site passed `nodataValue`**, so sentinel
  fills (−9999 …) survived into 16-bit normalization and clamped to pixel 0 = global min.
- Consequences: A0 estimate poisoned (spline 287 a0 = 7.45 local ≈ **−9 m ASL — impossible**; other
  streets nearby a0 ≈ 18.5–19 ≈ 2–3 m ASL, sane). The manufactured 25–30 m cliffs next to stamped
  corridors blow cells past the 44 m ceiling (5576 over-max ≈ the plaza footprint) and the pre-save
  spike fix neighbor-averages them into the needle mesa (the "Park Row mesa" render).
- **Fix `d7c26bc`**: `GeoTiffReader.FillNodataVoids` — BFS dilation inpaint on the raw `double[]`
  BEFORE normalization. Void = declared nodata | NaN/Inf | >1e6 | **< −1000 m** (catches undeclared
  −9999/−32767 when the tag is lost). Components > `DefaultMaxFillComponentCells` = 20 000 cells are
  LEFT ALONE (water-as-nodata coastal maps must not get shorelines smeared across rivers). Both
  `ConvertToHeightmap` call sites now pass the declared nodata value. Logs
  `Inpainted N DEM void cell(s)`. 6 TDD tests in `GeoTiffNodataInpaintTests`.
- **VERIFY (first task): rebuild, regen manhattan, then check:** the `Inpainted N …` log line exists;
  `[DAM-REPORT] spline=287` maxDev +22,63 → small; over-max 5576 drops; Park Row mesa gone.
- **If the line is STILL absent after a confirmed rebuild**: the pipeline loads a **cropped temp
  tiff** (`Loading heightmap from GeoTIFF: %TEMP%\cropped_geotiff_<guid>.tif`). Then the cropping
  step strips the nodata tag AND writes in-range values into void cells — find the producer of
  `cropped_geotiff_*.tif` and either preserve the nodata tag or run `FillNodataVoids` there.
  (Note: undeclared sentinel −9999 IS caught by the new `< −1000` check, so only an *in-range* bake
  can hide voids.)

### Engine B — Phase 1.9 `JunctionElevationPinner` pins street junctions to a CONTAMINATED heightmap
### (NOT fixed — the render's giant walls; THE task for this session)

Evidence from `…100409` (`[DAM-CAUSE]` diagnostics added in `4a99bc4` — each `[DAM-REPORT]` line is
followed by the junctions on that spline sitting >1.5 m above local A0, with contributors):

```
[DAM-REPORT] spline=156 (primary)     maxDev=+49,86 @s=2   nearestSpan=2101591116 d=40m
[DAM-REPORT] spline=300 (residential) maxDev=+49,77 @s=118 nearestSpan=2101591116 d=39m
[DAM-REPORT] spline=370 (residential) maxDev=+22,24 @s=1   nearestSpan=2101591116 d=68m
[DAM-CAUSE] spline=156 junction=249 (TJunction) station=0m   junctionZ=68,16 a0=18,77 excess=+49,4 pinned=True contributors=[156:p8000,157:p8000,300:p5500]
[DAM-CAUSE] spline=156 junction=618 (TJunction) station=31m  junctionZ=40,69 a0=18,68 excess=+22,0 pinned=True contributors=[51:p9500,156:p8000,370:p5500]
[DAM-CAUSE] spline=156 junction=250 (TJunction) station=166m junctionZ=29,99 a0=18,97 excess=+11,0 pinned=True contributors=[58:p9500,157:p8000,156:p8000]
```

- `bridge_2101591116` = span 2101591116 on **spline 51** (`Marked bridge span 2101591116 on spline 51
  [99,6,319,5]m as excluded`). Spline 51's own deck profile is z≈19–30 — the 68.16 is NOT its deck.
- Junctions 249/618/250 appear in NO `junction-raise` / `junction-on-deck` / `junction-lower` line
  (those all log contributors since `4a99bc4`) ⇒ the pin writer is the fourth site:
  **`JunctionElevationPinner.PinNetwork` (Phase 1.9, `EnablePhase19JunctionPinning`)** — it pins every
  Endpoint/TJunction to a RAW `heightMap` bilinear sample at the junction position, no sanity check
  (`JunctionElevationPinner.cs:38`).
- **z=68,16 is ABOVE the source DEM ceiling (maxHeight 44,01)** ⇒ at sample time the shared heightmap
  ALREADY contained processing output (earlier material's stamped embankments and/or blown-up cells —
  materials are processed sequentially against ONE shared heightmap). Phase 1.9 then hard-pins street
  junctions to that garbage; the junction blender transplants it onto every contributor
  (156/157/300/370…), the streets solve as +22…+50 m dams, their stamped embankments become the giant
  walls, edges/guard-mask patchwork become the needles. **Feedback loop: spikes → pins → dams →
  more spikes.**
- Related (same family, found earlier, also unfixed): `UnifiedRoadSmoother.PinOnDeckJunctions` (doc 08
  C3 step A) pins junctions inside raised spans UP to deck Z and transplants onto ALL contributors —
  its doc comment promises a side-road containment `GradeSeparationResolver.FlattenSideRoadDams`
  which **was never implemented** (grep: referenced only in that comment). E.g. junction 894
  (z 24,44→30,08, contributors [70,553]) is a legitimate deck-end raise whose Z still bleeds into
  side roads via the blender.

## 2. The task (in order)

1. **Verify Engine A**: rebuild `BeamNG_LevelCleanUp` (make sure the app is CLOSED so the DLLs copy —
   MSB3027 lock errors mean the old binary keeps running), regen manhattan 4096, check the §1-A
   verification list. If `Inpainted` is absent → chase the `cropped_geotiff_*.tif` producer.
2. **Root-cause Engine B precisely** (evidence first): add a file-only diagnostic in
   `JunctionElevationPinner` logging, per pinned junction, the sampled Z vs the A0 estimate at the
   nearest contributor cross-section when `sample − a0 > ~3 m` (network.EarlyElevationEstimate is
   available at Phase 1.9? verify — if not, log sample vs the ORIGINAL DEM by keeping a pristine copy
   or run before any stamping). Key question: WHY is the heightmap contaminated at Phase 1.9 time —
   which pass wrote 68 m at junction 249's position (multi-material sequential stamping? an earlier
   blow-up?).
3. **Fix Engine B by doctrine** (in-solver, NO post-solve road writes; NO grade clamps): candidate
   shapes, decide with evidence —
   a. Phase 1.9 pins from the **A0 estimate** (the natural centerline profile) instead of the raw
      heightmap, or clamp the pin to `a0 + small threshold` for junctions whose contributors are all
      non-structure roads;
   b. sample a **pristine copy** of the imported DEM (never the shared working heightmap);
   c. plus the deferred containment for legitimate deck raises (894-style): a street contributor of a
      deck-raised junction must not inherit full deck Z — §3.5-style distribution or a class-slope
      decay (this is the missing `FlattenSideRoadDams`, but done IN-solver, not post-solve).
4. **Regen + render**: the §1-B junction excesses (+49,4/+22,0/+11,0) collapse; `[DAM-REPORT]`
   splines 156/157/300/370 drop out of the >3 m list; the bridge_2101591116 walls gone; then
   re-check Park Row.

## 3. Cautions

- **User doctrine:** nothing post-solve may write road/deck elevations; post-solve shapes bare
  terrain only. No max-grade clamps as mitigation. Byte-identical behavior when flags are off.
- Doc-09 anchor (`EnableNaturalProfileAnchor`) + doc-10 consolidation
  (`EnableContiguousSpanConsolidation`) are preset-enabled and VALIDATED — don't regress
  (`NaturalProfileAnchorTests`, `ContiguousSpanConsolidationTests`, `BridgeSpanFootprintTests`,
  `BridgeDeckFootprintRasterTests`, `BridgePriorityDistributionTests` are guard rails; 741 green
  before you start).
- `EnablePhase19JunctionPinning` — find where it's set (UI/preset/default) before touching; a naive
  "just disable it" changes junction behavior network-wide (Phase 3 fallbacks take over) — A/B it,
  don't assume.
- Engine A's fill cap (20k cells) is deliberate — water-as-nodata coastal maps (bonifacio!) must not
  get shoreline smeared across rivers.
- Tunnel-aware elevation (Park Row's tunnel span is inert — `excludeTunnelsFromTerrain:false`, spans
  not excluded/tagged) is agreed as right but PARKED until the tunnels workstream (after bridges).
- `[DAM-REPORT]` deviations are measured vs A0 — where A0 itself is void-poisoned (spline 287 before
  fix A) the number overstates; check a0 plausibility (local ≈ ASL + 16.34 on manhattan) first.

## 4. Verification recipe (fresh session end state)

1. Rebuild (app closed!), regen manhattan 4096 with the existing preset.
2. Log: `Inpainted N DEM void cell(s)` present; `[DAM-REPORT] spline=287` maxDev ≪ 22; over-max ≪
   5576; `[DAM-CAUSE]` shows NO street junction with excess > ~3 m that isn't an explicit, logged
   bridge raise; splines 156/157/300/370 out of the >3 m dam list.
3. Render (user judges): bridge_2101591116 walls gone, Park Row mesa gone, roads below bridges
   drivable, no needle fields.
4. 741+ tests green.

Log dir: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`.
History: doc 10 §7 (span consolidation), commits `f093e35` (consolidation), `4a99bc4` ([DAM-CAUSE]
diagnostics), `d7c26bc` (DEM void inpaint). Sessions 1–12 in memory `bridge_rule_system_v2`.
