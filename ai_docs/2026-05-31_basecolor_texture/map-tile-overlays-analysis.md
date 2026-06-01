# Selectable Map Tile Overlays and Tile Fetch Techniques

**Date:** 2026-05-31
**Source app:** `D:\Source\Msf.Simon.ABP.Blazor.WASM\src\angular`
**Primary source files:**
- `src/app/map/layer/layer.component.ts`
- `src/app/map/control/layershop.component.ts`
- `src/app/map/map.service.ts`

---

## Executive Summary

The Angular app exposes six active user-selectable base map tile layers. They are all raster XYZ-style web tile sources in Web Mercator (`EPSG:3857`) using the conventional `{z}/{x}/{y}` tile addressing scheme, even when the URL parameter order differs.

Active selectable layers:

| Display name in app | Provider/source | URL template / implied template | Notes |
|---|---|---|---|
| `OSM` | OpenStreetMap via OpenLayers `OSM` source | Usually `https://tile.openstreetmap.org/{z}/{x}/{y}.png` | OpenLayers supplies the default URL internally. |
| `Google Roadmap` | Google map tiles | `https://mt0.google.com/vt/lyrs=m&hl=en&x={x}&y={y}&z={z}` | Road map styling. |
| `Google Terrain` | Google map tiles | `https://mt0.google.com/vt/lyrs=p&hl=en&x={x}&y={y}&z={z}` | Terrain/physical map styling. |
| `Google Satelite Only` | Google map tiles | `https://mt0.google.com/vt/lyrs=s&hl=en&x={x}&y={y}&z={z}` | Satellite imagery only. The app spelling is `Satelite`. |
| `Google Hybrid` | Google map tiles | `https://mt0.google.com/vt/lyrs=y&hl=en&x={x}&y={y}&z={z}` | Satellite imagery with labels/roads. |
| `ArcGIS Satelite` | Esri ArcGIS Online World Imagery | `https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}` | ArcGIS REST tile endpoint, path order is `z/y/x`. The app spelling is `Satelite`. |

The default layer is `Google Roadmap` when there is no saved user preference.

There are commented-out historical candidates in the source (`Watercolor`, `Toner`, and `Google Altered Roadmap`), but they are not active selectable overlays in the running app.

---

## How Selection Works in the Angular App

The app creates a base-layer group titled `Base Layers`. Every selectable item in that group is an OpenLayers tile layer with `baseLayer: true`.

Selection is handled by the `ol-ext/control/Layershop` control. When a layer becomes visible, the handler checks:

- whether `event.layer.get("baseLayer")` is true
- whether `event.layer.get("visible")` is true

If both are true, the app saves the selected title to browser local storage under the key `baseLayer`. On later map creation, the base-layer group compares each layer title against that saved value and makes only the matching layer visible.

Important behavioral details:

- Missing `baseLayer` preference defaults to `Google Roadmap`.
- The active layer identity is the display title string, not an enum.
- All active base layers use OpenLayers `TileLayer` sources.
- All explicit non-OSM layers use OpenLayers `XYZ` sources.
- Browser `crossOrigin: 'anonymous'` is set so tiles can be used in a browser canvas without tainting it when the server permits CORS. This does not matter for a C# tile fetcher.

---

## Tile Addressing Model

All active sources are Web Mercator slippy-map raster tiles.

Core assumptions for C# implementation:

- Projection: `EPSG:3857` / Web Mercator.
- Tile size: normally `256 x 256` pixels.
- Zoom level: integer `z`.
- Tile indices: integer `x`, `y`.
- Origin: top-left of the world.
- Y direction: increases southward.
- This is XYZ addressing, not TMS. Do not flip Y unless a provider explicitly requires TMS.

At zoom `z`, the world is split into `2^z` tiles horizontally and vertically.

For longitude/latitude in WGS84 degrees:

```text
n = 2^z
xTile = floor((lon + 180.0) / 360.0 * n)
yTile = floor((1.0 - ln(tan(latRad) + sec(latRad)) / PI) / 2.0 * n)
```

Where:

```text
latRad = lat * PI / 180.0
sec(latRad) = 1.0 / cos(latRad)
```

Clamp latitude to the Web Mercator practical range before converting:

```text
-85.05112878 <= lat <= 85.05112878
```

For a geographic bounding box, compute the tile coordinate for the north-west and south-east corners, then iterate:

```text
x = minXTile..maxXTile
y = minYTile..maxYTile
```

Because XYZ `y` increases downward:

- north latitude gives the smaller `y`
- south latitude gives the larger `y`

---

## Provider-Specific Fetching

