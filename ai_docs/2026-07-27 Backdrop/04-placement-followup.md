# Backdrop Placement Investigation — Follow-up / New-Session Handoff

**Written:** 2026-07-28 (end of the placement-debugging session).
**Branch:** `feature/backdrop`, HEAD `253d7c5` (naming fix `77c16a3`, perf plan §0.1+§1–§4, tooltips
`a4cd275` all landed; 1140 core tests green). NOT merged, NOT pushed.
**Level analyzed:** `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\rossfeldpanorama`
(bake of 2026-07-28 12:03–12:16).
**Related docs (same folder):** `03-task20-debug-handoff.md` (Finding C = short form of this),
`Backdrop-Performance-Improvement-Plan.md`, `Backdrop-Tutorial.md`, `00-status-and-handoff.md`.

> **RESOLVED 2026-07-28 — Outcome B CONFIRMED in game.** The user ran the flip experiment
> preemptively: `BackdropSceneWriter` exports with **`FlipUVVertical = false`** (texcoord test
> re-pinned: `ExportChunkDae_ExportsUVsUnchanged_NoVerticalFlip`; baker doc note updated), and the
> regenerated backdrop **renders in the correct rotation** ⇒ the engine samples the cooked `.color`
> DDS with V inverted relative to the raw north-up PNG; `false` is the shipped convention.
> Reconciliation of the seeming contradiction with the audit above: the first in-game run (which
> rendered correctly with `true`, line "the first in-game run … did render placement correctly")
> predated the `.color` naming fix `77c16a3` — the game then sampled raw PNGs without the DDS cook,
> so BOTH observations were correct for their era; the flip requirement changed when the cooker
> entered the chain. Revisit the flag only if the `.color` cook ever leaves the texture pipeline.
> The collision toggle (`05-collision-toggle-followup.md`, default OFF) landed in between — a
> fall-through when driving onto the backdrop is that default, not a placement symptom.

---

## TL;DR for a fresh session

The user reported backdrop chunk textures "on wrong coordinates or in the wrong rotation" (in-game
screenshot: mismatched seams between chunks, a light-blue gap line, terrain not where the imagery
suggests). A full offline audit of the actual level found **NO defect in the generation pipeline —
every artifact is provably correct**. The real finding: a **BeamNG world-editor save (12:55:53)
rewrote our scene entries** — the backdrop is currently `isRenderEnabled:false` (hidden) with
`collisionType:"None"` (no drivability) in the level on disk, and the screenshot almost certainly
shows a mixed in-memory editor state after a morning of regenerating underneath a running game.

**Do not change any pipeline code for this issue unless the clean-load protocol below reproduces it.**

## Timeline of the day (from file mtimes)

| Time | Event |
|---|---|
| 12:03–12:15 | Tool generates the 8 chunk DAEs (3×3 grid minus center) |
| 12:15–12:16 | Texture bake writes the 8 `.color.png` + `.meta.json` sidecars |
| 12:37–12:38 | Game compiles `.cdae` (its cache: `…\current\temp\levels\rossfeldpanorama`) |
| 12:42 | Game cooks `.color.dds` — cache is FRESH, built from the current files |
| **12:55:53** | **World editor saves the level** — rewrites both `items.level.json` files |
| 13:00 | `MT_settings.json` saved (tool side) |
| ~13:0x | User posts the screenshot |

## What was verified correct (evidence, not assumption)

| Chain link | Method | Result |
|---|---|---|
| Chunk PNGs contain the right windows | Gradient-anisotropy measurement (see §Reusable checks) | `backdrop_1_0` (2049×511 window in 2048² texture): ratio **3.87** raw → **1.19** after 4× vertical downscale; `backdrop_0_1` (511×2049): **0.35** → **1.03**; square calibration chunk `backdrop_0_0`: **1.15**. ⇒ windows are exactly anisotropically stretched into the squares, as designed. **Visual inspection MISJUDGES this — the stretched strips look like normal square areas. Never eyeball stretch in forest imagery; measure.** |
| Bake request parameters | `.meta.json` warp-fingerprint sidecars | Correct per-chunk W×H (2049×511 etc.), correct translated geotransform, WGS84 WKT present |
| Warp math | Code read (`MapTileOverlayService.CreateWarpedOverlay` :337-372) | X/Y scaled independently (`sourceWidth/outputSize`, `sourceHeight/outputSize`) — correct for non-square windows |
| Chunk registry | `MT_settings.json` BackdropSettings dump | bbox ↔ SourceRect ↔ (Cx,Cy) coherent; Cy grows north, srcY south; ~1 m/px; rows/columns tile exactly (edges 7167/9216 shared) |
| DAE world placement | Parsed `float_array` bounds of `backdrop_0_0.dae` | X,Y ∈ [-1535,-1024]² — the exact SW ring rect abutting the terrain |
| Terrain datum | `items.level.json` TerrainBlock + MT_settings georef | TerrainBlock position [-1024,-1024], 2048 px @ 1 m/px ⇒ terrain [-1024,+1024]², ring abuts it exactly |
| DAE UVs | Analytic correlation over all 33 296 vertices | max residual **0.0**: u = (x−minX)/w (west→east), t = 1−(y−minY)/h (**t=0 at NORTH** — FlipUVVertical applied), 1:1 vertex↔UV binding, `mt_backdrop_0_0` bound per-triangle |
| materials.json | Read | All 8 materials → correct `…/textures/backdrop_{cx}_{cy}.color.png` |
| Game cache staleness | mtimes | `.cdae`/`.dds` postdate the bake ⇒ compiled from current files. Stale-cache hypothesis dead |
| Perf commits (`cdb262c..4b7c3cd`) | Diff review | All inert for placement: timing line, BucketSize=4 (query partitioning only), MeshChunk out-leaves (debug reuse), BorderSets hoist (same four Subdivide calls, same axes), band-raster array (same iteration order) |

