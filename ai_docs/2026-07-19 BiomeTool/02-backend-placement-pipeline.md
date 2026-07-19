# Generate Biome — Backend Placement Pipeline

Date: 2026-07-19
Prereqs: doc 00 (formats/reuse), doc 01 (data model), doc 03 (parameter semantics from BeamNG).

## 0. Code placement

Pure algorithm stages (mask → bands → samples) go into **`BeamNgTerrainPoc\Terrain\Biome\`** so
they are unit-testable in `BeamNgTerrainPoc.Tests` (the existing 1000+-test suite). App glue
(level scanning, brush parsing, forest file writing, manifest, UI service) goes into
**`BeamNG_LevelCleanUp\LogicBiome\`**. Same split the terrain generator already uses.

## 1. Pipeline overview

```
LoadLevel
  └─ .ter (LayerMaskReader) + TerrainBlock params + brushes + managedItemData + OSM mask PNGs
Generate (per layer, Replace mode)
  1. delete previous manifest items of this layer          (§8)
  2. build region mask                                     (§2)
  3. distance field → zone bands                           (§3)
  4. per zone: sample placements                           (§4–§6)
  5. write forest/MT_biome_{LayerId}.forest4.json          (§7)
  6. negative-list cleanup                                 (§9)
  7. update manifest + settings, stamp .ter timestamp      (§8)
