# Handoff: Basecolor Manager Enhancements

Date: 2026-06-02
Branch: `bugfix/basecolormanager`

## Goal

Extend the Basecolor Manager with three user-facing improvements:

1. Overlay image adjustment controls.
2. Optional smoothing between material regions.
3. Enlarged preview dialog for the merged BaseColor preview.

This is a handoff document only. It describes the intended implementation and the files likely involved.

## Current Context

Read these first:

- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-manager-plan-and-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-overlay-implementation.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-06-01-basecolor-osm-layer-blend-exceptions-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-06-01-basecolor-manager-helptext-handoff.md`

Relevant code map:

- `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor.cs`
- `BeamNG_LevelCleanUp/LogicBasecolorManager/TerrainPbrMapBuilder.cs`
- `BeamNG_LevelCleanUp/LogicBasecolorManager/MapTileOverlayService.cs`
- `BeamNG_LevelCleanUp/Objects/MtSettings/MtSettings.cs`
- `BeamNG_LevelCleanUp/BlazorUI/Components/BasecolorManagerHelpDialog.razor`

Existing BaseColor preview:

- `_previewDataUri` is built by `BasecolorManagerService.BuildPreview(...)`.
- The preview image is shown in the sticky right panel of `BasecolorManager.razor`.
- Final `MT_basecolor.png` and preview both go through `TerrainPbrMapBuilder`, so visual processing should be shared there.

GenerateTerrain dialog patterns to reuse:

- `GenerateTerrain.razor.cs` opens full-screen / large dialogs through `IDialogService.ShowAsync<T>(...)`.
- `TerrainAnalysisDialog.razor` is a good example for a large visual dialog with viewport-sized content.
- `CropAnchorSelectorDialog.razor` is a good example for a dialog that uses most of the viewport and keeps controls in title/footer areas.

## Enhancement 1: Overlay Image Adjustment Controls

### User Goal

After selecting a local overlay image or fetched tile overlay, the user should be able to tune it before it is blended into `MT_basecolor.png`.

Suggested controls:

| Control | Range | Default | Purpose |
|---|---:|---:|---|
| Brightness | -100% to +100% | 0% | Make overlay darker or brighter. |
| Contrast | -100% to +100% | 0% | Reduce haze or soften harsh imagery. |
| Saturation | -100% to +100% | 0% | Desaturate satellite imagery or make colors stronger. |
| Hue / Color Tone | -180 to +180 degrees | 0 | Optional. Shifts color hue. Useful but less important. |
| Warmth / Tint | -100% to +100% | 0% | Alternative to Hue. Easier for users than full hue shift. |

Recommendation: implement **Brightness**, **Contrast**, and **Saturation** first. Add either **Warmth** or **Hue Shift** only if the UI still feels simple. Warmth is probably more user-friendly than Hue Shift for terrain imagery.

### Persistence

Add adjustment settings under `MtBasecolorOverlaySettings` in `MtSettings.cs`.

Suggested model:

```csharp
public double Brightness { get; set; } // -1.0..1.0, default 0
public double Contrast { get; set; }   // -1.0..1.0, default 0
public double Saturation { get; set; } // -1.0..1.0, default 0
public double Warmth { get; set; }     // -1.0..1.0, default 0, optional
```

Use normalized doubles in settings. Display as `-100..100` percent in UI.

Add helper properties/methods in `BasecolorManager.razor.cs` similar to `GlobalOverlayBlendPercent`:

```csharp
private int OverlayBrightnessPercent => ToPercent(_settings.BasecolorModeSettings.OverlaySettings.Brightness);
private void OnOverlayBrightnessChanged(int value) { ... }
```

Handlers should:

1. Clamp value.
2. Update settings.
3. Call `UpdateSettingsFromMaterialLists()`.
4. Refresh preview.

Use `Immediate="false"` on sliders to avoid rebuilding preview on every mouse move. If the UI feels too sluggish, add a single `Apply`/`Preview` button for overlay adjustments.

### Image Processing Location

Do not adjust the original selected image or cached tile PNG on disk. Apply adjustments when the overlay is loaded for preview/final generation.

Best place:

- `TerrainPbrMapBuilder.LoadOverlayImage(...)`

Current flow:

```text
BasecolorManagerService.CreateOverlayOptions(settings)
  -> BasecolorOverlayOptions
  -> TerrainPbrMapBuilder.LoadOverlayImage(...)
  -> GetBlendedMaterialColor(...)
