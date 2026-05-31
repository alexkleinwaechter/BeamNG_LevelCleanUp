# Basecolor Manager — Implementation Plan & Handoff (2026-05-31)

Branch context: created from `feature/basecolor_generator`. Companion knowledge doc:
`ai_docs/2026-05-31_basecolor_texture/basecolor-textures-knowledge-and-wizard-cap.md` (read it first —
it explains base-color vs heightmap size, the three places size lives, and the scaled-not-tiled rule).

User-confirmed decisions: **Tabs** UI · **heightmap-derived** Normal/AO/Height + per-region Roughness
(not flat placeholders) · **both modes** delivered end-to-end · map is selected as an **unpacked folder
operated on in place** (like `GenerateTerrain.razor`, NOT zip-extract/deploy like `CopyTerrains`).

---

## Context

In BeamNG, painting a PBR terrain material in the in-game terrain editor requires each material to have
its **own** set of "base" PBR textures (base color, normal, roughness, AO, height). Many finished maps
instead use **one shared base-color texture** that bakes all materials' colors into a single image — it
looks correct but makes the terrain **unpaintable** (every material shows the same baked colors).

This feature is a new page giving the user **two switchable modes** for their own map:

- **Paint Mode** — replace the terrain's base textures with per-material **single-color** placeholders
  (1024px) so every material becomes paintable in-game. Colors/roughness chosen per material, remembered.
- **BaseColor Mode** — regenerate a **merged full-terrain-size** base-color texture from the per-material
  colors painted into the `.ter` regions (plus derived Normal / Roughness / AO / optional Height), shared
  by all materials. Pretty, not paintable.

State persists in a new per-map **`MT_settings.json`** in the map root so the user can re-open the map
and switch modes with context. On first load (no settings), per-material colors + roughness are
auto-extracted from the `.ter` exactly like `CopyTerrains` does.

---

## What exists and gets reused (no rewrites)

| Need | Reuse |
|---|---|
| Generate flat-color PNG (RGBA/grayscale/normal) | `LogicCopyAssets/TerrainTextureGenerator.cs` → `GenerateSolidColorPng(...)` (public) |
| Scan terrain materials from `art/terrains/*.materials.json` | `LogicCopyAssets/TerrainCopyScanner.cs` → `ScanTerrainMaterials()` |
| First-run color extraction from `.ter` | `TerrainCopyScanner.ExtractTerrainMaterialColors(levelPath, copyAssets)` |
| First-run roughness extraction | `TerrainCopyScanner.ExtractTerrainMaterialRoughness(...)` |
| Read `.ter` raw binary (Size, `MaterialData byte[]`, `HeightData ushort[]`, `MaterialNames`) | `BeamNgTerrainPoc/.../LayerMaskReader.cs` → `ReadTerrainBinary()`; `Grille.BeamNG.Lib/IO/Binary/TerrainV9Binary.cs` |
| Per-material boolean masks | `LayerMaskReader.ReadLayerMasks()` |
| Find terrain materials.json / terrain size | `LogicCopyAssets/TerrainTextureHelper.cs` → `FindTerrainMaterialsJsonPath`, `GetTerrainSizeFromJson` |
| Write/ensure `TerrainMaterialTextureSet.baseTexSize` + TerrainBlock ref | `LogicCopyAssets/PbrUpgradeHandler.cs` → `EnsureTerrainMaterialTextureSetSize(size)` |
| Relaxed-JSON read / write-back | `Utils/JsonUtils.GetValidJsonNodeFromFilePath`, `Objects/BeamJsonOptions` |
| UI list + color picker + roughness controls | model after `BlazorUI/Pages/CopyTerrains.razor` rows; reuse `Objects/CopyAsset.cs` (`BaseColorHex`, `RoughnessPreset/Value/Calculated`) and `MudColorPicker` |
| Select + validate an **unpacked level folder** (in place, no zip) | model after `GenerateTerrain.razor` + `Services/TerrainMaterialService.LoadLevelFromFolder`: `FileSelectComponent SelectFolder="true"` → validate via `ZipFileHandler.GetNamePath(folder)` (fallback: folder containing `info.json`) → `levelPath`; `new BeamFileReader(levelPath,null).GetLevelName()` |
| Settings persistence pattern | model after `Objects/GameSettings.cs` (`Load`/`Save` via `BeamJsonOptions`) |

