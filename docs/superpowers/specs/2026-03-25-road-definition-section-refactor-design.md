# Road Definition Section UI Refactor — Design Spec

**Date:** 2026-03-25
**Goal:** Rename, reposition, and contextually adapt the DecalRoad generation section and per-material width parameters to better reflect their role in the terrain generation pipeline.

---

## Context & Motivation

The current "DecalRoad Generation" section in `GenerateTerrain.razor` has two problems:

1. **Naming and position**: The name "DecalRoad Generation" is an internal implementation detail. The section actually defines road lane counts, widths, visual layers (edge lines, lane markings, edge blends), and AI road paths. It sits after the materials list, but logically it configures global pipeline behavior that the per-material settings depend on — it should come first.

2. **Width parameter confusion**: With the per-segment road width feature (spec: `2026-03-24-per-segment-road-width-design.md`), road widths in the OSM pipeline are derived from a 5-level priority chain (OSM width tag > est_width > lane calculation > layerset defaults > parameters). The per-material Road Width / Road Surface Width / Master Spline Width fields become Priority 5 fallbacks, but nothing in the UI communicates this. Users see them as primary controls when they're actually last-resort defaults.

3. **PNG pipeline irrelevance**: The section requires OSM data and has no function when the heightmap source is PNG. Currently it shows as disabled; hiding it entirely is cleaner.

---

## Changes

### 1. Rename Section

**Current:** "DecalRoad Generation"
**New:** "Road Width, Lanes & DecalRoad Definition"

**Current checkbox label:** "Generate road markings and edge blends (DecalRoads)"
**New checkbox label:** "Enable DecalRoad and AI-Road generation"

**Current description:** "Generates visual road detail layers (edge lines, lane markings, edge blends) projected onto the terrain surface along road splines."
**New description:** "Defines road lane counts, widths, visual detail layers (edge lines, lane markings, edge blends) and AI road paths. Settings here apply globally via layer set defaults. Per-material overrides available below."

### 2. Default State

The enable checkbox should default to `true`. Currently `_enableDecalRoads` initializes to `false`.

**Change in `TerrainGenerationState.cs`:** The property initializer already defaults to `true` (line 71: `public bool EnableDecalRoads { get; set; } = true;`). However, `TerrainGenerationState.Reset()` (line 379) resets it to `false`. Change `Reset()` to set `EnableDecalRoads = true` so the default survives page resets.

**Preset import behavior:** If a preset is imported that has `EnableDecalRoads = false`, the imported value takes precedence over the default.

### 3. Reposition Section

Move the section from its current position (after the Terrain Materials list, line 745) to before the Terrain Materials list (before line 673).

**New page order:**
1. Folder selection
2. Preset import/export
3. Heightmap settings (terrain size, max height, etc.)
4. Features (enable buildings, enable road smoothing checkboxes)
5. **Road Width, Lanes & DecalRoad Definition** (moved here)
6. Terrain Materials list (with per-material TerrainMaterialSettings)
7. Generate button

### 4. Hide When No OSM Data

The entire section is hidden (not just disabled) when `!_canFetchOsmData`. This covers:
- PNG heightmap source (no geographic coordinates)
- Corrupt/invalid GeoTIFFs without WGS84 bounding box
- Any other case where OSM data cannot be fetched

**Implementation:** Wrap the section's `MudPaper` in `@if (_canFetchOsmData)` instead of the current approach of showing the section with disabled controls.

**Note:** The section currently lives inside `@if (_terrainMaterials.Any())` (line 673). Moving it before the materials list extracts it from that guard. The section should use `@if (_canFetchOsmData)` as its only visibility condition — it configures global pipeline behavior independent of whether materials are loaded. On initial page load (no GeoTIFF selected), `_canFetchOsmData` is `false`, so the section is correctly hidden until a valid GeoTIFF is imported.

### 5. Per-Material Width Parameters — OSM Pipeline Info Banner

When a material's layer source is OSM features (`IsUsingOsmSplines == true`), add an info banner above the Road Smoothing Parameters width fields.

**Banner text:** "OSM pipeline active — road widths are derived from lane data & layer set defaults. These values are used as fallback when no OSM width or lane data is available for a road segment."

**Visual treatment:**
- MudBlazor `MudAlert` with `Severity.Info`, compact/dense style
- Placed inside the Road Smoothing Parameters section, above the Road Width / Road Surface Width fields
- Width fields remain **fully visible and editable** — no muting, no opacity change, no collapsing

**Condition:** The banner appears when `IsUsingOsmSplines` is true — i.e., `Material.IsRoadMaterial && Material.LayerSourceType == LayerSourceType.OsmFeatures`.

### 6. Per-Material Width Parameters — PNG Pipeline

No change. When the PNG pipeline is active (or no layer source selected), width fields display as they do today — they are the primary width source (no info banner, no fallback messaging).

---

## Behavioral Details

### IsUsingOsmSplines Detection

The existing property in `TerrainMaterialSettings.razor.cs` (line 68-70) already provides this:

```csharp
private bool IsUsingOsmSplines =>
    Material.IsRoadMaterial &&
    Material.LayerSourceType == LayerSourceType.OsmFeatures;
```

This is the condition for showing the info banner.

### Interaction with DecalRoadSettings Parameter

`TerrainMaterialSettings` receives `DecalRoadSettings` as a parameter from `GenerateTerrain.razor`:

```csharp
DecalRoadSettings="@(_enableDecalRoads ? _state.DecalRoadSettings : null)"
```

This already passes `null` when DecalRoads are disabled. The per-material "DecalRoad Layers" section visibility is gated on `DecalRoadSettings != null`. No change needed here — the existing wiring is correct.

### Enable Default and Preset Import

The default `true` value for the enable checkbox must not override preset imports. The flow:

1. Page initializes → `_enableDecalRoads = true`
2. If user imports a preset → `_enableDecalRoads` is set to the preset's `EnableDecalRoads` value (which may be `false`)
3. If user toggles manually → value is whatever the user chose

This matches how other defaulted fields work (e.g., terrain size has a default but can be overridden by import).

---

## Files Modified

| File | Changes |
|------|---------|
| `BlazorUI/Pages/GenerateTerrain.razor` | Move DecalRoad section before materials list; rename title, checkbox label, description; wrap in `@if (_canFetchOsmData)` |
| `BlazorUI/State/TerrainGenerationState.cs` | Change `Reset()` to set `EnableDecalRoads = true` |
| `BlazorUI/Components/TerrainMaterialSettings.razor` | Add `MudAlert` info banner in Road Smoothing Parameters section, conditioned on `IsUsingOsmSplines` |

## Files Unchanged

| File | Why |
|------|-----|
| `TerrainMaterialSettings.razor.cs` | `IsUsingOsmSplines` property already exists; no new logic needed |
| `GenerateTerrain.razor.cs` | No code-behind changes needed (wiring already correct) |
| `DecalRoadSettings.cs` | No model changes |
| `DecalRoadGenerator.cs` | No generation logic changes |
| `RoadSmoothingParameters.cs` | No model changes |

---

## Out of Scope

1. **Moving width parameters to the global section** — width params stay per-material; this was considered (Option A in brainstorming) but rejected because PNG pipeline needs per-material widths as primary controls.
2. **Muting/collapsing width fields in OSM mode** — decided against; fields remain fully visible with info banner only.
3. **Renaming "Road Smoothing Parameters"** within TerrainMaterialSettings — not part of this refactor.
4. **Changes to the per-segment road width pipeline** — covered by the separate spec (`2026-03-24-per-segment-road-width-design.md`).
