# Generate Biome — Feature Design: UI, Data Model, Persistence

Date: 2026-07-19
Prereqs: doc 00 (reuse map), doc 03 (BeamNG biomeTool analysis + proposals).
Backend algorithm: doc 02.

## 1. Overview

New page **"Generate Biome"** (`@page "/GenerateBiome"`, nav entry in `BlazorUI\MyNavMenu.razor`).
Places forest items at scale into an unpacked level, driven by:

- **Terrain-material layers** — regions read from the `.ter` material bytes ("virtual layers in memory").
- **OSM layers** — regions read from `MT_TerrainGeneration` mask PNGs (e.g. `landuse_forest_polygon.png`).

Each layer has an ordered list of **zones** (distance bands from the region border inward). Each
zone selects forest brushes / item types via a checkbox treeview with per-item density sliders.
A **negative list** of layers drives a mandatory cleanup that removes generated items standing on
those layers. Everything the tool places is tracked in a manifest so **global delete** and
**per-layer delete** never touch hand-placed items. All selections persist under
`/levelroot/MT_Biome`.

UI skeleton, busy/progress machinery, persistence idioms: copied from BasecolorManager (doc 00 §4).

## 2. Page layout (top → bottom)

```
ErrorBoundary > CustomErrorContent
└─ MudContainer (ExtraLarge)
   ├─ Header row: MudText h4 "Generate Biome" + MudSpacer + Help MudIconButton (help dialog)
   ├─ MudExpansionPanel "Select Level Folder"  (Expanded="@(!HasLevel)")
   │    └─ FileSelectComponent SelectFolder="true"
   ├─ MudProgressLinear (global busy) + _busyMessage
   ├─ MudAlert Info: level name, terrain size, #materials, #placed items (from manifest) 
   │    + "Reset Page" button
   ├─ MudAlert Warning (staleness): ".ter changed since last generation" / "OSM masks missing"
   ├─ Action bar (MudStack Row):
   │    [Generate All]  [Save Settings]  [Cleanup (Negative List)]  [Delete All Generated]
   │    (each with per-operation MudProgressCircular via IsOperation(name))
   ├─ MudExpansionPanels MultiExpansion="true"
   │    ├─ Panel "Terrain Material Layers (n)"          ← §2.1
   │    ├─ Panel "OSM Layers (n)"                       ← §2.2
   │    └─ Panel "Negative List — Cleanup (n)"          ← §2.3
   ├─ MudDrawer message log (Errors/Warnings/Messages)
   └─ Footer: message buttons + "Open Level Folder"
```

### 2.1 Terrain Material Layers panel

A `MudTable<BiomeMaterialRow>` (or stacked `MudPaper` cards — cards preferred, the zone repeater
needs vertical room) listing **every terrain material of the level** with:

| Column | Source |
|---|---|
| color swatch + material name | `TerrainCopyScanner.ScanTerrainMaterials` + `ExtractTerrainMaterialColors` |
| **layer usage** ("12.4 % · 208,431 px · ~52.1 ha") | mask from `LayerMaskReader.ReadLayerMasks`, coverage % as in `TerrainColorExtractor.cs:139–151`, hectares = px · mpp² / 10 000 |
| items placed (from manifest) | manifest per-layer counts |
| `[+ Add Zone]` `[Generate Layer]` `[Delete Layer Items]` buttons | |

Materials with 0 % coverage render disabled (nothing to place on). Byte 255 (holes) is never a layer.

Expanding a material card shows its **Zone repeater** (§2.4).

### 2.2 OSM Layers panel

- `MudSelect<string>` listing **available OSM layers** + `[Add]` button → appends an
  `BiomeOsmLayerRow` card with the same Zone repeater + `[Generate Layer]` + `[Delete Layer Items]`
  + row delete `MudIconButton`.
- Available layers = union of, discovered at load:
  1. `MT_TerrainGeneration\osm_layer\*.png` — per-category masks (`landuse_forest_polygon.png`,
     `natural_wood_polygon.png`, `highway_residential_line.png`, …). Primary source.
  2. `MT_TerrainGeneration\{materialInternalName}_osm_layer.png` — per-material OSM selection masks.
  3. (v1.5) "Refresh OSM Layers" button: if `MT_settings.json` has a usable georeference
     (`MapTileOverlayService.HasUsableGeoReference` pattern) re-run `OsmLayerExporter` against the
     OSM cache to (re)create category masks — covers levels generated before the exporter existed
     or non-GeoTIFF terrains (exporter currently only runs for GeoTIFF sources).