```

All stages report via `PubSubChannel` and run chunked (Y-row or candidate batches) so progress is
visible and a `CancellationToken` can abort between chunks.

## 2. Level parameters and region masks

### 2.1 Terrain parameters

| Value | Source |
|---|---|
| `sizePx` | `.ter` header (`TerrainV9Binary.Size`) |
| `metersPerPixel` (mpp) | TerrainBlock `squareSize` — copy `TerrainMaterialService.LoadMetersPerPixelFromTerrainBlock` (`BlazorUI\Services\TerrainMaterialService.cs:306–351`, NDJSON scan of `main/MissionGroup/**/items.level.json`), fallback `terrain.json` |
| `maxHeight` | TerrainBlock NDJSON line — **no reader exists yet**; add one next to the mpp helper (heights in `.ter` are `ushort`: `h = u16 / 65535f * maxHeight`, `TerrainV9Serializer.cs:160–166`) |
| `terrainBaseHeight` | TerrainBlock `position[2]` (corner convention `[-half,-half,baseZ]`, `TerrainBlockUpdater.cs:126–129`) |

MT-generated levels also expose `TerrainSize`/`TerrainMetersPerPixel` in `MT_settings.json` — use
as cross-check only; the TerrainBlock is authoritative.

### 2.2 Region mask — terrain material layer

`LayerMaskReader.ReadLayerMasks(terFile)` → `bool[sizePx²]`, row-major, **row 0 = south**, index
`i = y*sizePx + x`, hole byte 255 excluded. No flip needed anywhere downstream — the same `(x,y)`
feeds both mask lookup and world transform.

### 2.3 Region mask — OSM layer

Load `MT_TerrainGeneration\osm_layer\{key}.png` (or `{material}_osm_layer.png`), L8 threshold
`> 127` (same rule as `MaterialLayerProcessor`), then **Y-flip into terrain space**:
`maskIndex = (sizePx - 1 - imageY) * sizePx + x` (`MaterialLayerProcessor.ProcessRow` pattern,
`Processing\MaterialLayerProcessor.cs:130–136`). Validate PNG dimensions == sizePx (warn + bail per
layer otherwise). Pixels that are terrain holes in the `.ter` are removed from the mask.

## 3. Zone bands — distance-to-border via existing EDT

Reuse `BeamNgTerrainPoc\Terrain\Algorithms\Blending\DistanceFieldCalculator.ComputeDistanceField(byte[,] mask, float mpp)`
(`:125–149`) — exact Felzenszwalb & Huttenlocher EDT, output **meters**.

- Build `outside` = inverse of the region mask as `byte[,]` (255 = foreground).
- `depth = ComputeDistanceField(outside, mpp)` → for every in-region pixel: distance to the nearest
  non-region pixel = **depth from border**. (Verify the `[dim0,dim1]` axis order against an
  existing caller like `DistanceFieldTerrainBlender` when implementing — do not assume.)
- Zone membership from the ordered zone list (doc 01 §2.4): zone k covers
  `start_k <= depth < start_k + DepthMeters_k` with `start_k = Σ depth of previous zones`;
  `IsInterior` covers `depth >= start_k`.
- Output per zone: a pixel index list (`List<int>`) — this doubles as the area measure
  (`area_m² = count · mpp²`) for the density → count mapping and the UI estimate.
- Disjoint region blobs need no special handling — banding is per-pixel.
- An empty band (region thinner than the accumulated depths) is fine: zero pixels → zero items.
  That is exactly how "empty border zone = keep-clear strip" works: the strip's pixels belong to
  the empty zone and to no other, so nothing is planted there.

## 4. Placement sampler (per zone)

Deterministic, seeded: `seed = Hash(GlobalSeed, LayerId, zoneIndex)` (`SeedOverride` honored).
Same seed + same settings + same terrain ⇒ identical forest (doc 03 proposal 3).

For each selected item type with `DensityPercent > 0`:

1. **Target count** `N = zoneArea_m² · ρ(item)` — density mapping in §5.
2. **Candidate generation**: pick a random pixel from the zone's index list, add sub-pixel jitter
   `(+u, +v) ∈ [0,1)²` → continuous terrain-space point `(x+u, y+v)·mpp`. (Uniform over the zone
   regardless of blob shape; no rasterized polygon math needed.)
3. **Rejection filters**, in cheap-first order:
   - **noise clump** (optional, §6): FBM value at point < threshold → reject
   - **slope**: `slopeDeg = atan(|∇h|)` from central differences of the decoded heightmap over
     mpp; reject outside `[slopeMin, slopeMax]` (zone override, else brush-element `slopeMin/Max`,
     else 0–90)
   - **elevation**: decoded height + `terrainBaseHeight` outside `[elevationMin, elevationMax]` →
     reject (zone override, else element values)
   - **spacing/occupancy**: spatial hash grid (cell = max item diameter); reject if any accepted
     item is closer than `spacingFactor · (r_a·s_a + r_b·s_b)` with `r` = `managedItemData.radius`
     (fallback 0.5 m), `s` = item scale, `spacingFactor` default 1.0 (0 disables). Prevents
     intersecting trunks (doc 03 proposal 4).
4. Retry budget: `attempts = N · oversample` (oversample default 4); stop at `N` accepted or budget
   exhausted (dense zones with strict filters saturate gracefully).
5. **Fair interleave** across item types: process items in weighted round-robin (largest remaining
   N first) rather than fully sequentially, so the first species doesn't monopolize space in
   crowded zones.

Per accepted candidate, randomize from the item's brush-element parameters (doc 00 §1.2; defaults
when absent):

- `scale`: uniform in `[scaleMin, scaleMax]` (default 0.8–1.2)
- yaw: uniform in `rotationRange` (default 360°) → `rotationMatrix = [cosθ,sinθ,0,−sinθ,cosθ,0,0,0,1]`
- `sink`: uniform in `[sinkMin, sinkMax]` (default 0–0.1 m), subtracted from Z

Note vs. BeamNG: the original decouples tool density from element `probability` (species mix).
Our per-item sliders replace `probability` entirely — when the user checks a whole brush, the
sliders initialize from the elements' `probability` (probability × 100 %) so the brush's intended
mix is the starting point.

## 5. Density mapping (slider % → items)

Slider stays 0–100 % (spec) but is physically anchored so 100 % ≈ full canopy for that species:

```
ρ_max(item) = 1 / (π · (spacingFactor · r · s̄)²)      items/m², s̄ = mean scale
ρ(item)     = DensityPercent/100 · ρ_max(item)
N           = zoneArea_m² · ρ(item)
```

The UI shows the derived `N` live next to each slider and the zone/run totals (doc 03 #11/#12);
hard warning dialog above 500 k items per run.

## 6. Noise clumping (optional per zone, doc 03 proposal 9)

Small self-contained FBM/value-noise implementation in `Terrain\Biome\` (no noise library exists in
the repo — do not add a dependency). Parameters per zone: `seed`, `featureSizeMeters` (frequency),
`coverage` 0–1 (threshold), `octaves` (default 3). Evaluated in terrain-space meters so results are
resolution-independent. Default off ⇒ uniform fill.

## 7. Writing forest items

Position (proven pattern — buildings pipeline, `BuildingGenerationOrchestrator.cs:101–110, 237–298`):

```
world.x = terrainX_m − halfSizeMeters          // half = sizePx/2 · mpp   (BeamNgCoordinateTransformer)
world.y = terrainY_m − halfSizeMeters
world.z = bilinear(height, terrainX_m, terrainY_m) + terrainBaseHeight − sink
```

Bilinear height sampling, not nearest pixel (`SampleHeightmapBilinear`, `:274–298`).

Output: **one NDJSON file per layer** `forest/MT_biome_{LayerId}.forest4.json`, mixed item types,
one compact line per item via `BeamJsonOptions.GetJsonSerializerOneLineOptions()` (ForestConverter
pattern; `Objects\Forest` POCO). Overwrite whole file on regenerate — never append across runs.

Pre-write ensures (doc 00 §1.4):

- `Forest` scene object exists in the MissionGroup (`{"class":"Forest","name":"theForest",...}`) —
  create in the vegetation group if missing (`MissionGroupCopier` precedent), else nothing renders.
- `ForestBrushGroup` SimGroup line exists in `main.forestbrushes4.json` (only if we ever write
  brushes; reading doesn't need it).
- Every referenced `type` exists in `art/forest/managedItemData.json` — it must, since the treeview
  was built from this level's own brushes; validate anyway and skip+warn on dangling references.

## 8. Manifest and delete operations — `MT_Biome\manifest.json`

```json
{
  "schemaVersion": 1,
  "terFileTimestampUtc": "2026-07-19T10:00:00Z",
  "layers": [{
    "layerId": "…", "kind": "TerrainMaterial", "sourceKey": "Grass2",
    "forestFile": "forest/MT_biome_{id}.forest4.json",
    "fileSha256": "…", "generatedAtUtc": "…", "seedUsed": 1234,
    "itemCount": 12480,
    "items": [ { "type": "oak_large", "pos": [x, y, z], "scale": 0.93 } ]
  }]
}
```

Item identity for matching: `type` + `pos` within ε=1e-3 + `scale` within 1e-3 (rotation not
needed). ~12 k items ≈ 1 MB JSON — acceptable; gzip later if maps get huge.

**Delete (global = all layers; per-layer = one):**

1. **Fast path**: owned file exists and SHA-256 matches manifest → delete the file. Nothing else
   can be in it, hand-placed items are untouched by construction.
2. **Fallback** (hash mismatch or file missing — e.g. the in-game editor re-saved/merged forest
   files): scan **all** `forest/*.forest4.json`, parse line-wise, drop lines matching manifest
   records of the target scope, rewrite only changed files **preserving all other lines verbatim**.
   Report matched/orphaned counts via PubSub (orphans = records not found anywhere → warn).
3. Drop the layer's manifest records; save manifest.

This is the property the whole feature hangs on: *generated items are deletable at any time without
ever touching a hand-placed item* — deliberately stronger than BeamNG's own half-broken UID
bookkeeping (doc 03 §1).

## 9. Negative-list cleanup (mandatory post-step)

1. Build combined negative mask: OR of the selected material masks (§2.2) + OSM masks (§2.3).
2. Optional **buffer meters** (global setting, default 0): EDT on the negative mask, membership =
   `distance ≤ buffer` — catches trees leaning over the road edge although their trunk pixel is off
   the mask.
3. For each manifest item (all layers): terrain-space `x_t = world.x + half`, pixel
   `(round(x_t/mpp), round(y_t/mpp))`; if inside the negative mask → remove (same line-removal
   machinery as §8 step 2, then update manifest).
4. `IncludeForeignItems` (opt-in, doc 01 §2.3): additionally test every line of every
   non-owned forest file and drop hits — confirmation dialog shows the count first.

Runs automatically after every generation and on the explicit `[Cleanup Now]` button.

## 10. New classes

| Class | Project/folder | Role |
|---|---|---|
| `BiomeRegionMaskBuilder` | `BeamNgTerrainPoc\Terrain\Biome\` | §2.2/§2.3 masks (bool[] + pixel lists) |
| `BiomeZoneBander` | 〃 | §3 EDT bands → per-zone pixel index lists |
| `BiomePlacementSampler` | 〃 | §4–§6: seeded sampling, filters, occupancy, noise |
| `BiomeNoise` | 〃 | small FBM |
| `BiomeDensityModel` | 〃 | §5 mapping + count estimates for the UI |
| `BiomeService` | `BeamNG_LevelCleanUp\LogicBiome\` | LoadLevel/Generate/Delete/Cleanup orchestration, PubSub |
| `BiomeForestWriter` | 〃 | §7 file writing, Forest-object ensure |
| `BiomeManifest` (+ store) | 〃 | §8 ledger, hashing, line-removal fallback |
| `BiomeBrushCatalog` | 〃 | brush/element/itemdata parsing (shared logic extracted from `ForestBrushCopyScanner`) |
| DTOs | `Objects\Biome\` | doc 01 §3 |

## 11. Implementation phases

1. **Phase 1 — terrain material layers end-to-end**: DTOs + settings persistence, LoadLevel,
   material list with coverage, zone repeater UI, brush treeview, sampler (uniform, slope filter,
   spacing), writer, manifest, global/per-layer delete, staleness stamp. *Ship-able alone.*
2. **Phase 2 — OSM layers + cleanup**: OSM mask discovery + Y-flip, OSM layer repeater, negative
   list UI + cleanup engine (+ buffer), auto-cleanup after generate.
3. **Phase 3 — quality**: noise clumping, elevation filters UI, estimated-count live preview,
   foreign-item cleanup opt-in, "Refresh OSM Layers" via georeference + `OsmLayerExporter`.
4. **Backlog** (doc 03): edge placement along borders, field/row mode, terrain-normal alignment,
   user mask-image layers, GroundCover integration hint (native per-material scatter — the
   zero-effort complement for grass/small vegetation; forest items for real trees).

## 12. Tests (`BeamNgTerrainPoc.Tests`)

- ZoneBander: synthetic 64² masks — band widths in meters vs mpp, interior zone, region thinner
  than bands, disjoint blobs, hole pixels excluded.
- Sampler determinism: same seed ⇒ identical placements; different seed ⇒ different.
- Density: slider→count monotonicity; 100 % never exceeds packing bound; spacing respected
  (no pair closer than the rule).
- Slope/elevation filters on a synthetic ramp heightmap.
- Manifest round-trip + line-removal fallback: merged foreign file keeps foreign lines verbatim.
- OSM PNG Y-flip: asymmetric fixture mask lands on the correct terrain rows.
- Coordinate: pixel↔world round-trip against `BeamNgCoordinateTransformer` for odd/even sizes.
