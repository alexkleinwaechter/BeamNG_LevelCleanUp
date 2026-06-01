# DecalRoad Layer Set Editor UI — Design Spec

**Date:** 2026-03-15
**Goal:** Build a reusable UI component for editing DecalRoad layer sets, used in three contexts: (1) editing AppData default layer sets, (2) per-material overrides, and (3) future per-OSM-feature overrides.

**Skills:** @beamng-road-layers, @beamng-decalroad-generation

---

## Context & Motivation

The DecalRoad generation system is fully implemented (generator, corridor overlap, scene writer, preset serialization). The missing piece is a UI for users to configure layer sets — the definitions that control which visual layers (edge lines, lane markings, edge blends, tread marks, AI roads) are generated for each road type.

Currently, layer sets are hardcoded in `DecalRoadDefaultLayerSets.cs` and persisted to `decalroad-defaults.json` via `DecalRoadDefaultsManager`. The UI has only an enable checkbox and re-generate button. Users cannot view or edit layer configurations.

### BeamNG Reference

The design is informed by BeamNG's Road Spline editor (`roadSpline.lua` + `layerMgr.lua`), which uses a selectable layer list with per-layer property panels, drag handle reordering, material selection, and auto-generated preset layers with toggle buttons.

---

## Architecture

### Component Hierarchy

```
GenerateTerrain.razor
├── DecalRoad section (enable toggle, re-gen button, "Edit Default Layer Sets" button)
│   └── DecalRoadLayerSetEditorDialog (multi-set mode, full-screen)
│       ├── Left sidebar: road type list with summary info
│       └── Right pane: DecalRoadLayerSetEditor (reusable)
│
TerrainMaterialSettings.razor
├── "DecalRoad Layers" section (use-defaults toggle, "Edit Layer Set" button)
│   └── DecalRoadLayerSetEditorDialog (single-set mode, full-screen)
│       └── DecalRoadLayerSetEditor (reusable, same component)
```

### Reusable Component: `DecalRoadLayerSetEditor`

A single Blazor component that edits one `DecalRoadLayerSet`. It renders:
- Layer set header (name, default lane count, default lane width, enabled toggle)
- Accordion layer cards with drag-to-reorder
- Add Layer / property editing / delete / duplicate actions

This component is context-agnostic — it doesn't know whether it's editing defaults or overrides. The parent dialog handles data loading, save semantics, and persistence.

### Dialog Wrapper: `DecalRoadLayerSetEditorDialog`

A full-screen `MudDialog` that operates in two modes:

**Multi-set mode** (defaults editor):
- Two-pane layout: left sidebar (~280px) + right pane
- Sidebar lists all road types from the loaded defaults dictionary
- Sidebar shows per-type: name, layer count, lane count, enabled status, modified indicator
- Clicking a road type loads that `DecalRoadLayerSet` into the editor
- "Add Custom Type" action at bottom of sidebar
- Save writes entire dictionary to AppData JSON via `DecalRoadDefaultsManager.Save()`
- Cancel discards all changes (works on a deep copy)

**Single-set mode** (per-material override):
- No sidebar, just the `DecalRoadLayerSetEditor` filling the dialog
- Editing a single `DecalRoadLayerSet`
- Save returns the edited layer set to the parent via callback
- Cancel discards changes

Both modes have explicit Save / Cancel buttons in the dialog action bar.

---

## Data Flow & Persistence

### Three-Level Override Cascade (unchanged from existing design)

```
Resolution order (DecalRoadLayerSetResolver):
1. DecalRoadSettings.OsmLayerSets[osmRoadType]     ← per-OSM-type override (future)
2. DecalRoadSettings.MaterialLayerSets[materialName] ← per-material override
3. AppData defaults[osmRoadType]                     ← edited via defaults dialog
4. null → skip road (no layer set found)
```

### Persistence Targets

