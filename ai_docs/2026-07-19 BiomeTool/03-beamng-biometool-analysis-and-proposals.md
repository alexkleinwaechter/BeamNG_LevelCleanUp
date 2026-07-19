# BeamNG Biome Tool — Analysis and Proposals for "Generate Biome"

Date: 2026-07-19
Source analyzed: `examples_for_ai\beamng_lua\ge\extensions\editor\biomeTool.lua` (3865 lines) plus
`ge\extensions\editor\forestEditor.lua`, `ge\extensions\editor\api\forest.lua`, `ge\extensions\core\forest.lua`.

## 0. Architectural fact up front

The Lua file is **only the UI/orchestration layer**. The actual placement algorithm (position
sampling, density realization, mask sampling, slope math, per-item scale/rotation randomization)
lives in the C++ engine class `ForestBrushTool` (`biomeTool.lua:3697`). Lua configures a "biome
process", pumps `runBiomeProcess()` once per frame in a modal with progress/cancel, then commits the
returned items with undo actions. The exact sampler (uniform-random vs. Poisson) is **not visible** —
our clone re-implements the C++ side from the parameter semantics below. That is design freedom,
not a porting job.

## 1. How BeamNG's tool works (condensed)

Two layer kinds (two tabs):

- **Level Biome** = terrain layers: placement wherever a chosen terrain material is painted
  (0-based material index into the `.ter` material map), OR wherever a grayscale mask image says so,
  OR wherever a procedural noise mask (RA_*) says so. Mask image wins over material index; noise map
  wins over mask (`biomeTool.lua:1717–1724`).
- **Biome Areas** = area layers: user-drawn lasso polygons (inclusion areas + exclusion zones
  subtracted from placement).

Per layer, **three brush zones** (`enum_forestBrushItemZone`, line 79):

| Zone | What it is | Own parameters |
|---|---|---|
| `central` | interior fill | `ForestDensity` 0–1 |
| `falloff` | border fringe band | `BordersFalloff` width, `BordersDensity` 0–1, own brush selection |
| `edge` | items **on** the boundary line | `EP_ItemDistance` spacing, `EP_RandomTilt` ±°, own brush |

Generation flow: "Generate Layer" → `initBiomeMatProc` / `initBiomeLassoProc` / `initBiomeFieldProc` /
`initBiomeEdgeProc` on the C++ tool → per-frame pump with progress+cancel → commit as
`AddBiomeItems` / `ReplaceBiomeItems` / `RemoveBiomeItems` undo actions → on level save,
`forest:saveForest()` writes `forest/*.forest4.json` and layer config goes to
`<level>/art/biomeTool/biomeTool.json`.

### Tool-level parameters (the contract our clone honors)

| Parameter | Range/default | Meaning |
|---|---|---|
| `ForestDensity` | 0–1, default 1 | central-zone density scalar (dimensionless — absolute counts derive from brush-element footprints C++-side) |
| `SlopeInfluence` | −1..1, default 0 | slope preference weight; 0 = slope filtering off (UI disables range row) |
| `SlopeRange` | 0–90°, default {0,90} | min/max terrain slope where items may spawn |
| `BordersFalloff` | −10..10, default 0 | border band width |
| `BordersDensity` | 0–1, default 1 | density inside the border band |
| `BlendingMethod` | Add / Replace / Delete | how generated items merge with existing ones |
| `EdgePlacement` | bool | enable edge-line brush |
| `EP_ItemDistance`, `EP_RandomTilt`, `EP_BorderFalloff` | | edge spacing / ±tilt° / band |
| `FieldPlacement` + `FieldItemDistance` / `FieldRowDistance` / `FieldRowOrientation` | 0–100 m / 0–100 m / 0–360° | agricultural row/grid mode inside polygons |
| `RA_Seed/Freq/Amp/Thr/Oct` | | Perlin-style procedural coverage mask (clumpy natural distribution) |
| `TerrainMask` | image path | grayscale placement mask |

### Element-level parameters (second half of the filter story)

Each `ForestBrushElement` carries `probability`, `scaleMin/Max`, `sinkMin/Max`, `slopeMin/Max`,
`elevationMin/Max`, `rotationRange` — consumed by the same C++ tool. **Elevation filtering exists
only at element level, not in the tool UI.** Tool-level AND element-level slope filters both apply.
Density and species mix are decoupled: tool density scales counts; element `probability` picks the
species — never merge these into one knob.

### Forest interaction

- Requires a `Forest` scene object; if missing, a modal offers to create `"theForest"`
  (`createForestObject`, line 3487). Our clone must do the same (silently).
- Spatial deletion uses `forestData:getItemsPolygon(...)` / `getItemsCircle(...)` — geometric
  queries instead of ownership records. Notably, **per-layer UID bookkeeping is half-broken in the
  shipped build** (UID capture commented out, lines 1781–1785); BeamNG works around it by passing
  previous UIDs into the proc and by geometric polygon deletes. **Our manifest-based bookkeeping
  (doc 01/02) is deliberately better than the original here** — it's what makes "never delete
  hand-placed items" possible.
