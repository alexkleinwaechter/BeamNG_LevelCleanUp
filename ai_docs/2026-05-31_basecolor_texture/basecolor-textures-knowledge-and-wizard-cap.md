# Base-color terrain textures — domain knowledge + the 2026-05-31 wizard cap

Status: reference / handoff for a larger upcoming feature.
Branch where the work landed: `bugfix/smaller_basecolor_textures` → PR #119 (base `develop`), commit `be9261e`.

This doc captures (1) what base-color terrain textures *are* and the BeamNG rules
around them, (2) how this app generates and sizes them today, (3) the exact code
paths and where size is decided, and (4) what we changed in this session and why.
It is meant to be enough context to design a bigger feature on top without
re-discovering the plumbing.

---

## 1. What base-color textures are (the user's domain knowledge)

- In BeamNG, to **paint a PBR terrain material in the in-game terrain editor**, each
  terrain material needs a full set of PBR "base" textures present: base color, ao,
  height, normal, roughness. Without them you can't paint that material onto the
  terrain.
- This app generates **placeholder one-color PNGs** for each terrain material so the
  material is paint-ready. The generated files start with `#base` (e.g.
  `#base_color_*.png`, `#base_ao.png`, `#base_h.png`, `#base_nm.png`, `#base_r_*.png`).
- **The hard BeamNG rule:** *all base-color textures in a terrain must have the same
  dimensions.* (They are the per-material "base" layer; the engine treats them as a
  set and they must agree in size.)
- The base-color texture is **scaled (not tiled)** across the terrain. So a 1024 base
  texture on a 2048 terrain simply means each base-texture pixel covers a 2×2 block of
  terrain pixels. See `BeamNgTerrainPoc/Terrain/ColorExtraction/MaskedColorCalculator.cs`
  (header comment) — it explicitly documents the scaled-not-tiled relationship.
- **Why size matters (the problem we fixed):** "Normally we would have one big
  base-color texture for all materials." If you instead give *every* material its own
  base-color texture at full terrain size (e.g. 4096/8192), and you have many
  materials, the GPU has to hold many huge textures → **VRAM blowup**, even though each
  one is a single flat color. The proven mitigation: in the wizard, **cap the
  base-color texture size at 1024**. A small shared size is valid (rule above) and
  lightweight, and because base color is scaled to the terrain, a flat 1024 placeholder
  looks identical to a flat full-size one.
- **Key conceptual separation to keep in mind for the bigger feature:**
  - *Heightmap / terrain size* = the `.ter` resolution and the `"size"` in
    `*.terrain.json`. This is the real terrain dimension.
  - *Base-color texture size* = `TerrainMaterialTextureSet.baseTexSize` (and the
    per-material `baseColorBaseTexSize`). This is **independent** and can/should be
    smaller. Historically the code conflated the two; the fix decouples them for the
    wizard.

---

## 2. Where size lives in BeamNG files

For a terrain material set, three things must agree on the base-color size:

1. **The generated PNG dimensions** — the actual `#base_color_*.png` pixel size.
2. **The per-material size property** in the terrain `main.materials.json`:
   `baseColorBaseTexSize` (and siblings `aoBaseTexSize`, `heightBaseTexSize`,
   `normalBaseTexSize`, `roughnessBaseTexSize`).
3. **The master `TerrainMaterialTextureSet`** object in `art/terrains/main.materials.json`:
   - `baseTexSize`  → `[N, N]`  ← the base-color size (the one we cap)
   - `detailTexSize` → `[N, N]` ← PBR *detail* maps (tiled), separate concept
   - `macroTexSize` → `[N, N]`  ← macro maps, separate concept

The heightmap size is a *different* field: `"size"` in `*.terrain.json`.

> Detail/macro textures are the real tiling PBR maps and are NOT the thing that blows
> up VRAM here — only the per-material **base** textures are. When capping, cap
> `baseTexSize` / base PNGs; leave detail/macro to their own (source-derived) sizes.

---

## 3. Code map — how base-color textures are generated & sized today

### 3.1 The generator

