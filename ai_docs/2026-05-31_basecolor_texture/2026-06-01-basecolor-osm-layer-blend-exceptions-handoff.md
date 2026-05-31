# Handoff: Basecolor Manager OSM Layer Blend Exceptions

Date: 2026-06-01

Read first:

- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-manager-plan-and-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-overlay-implementation.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-06-01-basecolor-osm-layer-blend-exceptions.md`

## Task

Extend the **Basecolor Manager** BaseColor Mode with a repeater below the material table for OSM-layer mask exceptions.

Users have 8-bit black/white PNG masks generated from OSM layers, usually in:

```text
{levelRoot}\MT_TerrainGeneration\osm_layer\*.png
```

White pixels are affected. Black pixels are not affected. In affected pixels, the row slider reduces the existing overlay blend:

- `0%` = exclude overlay blending in white mask pixels.
- `50%` = half of the material row's overlay blend.
- `100%` = no reduction.

## Implement

1. Add settings model support in `Objects/MtSettings/MtSettings.cs`:

```csharp
public List<MtOsmLayerBlendException> OsmLayerBlendExceptions { get; set; } = new();

public class MtOsmLayerBlendException
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public double AffectedBlendMultiplier { get; set; }
}
```

2. Extend `BasecolorOverlayOptions` so `BasecolorManagerService.CreateOverlayOptions(settings)` can pass enabled valid mask rows to `TerrainPbrMapBuilder`.

3. In `TerrainPbrMapBuilder`, load/rescale masks once per preview/build call and modify only the basecolor overlay blend:

```text
rowMultiplier = lerp(1.0, exception.AffectedBlendMultiplier, maskLuminance / 255.0)
exceptionMultiplier = min(rowMultiplier for enabled masks)
effectiveBlend = material.BaseColorOverlayBlend * exceptionMultiplier * overlayPixelAlpha
finalColor = lerp(materialBaseColor, overlayPixel, effectiveBlend)
```

Use nearest-neighbor resize for masks. Missing masks should produce a warning and be ignored.

4. In `BasecolorManager.razor`, add the repeater below the BaseColor Mode material table. Include add/remove controls, enabled checkbox, mask selector/file picker, and a `0..100%` slider.

5. In `BasecolorManager.razor.cs`, scan `{levelRoot}\MT_TerrainGeneration\osm_layer\*.png` after level load for dropdown choices. Do not auto-add discovered masks.

6. Ensure `Save Settings`, Preview, and `Regenerate BaseColor Mode` preserve and use the exception rows.

## Verify

Use a generated level with a mask such as:

```text
C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\osm_layer\natural_bay_polygon.png
```

Set a visible tile/image overlay and material blend, add the mask row with `0%`, then confirm the white mask region keeps generated material color while the rest still receives overlay. Change to `50%` and confirm partial blending. Regenerate BaseColor Mode and confirm `MT_basecolor.png` matches preview.

Build:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

Ignore app-running DLL lock errors (`MSB3027` / `MSB3021`); fix any `error CS`.