- Vestigial in the shipped build (don't copy): (Re)generate-all/Undo/Redo toolbar stubs
  (3127–3134), RA_Map custom field editor registered but unhandled, `onExtensionLoaded` referencing
  an undefined function.

### Clever details worth stealing

- **Terrain-normal alignment is a separate editor preference**, applied post-placement by
  ray-casting and rotating Z onto the surface normal (`forestEditor.lua:2289–2308`, with forest
  collision disabled during the cast). Offline we can use the heightmap normal directly.
- **Robust down-raycast** pattern for ground snapping: start +1 m, cast 100 m down, retry +100 m /
  −1000 m (`castRayDown`, line 288). Offline equivalent: bilinear heightmap sample.
- **Slope range UI disabled while influence is 0** — good affordance, copy it.
- **Two-phase brush selection dialog** (temp selection committed on OK, discarded on Cancel).
- **Chunked async generation** with progress and cancel — mandatory at 100k+ items.

---

## 2. Proposals — additions the request didn't mention but should have

The user's spec (layers → zones → brush treeview → density; OSM layers; negative list; MT_Biome
persistence; global/per-layer delete) covers the core. From the BeamNG code, these are the
additions I propose, ranked:

### Essential (would be missed immediately in results)

1. **Slope filter per zone** (`slopeMin`/`slopeMax` degrees + optional influence weight): without
   it, trees stand on cliffs and vertical embankment walls. Cheap to compute from the heightmap.
   Default: honor element-level `slopeMin/Max` from the brush automatically; expose a per-zone
   override.
2. **Elevation filter per zone** (`elevationMin/Max`): keeps pines off the beach; already a brush
   element concept, so read defaults from the brush.
3. **Random seed per layer (persisted)**: reproducible regeneration — same seed → same forest.
   Regenerating after a small settings tweak shouldn't reshuffle every tree on the map.
4. **Min-spacing / overlap avoidance** using each item type's `radius` from managedItemData
   (scaled): pure uniform random at high density produces intersecting trunks. Simple approach:
   jittered-grid or dart-throwing with per-cell occupancy; full Poisson disk not required.
5. **Scale/rotation/sink randomization from brush elements** (`scaleMin/Max`, `rotationRange`,
   `sinkMin/Max`, `probability` weighting): the placement must use these or every tree is identical
   and floats on slopes. `sink` matters: bury 0.05–0.3 m so trunks don't hover on uneven ground.
6. **Auto-create the `Forest` scene object + `ForestBrushGroup`** when missing — otherwise the
   generated forest simply doesn't render and the user files a bug.
7. **Blending method Add vs. Replace** per generate run: Replace = delete our previously generated
   items for that layer (from the manifest) then place fresh; Add = accumulate. Default Replace —
   idempotent regeneration is what users expect from "generate".
8. **Progress + cancel** (PubSub progress messages, chunked placement loop, CancellationToken):
   BeamNG pumps its job per-frame for a reason; a 4k×4k map with dense forest is hundreds of
   thousands of samples.

### Strongly recommended (cheap, high visual payoff)

9. **Procedural noise coverage mask per zone** (seed/frequency/threshold/octaves, BeamNG's RA_*):
   multiplies the zone mask so coverage is clumpy — natural clearings and groves instead of a
   uniform carpet. This is the single biggest "looks real vs. looks generated" factor.
10. **Edge placement mode** as a zone option: items *on* the border line at fixed spacing with
    random tilt (BeamNG's `EP_*`) — fence posts, tree lines along field borders, rock rows.
11. **Density as items per 100 m²** in the UI rather than an opaque 0–100% (or show the derived
    estimated count live per zone): BeamNG's dimensionless 0–1 confuses users; we can do better
    because we know the zone's pixel area.
12. **"Estimated item count" preview** before generating (zone area × density), plus a hard safety
    cap with a warning (e.g. > 500k items).
13. **Terrain-change staleness banner**: stamp `.ter` last-write time at generation
    (BasecolorManager's `LastBake*Utc` pattern); warn "terrain was modified since last biome
    generation — trees may float/sink; regenerate".

### Nice-to-have (backlog, keep out of v1)

14. **Field/row placement mode** (item distance × row distance × orientation) for
    vineyards/orchards/crops inside polygon or OSM `landuse=farmland`/`vineyard` layers.
15. **Terrain-normal alignment option** (tilt items to the surface normal, from heightmap
    gradient) — subtle for trees, good for rocks.
16. **Lasso/polygon area layers** (BeamNG's Biome Areas tab): manual polygons are a viewport
    feature; our page has no 3D viewport, so defer. OSM polygon layers cover most of the need.
17. **Grayscale mask-image layers**: accept a user-supplied PNG as a placement mask (BeamNG
    supports it; trivial once the mask pipeline exists).

### Explicitly out of scope (vs. BeamNG)

- In-viewport lasso drawing, drag-reflow, highlight blink — editor-viewport features.
- Engine undo/redo history — replaced by the manifest-based per-layer/global delete.
