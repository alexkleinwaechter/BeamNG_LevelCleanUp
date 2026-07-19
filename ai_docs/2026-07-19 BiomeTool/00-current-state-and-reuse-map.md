# Generate Biome — Current State and Reuse Map

Date: 2026-07-19
Status: research complete, design in docs 01–03.

Goal: a new Blazor page **"Generate Biome"** (`@page "/GenerateBiome"`) that places forest items
(trees, bushes, rocks) at scale into a level, driven by terrain-material layers and OSM layers,
with zone bands, per-brush/per-item density, a mandatory negative-list cleanup, and full
delete/regenerate bookkeeping persisted under `/levelroot/MT_Biome`.

This doc is the inventory of what already exists in the repo and what the new feature reuses.

---

## 1. Forest file formats (what we must write)

### 1.1 `forest/*.forest4.json` — placed instances (NDJSON)

One compact JSON object per line, **no array brackets, no commas**:

```jsonl
{"type":"oak_large","pos":[10,20,0],"rotationMatrix":[1,0,0,0,1,0,0,0,1],"scale":1}
{"type":"oak_large","pos":[15,25,0],"rotationMatrix":[0,-1,0,1,0,0,0,0,1],"scale":0.85}
```

| Field | Type | Notes |
|---|---|---|
| `type` | string | must match a key in `art/forest/managedItemData.json` |
| `pos` | `[x,y,z]` | **z is absolute world elevation** — generator must sample terrain height (+ optional sink) |
| `rotationMatrix` | 9 doubles | row-major 3×3; yaw-only = `[cos,sin,0,-sin,cos,0,0,0,1]` |
| `scale` | double | **uniform scalar**, not a 3-vector |
| `ctxid` | int, optional | Grille lib model only; safe to omit |

- Placement lines have **no persistentId**.
- Any filename with extension `.forest4.json` in `levels/{level}/forest/` is loaded; multiple files are normal.
- `ForestConverter` writes **one file per item type** (`forest/{typeName}.forest4.json`) — the ideal
  pattern for Generate Biome: our files get an `MT_` prefix so regeneration can overwrite only what we own.

### 1.2 `main.forestbrushes4.json` — brushes (NDJSON, level root)

```jsonl
{"name":"ForestBrush_Trees_Tropical_1","internalName":"Trees_Tropical_1","class":"ForestBrush","persistentId":"...","__parent":"ForestBrushGroup","forestItemData":"Trees_Tropical_1"}
{"internalName":"tro_tree_1_huge","class":"ForestBrushElement","persistentId":"...","__parent":"ForestBrush_Trees_Tropical_1","forestItemData":"tro_tree_1_huge","probability":0.8,"scaleMax":1.4,"scaleMin":1.2}
{"name":"ForestBrushGroup","class":"SimGroup","persistentId":"..."}
```

Three-level graph, joined by name strings:

```
SimGroup "ForestBrushGroup"
└── ForestBrush            key: name          (__parent = "ForestBrushGroup")
    └── ForestBrushElement ref: forestItemData (__parent = <brush name>, NOT internalName)
        └── TSForestItemData key in managedItemData.json → shapeFile .dae
```

**Per-element painting parameters** (all optional) — these are exactly the knobs the biome
generator should honor per item type:

- `probability` (0–1 relative weight within the brush)
- `scaleMin` / `scaleMax`
- `sinkMin` / `sinkMax` (embed depth into ground)
- `slopeMin` / `slopeMax` (degrees)
- `elevationMin` / `elevationMax` (meters)
- `rotationRange` (degrees random yaw)

Gotcha (`ForestBrushCopyScanner.cs:250–264`): brushes **with** child elements often carry a
`forestItemData` property equal to the brush's own name — not a real item-data key; ignore it.
Only element-less brushes have a valid direct `forestItemData`. The treeview must use the same
rule: children = `Elements` if non-empty, else the single `DirectForestItemData`.

### 1.3 `art/forest/managedItemData.json` — item type definitions (STANDARD indented JSON)

Keys = type names, values = `TSForestItemData` objects (`shapeFile`, `radius`, `windScale`, …).
Read-tolerant of `class:"ForestItemData"`, write `"TSForestItemData"`. Older maps may ship a
TorqueScript `managedItemData.cs` variant (`ForestScanner.GetShapeNamesCs()` handles it).

### 1.4 Game discovery — the `Forest` scene object

