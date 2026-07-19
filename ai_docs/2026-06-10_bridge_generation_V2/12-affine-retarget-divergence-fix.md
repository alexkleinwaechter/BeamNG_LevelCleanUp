# Doc 12 — §3 retarget divergence: the REAL engine behind the giant walls (doc-11 engines A+B both refuted)

**Date:** 2026-07-07 · **Status:** **VERIFIED** (fix `0df8511`, 744 green; regen `Log_…125539`, user render: "spikes are gone")
**Branch:** `feature/bridge_embankment_containment` · **Follow-up to:** doc 11 (`1a300eb`)

**Verification results (run `125539` vs `121225`):** `[BRIDGE-PLAN] affine-skip` fires (157:
`start … e=+10,19m unreachable` + ~12 more pin-locked endpoints that were previously accumulating
silently). Spline 157 **+113,75 → −8,10** (meanAbs 8,89 → 0,39; the residual −8,10 @ s=2 is the dip
UNDER the crossings — legitimate below-A0). Spline 156 **+49,86 → +6,24** (max moved to its other
end). Spline 300 **out of the report entirely** (< 0,5 m). Spline 370 **+22,24 → +4,41**. Junction 249
has ZERO mentions. Pre-save spike fixes **5576 → 574**. Totals len>3m 4356 → 3267 m. **Bonus: spline
287 (Park Row mesa) +22,63 → −5,41** (meanAbs 8,93 → 0,74) — its dam was ALSO fed by the divergence,
§5's "not expected to change" was pessimistic; the tunnels workstream item is now only the small dip
residual. Remaining top offenders are the moderate legitimate deck-raise family (58 +11,53 d=0m,
262 +10,96, 148 +10,69) = the FlattenSideRoadDams containment follow-up.

---

## 0. Executive summary

Doc 11 named two spike engines. **Both are refuted by direct measurement**; the real cause of the
giant walls at bridge_2101591116 (streets 156/157/300/370 dammed +49…+113 m) is a **divergence in the
§3 no-blend retarget loop**: a street whose junction endpoint is dip-pinned (it passes under bridges)
can never reach its junction target, so the affine correction is re-added IN FULL onto the unpinned
mid-body on every one of the loop's 8 passes. Fixed in-solver with two guards + 3 TDD tests.

## 1. Engine A (DEM voids) — DOES NOT EXIST for this dataset

Probed the four source tiles AND the live `cropped_geotiff_*.tif` with GDAL (scratch DemProbe app):

- All tiles declare nodata = −9999 but contain **zero** cells with that value. No NaN, nothing
  < −1000, nothing > 1e6. `FillNodataVoids` (`d7c26bc`) correctly finds nothing — the missing
  `Inpainted` line is **correct behaviour**, not a stale binary (run `121225` confirmed with a
  freshly built app).
- The tiles are **topobathy**: rivers carry real −8…−16.3 m ASL bathymetry (largest histogram bucket
  −8…−10 m). Global min −16.336 = the preset's `terrainBaseHeight` exactly. local = ASL + 16.336.
- Park Row's covered section (way 25564592, sampled at its OSM node coords): **6.3–7.4 m ASL corridor
  between 10–14 m flanks — sane**. The provider already interpolated the LiDAR shadow. No pit, no mesa
  source in the DEM.
- Keep `d7c26bc` (it protects real void datasets). Follow-up hardening only: both crop producers
  (`GeoTiffMetadataService.CropGeoTiffToFileAsync`, `GeoTiffCombiner.CombineAndCropDirect`) create the
  output without `SetNoDataValue` — the declared-nodata tag is stripped. Harmless here; preserve it
  someday for in-range-nodata datasets.
- Doc-11's "spline 287 a0 = 7.45 ≈ −9 m ASL impossible" numerology is moot: 287's dam (+22.63 @ s=171,
  unchanged) has **no [DAM-CAUSE] junctions at all** — it is the parked TUNNELS workstream
  (`tunnel=yes` way, `excludeTunnelsFromTerrain:false`), not a void and not this fix.

## 2. Engine B (Phase 1.9 pins contaminated heightmap) — MECHANISM IMPOSSIBLE

- Run `121225`: `Road materials: 1` — ONE smoother pass, heightmap pristine (0…44.01) at Phase-1.9
  time (33.6 s). `JunctionElevationPinner` samples bilinearly = convex combination ⇒ **cannot produce
  68.16**. Junction 249's pin was a sane street value; the 68.16 was written LATER.
- Per-iteration evidence: T#249 connection elevation **19.44 m (iter 1) → 24.75 m (iter 2) → 68.16
  (final)**. The damage happens AFTER the Phase-2/3 loop, in the ~1 s window of the post-loop no-blend
  passes (75.2–75.5 s).

## 3. The real mechanism (all numbers from log `Log_TerrainGen_4096_20260707_121225_Info.txt`)