- Display name = file stem prettified (`landuse_forest_polygon` → "Landuse: forest (polygon)").
- The same OSM layer can be added only once (already-added entries removed from the select).

### 2.3 Negative List panel (mandatory cleanup)

- One `MudSelect` with `MultiSelection="true"` + `MultiSelectionTextFunc` (or two — one for terrain
  materials, one for OSM layers) rendering selected entries as `MudChipSet` chips with close icons.
  Requirement: *easy multiselect* — a flat searchable multiselect beats per-row checkboxes here.
- Typical content: road/parking materials, `highway_*` OSM line layers, building footprints.
- `[Cleanup Now]` button + explanation text: *"Removes generated items standing on these layers.
  Runs automatically after every generation."*
- Safety default: cleanup only removes **manifest-tracked items** (never hand-placed ones). An
  advanced checkbox `Also remove foreign forest items on these layers` (default off, warning color,
  confirmation dialog) covers cleanup of items placed by the in-game biome tool — explicitly opt-in
  because it can delete hand-placed items.

### 2.4 Zone repeater (shared by both layer kinds)

Each layer holds an ordered list of zones, consumed **from the border inward**:

```
Zone 1  [band 0.0 – 5.0 m from border]   depth: (MudNumericField 5.0 m)
Zone 2  [band 5.0 – 20.0 m]              depth: 15.0 m
Zone 3  [interior — remaining area]      (IsInterior switch; depth disabled)
        [↑][↓ reorder]  [🗑 remove zone]
```

- `depth` = band thickness in meters; band start = sum of previous depths. A zone flagged
  `IsInterior` takes all remaining area (only valid as last zone, at most one).
- **A zone with no items checked is valid and useful** — it's a keep-clear border strip
  (explicit requirement). Render a subtle hint "empty zone = keep free".
- Per zone content:
  - **Brush treeview**: `MudTreeView<BiomeTreeNode>` — parents = the level's `ForestBrush`es
    (parsed from `main.forestbrushes4.json` via the shared `ForestBrushCopyScanner` parse logic),
    children = their `ForestBrushElement`s → item types. Checkbox on parent = toggle all children
    (tri-state). Element-less brushes show their single `DirectForestItemData` as the only child
    (doc 00 §1.2 gotcha). Item types present in `managedItemData.json` but referenced by no brush
    are listed under a synthetic "(unbrushed items)" parent so everything is reachable.
  - **Per-item density slider**: `MudSlider<int>` 0–100 % on each checked child (checked + 0 % =
    excluded; slider disabled while unchecked). Live label shows the derived estimate:
    *"~1 240 items"* (zone area × density mapping, doc 02 §5).
  - Zone-level optional filters (collapsed "Advanced" section, defaults from brush elements):
    slope min/max (°), elevation min/max (m), noise-clumping toggle + seed/scale/threshold
    (doc 03 proposals 1, 2, 9).
- Zone visual: nested `MudPaper Outlined` inside the layer card; add via layer `[+ Add Zone]`.

### 2.5 Buttons and flows

| Button | Scope | Behavior |
|---|---|---|
| **Generate All** | all layers | per layer: Replace-mode regenerate (delete manifest items of layer → place → cleanup) |
| **Generate Layer** | one layer | same, scoped |
| **Delete All Generated** | global | delete every manifest-tracked item + owned files; confirmation dialog with item count |
| **Delete Layer Items** | one layer | manifest-scoped delete; confirmation dialog |
| **Cleanup (Negative List)** | manifest items | remove tracked items standing on negative layers |
| **Save Settings** | — | persist §4 settings (also saved implicitly after every generate/delete) |

All long operations run through `RunBusyOperation` + `Task.Run`, progress via `PubSubChannel`,
result toast via `ISnackbar`. Confirmation dialogs via `IDialogService.ShowMessageBox`.

## 3. Data model (C# DTOs, `Objects\Biome\` or `LogicBiome\Model\`)

