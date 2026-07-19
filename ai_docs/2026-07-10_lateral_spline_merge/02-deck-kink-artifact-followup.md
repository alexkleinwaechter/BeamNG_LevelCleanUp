# Deck Kink Artifact on Span 1192907311 — Follow-up / Handoff

**Date:** 2026-07-11
**Branch:** `feature/combine_lanes_to_spline`
**Status:** UNRESOLVED in-game after five mesh-side fixes. Deck mesh is now VERIFIED geometrically
sound at the site; the artifact the user sees must come from a remaining layer (source-geometry
kink → DecalRoad, or game-side cache). This doc is the evidence trail + ranked next steps.

## 1. Symptom

On the merged trunk corridor (spline 13, span `1192907311`, the deck DAE
`art/shapes/MT_bridges/bridge_1192907311.dae`) a triangular defect cuts across the roadway:
the surface renders below the deck in a wedge/fan shape, lane markings warp into it, and vehicles
that drive over it wedge INTO the geometry ("car gets eaten") — a collision hole, not just visual.

- Reported location: **between merged OSM ways `432550257` and `46177152`** (user-identified),
  both members of spline 13 (`[WAY-MAP] spline=13 type=trunk ... ways=...,432550257,...,46177152,...`).
- World position of the fold (DAE coords): around `(-600..-614, -988..-1007)`, deck z ≈ 27.3–27.7.
- Span station ≈ **342–353 m** (shell segments 1169–1205 in the 130041-run DAE; near building
  `1255363984`, pier log "bay 11 blocked by building 1255363984 @ s=362,4m").
- NOT at a span boundary: span 1192907311 covers spline 13 `[0, 1704.8]` with `structureSegs=1`.

## 2. Root cause of the mesh fold (PROVEN, fixed in `2091670`)

The corridor centerline bends **~27° concentrated over ~11 m** at that station. On a **21.6 m wide**
deck this exceeds the geometric limit (local curvature > 1/halfWidth ≈ 1/10.8 m): the inside edge
polyline (Center ± Normal·halfWidth) **reverses direction**, the deck-top quads pleat over
themselves, and `BridgeDeckMeshBuilder.AddFace`'s winding enforcement (which force-orients every
face outward) turns the pleat into overlapping both-ways-up geometry. Collision = clone of the
visual mesh ⇒ vehicles wedge into the fold.

### Where the ~27° kink comes from (STRONG HYPOTHESIS, not yet fixed)

Spline 13 is a lateral merge: `[LATERAL-MERGE] ways 808611037(+11) + 931223088(+15) (trunk):
overlap=1761m sep~15,3m lanes=3+2 structureSegs=1 residualTails=1`.

At a merge-run boundary the averaged centerline (midline) tapers back onto the surviving single
carriageway: a lateral shift of **sep/2 ≈ 7.65 m** over `LateralCarriagewayMerger.BoundaryTaperMeters
= 30 m` ⇒ a **~14° dogleg**, further sharpened/blunted by Chaikin ×2 and whatever the raw way
geometry does there (ways 432550257/46177152 sit at/near that boundary). The observed rotation at
the fold (~12° of normal rotation across the welded fan, measured from the DAE) matches.

## 3. Fix history (this session, all committed)

| Commit | What | Outcome |
|---|---|---|
| `c2d8de5` | Butt-seam wedge extension: landed span ends extended `halfWidth·tanΔ` onto the partner deck, stations riding the partner surface | Fired correctly (log `[BRIDGE-MESH] seam extension`), but targeted the junction-24 butt seam at span START — a real but DIFFERENT defect than the user's triangle |
| `e95fdc9` | Extension redesign: butt-joints only, one filler per seam (wider deck owns), 3 cm sub-flush (exact-coplanar surfaces z-fight) | Sound; still not the user's triangle |
| `2091670` | **Anti-fold weld** (`BridgeDeckMeshBuilder.BuildAntiFoldEdges`): per-station edge arrays, any edge point whose plan advance reverses against the center direction is clamped to its predecessor (miter limit); shell/parapets/stamps consume welded arrays | **VERIFIED in the 18:33 regen DAE**: the fold is gone — stations 1200–1206 are now a clean fan pivoting on the frozen right edge `(-613.65,-1006.54, 27.43)` while the left edge advances. Valid, watertight, collidable geometry. **User still reports the artifact in-game.** |