| Context | Data Structure | Persistence | Edited Via |
|---------|---------------|-------------|------------|
| AppData defaults | `Dictionary<string, DecalRoadLayerSet>` | `decalroad-defaults.json` in `%LocalAppData%` | Defaults editor dialog (multi-set) |
| Per-material override | `DecalRoadSettings.MaterialLayerSets` | Terrain preset JSON (export/import) | Per-material dialog (single-set) |
| Enable toggle | `DecalRoadSettings.Enabled` + `TerrainGenerationState.EnableDecalRoads` | Terrain preset JSON | Checkbox on GenerateTerrain page |
| Global settings | `DecalRoadSettings.NodeSpacingMeters`, `.JunctionExclusionMarginMeters` | Terrain preset JSON | Numeric fields on GenerateTerrain page |

### Deep Copy Strategy

Both dialog modes work on deep copies of the data:
- On dialog open: deep-copy the source data (JSON round-trip via `System.Text.Json`)
- On Save: replace source data with the edited copy and persist
- On Cancel: discard the copy, source data unchanged

This ensures no partial edits leak into the live state.

---

## UI Design

### GenerateTerrain.razor — DecalRoad Section

The existing section (lines 744-780) is updated:

```
┌─ DecalRoad Generation ──────────────────────────────────────┐
│ ☑ Generate road markings and edge blends (DecalRoads)       │
│                                                              │
│ [Generates visual road detail layers...]                     │
│                                                              │
│ Node Spacing: [2.0] m    Junction Margin: [0.0] m           │
│                                                              │
│ [🔧 Edit Default Layer Sets]    [↻ Re-generate DecalRoads]  │
│                                                              │
│ ⚠ Generate terrain first to enable re-generation.           │
└──────────────────────────────────────────────────────────────┘
```

- Enable checkbox: binds to `_enableDecalRoads` (existing)
- Node Spacing: `MudNumericField` binding to `_state.DecalRoadSettings.NodeSpacingMeters`
- Junction Margin: `MudNumericField` binding to `_state.DecalRoadSettings.JunctionExclusionMarginMeters`
- "Edit Default Layer Sets" button: opens `DecalRoadLayerSetEditorDialog` in multi-set mode
- Re-generate button: existing functionality
- All DecalRoad controls disabled when `!_canFetchOsmData` (existing behavior)

### Defaults Editor Dialog (Multi-Set Mode)

```
┌─ DecalRoad Default Layer Sets ──────────── [Save] [Cancel] ─┐
│                                                               │
│ ┌──── Road Types ────┐ ┌──── Layer Set Editor ─────────────┐ │
│ │                     │ │                                    │ │
│ │ ► Motorway          │ │ Motorway              ☑ Enabled   │ │
│ │   5 layers · 4 ln   │ │ Default Lanes: [4]  Width: [3.5]m│ │
│ │                     │ │ [↻ Reset to Default]              │ │
│ │   Trunk             │ │                                    │ │
│ │   5 layers · 4 ln   │ │ Layers (5)           [+ Add Layer]│ │
│ │                     │ │                                    │ │
│ │   Primary           │ │ ⋮⋮ [EdgeLine] EdgeLine            │ │
│ │   4 layers · 2 ln   │ │    m_line_white  0.15m  pos:1.0   │ │
│ │                     │ │    ☑mir ☑jnc              ● ▶     │ │
│ │   Secondary         │ │                                    │ │
│ │   4 layers · 2 ln   │ │ ⋮⋮ [LaneMark] LaneMarking  ● ▼   │ │
│ │                     │ │ ┌──────────────────────────────┐  │ │
│ │   Residential  ✎    │ │ │ Material: m_line_white_disc  │  │ │
│ │   3 layers · 2 ln   │ │ │ Type: [LaneMarking ▾]       │  │ │
│ │                     │ │ │ Width: [0.15]  Pos: [0.0]    │  │ │
│ │   ...               │ │ │ TexLen: [10.0] Priority: [10]│  │ │
│ │                     │ │ │ ☐Mir ☑PerLane ☐TrkW ☑Jnc    │  │ │
│ │ [+ Add Custom Type] │ │ │ Fade: [0] / [0]              │  │ │
│ │                     │ │ │ DistFade: [1000] / [1500]     │  │ │
│ └─────────────────────┘ │ │ Drivability: [-1.0]           │  │ │
│                          │ │ LanesL: [1] LanesR: [1]       │  │ │
│                          │ │ ☐OneWay ☐FlipDir              │  │ │
│                          │ │        [🗑 Delete] [📋 Dup]   │  │ │
│                          │ └──────────────────────────────┘  │ │
│                          │                                    │ │
│                          │ ⋮⋮ [EdgeBlend] EdgeBlend1   ● ▶  │ │
│                          │ ⋮⋮ [EdgeBlend] EdgeBlend2   ● ▶  │ │
│                          │ ⋮⋮ [AIRoad] AIRoad          ○ ▶  │ │
│                          └────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────┘
```