```

Extend `BasecolorOverlayOptions` to carry adjustment values:

```csharp
public sealed record BasecolorOverlayOptions(
    string ImagePath,
    double GlobalBlend,
    IReadOnlyList<BasecolorMaskBlendExceptionOptions> MaskExceptions,
    double Brightness,
    double Contrast,
    double Saturation,
    double Warmth);
```

Update `BasecolorManagerService.CreateOverlayOptions(settings)` to pass the values.

Implementation options:

- Use ImageSharp processors if they are available in the current package version.
- If ImageSharp has gaps, implement pixel-level adjustment in `TerrainPbrMapBuilder` after resize.

Pixel-level processing is acceptable because the overlay is already loaded as `Image<Rgba32>`.

Suggested processing order:

1. Brightness.
2. Contrast.
3. Saturation.
4. Warmth or hue.

Pseudo-code:

```text
rgb = rgb / 255
rgb = (rgb - 0.5) * contrastFactor + 0.5
rgb += brightness
luma = dot(rgb, [0.299, 0.587, 0.114])
rgb = lerp(luma, rgb, saturationFactor)
r += warmth * smallAmount
g += warmth * smallAmount * 0.25
b -= warmth * smallAmount
clamp 0..1
```

Where:

```text
brightness = -1..1
contrastFactor = 1 + contrast          // clamp min around 0
saturationFactor = 1 + saturation      // 0 = grayscale, 2 = boosted
warmth smallAmount around 0.12
```

Keep alpha unchanged.

### UI Placement

Place controls in the BaseColor Mode overlay panel below the tile/image picker and before `Global Overlay Blend`.

Suggested UI:

- Use an expansion panel or a compact section named `Overlay Adjustments`.
- Sliders should be dense and not dominate the page.
- Add a small reset button with `Icons.Material.Filled.RestartAlt` or `Refresh` labeled `Reset Adjustments`.

Avoid using too many controls by default. A clean first pass:

- Brightness
- Contrast
- Saturation
- Reset Adjustments

### Help Dialog Update

Update `BasecolorManagerHelpDialog.razor` with a short section:

- Adjustments affect only the overlay image before it is mixed into the base color.
- They do not change the source image file or cached tile image.
- They are saved in `MT_settings.json`.

## Enhancement 2: Smoother Material Border Transitions

### User Goal

Terrain material regions currently change color sharply at material borders because `.ter` stores one material index per pixel. In the generated BaseColor image, this produces hard color boundaries.

We want an optional feature to make material transitions visually softer in `MT_basecolor.png`.

Important: this should **not** change the `.ter` material map. It should only affect generated BaseColor Mode textures.

### Recommended Feature Name

Use user-facing text like:

- `Soften Material Borders`
- `Border Blend Radius`
- `Border Blend Strength`

Avoid calling it `blur material IDs` in the UI.

### Settings

Add to `MtBasecolorModeSettings`:

```csharp
public bool EnableMaterialBorderBlend { get; set; }
public int MaterialBorderBlendRadius { get; set; } = 0; // pixels, 0 disables
public double MaterialBorderBlendStrength { get; set; } = 1.0; // 0..1
```

Suggested UI ranges:

- Radius: `0..32` pixels, step `1`, default `0`.
- Strength: `0..100%`, default `100%`.

Keep this in BaseColor Mode preview panel near the existing PBR sliders.

### Algorithm Ideas

There are a few possible approaches. Recommended implementation is Option A.

#### Option A: Edge-Masked Blur Of Generated Material Color/Roughness Images

This is the simplest robust implementation.

1. Build the material base color image as today, before overlay blend.
2. Build a boundary mask from `terrain.MaterialData`:
   - A pixel is boundary if any 4-neighbor or 8-neighbor has a different non-hole material index.
   - Holes (`255`) should stay transparent and should not bleed color into real terrain.
3. Expand/blur the boundary mask by `MaterialBorderBlendRadius`.
4. Create a blurred copy of the material base color image using ImageSharp blur, or a small separable box/gaussian blur.
5. For each pixel:
   - `edgeAmount = boundaryMask[x,y] * strength`
   - `finalMaterialColor = lerp(originalMaterialColor, blurredMaterialColor, edgeAmount)`
6. Then apply overlay image blending and OSM layer override behavior as usual.

Pros:

- Easy to reason about.
- Does not need per-material float arrays.
- Works for many terrain materials without large memory blowups.
- Same logic can be used for roughness with a grayscale image.

Cons:

- It can blur across narrow features if radius is too high.
- Overlay details are not border-blended unless applied after blur. That is probably fine; the feature is about material color transitions.

Recommendation:

- Apply border blend to material base color and roughness before overlay image blend.
- Do not blur normal/AO/height initially. Those are height-derived, not material-region-derived.

#### Option B: Local Weighted Neighborhood Sampling

For each output pixel near a boundary, sample a radius around it and average neighboring material colors by distance.

Pros:

- Very direct and does not require temporary images.

Cons:

- Cost grows with `terrainSize * radius^2`.
- Can be expensive for 8192/16384 terrains.

Use only if radius is capped low and performance is acceptable.

#### Option C: Per-Material Mask Blur And Normalize

Create one mask per material, blur all masks, normalize weights, and mix material colors.

Pros:

- Mathematically clean.

Cons:

- Memory-heavy for large terrains and many materials.
- Not recommended for this app unless implemented tile-by-tile.

### Suggested Internal Structure

Add a small options record:

```csharp
public sealed record MaterialBorderBlendOptions(
    bool Enabled,
    int Radius,
    double Strength);