Also confirmed from repo history: `ai_docs/2026-04-23-fbx-to-dae-converter.md:19` — BeamNG samples
DAE texcoords with **V=0 at the texture TOP (DirectX convention)**, empirically established. With
north-up PNGs and t=0-at-north UVs, the shipped convention is right — and the first in-game run
(brightness screenshot) did render placement correctly with these exact conventions.

## The real finding: world-editor rewrite

`main/MissionGroup/MT_backdrop/items.level.json` (and the parent) were saved by the editor at
12:55:53. Our writer's entries
(`position:[0,0,0]`, `rotationMatrix:identity`, `isRenderEnabled:true`, no collision overrides —
see `BackdropSceneWriter.CreateTSStaticEntry`, :254-269) became:

```json
{"name":"backdrop_0_0","class":"TSStatic","persistentId":"…","__parent":"MT_backdrop",
 "collisionType":"None","decalType":"None","isRenderEnabled":false,
 "shapeName":"/levels/rossfeldpanorama/art/shapes/MT_backdrop/backdrop_0_0.dae",
 "useInstanceRenderData":true}
```

- `isRenderEnabled:false` — backdrop hidden (an editor visibility-eye toggle got persisted).
- `collisionType:"None"` — drivability killed (spec wants full collision; the DAEs contain a
  `Colmesh-1` collision mesh that TSStatic default collision would use).
- Missing `position`/`rotationMatrix` — harmless (defaults are origin/identity; meshes are
  world-baked).
- `RemoveBackdrop`'s NDJSON filter still matches these rewritten lines (name/class survive).

## Protocol (user-side, ~5 minutes) and decision tree for the next session

1. Restore the backdrop: easiest is **Regenerate Backdrop** in the app (rewrites items.level.json
   with correct flags AND restores collision). Alternative: re-enable rendering in the editor scene
   tree and delete the `collisionType`/`decalType` overrides by hand.
2. Close BeamNG **completely**. Optionally delete
   `…\BeamNG.drive\current\temp\levels\rossfeldpanorama` (compiled cache — safe, regenerates).
3. Cold-start the game, load the level, view from the editor top view (known orientation).
4. Judge BOTH open issues in the same look: placement/seams AND the original brightness question
   (the `.color` sRGB cook is active in this bake — compare backdrop vs terrain at high overlay
   blend).

**Outcome A — seams continuous, placement correct:** case closed (it was editor-session state).
Continue with the Task 20 checklist in `00-status-and-handoff.md` Session 4.

**Outcome B — still wrong, and the west strip's in-game content equals `backdrop_0_1.color.png`
mirrored top-to-bottom IN PLACE:** the game samples cooked DDS with inverted V vs raw PNG. Fix:
set `FlipUVVertical=false` in `BackdropSceneWriter`'s export options, update the class-doc
convention note and `BackdropTextureBaker`'s doc comment, re-pin the texcoord test
(`BackdropSceneWriterTests.cs` ~:144 pins the flip decision), regenerate, re-verify in game.
One line + test pins — but ONLY on this confirmed observation.

**Outcome C — still wrong but NOT an in-place mirror (content from elsewhere / rotated):** do not
touch the flip. Collect: a screenshot with known camera orientation, which chunk (stand at a border
and note the world position), and re-run the reusable checks below against the new bake. Then
investigate with that data — the offline chain was clean, so suspect the game-side shape pipeline
next (e.g. cdae compile of multi-material ordering) with concrete evidence.

## Reusable checks (run against any level's backdrop)

Registry dump:
```
python -c "import json; b=json.load(open(r'<level>\MT_settings.json',encoding='utf-8'))['BackdropSettings']; [print(c['Cx'],c['Cy'],c['MinLongitude'],c['MaxLatitude'],c['SourceRectX'],c['SourceRectY'],c['TextureFile']) for c in b['Chunks']]"
```

Gradient-anisotropy (stretch detector; needs `pip install pillow numpy`). Ratio ≈ window
aspect raw, ≈ calibration (~1.1) after resizing to the window aspect ⇒ bake correct:
```python
from PIL import Image; import numpy as np
def r(img):
    a = np.asarray(img.convert('L'), float)
    return np.abs(np.diff(a,1)).mean() / np.abs(np.diff(a,0)).mean()
i = Image.open(r'<...>\backdrop_1_0.color.png')   # e.g. 2049x511 window
print(r(i), r(i.resize((2048,512), Image.LANCZOS)))
```

DAE UV correlation (residuals must be ~0 for u=(x−minX)/w and t=1−(y−minY)/h under 1:1 binding):
see the session transcript / adapt the snippet in Finding C of `03-task20-debug-handoff.md`.

## Post-merge follow-ups recorded

- `BackdropSceneWriter` should emit `collisionType`/`decalType` explicitly so an editor save cannot
  silently persist "no collision" (add to the post-merge hardening bundle in the SDD ledger).
- Tutorial candidate note: "don't keep the level open in BeamNG while regenerating; reload after
  every regeneration" — editor sessions across regens produce exactly this class of confusion.

## Standing rules (unchanged)

Core suite must stay green (1140); no AI attribution / Co-Authored-By in commits; never stage
`.claude/settings.json`; raw-byte literals in `CropAnchorSelector.razor(.cs)` corrupt on naive
editor round-trips — verify bytes after any edit there.
