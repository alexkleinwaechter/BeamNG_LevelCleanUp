# Handoff: Basecolor Manager User Help Text

Date: 2026-06-01

## Goal

Write and implement user help text for the **Basecolor Manager** page.

The help text must be written in **easy English**. Use short sentences. Avoid internal code words where possible. Explain what the user needs to know to use the tool safely.

The help text must be embedded with the same technique used by `TerrainMaterialOrderHelpDialog.razor`: a MudBlazor dialog component opened from the page with `IDialogService`.

## Documents Checked Before This Handoff

These documents were read/checked before writing this handoff:

- `ai_docs/2026-05-31_basecolor_texture/basecolor-textures-knowledge-and-wizard-cap.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-manager-plan-and-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-basecolor-overlay-implementation.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-05-31-generateterrain-georef-settings-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-06-01-basecolor-osm-layer-blend-exceptions.md`
- `ai_docs/2026-05-31_basecolor_texture/2026-06-01-basecolor-osm-layer-blend-exceptions-handoff.md`
- `ai_docs/2026-05-31_basecolor_texture/map-tile-overlays-analysis.md`
- `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialOrderHelpDialog.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Components/HeightmapSourceHelpDialog.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` help dialog open methods

## Existing Help Dialog Technique To Reuse

Use the same pattern as `TerrainMaterialOrderHelpDialog.razor`:

- Create a new `.razor` component in `BeamNG_LevelCleanUp/BlazorUI/Components/`.
- Wrap content in `<MudDialog>`.
- Use `<TitleContent>`, `<DialogContent>`, and `<DialogActions>`.
- Put the scrollable body inside:

```razor
<div style="max-height: calc(100vh - 200px); overflow-y: auto; padding-right: 8px;">
```

- Use `MudText`, `MudDivider`, `MudAlert`, `MudSimpleTable`, `MudExpansionPanels`, `MudList`, and `MudChip` like the existing help dialogs.
- Add a `Got it!` button in `DialogActions`.
- Use this close pattern:

```razor
@code {
    [CascadingParameter]
    private IMudDialogInstance MudDialog { get; set; } = null!;

    private void Close()
    {
        MudDialog.Close();
    }
}
```

Open it from the Basecolor Manager page with `IDialogService`, matching the GenerateTerrain help methods:

```csharp
private async Task OpenBasecolorManagerHelpDialog()
{
    var options = new DialogOptions
    {
        MaxWidth = MaxWidth.Medium,
        CloseButton = true,
        CloseOnEscapeKey = true
    };

    await DialogService.ShowAsync<BasecolorManagerHelpDialog>(
        "Basecolor Manager Guide",
        options);
}
```

## Files To Change

Add:

- `BeamNG_LevelCleanUp/BlazorUI/Components/BasecolorManagerHelpDialog.razor`

Update:

- `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor.cs`

Likely page changes:

- Inject `IDialogService` if it is not already injected.
- Add a small help icon button near the `Basecolor Manager` page title or near the BaseColor/Paint tabs.
- The button should use `Icons.Material.Filled.Help` and call `OpenBasecolorManagerHelpDialog`.

## Writing Style

Use easy English:

- Prefer short sentences.
- Say “map folder” instead of “validated level root” in user-facing text.
- Say “image” instead of “raster” unless needed.
- Say “paintable” only after explaining it means “you can paint materials in BeamNG’s terrain editor”.
- Do not mention implementation classes such as `TerrainPbrMapBuilder`, `MtSettings`, or `CopyAsset` in the dialog.
- Do not overload the dialog with math formulas.
- Avoid long paragraphs. Use short sections.

Do not use marketing text. This is a work tool. The help text should be practical and direct.

## Suggested Dialog Structure

### Title

`Basecolor Manager Guide`

Use `Icons.Material.Filled.Help` in the title, matching `TerrainMaterialOrderHelpDialog.razor`.

### Intro

Explain the feature in 2-3 short sentences:

Suggested text:

> The Basecolor Manager helps you switch your terrain between two useful states.
> Paint Mode makes terrain materials easy to paint in BeamNG.
> BaseColor Mode builds one finished color image for the whole terrain.

### Section 1: The Two Modes

Use a small table.

Suggested rows:

| Mode | What it does | Use it when |
|---|---|---|
| Paint Mode | Gives each material its own simple base textures. | You want to paint terrain materials in BeamNG. |
| BaseColor Mode | Builds one shared `MT_basecolor.png` from the terrain material areas. | You want the map to look finished. |

Explain:

- Paint Mode writes small placeholder textures.
- BaseColor Mode writes merged terrain-sized textures.
- Both modes use the colors and roughness values stored in the Basecolor Manager settings.

### Section 2: What Gets Saved

Suggested text:

> Settings are saved in `MT_settings.json` inside the selected map folder.
> This stores the current mode, material colors, roughness, overlay settings, and OSM mask exceptions.
> It does not store image bytes. It stores file paths for selected images and masks.

Mention that the tool edits the selected unpacked map folder in place.

Suggested warning alert:

> The selected map folder is changed directly. Make a backup if you want to keep the old files.

### Section 3: Preview And Regenerate

Explain clearly what the preview is:

Suggested text:

> The preview shows the merged base color image.
> It does not show normal, roughness, AO, or height maps.

Explain when Preview is needed:

- Some changes refresh preview automatically.
- For material color and material overlay blend changes, use the Preview button if the image does not update yet.
- To write files to the map, use `Regenerate BaseColor Mode` or `Regenerate Paint Mode`.

Important user concept:

> Preview only updates what you see in the tool. Regenerate writes the files used by BeamNG.

### Section 4: Overlay Image And Tile Overlay

Explain:

- In BaseColor Mode, an image or fetched tile overlay can be blended into `MT_basecolor.png`.
- The overlay affects only the base color image.
- It does not change normal, AO, roughness, or height.
- The global overlay slider is a master setter for the material overlay sliders.
- The real blend comes from each material row.

Suggested simple text:

> 0% keeps the generated material color.
> 100% uses the overlay image strongly for that material.
> Values in between mix both.

Tile provider note:

- If the map has georeference settings, tile overlays can be fetched.
- Fetched images are cached in `MT_Tiles` inside the map folder.
- If the same provider image already exists, it is reused.
- Provider licensing/redistribution is the user’s responsibility. Keep this short and non-scary.

Suggested warning alert:

> Map tile providers can have license rules. Check the rules before sharing a map that contains downloaded imagery.

### Section 5: OSM Layer Blend Exceptions

Explain this feature in very simple words:

> OSM Layer Blend Exceptions use black and white PNG masks.
> White areas are affected. Black areas are not affected.
> They reduce the overlay only in the white parts of the mask.

Slider meaning:

| Slider | Meaning |
|---|---|
| 0% | No overlay in white mask areas. |
| 50% | Half overlay strength in white mask areas. |
| 100% | No reduction. Same as normal. |

Mention:

- Masks are usually found in `{map folder}\MT_TerrainGeneration\osm_layer\*.png`.
- The tool lists found PNG masks, but does not add them automatically.
- Missing masks are ignored with a warning.
- If masks overlap, the strongest reduction wins.
- OSM exceptions only matter when an overlay image or tile overlay is active.

### Section 6: What Each Generated File Means

Keep this short. Use a small table.

| File | Meaning |
|---|---|
| `MT_basecolor.png` | The visible merged color image. |
| `MT_basecolor_nm.png` | Normal map from terrain height. |
| `MT_basecolor_ao.png` | Ambient occlusion from terrain height. |
| `MT_basecolor_r.png` | Roughness from material settings. |
| `MT_basecolor_h.png` | Height image, if enabled. |

Also mention:

> Holes in the terrain stay transparent.

### Section 7: Quick Workflow

Use a numbered list.

Suggested text:

1. Select an unpacked map folder.
2. Choose Paint Mode if you want to paint materials in BeamNG.
3. Choose BaseColor Mode if you want to build the final merged look.
4. Set material colors and roughness.
5. Add an overlay image or fetch a tile overlay if needed.
6. Add OSM mask exceptions if parts of the overlay should be weaker or disabled.
7. Use Preview to check the image.
8. Use Regenerate to write files to the map.
9. Use Save Settings to store your choices without changing the mode.

## Suggested UI Placement

Add a compact help icon button in the page title row.

Current title is approximately:

```razor
<MudText Typo="Typo.h4" Class="mb-3">Basecolor Manager</MudText>
```

Recommended replacement shape:

```razor
<MudStack Row="true" AlignItems="AlignItems.Center" Class="mb-3">
    <MudText Typo="Typo.h4">Basecolor Manager</MudText>
    <MudSpacer />
    <MudIconButton Icon="@Icons.Material.Filled.Help"
                   Color="Color.Primary"
                   Variant="Variant.Outlined"
                   OnClick="OpenBasecolorManagerHelpDialog" />
</MudStack>
```

This keeps the page clean and avoids adding visible explanatory text into the main tool surface.

## Implementation Checklist

1. Create `BasecolorManagerHelpDialog.razor` in `BlazorUI/Components`.
2. Use `TerrainMaterialOrderHelpDialog.razor` as the structural template.
3. Write all user-facing text in easy English.
4. Add `@using BeamNG_LevelCleanUp.BlazorUI.Components` if needed.
5. Inject `IDialogService` into `BasecolorManager.razor` or the code-behind.
6. Add `OpenBasecolorManagerHelpDialog()` to `BasecolorManager.razor.cs`.
7. Add a help icon button in the Basecolor Manager title row.
8. Build and fix Razor/C# errors.

## Verification

Run:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

If the app is running, DLL lock errors such as `MSB3027` or `MSB3021` can be ignored. Fix any `error CS` or Razor errors.

Manual check:

1. Open the app.
2. Go to Basecolor Manager.
3. Click the help icon.
4. Confirm the dialog opens.
5. Confirm the text is easy to read.
6. Confirm the dialog scrolls on smaller screens.
7. Confirm the `Got it!` button closes the dialog.

## Notes For The Writer

- The help text should explain the user workflow, not the code.
- Do not include equations for overlay or mask blending.
- Do not say the global blend is a final multiplier. It is only a master setter for material blend values.
- Do not imply Paint Mode uses satellite/tile overlays. Overlay features apply to BaseColor Mode only.
- Do not imply Preview writes files. Preview only updates the image shown in the app.
- Do not imply Regenerate changes the heightmap size. BaseColor Mode changes base texture outputs, not the terrain `.terrain.json` size.
- Keep the licensing note short and practical.