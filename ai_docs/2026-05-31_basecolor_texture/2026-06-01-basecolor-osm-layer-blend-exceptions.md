# Basecolor Manager OSM Layer Blend Exceptions

Date: 2026-06-01

## Goal

Extend the **Basecolor Manager** BaseColor Mode so users can reduce or disable overlay blending in areas that do not match terrain material layers.

The current BaseColor Mode blends one selected image or fetched map-tile overlay into `MT_basecolor.png` by terrain material region. That works when the user wants a per-material blend amount, but it cannot express cross-cutting regions such as bays, water polygons, reserves, fields, or other OSM-derived shapes that overlap multiple terrain materials.

The new feature adds a repeater below the BaseColor Mode material table. Each row points to an 8-bit PNG mask generated from an OSM layer. White pixels are affected; black pixels are unaffected. For affected pixels, the row's slider controls how much of the existing overlay blend remains.

Example mask path from a generated map:

```text
C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\osm_layer\natural_bay_polygon.png
```

## Current Behavior To Preserve

- This applies only in **BaseColor Mode**. Paint Mode still writes flat per-material placeholder textures.
- The existing overlay blend is material-driven:

```text
finalColor = lerp(materialBaseColor, overlayPixel, material.BaseColorOverlayBlend * overlayPixelAlpha)
```

- The existing global blend slider is only a master setter for all material rows. It is not multiplied during generation.
- The overlay affects only `MT_basecolor.png`. Normal, AO, Roughness, and Height stay terrain/material-derived.
- Preview and final output must use the same orientation and mask sampling path.

## User Experience

In the **BaseColor Mode** tab, keep the existing overlay panel, preview panel, and material table. Add a new section directly below the material table:

```text
OSM Layer Blend Exceptions
[Add OSM Layer]

Enabled | Layer / PNG | Blend inside white area | Actions
```

Each repeater row contains:

- Enabled checkbox.
- Layer selector / file picker for a PNG mask.
- Read-only layer name, defaulting to the file name without extension, for example `natural_bay_polygon`.
- Slider `Blend inside white area` from `0%` to `100%`.
- Remove button.

Slider semantics:

- `0%` means white pixels are excluded from overlay blending entirely. The result stays the generated material base color in that area.
- `50%` means white pixels receive half of the material row's overlay blend.
- `100%` means the mask row does not reduce blending. This is useful for temporarily neutral rows but should not be the default.

Recommended default for a newly added exception is `0%`, because the primary user story is "exclude this area from blending".

## Mask Discovery

When a level is loaded, scan this folder if it exists:

```text
{levelRoot}\MT_TerrainGeneration\osm_layer\*.png
```

Use the discovered masks for dropdown choices in the repeater. Still allow selecting an arbitrary PNG, because users may bring their own masks or move generated masks.

Discovery is only for convenience; settings persist the exact selected path.

## Settings Schema

Extend `MT_settings.json` under `BasecolorModeSettings`:

```jsonc
{
  "BasecolorModeSettings": {
    "OverlaySettings": {
      "SelectedImagePath": "",
      "SelectedTileProvider": "Google Satelite Only",
      "CachedTileImagePath": "...\\MT_Tiles\\google-satelite-only.png",
      "UseTileProvider": true,
      "GlobalBlend": 0.5
    },
    "Materials": [
      {
        "InternalName": "Grass_italy",
        "BaseColorOverlayBlend": 0.5
      }
    ],
    "OsmLayerBlendExceptions": [
      {
        "Id": "natural_bay_polygon",
        "Name": "natural_bay_polygon",
        "ImagePath": "C:\\Users\\aklei\\AppData\\Local\\BeamNG\\BeamNG.drive\\current\\levels\\franco_same_prio\\MT_TerrainGeneration\\osm_layer\\natural_bay_polygon.png",
        "Enabled": true,
        "AffectedBlendMultiplier": 0.0
      }
    ]
  }
}
```

Suggested model additions in `Objects/MtSettings/MtSettings.cs`:

```csharp
public class MtBasecolorModeSettings
{
    // existing fields
    public List<MtOsmLayerBlendException> OsmLayerBlendExceptions { get; set; } = new();
}

public class MtOsmLayerBlendException
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // 0 = exclude overlay in white mask pixels, 1 = keep original blend.
    public double AffectedBlendMultiplier { get; set; }
}
```

Do not store mask bytes in settings. These masks are local generated assets beside the unpacked level and should remain file references.

## Blending Math

Keep the existing material blend as the base value:

```text
materialBlend = material.BaseColorOverlayBlend
```

For every enabled mask row with an existing image path, sample the mask at the output pixel. The masks are intended to be terrain-sized 8-bit black/white PNGs, but the implementation should tolerate grayscale and resizing:

```text
maskAmount = maskPixelLuminance / 255.0
rowMultiplier = lerp(1.0, exception.AffectedBlendMultiplier, maskAmount)
```

Combine multiple overlapping exceptions by choosing the strongest reduction:

```text
exceptionMultiplier = min(rowMultiplier for all enabled rows)
```

Then apply the existing overlay alpha:

```text
effectiveBlend = materialBlend * exceptionMultiplier * overlayPixelAlpha
finalColor = lerp(materialBaseColor, overlayPixel, effectiveBlend)
```

Why `min`: if a pixel is inside two exception masks and one mask says "exclude" while another says "reduce to 50%", exclusion should win. This is predictable and avoids order-dependent results.

Black pixels leave blending unchanged because `maskAmount = 0` gives `rowMultiplier = 1`.