Topology at the Brooklyn Bridge ramps: spline 58 (motorway_link) carries raised span 904452323;
junctions 250/251 (near-duplicates, 40 cm apart, contributors [58,157,156]) sit on it — 251 is
junction-on-deck raised to z=34.53 (the legitimate deck raise). Spline 157 (primary street) STARTS
there and passes UNDER two more bridges (splines 26/51 cross over it 20.7/29 m from its start) —
**dip-as-pin pins its start sections** (`EnableDipAsPin`). Junction 249 sits mid-157 at s=165
(terminating: 156 start, 300 end).

The §3 retarget (`RetargetTerminatingRoadsToSettledThrough`, up to 8 passes, "each pass is a
contraction" by design) then:

1. Targets 157.start at the deck junction z (~30–34.5) ⇒ e ≈ +14.9 m.
2. `ApplyAffineLeveling` adds the correction — but the D6 pin-weight exemption
   (`BuildAffinePinWeights`, `AffinePinBlendMeters=40`) zeroes it on the pinned start and smoothsteps
   to full by s ≈ 75–80. **The endpoint never moves ⇒ e never shrinks ⇒ every pass re-adds the full
   correction onto the unpinned body.**
3. Log proof — per-pass `[BRIDGE-PLAN] affine-decay` errors stay CONSTANT instead of contracting:
   - spline 157: +14.91, +14.89, +14.88, +14.87×3, +14.86×2 → Σ≈119 ≈ final **maxDev +113.75 @ s=80**
     (pin end ≈ s 40 + 40 m blend = the exact station).
   - spline 156: +5.56, +6.25, +6.20×6 → Σ≈49.0 ≈ final **+49.86 @ s=2**.
   - spline 300: same → Σ≈49.2 ≈ final **+49.77 @ s=118**.
4. Junction 249's z is recomputed EVERY pass from through-road 157's rising body
   (`junction.HarmonizedElevation = settled` — overrides the Phase-1.9 pin) → **68.16**, and becomes
   the affine target for terminating 156/300 → they climb +6.2/pass. The loop's convergence measure
   (max junction-z change) is kept moving by the runaway itself ⇒ all 8 passes always run.
5. Phase 4 stamps the dammed streets' embankments ⇒ the giant needle-wall boxes in the render.

## 4. The fix (this session) — in-solver, no post-solve writes, no grade clamps

`BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`:

- **Affine-skip pin-locked endpoints** (`ApplyAffineLeveling`, guards BOTH call sites — in-loop
  Phase 2 + §3 retarget): if the affine pin weight AT the junction endpoint is
  < `AffineEndpointPinLockWeight` (0.01), that endpoint's target is dropped — the pinned profile is
  authoritative; the correction could never reach the target and would only bulge the body. Logs
  `[BRIDGE-PLAN] affine-skip spline=… start|end: endpoint pin-locked (w=…) e=…m unreachable`.
- **Baseline re-application in the retarget loop**: each terminating spline is snapshotted the first
  time it is leveled and RESTORED to that baseline before every re-application — pass k yields
  baseline + correction(k), absolute not cumulative. Bounded fixed point instead of accumulation.

Tests: `BeamNgTerrainPoc.Tests/Junction/RetargetPinLockedEndpointTests.cs` — mini-Manhattan
(deck road A at 160 / pinned street B at 150 / side street C at J1 where B is through). All 3 watched
RED (body 170, side road 185, partial-lock 159.96) → GREEN (150/150/≤155). Existing 6 retarget tests +
full suite: **744 green**.

## 5. Verification recipe (next regen — user)

1. Close the app (VS debugger held DLL locks at session end — MSB3027), rebuild, regen manhattan 4096.
2. Log: `[BRIDGE-PLAN] affine-skip` lines appear for the 156/157 family; `[DAM-REPORT]` splines
   157/156/300/370 drop out of the >3 m list (157 was +113.75); `[DAM-CAUSE]` junction 249 excess ≈ 0;
   over-max spike-fix count drops from ≈5576.
3. Render: bridge_2101591116 giant walls gone; roads under the ramps drivable.
4. NOT expected to change: spline 287 (+22.63, Park Row mesa — tunnels workstream, parked);
   `[BRIDGE-CLEAR]` deficits (§9.3); the legitimate deck-junction raise itself (251 → 34.53) — a
   street terminating at a deck junction still gets ONE bounded class-slope decay; whether even that
   single application is wanted is the FlattenSideRoadDams containment question (doc 11 §1-B
   "related"), now NON-amplified and the natural next work item.

## 6. Session-scratch reference

DEM probe tool (GDAL console app, reusable): scratchpad `DemProbe` — tile stats mode + `sample <tif>
<lon> <lat> <halfWinPx>` window mode. Park Row way node coords via OSM API
(`api.openstreetmap.org/api/0.6/way/25564592/full`; Overpass rejected PowerShell POSTs with 406).