`.ter` heights are read **raw** (no `maxHeight` needed) via `TerrainV9Binary.HeightData`.

---

## New files

```
Objects/MtSettings/MtSettings.cs            # root settings model + Load(levelRoot)/Save(levelRoot)
Objects/MtSettings/BasecolorMode.cs         # enum { None, PaintMode, BaseColorMode } (persisted as text)
LogicBasecolorManager/BasecolorManagerService.cs   # load list (settings-or-extract), save, mode switch entry points
LogicBasecolorManager/PaintModeApplier.cs          # generate per-material #base + rewrite materials.json
LogicBasecolorManager/BaseColorModeApplier.cs      # build merged set + rewrite json + delete #base
LogicBasecolorManager/TerrainPbrMapBuilder.cs      # .ter -> merged color / normal / AO / height / roughness PNGs
BlazorUI/Pages/BasecolorManager.razor + .razor.cs  # the page (Tabs)
```
Edit: `BlazorUI/MyNavMenu.razor` — add
`<MudNavLink Href="/BasecolorManager" Icon="@Icons.Material.Filled.Palette">Basecolor Manager</MudNavLink>`.

---

## Settings schema — `MT_settings.json` (map root = validated `levelPath`, in place)

```jsonc
{
  "CurrentMode": "PaintMode",                 // enum text for readability; "" when untouched
  "PaintModeSettings": {
    "Materials": [
      { "InternalName": "...", "Name": "...", "BaseColorHex": "#rrggbb",
        "RoughnessPreset": "Calculated", "RoughnessValue": 128, "CalculatedRoughnessValue": 140 }
    ]
  },
  "BasecolorModeSettings": {
    "Materials": [ /* same per-material shape (colors/roughness for the merged build) */ ],
    "MergedTextureSize": 2048,                 // = terrain size from .ter/terrain.json
    "GenerateHeight": false,
    "NormalStrength": 1.0,
    "AoRadius": 2,
    "AoIntensity": 1.0
  }
}
```

- One shared per-material POCO (`MtTerrainMaterialSetting`) maps 1:1 to the UI's `CopyAsset` fields.
- **No originals snapshot is kept.** The per-material colors/roughness in settings are the single source
  of truth — either mode is fully regenerated from them (+ the `.ter` regions). Once the user is in Paint
  Mode and has painted in BeamNG, there is no need to restore the map's pre-existing base textures.
- The two mode material lists are kept synchronized by the UI. A color or roughness edit in either tab
  immediately updates the matching material in the other tab and both settings lists. This prevents
  Paint Mode and BaseColor Mode from drifting apart.
- If the map is currently in Paint Mode and Paint Mode has usable non-default colors, opening or activating
  BaseColor Mode uses the saved Paint Mode colors/roughness. If Paint Mode has no usable colors yet (for
  example all `#808080` defaults), BaseColor Mode keeps its own extracted/settings fallback.
- `Load/Save` mirror `GameSettings` but take the level-root folder and use `MT_settings.json`.

---

## Load flow (page → service)

1. User selects an **unpacked level folder** via `FileSelectComponent SelectFolder="true"` (same UX as
   GenerateTerrain). Validate: `levelPath = ZipFileHandler.GetNamePath(folder)`; if empty, accept `folder`
   directly when it contains `info.json`, else show the same "not a valid BeamNG level" error.
   `_levelName = new BeamFileReader(levelPath,null).GetLevelName()`.
   **All reads/writes happen in place under `levelPath`** — no temp extraction, no deploy zip.
2. `MtSettings.Load(levelPath)`:
   - **Exists** → build the UI list (`List<CopyAsset>`) from `PaintModeSettings`/`BasecolorModeSettings`
     materials; set current mode + banner. When `CurrentMode == PaintMode` and Paint Mode has usable
     colors, copy Paint Mode materials into BaseColor Mode before preview generation.
   - **Missing** → `FindTerrainMaterialsJsonPath` + `TerrainCopyScanner.ScanTerrainMaterials` to get
     materials, then `ExtractTerrainMaterialColors` + `ExtractTerrainMaterialRoughness` to fill
     `BaseColorHex`/roughness (same as CopyTerrains first run). `CurrentMode = None`.
     Important implementation detail: this must match `CopyTerrains` path semantics. The scanner resolves
     `/levels/<levelname>/...` texture paths from the parent `levels` folder when the selected map is
     `levels/<levelname>`, while `ExtractTerrainMaterialColors/Roughness` still receive the actual level
     root (`levelPath`) so they find the `.ter` file. Passing `levelPath` for both caused all materials to
     fall back to `#808080` because base textures resolved one folder too deep.