There is **no registry** of forest files. The game only loads `forest/*.forest4.json` when a scene
object of class `Forest` (usually named `theForest`) exists in `items.level.json`:

```json
{"class":"Forest","name":"theForest","position":[0,0,0],"rotationMatrix":[1,0,0,0,1,0,0,0,1],"scale":[1,1,1],"lodReflectScalar":2}
```

**Generate Biome must ensure this object exists or nothing renders.** The repo already treats
`Forest`/`ForestWindEmitter` as vegetation classes (`MissionGroupCopier.cs:754–763`,
`BeamFileReader.cs:246–255`).

No forest cache file exists (no `forest.dat` handling anywhere; placements re-read on level load).

---

## 2. Existing forest code to reuse

| Class | Path | Reuse for Generate Biome |
|---|---|---|
| `ForestBrushCopyScanner` | `BeamNG_LevelCleanUp\LogicCopyForest\ForestBrushCopyScanner.cs` | NDJSON brush+element parsing (`ScanForestBrushes`, element↔brush linking at 236–248) — extract/share the parse logic to enumerate the **target** level's brushes for the treeview |
| `ForestBrushCopier` | `BeamNG_LevelCleanUp\LogicCopyForest\ForestBrushCopier.cs` | `MergeManagedItemData` pattern (269–335), persistentId regeneration, `.link` stripping (`FileUtils.StripLinkExtension`) |
| `ForestConverter` | `BeamNG_LevelCleanUp\LogicConvertForest\ForestConverter.cs` | **Closest programmatic placement writer**: `AddForestType()` (76–110) merges a TSForestItemData into managedItemData.json; `AddForestItem()` (49–74) appends one-line JSON to `forest/{type}.forest4.json` |
| `ForestScanner` | `BeamNG_LevelCleanUp\Logic\ForestScanner.cs` | read-only scan of all forest4 files (line 102 deserializes `Objects.Forest`) — reuse for the negative-list cleanup scan |
| `Objects\Forest.cs`, `Objects\ManagedForestData.cs`, `Objects\ForestBrushInfo.cs` | `BeamNG_LevelCleanUp\Objects\` | POCOs; `ForestBrushInfo.Elements` is already the treeview data shape |
| Grille lib forest stack | `Grille.BeamNG.Lib\SceneTree\Forest\` (`ForestGroup`, `ForestItemCollection`, `ForestItem`), `IO\Text\SimItemsJsonSerializer` | cleanest **bulk** writer: `ForestGroup.LoadTree(dir)` / `SaveTree(dir)`, one compact line per item |
| `BeamFileReader.ReadForestBrushesForCopy()` | `Logic\BeamFileReader.cs:211/930` | scanning entry-point pattern |

Serialization discipline:

- One-line NDJSON via `BeamJsonOptions.GetJsonSerializerOneLineOptions()` (`JsonOptions.cs:22`);
  indented standard JSON via `GetJsonSerializerOptions()`.
- Culture-invariant decimals always (German-Windows gotcha; invariant culture is forced in Program.cs).
- New persistentIds: `Guid.NewGuid().ToString().ToLowerInvariant()` — brushes/elements/item-data only.
- Parse vanilla files with relaxed-JSON helpers (`JsonUtils.GetValidJsonDocumentFromString`).
- Never copy from `/assets/` (core game assets).

---

## 3. Terrain layer usage (the material masks)

The canonical API already exists:

| API | Path | What it gives |
|---|---|---|
| `LayerMaskReader.ReadTerrainBinary(terFile)` | `BeamNgTerrainPoc\Terrain\ColorExtraction\LayerMaskReader.cs:95` | full `TerrainV9Binary` (`Size`, `ushort[] HeightData`, `byte[] MaterialData`, `string[] MaterialNames`, row-major Size×Size) |
| `LayerMaskReader.ReadLayerMasks(terFile)` | `LayerMaskReader.cs:24` | `Dictionary<string, bool[]>` — one boolean mask per material name, hole byte **255** skipped |
| `LayerMaskReader.ReadTerrainInfo(terFile)` | `LayerMaskReader.cs:72` | size + material names only |
| coverage % | `TerrainColorExtractor.cs:139–151` | `CountMaskedPixels(mask)` / total — reuse for the "layer usage" column in the material list |
| hole sentinel | `TerrainHoleCutter.HoleMaterialIndex` (= 255) | reserved; never treat as a paintable layer |

Y-flip gotcha: BeamNG texture row order is inverted vs `.ter` row order — `TerrainPbrMapBuilder.ToBeamNgTextureIndex`
(`LogicBasecolorManager\TerrainPbrMapBuilder.cs:287–294`, `terrainY = size - 1 - y`). Any code that
correlates `.ter` pixels with `MT_TerrainGeneration\osm_layer\*.png` images must apply the same flip.

Pixel→world mapping + height sampling: see doc 02 (backend pipeline).

---

## 4. UI pattern to copy — BasecolorManager

Files: `BlazorUI\Pages\BasecolorManager.razor` (517 lines) + `.razor.cs` (1090 lines); no separate
State class — private fields in the code-behind. Service layer:
`LogicBasecolorManager\BasecolorManagerService` returning a result object (`Success/ErrorMessage` +
loaded data), errors routed through `PubSubChannel`, not exceptions.

Page skeleton to replicate:

1. `ErrorBoundary` + `CustomErrorContent` wrapper.
2. Header `MudStack Row`: `MudText Typo.h4` title + help `MudIconButton` → `IDialogService` help dialog.
3. `MudExpansionPanel` level selector, auto-expanded until loaded (`Expanded="@(!HasLevel)"`), hosting
   `FileSelectComponent` with `SelectFolder="true"` — **folder-based, not ZIP** (operates directly on
   an unpacked level; level root resolved via `ZipFileHandler.GetNamePath` + `info.json` fallback,
   `BasecolorManagerService.cs:261–275`).
4. Global busy: `MudProgressLinear Indeterminate` + `_busyMessage`.
5. Level info `MudAlert Severity.Info` + page Reset button.
6. Feature body (for us: material list, OSM layer repeater, negative list, generate/delete buttons).
7. Footer: Errors/Warnings/Messages buttons → `MudDrawer` message log; "Open Level Folder" button.

Machinery to copy wholesale:

- `RunBusyOperation(operation, message, action)` + `IsOperation(name)` per-button
  `MudProgressCircular` (`.razor.cs:1020–1050`) — named-operation busy state, re-entrancy guard,
  `Task.Yield()` before work, `finally` cleanup.
- PubSub consumer loop `ReadPubSubMessages` (`.razor.cs:1066–1089`) + drawer/footer block
  (razor:449–480).
- CPU/IO work in `await Task.Run(...)`.
- `ISnackbar` for final success toasts only.
- `ReloadTerrainFromDisk` before every apply (picks up in-game edits).

Persistence pattern (model for MT_Biome):

- `MtSettings` pattern (`Objects\MtSettings\MtSettings.cs`): settings file with `[JsonPropertyName]`
  DTOs, **null-tolerant `Load`** (missing/corrupt → null → rebuild defaults from scan), save on
  explicit button **and** implicitly at end of each apply, `Ensure*Defaults` hydration for schema
  evolution.
- Feature folder in level root created on demand (`MT_Tiles` precedent, `MapTileOverlayService.cs:104`).
- Staleness stamps (`LastBake*Utc`) + warning `MudAlert` + "Reset & Rebake" recovery button
  (`.razor.cs:204–253, 316–342`) — mirror as "terrain changed since last biome generation" banner.

Registration: one `MudNavLink` in `BlazorUI\MyNavMenu.razor` (note: file is in `BlazorUI\`, not
`BlazorUI\Components\`), page directive `@page "/GenerateBiome"`. No DI registration — services are
`new()`'d in the code-behind; only `IDialogService`/`ISnackbar` injected.

Reusable components verbatim: `FileSelectComponent` (folder/file picker on STA thread),
`CustomErrorContent`, help-dialog pattern (`BasecolorManagerHelpDialog` as template).

---

## 5. Gaps — what does not exist yet (built in docs 01/02)

1. **No treeview UI** anywhere for brushes → elements (CopyForestBrushes shows a flat MudTable with
   tooltip). We build a `MudTreeView`-based selector.
2. **No distance-transform / border-band code** for terrain masks (zones "N meters from layer
   border" need a distance field; nothing morphological exists in the repo).
3. **No placement sampler** (random/Poisson scatter with density, slope/elevation filters).
4. **No placement manifest/bookkeeping** (who placed which item) — required for safe global and
   per-layer delete that never touches hand-placed items.
5. **OSM layer region source** — what's on disk after generation (`MT_TerrainGeneration\osm_layer\*.png`
   masks etc.) is detailed in doc 02.
