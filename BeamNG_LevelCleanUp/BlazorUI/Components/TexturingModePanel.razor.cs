using BeamNG_LevelCleanUp.BlazorUI.State;
using Microsoft.AspNetCore.Components;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
///     GenerateTerrain "Terrain Texturing Mode" section (backdrop follow-up doc 06): Paint Mode vs
///     BaseColor Mode radio + the non-road satellite blend knob. Pure UI, same contract shape as
///     <c>BackdropSettingsPanel</c> — the page owns the actual post-generation automation
///     (<c>BasecolorAutoApplyService</c>).
/// </summary>
public partial class TexturingModePanel : ComponentBase
{
    [Parameter] public TexturingSettings Settings { get; set; } = null!;
    [Parameter] public EventCallback SettingsChanged { get; set; }
    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    ///     True while backdrop generation is enabled: BaseColor Mode is displayed and locked. The
    ///     user's own <see cref="TexturingSettings.Mode"/> is never overwritten — turning the
    ///     backdrop off returns to it (see <c>TerrainGenerationState.EffectiveTexturingMode</c>).
    /// </summary>
    [Parameter] public bool ForceBaseColor { get; set; }

    private bool _expanded;

    private TerrainTexturingMode EffectiveMode =>
        ForceBaseColor ? TerrainTexturingMode.BaseColorMode : Settings.Mode;

    /// <summary>
    ///     CSS-hidden rather than conditionally rendered while Paint Mode is active — same MudBlazor
    ///     visibility rule as <c>BackdropSettingsPanel.FieldStyle</c> (late-mounted inputs misrender).
    /// </summary>
    private string? FieldStyle => EffectiveMode == TerrainTexturingMode.BaseColorMode ? null : "display:none";

    private async Task OnModeChanged(TerrainTexturingMode value)
    {
        Settings.Mode = value;
        await SettingsChanged.InvokeAsync();
    }

    private async Task NotifyChanged() => await SettingsChanged.InvokeAsync();
}