### OSM

The app uses OpenLayers `new OSM({ crossOrigin: 'anonymous' })`, so the URL is supplied by OpenLayers rather than written in the code. For a C# implementation, the effective standard template is:

```text
https://tile.openstreetmap.org/{z}/{x}/{y}.png
```

Recommended fetch behavior:

- Send a clear `User-Agent` identifying the application.
- Respect OpenStreetMap tile usage policy.
- Cache aggressively on disk.
- Avoid high parallelism against public OSM tile servers.
- For bulk texture generation, prefer a local tile server, a paid tile provider, or pre-downloaded raster data instead of hammering public infrastructure.

### Google Roadmap / Terrain / Satellite / Hybrid

The app directly uses Google tile URLs:

```text
https://mt0.google.com/vt/lyrs=m&hl=en&x={x}&y={y}&z={z}
https://mt0.google.com/vt/lyrs=p&hl=en&x={x}&y={y}&z={z}
https://mt0.google.com/vt/lyrs=s&hl=en&x={x}&y={y}&z={z}
https://mt0.google.com/vt/lyrs=y&hl=en&x={x}&y={y}&z={z}
```

Observed `lyrs` meanings in this app:

| `lyrs` | App layer | Meaning |
|---|---|---|
| `m` | `Google Roadmap` | standard map/roadmap |
| `p` | `Google Terrain` | terrain/physical map |
| `s` | `Google Satelite Only` | satellite imagery |
| `y` | `Google Hybrid` | hybrid satellite with roads/labels |

Implementation notes:

- These are still XYZ Web Mercator tiles.
- The URL puts `x`, `y`, and `z` in query parameters rather than path segments.
- `mt0` is one tile host. Other Google tile hosts exist, but the app uses only `mt0`.
- For production C# use, verify licensing and terms. These direct `mt0.google.com/vt` URLs are commonly used in web map experiments but are not the same as using an official Google Maps Platform SDK or licensed tile API.

### ArcGIS World Imagery

The app uses:

```text
https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}
```

Important difference:

- It is still XYZ-style Web Mercator tile addressing.
- The path order is `z/y/x`, not `z/x/y`.

This is an ArcGIS REST MapServer tile endpoint. The tile matrix is the usual Web Mercator global tile pyramid used by ArcGIS Online basemaps.

Recommended fetch behavior:

- Treat the tile payload as an image response, commonly JPEG for imagery.
- Cache by provider/layer/z/x/y.
- Respect Esri service terms and attribution requirements.
- Use retries for transient HTTP failures, but avoid retry storms.

---

## C# Fetch Strategy

A useful C# abstraction is a provider definition plus a tile fetch service.

Suggested provider model:

```csharp
public sealed record TileProvider(
    string Name,
    string UrlTemplate,
    int TileSize = 256,
    bool UsesQueryXyz = false,
    bool PathOrderIsZyx = false);
```

Example provider definitions:

```csharp
var providers = new[]
{
    new TileProvider("OSM", "https://tile.openstreetmap.org/{z}/{x}/{y}.png"),
    new TileProvider("Google Roadmap", "https://mt0.google.com/vt/lyrs=m&hl=en&x={x}&y={y}&z={z}", UsesQueryXyz: true),
    new TileProvider("Google Terrain", "https://mt0.google.com/vt/lyrs=p&hl=en&x={x}&y={y}&z={z}", UsesQueryXyz: true),
    new TileProvider("Google Satelite Only", "https://mt0.google.com/vt/lyrs=s&hl=en&x={x}&y={y}&z={z}", UsesQueryXyz: true),
    new TileProvider("Google Hybrid", "https://mt0.google.com/vt/lyrs=y&hl=en&x={x}&y={y}&z={z}", UsesQueryXyz: true),
    new TileProvider("ArcGIS Satelite", "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", PathOrderIsZyx: true),
};
```

The URL builder can simply replace tokens:

```csharp
static string BuildTileUrl(string template, int z, int x, int y)
{
    return template
        .Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
        .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
        .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
}
```

Suggested fetch flow:

1. Choose provider by the same display names used in the Angular app.
2. Choose zoom level based on desired meters-per-pixel or output texture resolution.
3. Convert the target WGS84 bounding box to a tile range at that zoom.
4. For each tile coordinate, build the provider URL.
5. Check disk cache first.
6. Fetch missing tiles with `HttpClient`.
7. Decode the image bytes with an image library.
8. Stitch tiles into a single raster mosaic.
9. Crop the mosaic to the exact requested bounding box.
10. Reproject or resample as needed for the terrain/basecolor texture pipeline.