`BeamNG_LevelCleanUp/LogicCopyAssets/TerrainTextureGenerator.cs`
- Constructor takes `(string terrainFolderPath, int terrainSize)` and stores
  `_terrainSize`. **There is no per-call size argument** — the size is fixed for the
  generator instance.
- `GenerateSolidColorPng(hexColor, baseFileName, textureType, fileNameSuffix?, customGreyscaleValue?)`
  writes one flat PNG at `_terrainSize × _terrainSize` (`GenerateRgbaImage` /
  `GenerateGrayscaleImage` / `GenerateNormalMapImage`).
- The texture catalog (color + filename + type) is the static `TextureDefinitions`
  dictionary: `baseColorBaseTex` → `#base_color` (#808080 default), `aoBaseTex` →
  `#base_ao` (#FFFFFF), `heightBaseTex` → `#base_h` (#000000), `normalBaseTex` →
  `#base_nm` (#8080FF), `roughnessBaseTex` → `#base_r` (#EFEFEF).
- Filename rules: base color embeds the hex (`#base_color_{hex}{suffix}`); roughness
  embeds the grayscale value (`#base_r_{value}{suffix}`); others use the plain name.
- **Gotcha:** if the output file already exists it is *not* regenerated
  (`if (File.Exists(outputPath)) return;`). Filenames don't encode size, so a stale
  full-size PNG from a previous run in the same temp folder will be reused. Clean temp
  between runs when testing size changes.

### 3.2 The one and only call site

`BeamNG_LevelCleanUp/LogicCopyAssets/TerrainTextureHelper.cs` →
`CopyTerrainTextures(material, materialObj, targetTerrainFolder, baseColorHex,
roughnessValue, int? terrainSize, …)`:
- Builds the generator only if `terrainSize.HasValue`:
  `new TerrainTextureGenerator(targetTerrainFolder, terrainSize.Value)`.
- For each replaceable map type it calls `GenerateSolidColorPng(...)` and then writes
  the matching size property back into the material:
  `materialObj[matFile.MapType + "Size"] = terrainSize.Value;`
  → so the **PNG size and the per-material `*Size` property always come from the same
  `terrainSize` value** (they can't drift).
- `/assets/` paths (core game assets) are skipped — never copied or rewritten.

So: **whoever passes `terrainSize` into `CopyTerrainTextures` decides the base-color
size.** Two callers do.

### 3.3 Caller A — add a terrain material (`TerrainMaterialCopier`)

`BeamNG_LevelCleanUp/LogicCopyAssets/TerrainMaterialCopier.cs`, `Copy(...)`:
```csharp
_baseTextureSize ??= PathResolver.WizardTerrainSize
                     ?? TerrainTextureHelper.LoadBaseTextureSize(_targetLevelPath);
```
Then passes `_baseTextureSize` into `CopyTerrainTextures`.

### 3.4 Caller B — replace a terrain material (`TerrainMaterialReplacer`)

`BeamNG_LevelCleanUp/LogicCopyAssets/TerrainMaterialReplacer.cs`, `Replace(...)`:
```csharp
_baseTextureSize ??= TerrainTextureHelper.LoadBaseTextureSize(_targetLevelPath);
```
(No wizard branch — replace is a standalone operation.)

### 3.5 The master `TerrainMaterialTextureSet.baseTexSize`

Set separately in `BeamNG_LevelCleanUp/LogicCopyAssets/AssetCopy.cs`
(`CopyTerrainMaterialsBatch`), **only** when the target terrain `materials.json` is
empty or a PBR upgrade was requested:
```csharp
var terrainSize = PathResolver.WizardTerrainSize
                  ?? TerrainTextureHelper.GetTerrainSizeFromJson(PathResolver.LevelNamePath)
                  ?? 1024;
// → pbrUpgradeHandler.AddTerrainMaterialTextureSet(terrainSize, detail, macro)
```
`PbrUpgradeHandler` (`AddTerrainMaterialTextureSet` / `UpdateTerrainMaterialTextureSetSize`
/ `EnsureTerrainMaterialTextureSetSize`) writes/updates the `baseTexSize` array.

### 3.6 Size-reading helpers (`TerrainTextureHelper`)

- `LoadBaseTextureSize(levelPath)` = `GetBaseMaterialSize(levelPath)` ??
  `GetTerrainSizeFromJson(levelPath)` ?? `2048`.
- `GetBaseMaterialSize` → first element of `TerrainMaterialTextureSet.baseTexSize` in
  `art/terrains/*.materials.json` (via `GetAllTextureSizes`).
- `GetTerrainSizeFromJson` → `"size"` from `*.terrain.json` (this is the **heightmap**
  size, used as a fallback only).

---

## 4. Wizard vs standalone — how they differ

The discriminator is the static flag `PathResolver.WizardTerrainSize` (nullable int).

- **Wizard mode:** `CopyTerrains.razor.cs` sets
  `PathResolver.WizardTerrainSize = WizardState.TerrainSize` right before copying, and
  clears it (`= null`) afterward / on cancel / on wizard cleanup. So during the wizard
  copy step, Caller A and §3.5 both read the wizard value.
- **Standalone mode:** `WizardTerrainSize` is `null`, so the size comes from the
  **target (existing) map** via `LoadBaseTextureSize` → the existing
  `baseTexSize` (then heightmap `size`, then 2048). This is exactly the desired
  "stick to the existing map's settings" behavior, and it already worked.

Important nuance discovered: despite its name, `PathResolver.WizardTerrainSize` is
**only** consumed by the two base-color-sizing sites (Caller A §3.3 and the texture-set
size §3.5). It is **not** read by heightmap/`.ter` generation. So it is effectively
"the base-color texture size for copied terrain materials in wizard mode," not a
terrain dimension.

### `WizardState.TerrainSize` consumers (full list, verified)

1. `CopyTerrains.razor.cs` → `PathResolver.WizardTerrainSize` (base-color sizing).
2. `GenerateTerrain.razor.cs` `LoadLevelFromWizardState()` → seeds the GenerateTerrain
   step's `_terrainSize` **as a fallback only**: used when there is no existing
   `terrain.json` (`result.ExistingTerrainSize`) and no GeoTIFF-suggested size. It is
   otherwise overridden by existing terrain config, GeoTIFF, or the user's own Terrain
   Size dropdown on that page.
3. (Previously) the Terrain Size `<MudSelect>` on the first wizard form — removed this
   session.

Everything else in `GenerateTerrain` uses the page's own `_state.TerrainSize`
(`TerrainGenerationState`, default 2048), independent of the wizard value.

---

## 5. What we changed this session (and why)

Goal: stop the wizard from generating per-material base-color PNGs at full terrain
size (VRAM blowup). Decision: cap the wizard base-color size at **1024** and stop
asking the user for a terrain size on the first wizard form (it was redundant — the
real terrain size is chosen on the GenerateTerrain step).

Changes (commit `be9261e`):

1. **`BeamNG_LevelCleanUp/Objects/CreateLevelWizardState.cs`**
   - Added `public const int WizardBaseColorTextureSize = 1024;` with a doc comment
     explaining the VRAM rationale.
   - `public int TerrainSize { get; set; } = WizardBaseColorTextureSize;` (was `2048`).
   - `Reset()` now re-applies `TerrainSize = WizardBaseColorTextureSize;` so the static
     wizard-state singleton stays at 1024 across runs.

2. **`BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`**
   - Removed the "Terrain Size" `<MudSelect>` from the first wizard form (kept the
     surrounding `MudPaper` and the Initialize button).

Net effect: in the wizard, `WizardState.TerrainSize` is always 1024, which flows
unchanged through `PathResolver.WizardTerrainSize` into:
- the `#base_*` PNG dimensions (via §3.2 generator),
- the per-material `baseColorBaseTexSize` (and siblings) (via §3.2),
- the master `TerrainMaterialTextureSet.baseTexSize` (via §3.5).

All three agree at 1024 → BeamNG's "same dimensions" rule satisfied, VRAM kept low.

Side effect (accepted): the GenerateTerrain step's *fallback default* heightmap size in
wizard mode is now 1024 instead of 2048 — only relevant when no terrain.json/GeoTIFF is
present, and the user can still pick any size there.

Standalone copy path: unchanged.

Build verified green. The unrelated working-tree items in the same commit (a one-line
`UnifiedRoadSmoother.cs` tweak and some `ai_docs/` relocations) were swept in at the
maintainer's request and are noted in the PR.

---

## 6. Open items / hooks for the bigger feature

These are the natural extension points if/when base-color sizing becomes a first-class
feature (e.g. user-selectable base-color resolution, or auto-sizing per material):

- **Rename for clarity.** `PathResolver.WizardTerrainSize` is misnamed — it is the
  base-color texture size for the wizard, not a terrain dimension. Consider
  `WizardBaseColorTextureSize` to remove the conceptual conflation. (Left as-is now to
  keep the change minimal.)
- **Decouple fully.** The two size sources (Caller A `_baseTextureSize` via
  `LoadBaseTextureSize`, and §3.5 `terrainSize` via `GetTerrainSizeFromJson`) are
  computed independently. They agree in the common cases, but there is a latent
  edge case: a **standalone copy into an empty / PBR-upgraded** target seeds the
  texture-set `baseTexSize` from the heightmap `size` (§3.5) while the PNGs/per-material
  size come from the existing `baseTexSize` (§3.3). In the normal "copy into an existing
  populated map" path the §3.5 block is skipped, so they don't drift. A unified
  "resolve base-color size" helper would remove this hazard.
- **Stale-PNG cache.** `GenerateSolidColorPng` skips regeneration if the file exists and
  filenames don't encode size. If a feature lets users change base-color size, generated
  files must be invalidated (size in filename, or clear the target folder) or old-size
  PNGs will be silently reused.
- **Detail/macro sizes.** Out of scope for the VRAM fix but part of the full PBR
  texture-set story: `detailTexSize` / `macroTexSize` (tiled maps) are sized separately
  (from the source level when available). A complete base-texture feature should decide
  these explicitly too.
- **Standalone consistency.** If the feature adds a base-color size control to the
  standalone GenerateTerrain/Copy pages, it should write through the same three places
  (PNG, per-material `*Size`, texture-set `baseTexSize`) to keep them in lockstep.

---

## 7. Quick reference — file index

| Concern | File |
|---|---|
| PNG generation (flat color, fixed size) | `LogicCopyAssets/TerrainTextureGenerator.cs` |
| Only call site + writes per-material `*Size` | `LogicCopyAssets/TerrainTextureHelper.cs` (`CopyTerrainTextures`) |
| Size readers (`LoadBaseTextureSize`, `GetBaseMaterialSize`, `GetTerrainSizeFromJson`, `GetAllTextureSizes`) | `LogicCopyAssets/TerrainTextureHelper.cs` |
| Add terrain material (base-color size A) | `LogicCopyAssets/TerrainMaterialCopier.cs` |
| Replace terrain material (base-color size B) | `LogicCopyAssets/TerrainMaterialReplacer.cs` |
| Texture-set `baseTexSize` write | `LogicCopyAssets/AssetCopy.cs` + `LogicCopyAssets/PbrUpgradeHandler.cs` |
| Wizard size knob (static flag) | `Logic/PathResolver.cs` (`WizardTerrainSize`) |
| Wizard state + the 1024 constant | `Objects/CreateLevelWizardState.cs` |
| Wizard sets/clears the knob | `BlazorUI/Pages/CopyTerrains.razor.cs` |
| First wizard form (size field removed) | `BlazorUI/Pages/CreateLevel.razor` |
| GenerateTerrain page size handling | `BlazorUI/Pages/GenerateTerrain.razor(.cs)`, `BlazorUI/State/TerrainGenerationState.cs` |
| Base texture is scaled-not-tiled (color extraction) | `BeamNgTerrainPoc/Terrain/ColorExtraction/MaskedColorCalculator.cs` |