```csharp
class BiomeSettings {                      // MT_Biome\settings.json (MtSettings pattern)
    int SchemaVersion = 1;
    List<BiomeLayerSettings> MaterialLayers;   // one entry per terrain material the user configured
    List<BiomeLayerSettings> OsmLayers;        // user-added OSM layer entries
    BiomeNegativeList NegativeList;
    int GlobalSeed;                            // per-layer seeds derive from this + LayerId
}

class BiomeLayerSettings {
    string LayerId;                    // stable GUID, created when the layer row is added
    BiomeLayerKind Kind;               // TerrainMaterial | Osm
    string SourceKey;                  // material internalName  OR  osm mask file stem
    List<BiomeZoneSettings> Zones;     // ordered, border → interior
    int? SeedOverride;
}

class BiomeZoneSettings {
    double DepthMeters;                // band thickness; ignored when IsInterior
    bool IsInterior;
    List<BiomeItemSelection> Items;    // empty ⇒ keep-clear zone
    double? SlopeMinDeg, SlopeMaxDeg;  // null ⇒ inherit from brush element / no limit
    double? ElevationMin, ElevationMax;
    BiomeNoiseSettings? Noise;         // optional clumping mask
}

class BiomeItemSelection {
    string BrushName;                  // ForestBrush.name ("" for synthetic unbrushed parent)
    string ItemDataName;               // managedItemData key (= forest4 "type")
    int DensityPercent;                // 0–100 slider
}

class BiomeNegativeList {
    List<string> MaterialInternalNames;
    List<string> OsmLayerKeys;
    bool IncludeForeignItems;          // dangerous option, default false
}
```

Selections reference brushes/items **by name**; on load, entries whose brush/item no longer exists
render with a warning icon and are skipped at generation (PubSub warning) — never deleted silently.

## 4. Persistence — `/levelroot/MT_Biome/`

```
MT_Biome/
├── settings.json      # BiomeSettings — everything the UI shows (load-on-open, MtSettings pattern:
│                      #   [JsonPropertyName] DTOs, null-tolerant Load → defaults, Ensure*Defaults
│                      #   hydration for schema evolution, save on apply + on Save Settings)
└── manifest.json      # BiomeManifest — the delete ledger (doc 02 §8), includes:
                       #   TerFileTimestampUtc stamp at last generation (staleness banner input),
                       #   per-layer: owned forest file name + SHA-256 + item records
```

Generated placements go to **one NDJSON file per layer** (mixed item types are valid):

```
forest/MT_biome_{LayerId}.forest4.json
```

File-level ownership makes per-layer delete trivial; the manifest's per-item records are the
fallback when the game editor rewrites/merges forest files (doc 02 §8). `MT_` prefix matches the
`MT_Tiles` / `MT_TerrainGeneration` / `MT_settings.json` family.

Not persisted (recomputed on load): masks, distance fields, coverage stats, brush treeview.

## 5. Page/state/service code layout

```
BlazorUI\Pages\GenerateBiome.razor          # markup (BasecolorManager skeleton)
BlazorUI\Pages\GenerateBiome.razor.cs       # code-behind: private state fields, RunBusyOperation,
                                            #   PubSub consumer, no separate State class (like BasecolorManager)
BlazorUI\Components\GenerateBiomeHelpDialog.razor
LogicBiome\BiomeService.cs                  # LoadLevel → BiomeLoadResult (result-object pattern);
                                            #   Generate/Delete/Cleanup orchestration (doc 02)
LogicBiome\...                              # mask builder, sampler, writer, manifest (doc 02 §10)
Objects\Biome\*.cs                          # DTOs of §3
```

`BiomeService.LoadLevel` mirrors `BasecolorManagerService.LoadLevel`: validate level root
(`ZipFileHandler.GetNamePath` + `info.json` fallback) → level name via `BeamFileReader` → find
`.ter` → `LayerMaskReader.ReadTerrainBinary` → TerrainBlock params (squareSize/maxHeight/baseZ,
doc 02 §2) → scan terrain materials + colors → parse brushes + managedItemData → discover OSM mask
PNGs → load `MT_Biome\settings.json` (or defaults) → load manifest → compute staleness.

## 6. Validation & guard rails

- Generate disabled until a level is loaded and ≥1 zone has items (or a delete/cleanup is possible).
- Estimated-count preview per zone and per run; hard warning dialog above 500 k items (doc 03 #12).
- Staleness banner when `.ter` last-write ≠ manifest stamp (trees may float/sink → offer
  "Regenerate All").
- Before every generate/delete: `ReloadTerrainFromDisk` equivalent (re-read `.ter`) — picks up
  in-game repainting.
- Ensure `Forest` scene object ("theForest") + `ForestBrushGroup` exist before writing (doc 00 §1.4).
- Numeric inputs: prefer `MudSlider`/`MudNumericField` with invariant culture already forced in
  Program.cs (German-Windows gotcha).