**Left sidebar (280px):**
- `MudList` with selectable items
- Each item shows: road type name, layer count, lane count, enabled status dot
- Modified indicator (✎) when the layer set differs from hardcoded defaults
- "Add Custom Type" at bottom (creates new entry with empty layer set)
- Selected item highlighted with accent border

**Right pane:**
- Layer set header: name (read-only for built-in types, editable for custom), default lane count, default lane width, enabled toggle, "Reset to Default" button
- Layer list: `MudDropContainer` with `MudExpansionPanel`-style cards
- Each card: drag handle, type chip (color-coded by LayerType), name, material, width, position, key flags, enabled dot, expand chevron

### Layer Card — Collapsed State

One-row summary with key properties at a glance:

| Element | Purpose |
|---------|---------|
| Drag handle (⋮⋮) | Reorder via MudDropContainer |
| LayerType chip | Color-coded badge (EdgeLine=blue, CenterLine=orange, EdgeBlend=green, TreadMarks=purple, AIRoad=gray, Custom=default) |
| Name | Layer name text |
| Material | Material name in secondary color |
| Width | e.g. "0.15m" or "trk" (if IsTrackWidth) or "lane" (if IsLaneWidth) |
| Position | e.g. "pos:1.0" |
| Key flags | Compact: "mir" "jnc" "perLn" shown only when true |
| Enabled dot | Green (enabled) / gray (disabled) |
| Expand chevron | ▶ / ▼ |

### Layer Card — Expanded State

Two-column property grid using `MudGrid`:

**Row 1:** Material (text field, full width or with future browse button) | LayerType (MudSelect dropdown)

**Row 2:** Width (MudNumericField, m, min 0.0) | Position (MudNumericField, -5.0 to +5.0, step 0.05)

**Row 3:** TextureLength (MudNumericField, m) | RenderPriority (MudNumericField, 0-100)

**Row 4:** Checkboxes row: IsEnabled, IsMirrored, IsPerLane, IsTrackWidth, IsLaneWidth, InterruptAtJunctions

**Row 5:** FadeIn / FadeOut (two numeric fields) | DistanceFade start / end (two numeric fields)

**Row 6:** Drivability (MudNumericField) | LanesLeft / LanesRight (two numeric fields)

**Row 7:** OneWay / FlipDirection checkboxes

**Row 8:** Actions: Delete (red) | Duplicate (accent)

### TerrainMaterialSettings.razor — DecalRoad Section

New section added below "Master Spline Export", visible when `Material.IsRoadMaterial || Material.EnableRoadPainting`:

```
┌─ DecalRoad Layers ──────────────────────────────────────────┐
│ ☑ Use defaults (resolved via OSM type / material cascade)   │
│   Active: "Primary" (4 layers, 2 lanes)                     │
│                                                              │
│   — OR when unchecked: —                                     │
│                                                              │
│ ☐ Use defaults                                               │
│   [🔧 Edit Custom Layer Set]                                 │
│   Custom: 3 layers, 2 lanes                                  │
└──────────────────────────────────────────────────────────────┘
```

- "Use defaults" checkbox: when checked, no entry in `MaterialLayerSets` — cascade resolves normally
- Summary text: shows which layer set would resolve for this material (name, layer count, lane count)
- When unchecked: "Edit Custom Layer Set" button opens single-set dialog
- On first uncheck: deep-copy the resolved default layer set as starting point
- The custom layer set is stored in `DecalRoadSettings.MaterialLayerSets[material.InternalName]`

### Per-Material Override — Data Access

`TerrainMaterialSettings` needs access to `DecalRoadSettings` to read/write `MaterialLayerSets`. New parameters added to the component:

```csharp
[Parameter] public DecalRoadSettings? DecalRoadSettings { get; set; }
[Parameter] public EventCallback<DecalRoadSettings> DecalRoadSettingsChanged { get; set; }
```