---

## Choosing Zoom for Basecolor Texture Work

For Web Mercator, approximate ground resolution at latitude `lat` is:

```text
metersPerPixel = cos(latRad) * 156543.03392804097 / 2^z
```

Invert this to choose a zoom from target meters-per-pixel:

```text
z = log2(cos(latRad) * 156543.03392804097 / targetMetersPerPixel)
```

Round to an integer zoom. For texture generation, it is often better to choose a slightly higher zoom and downsample than to choose a lower zoom and upscale.

Because Web Mercator resolution varies by latitude, use the center latitude of the terrain area for a practical estimate.

---

## Stitching and Cropping Details

For a tile range:

```text
minX..maxX
minY..maxY
```

The stitched pixel size is:

```text
widthPixels = (maxX - minX + 1) * 256
heightPixels = (maxY - minY + 1) * 256
```

Tile placement in the mosaic:

```text
destX = (x - minX) * 256
destY = (y - minY) * 256
```

To crop to an exact bounding box, convert the bbox corners to fractional global pixel coordinates at zoom `z`:

```text
worldPixelX = ((lon + 180.0) / 360.0) * 256 * 2^z
worldPixelY = ((1.0 - ln(tan(latRad) + sec(latRad)) / PI) / 2.0) * 256 * 2^z
```

Then subtract the global pixel coordinate of the mosaic origin:

```text
mosaicOriginPixelX = minXTile * 256
mosaicOriginPixelY = minYTile * 256

cropLeft = westWorldPixelX - mosaicOriginPixelX
cropTop = northWorldPixelY - mosaicOriginPixelY
cropRight = eastWorldPixelX - mosaicOriginPixelX
cropBottom = southWorldPixelY - mosaicOriginPixelY
```

Use floating-point coordinates until the final crop/resample step to avoid small alignment errors.

---

## Caching Recommendations

Use a stable cache key such as:

```text
{providerSlug}/{z}/{x}/{y}.{extension}
```

Examples:

```text
google-satellite/18/136201/89542.jpg
arcgis-world-imagery/18/136201/89542.jpg
osm/18/136201/89542.png
```

Recommendations:

- Store the original response bytes, not just decoded pixels.
- Keep provider caches separate.
- Preserve content type when possible to determine `.png`, `.jpg`, or `.webp`.
- Add an HTTP timeout.
- Limit concurrency per host.
- Use exponential backoff for `429`, `500`, `502`, `503`, and `504`.
- Do not retry `400`, `401`, `403`, or `404` blindly.
- Optionally keep a small metadata file with `ETag`, `Last-Modified`, content type, and fetch time.

---

## Legal and Attribution Notes

The Angular code contains direct tile URLs, but a C# port should still treat providers as licensed data sources rather than anonymous image endpoints.

Important cautions:

- OpenStreetMap tiles have usage policies and attribution requirements.
- Google tile usage should be checked against Google Maps Platform terms; direct `mt0.google.com/vt` fetching may not be acceptable for production or bulk offline texture generation.
- Esri World Imagery has attribution and usage terms.
- For generated game terrain/basecolor textures, offline storage and redistribution rights matter. Verify provider terms before baking imagery into assets.

If the goal is reliable basecolor generation, consider supporting user-supplied local raster sources as the long-term path:

- GeoTIFF orthophotos
- WMTS/XYZ tile packages with a license
- MBTiles
- local ArcGIS/WMTS cache exports

---

## Non-Active / Commented-Out Layers

The source contains commented-out tile definitions. These are not user-selectable in the current app:

| Commented name | Source idea | Status |
|---|---|---|
| `Watercolor` | Stamen watercolor | Commented out. Also no active Stamen import in the file. |
| `Toner` | Stamen toner | Commented out. Also no active Stamen import in the file. |
| `Google Altered Roadmap` | `http://mt0.google.com/vt/lyrs=r&hl=en&x={x}&y={y}&z={z}` | Commented out and uses `http`, not `https`. |

Do not include these as active choices unless the app is intentionally changed to re-enable them.

---

## Practical Recommendation for the C# Port

Start with these provider IDs and keep the exact display names for compatibility with saved Angular settings:

```text
OSM
Google Roadmap
Google Terrain
Google Satelite Only
Google Hybrid
ArcGIS Satelite
```

Represent all six as XYZ Web Mercator tile providers. The only provider-specific differences needed for the first C# implementation are:

- URL template
- expected image format/content type
- request headers and policy/concurrency settings
- attribution/licensing metadata

The tile math, cache layout, stitching, and crop logic can be shared across all six active providers.