(Fixes 1–3 of the session — T-junction admission `dd24610`, landing-anchor plan-Z `8e5df4f`,
parapet-pierce window `85ece67` — are validated or unrelated to this artifact.)

## 4. Verification method (reusable)

Dissect the deck DAE directly — shell vertices are written 16 per segment in construction order
(top face first: v0..v3), so segment k's top corners are floats `[48k .. 48k+11]` of the first
`<float_array>`:

```powershell
$m = [regex]::Match((Get-Content $dae -Raw), '<float_array[^>]*>([^<]+)</float_array>')
# fold scan: per segment compare left/right edge deltas against the previous ones;
# dot < 0 => backtracking (pleat). Caveat: AddFace winding-flip reorders degenerate
# (welded fan) quads, so re-check flagged segments by dumping raw corners before
# concluding a real fold. The last ~2 "segments" are the shell/cap boundary — ignore.
```

Scan results: run 130041 DAE → 4 real folds (segs 1169/1174/1200/1205). Run 183350 DAE (with weld)
→ 0 real folds (10 zero-advance welded segments; remaining flags were scanner artifacts).

## 5. Why it can still look broken in-game (ranked)

1. **The DecalRoad layer is NOT welded.** The visible asphalt + lane markings are the DecalRoad,
   built from the same kinked cross-sections at full corridor width (~21.6 m). At the kink the
   decal ribbon self-overlaps/pleats exactly like the deck top used to — self-overlapping decal
   geometry renders as the same dark fan, and markings warp into it. The user ruled decals out
   earlier, but that was for the ORIGINAL artifact, which demonstrably was a real mesh fold — now
   that the mesh is clean, the decal pleat is the remaining renderer of the same source kink.
   Check: disable/hide the DecalRoad layer at the site (or compare a screenshot against the bare
   deck) — if the triangle vanishes, it's the decal.
2. **Game-side caching.** BeamNG caches compiled collision/shape data. The DAE on disk is new
   (18:37:48) but the level may need a full reload / cache clear before the new deck collision is
   active. If the car still sinks INTO the surface at the fan, suspect stale collision — the new
   fan is watertight.
3. **A second fold site elsewhere.** Only bridge_1192907311.dae was dissected. If the user's test
   spot differs from stations 342–353, scan that DAE/segment range first (method above).

## 6. Recommended next steps (in order)

1. **Get a fresh screenshot + exact position** after a full game restart (cache). Compare against
   the deck-only expectation: the deck itself now shows a flat fan (slight texture pinch, no hole).
2. **Fix the SOURCE kink** — this heals deck, decal, markings, and banking in one shot:
   - In `LateralCarriagewayMerger`, scale the run-boundary taper with separation:
     `taper = max(BoundaryTaperMeters, k · sep)` with k ≈ 8 (sep 15.3 ⇒ ~120 m ⇒ ~3.6° dogleg,
     safely under the 1/halfWidth fold limit AND visually smooth).
   - Alternatively/additionally: post-merge arc-blend any polyline corner sharper than ~8° on
     merged paths (the corner is at a known index — the run boundary).
   - Watch: the taper end must not collide with junctions/structure spans reprojection
     (OriginalStart/EndPoint anchors are taper-independent, so span stations survive).
3. If decal self-overlap remains after the source fix (kinks can also come from raw OSM), consider
   a decal-node simplification/weld at high-curvature points in the DecalRoad generator.

## 7. Reference data

- Preset: `d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json`
- Logs: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`
  (relevant runs: 122719, 130041, 180402, 183350)
- Deck DAE: `...\levels\manhattan\art\shapes\MT_bridges\bridge_1192907311.dae`
- Merged pair: `808611037 + 931223088` (trunk, sep 15.3 m); kink between ways `432550257`/`46177152`
- Fold site: span station ≈ 342–353, world ≈ `(-600..-614, -988..-1007)`, z ≈ 27.3–27.7
- Tests: `BridgeDeckMeshBuilderTests.AntiFoldWeld_*`, `SeamlessDeckOverlapExportTests.ButtSeamKink_*`
  / `MidDeckGoreLanding_DoesNotExtend` (876 total green)