3. Read `.ter` (`LayerMaskReader.ReadTerrainBinary`) once → cache `Size`, `MaterialData`, `HeightData`,
   `MaterialNames`; render a **downscaled merged-color preview** (data-URI `<img>`) for the BaseColor tab.

---

## Activate **Paint Mode** (`PaintModeApplier`)

For the loaded map's `art/terrains/<terrain>.materials.json`:
1. For each TerrainMaterial, generate (size **1024**, via `TerrainTextureGenerator`, written into `art/terrains`):
   - `#base_color_{hex}.png` (RGBA, **per-material distinct** — this is what makes painting visible)
   - shared `#base_nm.png` (flat 8080FF), `#base_ao.png` (white), `#base_h.png` (black),
     `#base_r_{value}.png` (grayscale per roughness value)
   - Rewrite that material's `baseColorBaseTex/normalBaseTex/aoBaseTex/heightBaseTex/roughnessBaseTex`
     to the generated files (BeamNG `/levels/<name>/art/terrains/...` paths) and set each `*Size = 1024`.
2. `new PbrUpgradeHandler(materialsJsonPath, levelName, levelPath).EnsureTerrainMaterialTextureSetSize(1024)`.
3. Persist `CurrentMode="PaintMode"` + `PaintModeSettings.Materials` (current colors/roughness) →
   `MtSettings.Save(levelPath)`.
4. If Paint Mode has usable colors, sync those colors/roughness into `BasecolorModeSettings` as well so
  BaseColor Mode can regenerate from the same choices.
5. JSON writes use `JsonUtils.GetValidJsonNodeFromFilePath` + `BeamJsonOptions` (same as `PbrUpgradeHandler`).

(JSON-rewrite logic is a single-map simplification of `TerrainTextureHelper.CopyTerrainTextures` — no
`PathConverter`/source copy needed since everything stays inside this one level.)

## Activate **BaseColor Mode** (`BaseColorModeApplier` + `TerrainPbrMapBuilder`)

1. `terrainSize = TerrainV9Binary.Size` (cross-check `GetTerrainSizeFromJson`).
2. `TerrainPbrMapBuilder` reads the cached `.ter` and writes, at **terrainSize**, into `art/terrains`:
   - **Merged base color** `MT_basecolor.png` (RGBA): each pixel = the chosen color of `MaterialData[i]`
     (hole index 255 → transparent). Final BeamNG texture orientation is a vertical flip of raw `.ter`
     row-major data: output pixel `(x,y)` samples terrain index `(size - 1 - y) * size + x`. During manual
     validation, direct row-major output was wrong; a 180-degree rotation was closer but still mirrored,
     and the final fix was the horizontal mirror of that rotation.
   - **Normal** `MT_basecolor_nm.png`: Sobel gradient of `HeightData` (scaled by `NormalStrength`, using
     terrain square spacing); encode to tangent-space RGB, flat = (128,128,255).
   - **AO** `MT_basecolor_ao.png` (grayscale): local-concavity approximation from `HeightData`
     (flat→white, hollows darker); white fallback if degenerate.
   - **Roughness** `MT_basecolor_r.png` (grayscale): per-region fill from each material's roughness value.
   - **Height** `MT_basecolor_h.png` (grayscale, only if `GenerateHeight`): `HeightData` normalized min→max.
3. Point **every** TerrainMaterial's base-tex props at the shared merged files; set every `*Size = terrainSize`.
4. `PbrUpgradeHandler.EnsureTerrainMaterialTextureSetSize(terrainSize)` → updates master `baseTexSize`.
   **Do NOT touch `*.terrain.json` `"size"`** (heightmap resolution — separate concept per the knowledge
   doc). Only base-color sizes change, which is exactly the "Größeninformation in den BeamNG json" meant.
