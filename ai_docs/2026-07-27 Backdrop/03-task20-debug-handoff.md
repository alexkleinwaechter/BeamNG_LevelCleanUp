# Task 20 Debug Handoff — Backdrop In-Game Findings

**Written:** 2026-07-28, after the user's first in-game validation run (Task 20).
**Branch state:** `feature/backdrop` at `bb0d7d3` (34 commits, final review clean), NOT merged/pushed.
Uncommitted in the tree: `docs/Backdrop-Tutorial.md`, `docs/Backdrop-Performance-Improvement-Plan.md`
(+ the usual `.claude/settings.json` / this folder's `00-status-and-handoff.md` — never commit the
settings file).
**Related but separate workstream:** generation is very slow — see
`docs/Backdrop-Performance-Improvement-Plan.md` (measure-first plan, ranked suspects). Do not mix
the perf work into the fixes below; keep commits separate.

Two defects observed in game. They are probably ONE root cause plus residuals — fix order matters.

---

## Finding B first: texture naming convention (`.color.png`) — **DONE, commit `77c16a3`** (2026-07-28)

> Implemented: planner emits `backdrop_{cx}_{cy}.color.png` (comment documents the cooker/sRGB
> reason); the 2 pinning test assertions + 5 fixture strings updated; 1140/1140 green; solution
> build zero `error CS` (only DLL-lock MSB noise — app was running). Tutorial §7 updated.
> **User: regenerate the backdrop (or full terrain run) so the level picks up the new names, then
> re-judge the brightness in game.** The section below is kept for reference.

**User requirement:** backdrop chunk textures must be named `backdrop_{cx}_{cy}.color.png` instead
of `backdrop_{cx}_{cy}.png`. The `.color` part triggers BeamNG's in-game texture cooker, which
converts the PNG into DDS.

**Codebase evidence this is the established convention:**
- `ai_agent_md_files_history_some_outdated/BeamNG_Materials_Documentation.md:98`:
  "`.color.png` → Compiled to BC7 **sRGB**"; `:505`: BC7 sRGB ↔ `.color.dds` for base color.
  Materials there reference the full name (e.g. `"diffuseMap[0]": ".../concrete_d.color.png"`).
- The building pipeline already ships `.color.png` everywhere
  (`BeamNgTerrainPoc/Terrain/StyleConfig/StyleConfigGenerator.cs:39ff`, `StyleConfigLoader.cs:131`).

**Where the name lives — single source, everything else flows from it:**

| Anchor | Role |
|---|---|
| `BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkPlanner.cs:123` — `TextureFileName = $"backdrop_{cx}_{cy}.png"` | THE source. Change to `backdrop_{cx}_{cy}.color.png`. |
| `BackdropSceneWriter.cs:215` — `Material.CreateWithTexture(..., "textures/" + chunk.TextureFileName)` | DAE `init_from` reference — flows automatically. Verify the Collada exporter doesn't mangle the double extension. |
| `BackdropSceneWriter.cs:279` — `["baseColorMap"] = texturePath + textureFile` | materials.json reference — flows automatically. |
| `MtBackdropChunk.TextureFile` (MT_settings block, written by `BackdropOrchestrator` from the plan) | Flows automatically. |
| `BackdropTextureBaker.cs:51` — `Path.Join(texturesDir, Path.GetFileName(chunk.TextureFile))` | Flows automatically (`GetFileName` keeps the double extension). |

**Tests that pin the old name (must be updated with the change — they are new-on-branch backdrop
tests, so editing them is legitimate):**
- `BeamNgTerrainPoc.Tests/Backdrop/BackdropChunkPlannerTests.cs:75` — asserts
  `$"backdrop_{Cx}_{Cy}.png"` verbatim.
- `BeamNgTerrainPoc.Tests/Backdrop/BackdropSceneWriterTests.cs:79` — asserts the `baseColorMap`
  path ends in `backdrop_0_1.png`.
- Fixture-only occurrences (no assertion on the convention, update for consistency):
  `BackdropSceneWriterTests.cs:29/31/126/168`, `BackdropQuadtreeMesherTests.cs:41`,
  `BackdropTriangulationTests.cs:40`, `BackdropQuadtreeMesherBalancePerfTests.cs:54`.

**Also update:** `docs/Backdrop-Tutorial.md` §7 file-layout tree (`backdrop_{cx}_{cy}.png` →
`.color.png`).

**Migration:** none needed — the feature is unreleased. Levels baked with the old name are cleaned
up by regeneration (`CleanPreviousOutputs` wipes `art/shapes/MT_backdrop/` including `textures/`)
or by Remove Backdrop. Tell the user to **Regenerate Backdrop** (or re-generate terrain) after the
fix; a stale `MT_settings.json` `TextureFile` entry from an old bake self-heals on the next
generation because the settings block is rewritten from the fresh plan.

