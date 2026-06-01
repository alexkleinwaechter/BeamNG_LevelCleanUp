# Basecolor Manager Overlay Extension

Date: 2026-05-31

## Scope

The Basecolor Manager can now blend an external raster image or a downloaded map tile overlay into the generated BaseColor texture. This is only applied in BaseColor Mode, because Paint Mode still needs flat per-material placeholder textures for BeamNG terrain painting.

## UI Behavior

- The BaseColor Mode tab has an image selector below the action buttons and above the material table.
- If `MT_settings.json` contains usable `GeoReferenceSettings`, the same panel also shows a tile provider dropdown and a fetch button.
- Provider names come from `map-tile-overlays-analysis.md` and intentionally keep the original spelling:
  - `OSM`
  - `Google Roadmap`
  - `Google Terrain`
  - `Google Satelite Only`
  - `Google Hybrid`
  - `ArcGIS Satelite`
- The global blend slider is implemented as a master slider: changing it updates all material-layer blend sliders to the same value.
- Each material row has its own overlay blend slider. This controls the final alpha blend for only that terrain material's painted region.

## Blending Decision

The implementation uses alpha-style interpolation per output pixel:

```text
finalColor = lerp(materialBaseColor, overlayPixel, materialOverlayBlend * overlayPixelAlpha)
```

This gives predictable behavior:

- `0%` keeps the generated material color.
- `100%` uses the overlay image color for that material region.
- Per-material sliders let roads, grass, rock, or other layers receive different amounts of satellite detail.

The global slider is not multiplied again during generation. It is a master setter for all material sliders, avoiding surprising squared values like `50% global * 50% material = 25%`.

## Settings Additions

`MT_settings.json` now persists:

```jsonc
"BasecolorModeSettings": {
  "OverlaySettings": {
    "SelectedImagePath": "D:\\orthophoto.png",
    "SelectedTileProvider": "Google Satelite Only",
    "CachedTileImagePath": "D:\\...\\levels\\map\\MT_Tiles\\google-satelite-only.png",
    "UseTileProvider": true,
    "GlobalBlend": 0.5
  },
  "Materials": [
    {
      "InternalName": "asphalt",
      "BaseColorOverlayBlend": 0.75
    }
  ]
}
```

`GlobalBlend` stores the last master slider value. The effective generation value is the per-material `BaseColorOverlayBlend`.

## Tile Download and Cache

Downloaded overlay output is stored in the map root under `MT_Tiles` with immutable provider filenames:

```text
MT_Tiles/osm.png
MT_Tiles/google-roadmap.png
MT_Tiles/google-terrain.png
MT_Tiles/google-satelite-only.png
MT_Tiles/google-hybrid.png
MT_Tiles/arcgis-satelite.png
```

Raw web tiles are cached separately below:

```text
MT_Tiles/cache/{providerSlug}/{z}/{x}/{y}.img
```

If the provider output image already exists, it is reused and no web requests are made. To force a refetch for the same provider, delete the corresponding `MT_Tiles/{providerSlug}.png` file.

## Tile Fetch Math

- Uses WGS84 bounds from `GeoReferenceSettings`.
- Fetches XYZ Web Mercator tiles (`EPSG:3857`).
- Chooses zoom from `TerrainMetersPerPixel` and center latitude, rounded up and clamped to zoom `1..19`.
- Stitches all intersecting tiles, crops to the exact terrain bounding box, then resizes to the terrain texture size.

## Image Overlay Behavior

User-selected images are loaded directly from their selected path and resized to the terrain texture size during preview/generation. For best results, use a square image already aligned to the generated terrain bounds. Tile overlays are generated as square terrain-sized images by the fetcher.

## Important Caveats

- Tile provider licensing and attribution are not solved by this feature. Google, OSM, and Esri imagery can have restrictions around offline storage and redistribution in map assets.
- Cached provider images are intentionally reused. If georeference bounds change for the same map folder, delete `MT_Tiles/{providerSlug}.png` before fetching again.
- The overlay is applied only to the merged BaseColor PNG. Normal, AO, Roughness, and Height generation remain terrain/material-derived.
- Holes (`MaterialData == 255`) remain transparent and do not receive overlay color.
- Preview and final output share the same material-to-texture orientation path, so visual checks in the preview should match `MT_basecolor.png`.

## Code Map

- `Objects/MtSettings/MtSettings.cs` stores overlay settings and per-material blend.
- `Objects/CopyAsset.cs` carries the per-material blend value in the UI list.
- `LogicBasecolorManager/MapTileOverlayService.cs` handles provider definitions, tile math, download, cache, stitch, crop, and final provider PNG creation.
- `LogicBasecolorManager/TerrainPbrMapBuilder.cs` blends the overlay into preview and final `MT_basecolor.png`.
- `LogicBasecolorManager/BaseColorModeApplier.cs` passes overlay options into map generation.
- `BlazorUI/Pages/BasecolorManager.razor(.cs)` contains the image picker, provider dropdown, fetch action, global slider, and material sliders.