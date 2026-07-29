# Backdrop Generation — Tutorial

The backdrop is a satellite-textured 3D ring around your playable terrain. It is built
from the same GeoTIFF elevation data as the terrain itself, meshed adaptively (fine near the terrain
edge, coarse far away), textured with satellite imagery, and exported as static meshes
(`MT_backdrop`) into your level. From inside the map, the horizon shows real surrounding landscape
instead of a skybox void — and with the **Collision Mesh** switch on, you can even drive onto it
(it is off by default in favor of much faster level loading).

The feature is **off by default**. Nothing changes in your maps unless you enable it.

---

## 1. Prerequisites

- Your terrain must come from a **GeoTIFF-based source** (single GeoTIFF, GeoTIFF folder, or
  XYZ/elevation-API download). PNG heightmaps have no georeference and no surrounding data — the
  backdrop panel stays hidden for them.
- The GeoTIFF mosaic must be **larger than your terrain crop**. The backdrop is cut from the area
  *around* the terrain rectangle, so if you cropped nothing, there is nothing to build a ring from.
- Internet access for satellite tiles (same tile providers as the BaseColor Manager overlay).

> **Important — "Reduce to crop":** if you use the reduce/crop-to-file feature, the reduced file
> contains *only* the terrain area. The backdrop then has no surrounding data and will warn instead
> of generating. Keep the original (larger) source selected when you want a backdrop.

## 2. Quick start

1. **Generate Terrain** page → load your GeoTIFF source and place the terrain crop box as usual.
2. Open the **Backdrop (Experimental)** section (below Bridges & Tunnels) and switch
   **Generate Backdrop** on.
3. A second, dashed box appears in the map selector around your terrain box — this is the backdrop
   area. It always contains the terrain box. Drag its body to move it, drag the 8 handles to
   resize it (the fullscreen dialog additionally offers typed S/W/N/E coordinates in a "Backdrop"
   field group). Zero margin on one side is allowed if you only want a backdrop in some directions.
4. Optionally press **Update estimate** — you get triangle count, texture memory, tile downloads and
   chunk count, with a yellow/red warning at high cost. The estimate never blocks generation.
5. Press **Generate Terrain** as usual. The backdrop is generated as an extra stage at the end of
   the terrain run; its textures are baked right after. A backdrop failure never fails the terrain
   run — you get a warning and the terrain finishes normally.
6. Load the level in BeamNG. The backdrop meshes are in the `MT_backdrop` scene group.

That is the whole happy path. The defaults (50 m fine edge band, 1 m / 16 m vertical error,
2048 m chunks, 1 m/px near texel density) are sized for a compact result; if you want a more
faithful backdrop surface and accept several times the mesh data, lower the vertical errors and
widen the edge band (see §3 and the size box below the table).

## 3. Settings reference

| Setting | Default | What it does |
|---|---|---|
| Edge band (m) | 50 | Width of the high-detail strip along the terrain edge. Inside it, the mesh honors the *near* error tolerance and heights are blended so the backdrop meets the terrain seam exactly. |
| Max vertical error near (m) | 1.0 | Mesh accuracy in the edge band. Lower = finer mesh. |
| Max vertical error far (m) | 16.0 | Mesh accuracy at the outer rim. The tolerance blends from near to far with distance. |
| Chunk size (m) | 2048 | Target size of one exported mesh piece (one DAE + one texture per chunk). Powers of two keep chunk widths on the mesher's dyadic split lattice — prefer 1024/2048/4096. |
| Texel density near (m/px) | 1.0 | Satellite texture resolution at the terrain edge; it coarsens automatically (up to 4×) toward the outer rim. |
| Max chunk texture | 2048 | Upper bound for a single chunk texture (512–4096). |
| Far raster cap | 8192 | Memory guard: the surrounding elevation data is loaded at most at this resolution. Bigger = more far-field height detail, more RAM during generation. |
| Seam skirt | on | A small apron along the terrain edge that hides hairline cracks between terrain and backdrop at a distance. Leave it on. |
| Collision mesh | off | Lets you drive on the backdrop. The game builds collision from the visible mesh (scene property, no extra mesh data on disk); switching it off skips that physics build and makes the level load faster. Off, you fall through the backdrop if you drive onto it. |