```

Add it to `TerrainPbrMapBuilder.BuildMaps(...)` and `BuildPreviewDataUri(...)`, or include it in an existing options object if cleaner.

`BasecolorManagerService.BuildPreview(...)` and `BaseColorModeApplier.Apply(...)` should pass settings through.

Potential helper methods in `TerrainPbrMapBuilder`:

```csharp
private static Image<L8> BuildMaterialBoundaryMask(TerrainV9Binary terrain, int size, int radius, double strength)
private static void ApplyMaterialBorderBlend(Image<Rgba32> baseColorImage, Image<L8> boundaryMask, int radius)
private static void ApplyRoughnessBorderBlend(Image<L8> roughnessImage, Image<L8> boundaryMask, int radius)
```

Keep orientation consistent. Use `ToBeamNgTextureIndex(size, x, y)` everywhere when sampling `.ter` material data.

### Preview And Final Must Match

The preview and final generated PNG must use the same border blend logic.

Do not implement the preview as a separate approximation. Users will trust the preview for this feature.

### Edge Cases

- Material holes (`MaterialData == 255`) remain transparent.
- Do not blend hole pixels into real pixels.
- Clamp radius to safe values.
- For very large terrains, warn or keep radius small.
- If the feature is too slow on 8192+ terrains, optimize later with separable blur or tile processing.

## Enhancement 3: Enlarge Preview Button

### User Goal

The sticky `Merged Preview` panel is useful, but it is too small to inspect overlay details and material border smoothing. Add an enlarge button that opens a bigger preview.

### Recommended UI

In `BasecolorManager.razor`, change the preview card header from plain text to a row:

```razor
<MudStack Row="true" AlignItems="AlignItems.Center" Class="mb-2">
    <MudText Typo="Typo.subtitle1">Merged Preview</MudText>
    <MudSpacer />
    <MudTooltip Text="Open large preview">
        <MudIconButton Icon="@Icons.Material.Filled.OpenInFull"
                       Size="Size.Small"
                       Color="Color.Primary"
                       Disabled="@(string.IsNullOrWhiteSpace(_previewDataUri))"
                       OnClick="OpenMergedPreviewDialog" />
    </MudTooltip>