5. Delete the `#base_*` Paint-Mode PNGs we previously generated (colors are safely in settings).
6. Persist `CurrentMode="BaseColorMode"` + `BasecolorModeSettings` → `MtSettings.Save(levelPath)`.

Switching modes again just re-runs the opposite applier from settings (idempotent; regenerate-and-rewrite).
If the map is already in BaseColor Mode, the UI keeps the button enabled as **Regenerate BaseColor Mode**
so the user can tweak a color/roughness value and flush new merged textures without switching modes.
Paint Mode has the same **Regenerate Paint Mode** behavior.

---

## UI (`BasecolorManager.razor`, MudBlazor v8, Tabs)

- Folder-select panel (`FileSelectComponent SelectFolder="true"`, GenerateTerrain-style) + validation.
- `MudAlert` banner: current mode + map name.
- `<MudTabs>`:
  - **Paint Mode** tab: shared material `MudTable` — name, `MudColorPicker @bind-Text=BaseColorHex`
    (same config as CopyTerrains), roughness `MudSelect`/slider block (copied from CopyTerrains rows).
    `Activate Paint Mode` / `Regenerate Paint Mode` button + explanatory `MudText`.
  - **BaseColor Mode** tab: same list + **preview `<img>`** (downscaled merged color), read-only
    "Merged size: {terrainSize}", `Generate Height` `MudCheckBox`, `NormalStrength`, `AoRadius`, and
    `AoIntensity` sliders, `Activate BaseColor Mode` / `Regenerate BaseColor Mode` button.
- Footer: Errors/Warnings/Messages drawer + "Open Level Folder" link (no zip build — edits are written
  in place in the selected folder, like GenerateTerrain). PubSub consumer in `OnInitialized` (copy from
  CopyTerrains).
- Editing a color/roughness uses explicit change handlers instead of isolated two-way bindings. The handler
  updates the edited material, updates the matching material in the other mode, refreshes in-memory settings,
  and **Save** happens on activation/regeneration. A `Save Settings` button writes `MT_settings.json` without
  switching mode.

---

## Build / verify

1. `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj` (app may be running → DLL-lock
   MSB3027/MSB3021 are not compile errors; check `error CS`).
2. Run app → **Basecolor Manager** → select an **unpacked** level folder (e.g. a copy of vanilla
   `west_coast_usa` / `driver_training` under the BeamNG mods/levels dir) containing `info.json`:
   - First load shows materials with auto-extracted colors + roughness; preview renders.
   - **Activate Paint Mode** → confirm `#base_color_{hex}_*` PNGs (1024) in `art/terrains`,
     materials.json base-tex props + `*Size`=1024 + `baseTexSize`=[1024,1024], `MT_settings.json` written
     in the level folder with `CurrentMode:"PaintMode"`. Open the folder in BeamNG, verify each material
     paints distinctly in the terrain editor.
   - **Activate BaseColor Mode** → confirm merged `MT_basecolor*.png` at terrain size, all materials
     share them, `baseTexSize` = terrain size, `#base_*` deleted, settings updated. Open in BeamNG,
     verify the baked look + lighting from derived normal/AO.
   - While already in BaseColor Mode, change one material color, click **Regenerate BaseColor Mode**, and
     confirm the merged output updates and the same color is visible in Paint Mode settings.
   - Re-select the same folder → list/mode restored from `MT_settings.json`; switch modes back and forth.
3. Pure functions in `TerrainPbrMapBuilder` (merged-color pixel mapping, normal of flat patch = up) →
   add focused tests in `Grille.BeamNG.Lib_Tests` if low-cost. No automated tests exist for this area
   otherwise (manual workflow per CLAUDE.md).

## Notes / risks
- Stale-PNG cache: `TerrainTextureGenerator.GenerateSolidColorPng` skips if file exists. Paint `#base`
  names embed hex/value (safe). The merged builder must **overwrite** its `MT_basecolor*` outputs.
- Operate **in place** on the user's selected unpacked folder (no temp copy, no deploy zip) — the user
  manages packaging themselves, same as GenerateTerrain.