> **Output size rule of thumb:** the chunk DAEs cost ≈ 92 MB per million triangles (the collision
> switch does not change disk size — it is a scene property) — press **Update estimate** and
> multiply. Triangles are driven by,
> in order of impact: the *far* vertical error over rugged terrain, the *near* error, and the edge
> band (~2 triangles per m² of band area, i.e. terrain perimeter × band width). Reference
> measurement (32-chunk alpine bake, 2026-07-28): the pre-2026-07-28 defaults (200 m / 0.5 m / 8 m)
> produced **1.78 GB** of DAEs; the current defaults (50 m / 1 m / 16 m) target roughly a third to
> a half of that. Satellite textures are small by comparison (115 MB of PNGs in the same bake).
> Shrinking the backdrop box is the only lever that scales *everything* down linearly.

With **Collision Mesh** on, the TSStatic entries say `collisionType`/`decalType`
`"Visible Mesh Final"` — the game builds physics from the visual mesh itself; the DAEs never
contain collision geometry. Off, both say `"None"`. The type is always written explicitly, so a
world-editor save round-trips it faithfully.

## 4. The BaseColorManager and the backdrop — how and when

This is the part that is easiest to get wrong, so here is the mental model first:

**There is one satellite pipeline with two consumers.** The BaseColor Manager bakes satellite
imagery *onto your terrain* (blended with your terrain materials). The backdrop bakes satellite
imagery *onto the ring around the terrain* (pure satellite, no materials out there). Both use the
same tile provider, the same imagery date, the same brightness/contrast/saturation adjustments, and
the same tile cache (`MT_Tiles\cache` — tiles download once, ever). The two are interlocked so they
cannot drift apart.

### What happens automatically

- **During terrain generation** (backdrop enabled): after the meshes are built, the backdrop
  textures are baked immediately, using whatever overlay settings your level currently has in
  `MT_settings.json`. On a fresh level that means the default provider ("Google Satelite Only")
  with neutral adjustments.
- **In the BaseColor Manager**: whenever you press **Apply BaseColor Mode** or **Reset & Rebake**,
  the backdrop chunk textures are re-baked as part of the same operation — with the provider,
  imagery date and brightness/contrast/saturation you just configured. You never rebake the
  backdrop textures by hand; it rides along.
- **Staleness banner**: if the backdrop textures no longer match the current provider/imagery date
  or the georeference changed, the BaseColor Manager shows its stale-bake banner with the reason
  "the backdrop textures no longer match the provider or georeference". Press **Reset & Rebake**
  and both terrain and backdrop are consistent again.

### So when do you actually open the BaseColor Manager?

**After generating, once — to make the terrain match the backdrop.** The backdrop is *pure*
satellite imagery. Your terrain, by default, shows its terrain materials. If you stop here, you get
a visible style break at the terrain edge: painted terrain inside, aerial photo outside. To make
the transition seamless:

1. Open the **BaseColor Manager**, load the level.
2. Pick your tile provider / imagery date and fetch the overlay.
3. Set the **Global Overlay Blend high** (near 100 %), at least for the materials that dominate your
   map borders. The higher the blend, the closer the terrain's ground texture is to the aerial
   image, and the less visible the seam. (This is exactly what the help note in the backdrop panel
   means: *"For a seamless look at the terrain edge, use a high satellite overlay blend in the
   BaseColor Manager — the backdrop is pure satellite imagery."*)
4. Adjust brightness/contrast/saturation to taste.
5. Press **Apply BaseColor Mode**. The terrain basecolor is baked *and* every backdrop chunk texture
   is re-baked with the same provider and adjustments — one click, both sides consistent.

**Again, whenever you change imagery.** Different provider, different imagery date, different
brightness/contrast/saturation → press **Apply BaseColor Mode** (or **Reset & Rebake**) and the
backdrop follows. The adjustment values are fingerprinted into the bake, so an unchanged setup
reuses the cached warp (fast) and a changed one forces a fresh bake (correct).

**Not at all, if you only touched geometry.** Regenerating the backdrop mesh (see below), moving
the backdrop box, or changing mesh tolerances does not require a BaseColor Manager visit — the
generation/regeneration path bakes textures itself with the current settings.

### Recommended order

Either order works because of the interlock, but this one avoids double tile downloads for new
levels:

1. Generate terrain **with** backdrop (gets you meshes + first textures with default imagery).
2. BaseColor Manager: provider, blend high, adjustments → **Apply BaseColor Mode**.
3. Iterate on the look purely in the BaseColor Manager from then on.

If the level already has a tuned basecolor setup and you add a backdrop later, just generate (or
**Regenerate Backdrop**) — the bake picks up your existing provider/adjustments automatically.
You do not need to reload the level in the BaseColor Manager if it is already open; the backdrop
data is picked up on the next Apply/Rebake.

### Known limitation (V1)

The terrain blends satellite imagery with material colors; the backdrop is satellite only. At
overlay blends **well below 100 %** a tint difference at the terrain edge is possible. There is no
automatic fix in V1 — the mitigation is the shared adjustment sliders and a high blend near the
edges.

## 5. Regenerating and removing

- **Regenerate Backdrop** (button in the backdrop panel): rebuilds meshes *and* textures without
  rerunning the whole terrain pipeline. Works in the same session (uses the cached heightmap) and
  across sessions (reconstructs the heightmap from the level's `theTerrain.ter`). Requires the
  GeoTIFF source metadata to be loaded and a backdrop selection to exist — the button stays
  disabled otherwise. Use it after changing backdrop settings or the backdrop box.
- **Remove Backdrop** (button, with confirmation): deletes `art/shapes/MT_backdrop/`, the
  `MT_backdrop` scene group, and the settings block. This is the *only* destructive action —
  a terrain run with the backdrop switch off leaves an existing backdrop completely untouched.
- **Disabling ≠ removing.** Switching "Generate Backdrop" off just skips the stage on the next run.

## 6. Presets

Backdrop settings (enabled flag, box position/size, all tunables) round-trip through terrain
presets. Importing a preset restores the switch, the tunables and the box; the box is applied after
the GeoTIFF loads (same deferred mechanism as the crop box, and with the same caveat: the box is
stored in source pixels, so it fits the source the preset was made for). Old presets without a
backdrop block import exactly as before and leave the backdrop off.

## 7. What lands in your level

```
levels/{name}/
├── art/shapes/MT_backdrop/
│   ├── backdrop_{cx}_{cy}.dae        one mesh per chunk (visual only — never collision geometry)
│   ├── backdrop.materials.json
│   └── textures/backdrop_{cx}_{cy}.color.png   (.color → in-game cooker compiles to sRGB DDS)
├── main/MissionGroup/MT_backdrop/items.level.json   (TSStatics, one per chunk)
├── MT_settings.json                  "BackdropSettings" block (chunk registry — do not hand-edit)
└── MT_TerrainGeneration/backdrop/    debug artifacts (rasters, quadtree maps, stats)
```


## 8. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Backdrop panel content hidden | PNG heightmap source — backdrop needs a georeferenced GeoTIFF source. |
| Warning "backdrop rect …" and no backdrop | The backdrop box does not fit the source mosaic, or the source is the reduced/cropped file. Reselect the original source and redraw the box. |
| A chunk is flat gray | Tile download failed twice for that chunk (network/provider hiccup). Fix connectivity, then **Reset & Rebake** in the BaseColor Manager or **Regenerate Backdrop**. |
| Visible tint difference at the terrain edge | Overlay blend below 100 % — raise the Global Overlay Blend / per-material blends and **Apply BaseColor Mode** (see §4). |
| Stale-bake banner mentions the backdrop | Provider/imagery date/georeference changed since the last texture bake — **Reset & Rebake**. |
| First generation with backdrop is slow | Large backdrop areas mesh and bake per chunk; watch the message log for progress. Reduce the area, raise the far error tolerance, or lower the far raster cap. Known code-side hot spots and planned fixes: [Backdrop-Performance-Improvement-Plan.md](Backdrop-Performance-Improvement-Plan.md). |
| Backdrop still in the level after disabling | By design — disabling only skips future runs. Use **Remove Backdrop** to delete it. |