</MudStack>
```

Use `OpenInFull` or `ZoomOutMap` icon.

### New Component

Add:

```text
BeamNG_LevelCleanUp/BlazorUI/Components/BasecolorPreviewDialog.razor
```

Suggested parameters:

```csharp
[Parameter] public string PreviewDataUri { get; set; } = string.Empty;
[Parameter] public int TerrainSize { get; set; }
[Parameter] public BasecolorMode CurrentMode { get; set; }
```

Dialog layout:

- `MudDialog`
- `TitleContent`: icon + `Merged BaseColor Preview`
- `DialogContent`: large image area with `height: calc(100vh - 180px)`
- Image style:

```css
max-width: 100%;
max-height: 100%;
object-fit: contain;
image-rendering: auto;
border: 1px solid rgba(255,255,255,.18);
```

Optional controls:

- `1:1` toggle is not needed for first pass.
- Pan/zoom is nice but not required.
- A `Refresh Preview` button can remain on the main page only.

Open method in `BasecolorManager.razor.cs`:

```csharp
private async Task OpenMergedPreviewDialog()
{
    if (string.IsNullOrWhiteSpace(_previewDataUri))
        return;

    var parameters = new DialogParameters<BasecolorPreviewDialog>
    {
        { x => x.PreviewDataUri, _previewDataUri },
        { x => x.TerrainSize, _terrainSize },
        { x => x.CurrentMode, _settings.CurrentMode }
    };

    var options = new DialogOptions
    {
        MaxWidth = MaxWidth.ExtraExtraLarge,
        FullWidth = true,
        CloseButton = true,
        CloseOnEscapeKey = true
    };

    await DialogService.ShowAsync<BasecolorPreviewDialog>(
        "Merged BaseColor Preview",
        parameters,
        options);
}
```

If `MaxWidth.ExtraExtraLarge` is not available in the MudBlazor version, use `MaxWidth.ExtraLarge` and `FullWidth = true`, or copy the larger dialog style from `CropAnchorSelectorDialog.razor`.

### Future Option

If users need real inspection later, add a zoomable preview similar to `TerrainAnalysisDialog.razor`:

- Mouse wheel zoom.
- Drag to pan.
- Reset view button.

For this handoff, a simple large dialog is enough.

## Implementation Order

Recommended order:

1. Add settings fields for overlay adjustments and material border blend.
2. Wire UI controls and persistence only.
3. Extend `BasecolorOverlayOptions` and apply overlay adjustments in `TerrainPbrMapBuilder.LoadOverlayImage(...)`.
4. Add material border blend support in `TerrainPbrMapBuilder` and pass options through preview/final generation.
5. Add `BasecolorPreviewDialog.razor` and the enlarge button.
6. Update `BasecolorManagerHelpDialog.razor`.
7. Build and manually test.

## Manual Test Plan

Use an unpacked map generated by this app with georeference settings and terrain materials.

Overlay adjustment tests:

1. Select a local overlay image or fetch a tile overlay.
2. Move Brightness, Contrast, and Saturation.
3. Confirm preview changes.
4. Save settings, reload the level, confirm slider values are restored.
5. Regenerate BaseColor Mode and confirm `MT_basecolor.png` matches preview.

Material border blend tests:

1. Pick a map with clear adjacent material regions.
2. Set radius `0`: preview should match current sharp boundaries.
3. Set radius `4..12`: preview should show softer boundaries.
4. Confirm holes remain transparent.
5. Regenerate BaseColor Mode and compare final PNG to preview.
6. Try a large terrain size and ensure processing time is acceptable.

Preview enlarge tests:

1. Open BaseColor Mode with a preview visible.
2. Click the enlarge button.
3. Confirm dialog opens and image fits without layout overlap.
4. Change overlay controls, refresh preview, reopen dialog and confirm updated image.

Build:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

Existing repo-wide warnings are expected. Fix any new `error CS` or new Basecolor-specific MudBlazor analyzer warnings.

## Notes And Open Questions

- For overlay color tone, prefer `Warmth` over a full hue-shift slider unless users specifically ask for hue rotation.
- Border blend should initially affect only BaseColor and roughness. Do not blur height-derived normal/AO/height maps.
- If border blending is too slow on very large terrains, cap the radius or optimize with separable blur.
- The enlarged preview should use the existing `_previewDataUri`; do not regenerate inside the dialog on first pass.