Passed from `GenerateTerrain.razor` where `_state.DecalRoadSettings` is already available. The component checks `DecalRoadSettings?.MaterialLayerSets.ContainsKey(Material.InternalName)` to determine if a custom override exists. No new model property needed on `TerrainMaterialItemExtended` — it's a runtime lookup against the settings dictionary.

The "DecalRoad Layers" section is only visible when `DecalRoadSettings != null` (i.e., DecalRoad generation is enabled) AND `Material.IsRoadMaterial || Material.EnableRoadPainting`.

---

## Component Parameters

### DecalRoadLayerSetEditor.razor

```csharp
[Parameter] public DecalRoadLayerSet LayerSet { get; set; }
[Parameter] public EventCallback LayerSetChanged { get; set; }
[Parameter] public bool ReadOnly { get; set; } = false;
```

**Mutation model:** The component mutates the passed-in `LayerSet` object directly (it is always a deep copy owned by the dialog). The `LayerSetChanged` callback is a notification-only event (no payload) invoked after any mutation to trigger parent re-renders (e.g., sidebar summary updates). This is not two-way binding — it is in-place mutation with change notification.

### DecalRoadLayerSetEditorDialog.razor

```csharp
// Multi-set mode (defaults editor)
[Parameter] public Dictionary<string, DecalRoadLayerSet>? DefaultLayerSets { get; set; }

// Single-set mode (per-material override)
[Parameter] public DecalRoadLayerSet? SingleLayerSet { get; set; }
[Parameter] public string? SingleLayerSetTitle { get; set; }

// Common
[CascadingParameter] private IMudDialogInstance MudDialog { get; set; }
```

Mode is inferred: if `DefaultLayerSets` is provided → multi-set mode. If `SingleLayerSet` is provided → single-set mode.

Dialog result types:
- **Multi-set mode:** `MudDialog.Close(DialogResult.Ok(editedDictionary))` where `editedDictionary` is `Dictionary<string, DecalRoadLayerSet>`
- **Single-set mode:** `MudDialog.Close(DialogResult.Ok(editedLayerSet))` where `editedLayerSet` is `DecalRoadLayerSet`
- **Cancel (both modes):** `MudDialog.Cancel()`

The caller casts `dialog.Result.Data` to the expected type based on which mode it opened.

---

## Behavioral Details

### Deep Copy Mechanism

Use JSON round-trip for deep copying:

```csharp
var json = JsonSerializer.Serialize(source, jsonOptions);
var copy = JsonSerializer.Deserialize<T>(json, jsonOptions);
```

Where `jsonOptions` matches the existing `DecalRoadDefaultsManager` options (camelCase, enum converter).

### "Reset to Default" per Road Type

In multi-set mode, each road type has a "Reset to Default" button that:
1. Calls `DecalRoadDefaultLayerSets.GetDefaults()` to get hardcoded defaults
2. Replaces the current entry with the hardcoded version
3. Updates the modified indicator in the sidebar

### Modified Indicator

A road type is "modified" if it differs from the hardcoded default. Computed once when the dialog opens (and updated on Reset): serialize both the current entry and hardcoded default to JSON and compare strings. The result is cached in a `Dictionary<string, bool> _modifiedFlags` — not recomputed on every render. Updated when a layer set is edited (set `_modifiedFlags[key] = true`) or reset (recompute for that key).

### Layer Type Color Coding

| LayerType | Color | MudBlazor Color |
|-----------|-------|-----------------|
| CenterLine | Orange | `Color.Warning` |
| LaneMarking | Teal | `Color.Tertiary` |
| EdgeLine | Blue | `Color.Info` |
| EdgeBlend | Green | `Color.Success` |
| TreadMarks | Purple | `Color.Secondary` |
| AIRoad | Gray | `Color.Default` |
| Custom | Default | `Color.Default` |

### Add Layer

"Add Layer" creates a new `DecalRoadLayerDefinition` with sensible defaults:
- Name: "New Layer"
- LayerType: Custom
- IsEnabled: true
- Material: "" (empty)
- Width: 0.2f
- Position: 0.0f
- Other defaults from the class definition

### Add Custom Road Type (Multi-Set Mode)