White pixels use the row slider exactly because `maskAmount = 1` gives `rowMultiplier = AffectedBlendMultiplier`.

## Image Handling

- Accept PNG masks. The source story is 8-bit grayscale black/white PNG, but ImageSharp can load to `L8` or `Rgba32`; use luminance so palette/grayscale/RGBA PNGs all work.
- If mask dimensions equal terrain size, sample directly.
- If mask dimensions differ, resize to the target terrain or preview size before sampling, matching overlay image behavior.
- Use nearest-neighbor resize for masks to preserve crisp binary edges. If anti-aliased masks appear later, luminance math still works.
- Cache loaded/resized masks per generation/preview call so each mask is loaded once.
- Missing mask files should not fail the whole generation. Send a warning and ignore that row.

## Code Map

Likely files to change:

| Concern | File |
|---|---|
| Settings model | `BeamNG_LevelCleanUp/Objects/MtSettings/MtSettings.cs` |
| Overlay options passed to builder | `BeamNG_LevelCleanUp/LogicBasecolorManager/BasecolorManagerService.cs` |
| Final generation options | `BeamNG_LevelCleanUp/LogicBasecolorManager/BaseColorModeApplier.cs` |
| Preview/final blend math | `BeamNG_LevelCleanUp/LogicBasecolorManager/TerrainPbrMapBuilder.cs` |
| Repeater UI and handlers | `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor` and `.razor.cs` |

Possible supporting type:

```csharp
public sealed record BasecolorMaskBlendExceptionOptions(
    string Name,
    string ImagePath,
    double AffectedBlendMultiplier);

public sealed record BasecolorOverlayOptions(
    string ImagePath,
    double GlobalBlend,
    IReadOnlyList<BasecolorMaskBlendExceptionOptions> MaskExceptions);
```

`GlobalBlend` remains present for compatibility, but generation should continue using per-material `BaseColorOverlayBlend` as it does today.

## UI Implementation Notes

- Place the repeater below the BaseColor Mode material table, not inside the existing overlay image selector panel.
- Use compact rows; this is a work tool, not a landing-page panel.
- Use icon buttons for add/remove when practical with MudBlazor icons.
- A row should be valid only when `ImagePath` exists. Invalid rows remain visible so the user can fix them.
- Add a Preview refresh after row changes, or mark preview stale and let the existing Preview button regenerate it. The simplest useful behavior is to refresh preview after add/remove/slider/file changes.
- `Save Settings` should persist the repeater rows without activating BaseColor Mode.

Suggested methods in `BasecolorManager.razor.cs`:

```csharp
private List<string> _availableOsmLayerMaskPaths = new();

private void LoadAvailableOsmLayerMasks()
private Task AddOsmLayerException()
private Task RemoveOsmLayerException(MtOsmLayerBlendException exception)
private Task OnOsmLayerExceptionPathChanged(MtOsmLayerBlendException exception, string path)
private Task OnOsmLayerExceptionMultiplierChanged(MtOsmLayerBlendException exception, int percent)
```

## Load Flow

On level load:

1. Existing `BasecolorManagerService.LoadLevel(folder)` loads `MT_settings.json` or creates defaults.
2. Ensure `settings.BasecolorModeSettings.OsmLayerBlendExceptions` is non-null.
3. UI scans `{levelRoot}\MT_TerrainGeneration\osm_layer\*.png` into `_availableOsmLayerMaskPaths`.
4. Preview generation uses `BasecolorManagerService.CreateOverlayOptions(settings)` with enabled valid mask rows.

On first load without settings, do not auto-add all discovered masks. Discovery can find many OSM layers, and automatically changing blend behavior would be surprising. The user chooses which layer masks matter.

## Apply Flow

When activating or regenerating BaseColor Mode:

1. `UpdateSettingsFromMaterialLists()` also preserves `OsmLayerBlendExceptions`.
2. `BasecolorManagerService.CreateOverlayOptions(settings)` includes enabled mask rows with existing paths.
3. `BaseColorModeApplier.Apply(...)` passes the options to `TerrainPbrMapBuilder.BuildMaps(...)`.
4. `TerrainPbrMapBuilder.WriteMergedBaseColor(...)` applies the mask multiplier only to basecolor overlay blending.
5. Settings are saved with the repeater rows.

## Verification

Manual test using the example map:

1. Open Basecolor Manager and select the unpacked level folder.
2. Fetch or select a satellite/tile overlay and set several material overlay blends to visible values such as `50%`.
3. Add `MT_TerrainGeneration\osm_layer\natural_bay_polygon.png` as an OSM Layer Blend Exception.
4. Set `Blend inside white area` to `0%`.
5. Preview should show normal overlay outside the bay mask and generated material color inside white bay pixels.
6. Set the slider to `50%`; the bay mask area should receive half the overlay strength it had before.
7. Regenerate BaseColor Mode and confirm `MT_basecolor.png` matches the preview.
8. Save settings, reload the same level, and confirm the exception row is restored.

Build:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

If the running app locks output DLLs, ignore `MSB3027` / `MSB3021` and look for `error CS`.

## Edge Cases

- No overlay image selected: mask exceptions have no visual effect, because there is no overlay blend to reduce.
- Missing mask file: warn and ignore the row.
- Mask path outside level folder: allow it, but keep the path absolute in settings.
- Overlapping masks: strongest reduction wins via `min`.
- Hole material index `255`: stays transparent and receives no overlay, same as current behavior.
- GeoReference settings are not required for this feature. They are only needed for tile fetching.