- Holes (`MaterialData==255`) excluded from merged color/roughness.
- If a map was opened once with an older broken first-load extraction and saved `MT_settings.json` with all
  `#808080`, delete that settings file to force first-load extraction again, or edit/save colors manually.
- Orientation is easy to regress: all BaseColor-mode outputs and the preview must use the same
  `ToBeamNgTextureIndex(size,x,y)` mapping (`terrainX=x`, `terrainY=size-1-y`).

---

## HANDOFF PROMPT (paste into a fresh session on branch `feature/basecolor_generator`)

> Implement the **Basecolor Manager** feature for the BeamNG mapbuilder app. The full plan is in
> `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-manager-plan-and-handoff.md` — read it and
> the companion `basecolor-textures-knowledge-and-wizard-cap.md` first.
>
> Goal: a new page + two switchable modes for the user's own (unpacked, in-place) map:
> **Paint Mode** writes per-material single-color 1024px `#base` PBR placeholders so each terrain
> material is paintable in-game; **BaseColor Mode** builds one merged full-terrain-size base-color
> texture from the `.ter` material regions + per-material colors, plus heightmap-derived Normal/AO/Height
> and per-region Roughness, shared by all materials. State persists in `MT_settings.json` in the map root
> (`CurrentMode`, `PaintModeSettings`, `BasecolorModeSettings`). The per-material colors/roughness in
> settings are the single source of truth for regenerating either mode — do NOT snapshot the map's
> original terrain textures (not needed once the user has painted in BeamNG). Keep Paint Mode and
> BaseColor Mode material settings synchronized when the user edits color/roughness in either tab.
> On first load (no settings), extract colors+roughness from the `.ter` like CopyTerrains, including the
> same path semantics: scan texture paths from the parent `levels` folder when selected map root is
> `levels/<levelname>`, but run color/roughness extraction against the actual level root.
>
> Confirmed decisions: Tabs UI; heightmap-derived PBR maps (not flat); both modes end-to-end; map is a
> selected unpacked folder validated like GenerateTerrain (`FileSelectComponent SelectFolder="true"` →
> `ZipFileHandler.GetNamePath` / `info.json` check), operated on **in place** (no zip extract/deploy).
>
> Reuse (do not rewrite): `TerrainTextureGenerator.GenerateSolidColorPng`, `TerrainCopyScanner`
> (`ScanTerrainMaterials` / `ExtractTerrainMaterialColors` / `ExtractTerrainMaterialRoughness`),
> `LayerMaskReader.ReadTerrainBinary`/`ReadLayerMasks` + `TerrainV9Binary` (Size/HeightData/MaterialData/
> MaterialNames), `TerrainTextureHelper.FindTerrainMaterialsJsonPath`/`GetTerrainSizeFromJson`,
> `PbrUpgradeHandler.EnsureTerrainMaterialTextureSetSize`, `JsonUtils`+`BeamJsonOptions`, `CopyAsset` +
> `MudColorPicker`, and the `GameSettings` Load/Save pattern.
>
> New files: `Objects/MtSettings/MtSettings.cs` (+ `BasecolorMode` enum), `LogicBasecolorManager/`
> (`BasecolorManagerService`, `PaintModeApplier`, `BaseColorModeApplier`, `TerrainPbrMapBuilder`),
> `BlazorUI/Pages/BasecolorManager.razor(.cs)`, and a nav link in `MyNavMenu.razor`. Important:
> BaseColor Mode updates base-color sizes (`baseTexSize` + per-material `*Size`) to terrain size but must
> NOT change `*.terrain.json` `"size"`. Exclude holes (material index 255). BaseColor-mode outputs and
> the preview must use the validated BeamNG texture orientation mapping: output `(x,y)` samples raw `.ter`
> data at `(terrainX=x, terrainY=size-1-y)`. Keep activate buttons enabled in the current mode as
> regenerate buttons so users can tweak colors and flush new textures without switching modes. Build with
> `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj` (ignore DLL-lock MSB3027/MSB3021; only
> `error CS` matters). Start by creating the settings model + service, then PaintMode, then BaseColorMode,
> then the page. Default derived-map params are `NormalStrength=1.0`, `AoRadius=2`, `AoIntensity=1.0`,
> and `GenerateHeight=false`.