"Add Custom Type" in the sidebar:
1. Opens a small input dialog for the type name (e.g., "motorway_link")
2. Validates that the name doesn't already exist in the dictionary (show error if duplicate)
3. Creates a new empty `DecalRoadLayerSet` with that name
4. Adds to the dictionary and selects it

### Validation Rules

**Layer-level validation** (visual indicators, non-blocking):
- `Material`: empty string shows a warning chip "No material" in collapsed state. The generator will skip layers with empty material.
- `Width`: min 0.0, no max. 0 is valid (used with IsTrackWidth/IsLaneWidth).
- `Position`: unbounded float. Values beyond ±1.0 are valid and used (edge blends use 1.1–1.35 to extend past road edges). The numeric field uses step 0.05, no min/max clamp.
- `TextureLength`: min 0.1, max 500.0
- `RenderPriority`: min 0, max 100
- `DefaultLaneCount`: min 1, max 8
- `DefaultLaneWidth`: min 1.0, max 10.0
- `Drivability`: min -1.0, max 1.0
- `LanesLeft` / `LanesRight`: min 0, max 8
- `FadeIn` / `FadeOut`: min 0.0, max 500.0
- `DistanceFade`: min 0.0, max 10000.0

**Layer set-level:** No validation on empty layer lists (valid to have a layer set with 0 layers — it just won't generate anything). Duplicate layer names within a set are allowed (names are display-only, not identifiers).

### Drag-and-Drop Interaction

Layer cards use `MudDropContainer` for reordering. **Expanded cards must be collapsed before dragging** — the expand/collapse state is tracked per-layer, and initiating a drag on a card auto-collapses it. This avoids jarring UX with tall expanded cards moving around. The drag handle is always in the collapsed header row.

### Delete Layer Behavior

Delete removes the layer immediately from the in-memory copy. Since the dialog operates on a deep copy with explicit Save/Cancel, accidental deletion is recoverable by clicking Cancel. No confirmation dialog is needed.

---

## Out of Scope (Mentioned for Future)

1. **Per-OSM-feature override** — selecting road features in the OSM feature selector component and assigning custom layer sets. Will use `DecalRoadSettings.OsmLayerSets`. The reusable component is designed to support this context.
2. **Material browser/picker** — currently material names are typed as text. A future material browser dialog could be added.
3. **Live preview** — showing a visual preview of the layer stack. Would require a custom canvas/SVG renderer.
4. **Layer templates** — predefined layer presets (like BeamNG's auto-generated layers) that can be toggled on/off. The current "Add Layer" is manual.

---

## File Summary

### New Files

| File | Purpose |
|------|---------|
| `BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Reusable layer set editor component (accordion cards) |
| `BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Code-behind for layer set editor |
| `BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor` | Full-screen dialog wrapper (multi/single mode) |
| `BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs` | Code-behind for dialog (deep copy, save/cancel, sidebar) |

### Modified Files

| File | Changes |
|------|---------|
| `BlazorUI/Pages/GenerateTerrain.razor` | Add "Edit Default Layer Sets" button, NodeSpacing/JunctionMargin fields |
| `BlazorUI/Pages/GenerateTerrain.razor.cs` | Add dialog open handler, ensure DecalRoadSettings initialized |
| `BlazorUI/Components/TerrainMaterialSettings.razor` | Add "DecalRoad Layers" section below Master Spline Export |
| `BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Add `DecalRoadSettings` parameter, dialog open handler, use-defaults toggle logic |

### Unchanged Files (Already Support This)

| File | Why Unchanged |
|------|---------------|
| `DecalRoadLayerDefinition.cs` | All properties already present (including IsLaneWidth) |
| `DecalRoadLayerSet.cs` | Model sufficient |
| `DecalRoadSettings.cs` | MaterialLayerSets/OsmLayerSets dictionaries already present |
| `DecalRoadDefaultsManager.cs` | Load/Save already working |
| `TerrainGenerationState.cs` | EnableDecalRoads + DecalRoadSettings already present |
| `TerrainPresetExporter.razor` | Already exports DecalRoadSettings |
| `TerrainPresetImporter.razor` | Already imports DecalRoadSettings |
| `TerrainGenerationOrchestrator.cs` | Already wires DecalRoadSettings to pipeline |
