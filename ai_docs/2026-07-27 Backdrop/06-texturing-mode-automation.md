# Texturing Mode Toggle + BaseColor Automation (Backdrop follow-up)

**Date:** 2026-07-28 · **Branch:** `feature/backdrop` · **Status:** implemented in this session

## Goal

GenerateTerrain gets a **Texturing Mode** toggle: **Paint Mode** (default — exactly today's behavior,
no new side effects) vs **BaseColor Mode** (remote-controls the Basecolor Manager). When **backdrop
generation is enabled, BaseColor Mode is forced on** (the backdrop is pure satellite imagery; the
terrain must match it).

When the effective mode is BaseColor Mode, a successful terrain generation automatically:

1. downloads the satellite tile overlay for the terrain extent (same warp pipeline the Basecolor
   Manager's *Fetch* button uses; shared `MT_Tiles` cache with the backdrop chunk baker),
2. sets the per-material **Overlay Blend to 0** for every material the user selected for **road
   smoothing or road painting** on this page (`IsRoadMaterial || EnableRoadPainting`) — so no
   satellite texture bleeds into the road system,
3. sets all other materials' Overlay Blend to the new **"Satellite blend (non-road materials)"**
   knob (default 100 %),
4. activates **BaseColor Mode** (`BaseColorModeApplier.Apply` — bakes the merged terrain PBR maps,
   rewires the terrain materials JSON, sets `MtSettings.CurrentMode = BaseColorMode`),
5. rebakes backdrop chunk textures if a backdrop exists (`RebakeBackdropTexturesAsync` — fingerprint
   cache makes this cheap; keeps the manager's staleness stamps consistent).

Everything is warn-only: a failure in the automation never fails the terrain run.

## Key design decisions

- **Effective mode is derived, not mutated:** `TerrainGenerationState.Texturing.Mode` stores the
  user's choice; `EffectiveTexturingMode => Backdrop.Enabled ? BaseColorMode : Texturing.Mode`.
  Disabling the backdrop returns to the user's own choice — nothing is silently overwritten.
- **Paint Mode = strict no-op.** No `PaintModeApplier` call, no `CurrentMode` write. Preserves the
  repo's default-off discipline: existing generation outputs stay byte-identical.
- **Hook point:** after `SaveGeoReferenceSettingsAfterGeneration()` in the success branch of
  `ExecuteTerrainGeneration` — the tile fetch needs the georef block on disk. The second entry
  point `ExecuteTerrainGenerationWithAnalysis` did **not** save georef settings at all (pre-existing
  gap); it now calls the same georef save + automation.
- **Automation lives in `BlazorUI/Services/BasecolorAutoApplyService.cs`** (same layer as
  `BackdropOrchestrator`), built entirely on existing public Basecolor Manager APIs
  (`LoadLevel`, `EnsureOverlayImageAsync`, `BaseColorModeApplier.Apply`,
  `RebakeBackdropTexturesAsync`, `UpdateSettingsFromMaterialLists`).
- **Provider:** reuses `OverlaySettings.SelectedTileProvider` if set (and writes it back explicitly —
  avoids the staleness false-positive where `LastBakeProvider` is stamped but the setting stays
  empty); falls back to `"Google Satelite Only"`. A date-requiring provider (ArcGIS Wayback) without
  a stored date falls back to the default provider instead of throwing.
- **Missing-material guard:** any road/paint material from the page that has no entry in the level's
  Basecolor material list is appended with defaults (gray, blend per road/non-road rule) — the PBR
  baker's lookup fallback is blend = 1.0 (full satellite), which would otherwise bleed satellite
  into exactly the roads we're protecting. A PubSub warning names such materials.
- **`GlobalBlend`** is set to the non-road value so the manager's global slider reflects reality
  (the renderer only honors per-material values; the global slider is a copy-down convenience).
- Known edge (accepted): if *every* terrain material is a road material, all blends end up 0 and the
  manager's `EnsureDefaultOverlayBlend` guard will push 50 % onto all of them on the next manual
  overlay interaction in the Basecolor Manager. Not worth special-casing.

## Files

```
BeamNG_LevelCleanUp/BlazorUI/State/TexturingSettings.cs            NEW  enum TerrainTexturingMode + POCO (Mode, NonRoadOverlayBlendPercent)
BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs       MOD  Texturing property + EffectiveTexturingMode + Reset()
BeamNG_LevelCleanUp/BlazorUI/Components/TexturingModePanel.razor   NEW  section shell + radio group + blend slider (+ .razor.cs)
BeamNG_LevelCleanUp/BlazorUI/Services/BasecolorAutoApplyService.cs NEW  the automation pipeline (steps 1–5 above)
BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor           MOD  panel above BackdropSettingsPanel; exporter wiring
BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs        MOD  RunBasecolorAutomationAsync + both success branches + preset apply
BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor MOD  texturingSettings block
BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor MOD  parse texturingSettings
BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs      MOD  TexturingMode? + TexturingNonRoadOverlayBlendPercent?
```

Preset JSON (v3.0, appSettings): `"texturingSettings": { "mode": "PaintMode"|"BaseColorMode",
"nonRoadOverlayBlendPercent": 0–100 }` — absent block (old preset) touches nothing.

## Manual verification checklist

- [ ] Paint Mode (default), no backdrop → generation output identical to before (no MT_Tiles
      download, `MtSettings.CurrentMode` untouched).
- [ ] Enable backdrop → radio jumps to BaseColor Mode and is locked with explanatory alert;
      disable backdrop → radio returns to the previous user choice.
- [ ] BaseColor Mode generate (georeferenced GeoTIFF source): `[BASECOLOR-AUTO]` log lines, tiles in
      `MT_Tiles`, `MT_basecolor*.png` in `art/terrains`, `MT_settings.json` has
      `CurrentMode: BaseColorMode`, road-smoothing/painting materials at `BaseColorOverlayBlend: 0`,
      others at the slider value.
- [ ] Open Basecolor Manager afterwards: overlay auto-attached, blends as set, no staleness banner
      for the backdrop (provider stamped), roads stay satellite-free after a manual Rebake.
- [ ] PNG heightmap + BaseColor Mode → warning ("needs georeferenced elevation"), run still succeeds.
- [ ] Preset round-trip: export → import restores mode + slider; old presets leave defaults.