**Verification:** `dotnet test … --filter "FullyQualifiedName~Backdrop"` green (suite total 1140);
`dotnet build BeamNG_LevelCleanUp.sln` zero `error CS`; in game: the cooker produces
`backdrop_*.color.dds` (check the game's texture cache/level folder) and the meshes are textured.

---

## Finding A: backdrop textures render far too bright / washed out

**Observation (screenshot in the session):** playable terrain shows normally (dark, saturated
forest); every backdrop chunk around it renders pale/washed-out — uniformly lifted, like a fog or
gamma shift, not a per-chunk or per-area artifact. Geometry, texture placement and orientation look
correct (roads/valleys continue across the seam — north-up chain works).

### Hypothesis 1 (primary — fix B and re-test before touching anything else)

**Missing `.color` suffix ⇒ no BC7 sRGB cook ⇒ the game samples the color texture as linear
data.** An sRGB-encoded image interpreted as linear comes out exactly like the screenshot:
uniformly brighter/washed out (effectively a ~1/2.2 gamma lift). This is the one mechanism that
explains a *uniform* shift across all chunks while the terrain (whose basecolor pipeline uses the
game's terrain texture path) stays correct.

→ **Action: implement Finding B, regenerate, re-check in game. Only if a brightness delta remains,
work the list below.**

### Hypothesis 2: adjustments desync between terrain and backdrop

The terrain basecolor and the backdrop chunks share provider/imagery date/brightness/contrast/
saturation, but they are baked at different moments:
- Backdrop chunks bake during generation (and on BaseColorManager **Apply BaseColor Mode** /
  **Reset & Rebake** via the interlock) with adjustments baked INTO the PNG
  (`BackdropTextureBaker` → `TerrainPbrMapBuilder.ApplyOverlayAdjustments`, applied only when
  `!result.ReusedFinalImage`; the `adj:` ExtraFingerprint forces a re-warp when values change).
- The terrain overlay PNG (`MT_Tiles/{slug}-terrain-warp-v2.png`) is RAW; adjustments are applied
  later during the basecolor bake.

**Diagnostic:** open a backdrop chunk PNG and the terrain-warp PNG side by side. The chunk PNG
must differ from the raw warp exactly by the configured adjustments. If a chunk PNG is *raw*
although adjustments are configured, the adjustment step was skipped → check `MT_settings.json`
`BackdropSettings.LastTextureBakeUtc/LastBakeProvider/LastBakeImageryDate` vs the overlay settings,
and reproduce via a Reset & Rebake (watch for the staleness banner: "the backdrop textures no
longer match the provider or georeference").

### Hypothesis 3: material/shader response (only if 1+2 come back clean)

`BackdropSceneWriter.CreateMaterialEntry` (`BackdropSceneWriter.cs:273-291`) writes a PBR v1.5
material — one textured stage, doc comment says "untinted, fully rough — the texture IS the
color". If brightness remains after B:
- Diff the emitted material JSON against a known-good satellite-textured static material
  (building materials in `BeamNG_Materials_Documentation.md:368ff` are the closest precedent) —
  suspect fields: missing/wrong roughness/metallic factors, a non-1.0 color factor, emissive.
- Check lighting response: rotate time-of-day in the editor. A uniform lift that changes with sun
  angle points at material response; one that doesn't points back at gamma (H1).
- Normals: Task 8 left a known cosmetic "skirt-top shading normal curl"; whole-surface normal
  problems would show as slope-dependent shading, not a uniform lift — low probability.

### Hypothesis 4: the §10 design caveat (expected residual, not a bug)

Terrain basecolor = satellite **blended** with terrain materials per material blend factor;
backdrop = pure satellite. At Global Overlay Blend < 100 % a *tint* difference at the seam is
expected and documented (design `01-design.md` §10 "Known appearance caveat", tutorial §4). This
explains small tint/saturation steps at the boundary — it does NOT explain the massive uniform
wash-out in the screenshot. Ask the user for their blend value when evaluating what remains
after H1–H3.

---

## Finding C: "textures on wrong coordinates or rotation" (2026-07-28, second in-game report) — INVESTIGATED, NO PIPELINE DEFECT FOUND

User screenshot showed backdrop pieces with mismatched seams, a light-blue gap line, and the terrain
(green X) not where the imagery suggests. Systematic offline audit of the ACTUAL level
(`levels/rossfeldpanorama`, bake of 12:03–12:16) verified EVERY link of the chain correct:

- **Chunk PNGs are exactly right** — proven numerically, not visually: gradient-anisotropy ratio of
  `backdrop_1_0` (2049×511 window in a 2048² texture) = 3.87 raw → 1.19 after 4× vertical downscale
  (calibration square chunk = 1.15); `backdrop_0_1` = 0.35 → 1.03. I.e. the windows are correctly
  anisotropically stretched into the square textures (visual inspection MISJUDGES this — do not
  eyeball stretch in forest imagery, measure it).
- Warp-fingerprint sidecars carry the correct per-chunk parameters (2049×511, right geotransform,
  WGS84 WKT); `CreateWarpedOverlay` maps X/Y with independent scales (correct); registry
  bbox↔SourceRect↔Cy all coherent (Cy grows north, srcY south; ~1 m/px).
- DAE vertex bounds = exact world rects (e.g. (0,0) = [-1535,-1024]²) abutting the TerrainBlock
  (position [-1024,-1024], 2048 px @ 1 m/px) — datum correct.
- DAE UVs analytically exact for all 33 296 vertices: u = west→east, t = 1−v (t=0 at NORTH),
  1:1 vertex↔UV binding, `mt_backdrop_{cx}_{cy}` bound per-triangle; materials.json → `.color.png`
  paths all correct.
- Game cache FRESH: `.cdae` 12:37–12:38, cooked `.color.dds` 12:42 — compiled from the current files
  (stale-cache hypothesis dead).

**What WAS found:** `main/MissionGroup/MT_backdrop/items.level.json` (and the parent) were REWRITTEN
BY THE BEAMNG WORLD EDITOR at 12:55:53 — our writer's `position:[0,0,0]`/`rotationMatrix`/
`isRenderEnabled:true` fields are gone, and the entries now carry **`isRenderEnabled:false`** (the
backdrop is hidden in the current level state!) plus **`collisionType:"None"`/`decalType:"None"`**
(drivability killed — spec wants full collision). This is an editor-session save (visibility-eye
toggled off while debugging + save). Placement-wise the entries are still neutral (missing
position/rotation default to origin/identity).

**Conclusion:** the screenshot almost certainly shows a MIXED editor-session state (game was open
across regenerations; editor in-memory scene ≠ freshly-loaded disk state), not a generation defect.
Decisive protocol for the user (clean observation):
1. In the editor scene tree, re-enable rendering on the `MT_backdrop` group/objects (or simply
   **Regenerate Backdrop** — the tool rewrites items.level.json with correct flags) and remove the
   `collisionType`/`decalType` overrides so collision returns.
2. Close BeamNG COMPLETELY. Optionally delete the level's compiled cache
   (`…\BeamNG.drive\current\temp\levels\rossfeldpanorama`) — safe, it regenerates.
3. Cold-start, load the level, view from above with known orientation (editor top view).
4. If seams are now continuous → transient editor state, case closed.
5. If STILL displaced/rotated on a clean load: stand at the WEST strip and compare in-game rendering
   against `backdrop_0_1.color.png` opened in a viewer. ONE question: is the in-game content this
   image MIRRORED top-bottom (in place)? If yes → the game samples cooked-DDS V inverted vs raw PNG →
   one-line fix candidate: drop `FlipUVVertical` (+ re-pin the two texcoord tests) — apply ONLY on
   this confirmed observation. If the content is from elsewhere instead → report back with the
   screenshot + camera orientation; do NOT change the flip.

**Feature learnings to keep:** an editor save NORMALIZES our TSStatic entries (drops
position/rotation, persists visibility/collision overrides). Harmless for placement (defaults =
origin/identity) but it can silently persist "hidden"/"no collision". `RemoveBackdrop`'s NDJSON
filter still matches (name/class survive). Consider (post-merge): writer emitting
`collisionType`/`decalType` explicitly so editor round-trips keep drivability.

## Suggested session plan

1. Implement Finding B (planner constant + 2 test assertions + fixture strings + tutorial §7).
   One commit: `fix(backdrop): .color texture suffix so the game cooker compiles chunk PNGs to sRGB DDS`.
2. Build + backdrop-filtered tests + full suite (1140).
3. User regenerates + re-checks in game (brightness expected fixed or greatly reduced).
4. If residual brightness: work H2 → H3 diagnostics above; H4 is the documented floor.
5. Update `00-status-and-handoff.md` (Session 5 log) + the SDD ledger is NOT the place for this —
   Task 20 findings live here and in the status doc.

## Do-not-break notes for whoever picks this up

- Core suite must stay green; only the backdrop-owned tests listed above may change (they pin the
  convention being changed — that's the point).
- Never stage `.claude/settings.json`. Watch the raw-byte literals if touching
  `CropAnchorSelector.razor(.cs)` (0xD7/0xB0/0xA9 corrupt on naive editor round-trips).
- Determinism guarantee: identical inputs ⇒ identical outputs still holds after the rename (name is
  part of the output, changed intentionally).
- No AI attribution / Co-Authored-By in commits.
