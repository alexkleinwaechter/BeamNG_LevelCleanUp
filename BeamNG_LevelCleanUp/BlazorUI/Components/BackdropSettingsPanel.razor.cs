using BeamNG_LevelCleanUp.BlazorUI.State;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
///     Backdrop generation settings panel (spec §5, Task 18): tunables + cost estimate +
///     regenerate/remove actions. Follows the <c>BankingSettingsPanel</c> contract shape (settings
///     object + change callback + action callbacks), rendered as a page section shell (MudPaper +
///     clickable header + MudCollapse, matching <c>GenerateTerrain.razor</c>'s other sections) rather
///     than BankingSettingsPanel's own MudExpansionPanel markup. This component is pure UI — the page
///     (<c>GenerateTerrain</c>) owns every actual <c>BackdropOrchestrator</c>/<c>BackdropGenerator</c> call.
/// </summary>
public partial class BackdropSettingsPanel : ComponentBase
{
    [Parameter] public BackdropSettings Settings { get; set; } = null!;
    [Parameter] public EventCallback SettingsChanged { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool CanRegenerate { get; set; }
    [Parameter] public bool HasGeoTiffSource { get; set; }
    [Parameter] public EventCallback OnRegenerateBackdrop { get; set; }
    [Parameter] public EventCallback OnRemoveBackdrop { get; set; }
    [Parameter] public EventCallback OnUpdateEstimate { get; set; }
    [Parameter] public BackdropEstimateDisplay? Estimate { get; set; }

    [Inject] private IDialogService DialogService { get; set; } = default!;

    private bool _expanded;

    /// <summary>
    ///     Always-rendered / CSS-hidden while the "Generate Backdrop" switch is off (MudBlazor
    ///     visibility rule, see <c>GenerateTerrain.razor:738-744</c>'s bridge fields for the same
    ///     pattern): MudNumericFields created after the initial render show an empty input until
    ///     blurred, so the tuning fields must stay mounted and merely toggle visibility.
    /// </summary>
    private string? FieldStyle => Settings.Enabled ? null : "display:none";

    private async Task NotifyChanged() => await SettingsChanged.InvokeAsync();

    /// <summary>Destructive action — confirm before invoking <see cref="OnRemoveBackdrop"/>.</summary>
    private async Task ConfirmRemoveBackdrop()
    {
        var confirmed = await DialogService.ShowMessageBox(
            "Remove Backdrop",
            "This deletes the generated backdrop meshes, textures, and scene entries. This cannot be undone.",
            yesText: "Remove", cancelText: "Cancel");
        if (confirmed == true)
            await OnRemoveBackdrop.InvokeAsync();
    }
}

/// <summary>UI DTO for the cost estimate (filled by the page from BackdropGenerator.Estimate + tile count).</summary>
public sealed record BackdropEstimateDisplay(long Triangles, long TextureBytes, int TileCount, int ChunkCount)
{
    // Spec §5/§15 thresholds: yellow > 2 M tris or > 256 MB, red > 8 M tris or > 1 GB. Never blocks (D6).
    public Severity Severity =>
        Triangles > 8_000_000 || TextureBytes > 1L << 30 ? Severity.Error :
        Triangles > 2_000_000 || TextureBytes > 256L << 20 ? Severity.Warning : Severity.Info;
}
