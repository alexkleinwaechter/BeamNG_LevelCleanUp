namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>
///     How the generated terrain gets its textures. Mirrors the Basecolor Manager's two apply modes
///     (<c>Objects.MtSettings.BasecolorMode</c>) minus the "None" state, which is not a valid choice.
/// </summary>
public enum TerrainTexturingMode
{
    /// <summary>In-game terrain layer painting — the pre-existing GenerateTerrain behavior.</summary>
    PaintMode,

    /// <summary>Baked satellite-imagery basecolor maps, remote-controlling the Basecolor Manager.</summary>
    BaseColorMode
}

/// <summary>
///     UI/state POCO for the GenerateTerrain "Terrain Texturing Mode" section (backdrop follow-up
///     doc 06). <see cref="Mode"/> is the USER's choice; the effective mode is derived in
///     <c>TerrainGenerationState.EffectiveTexturingMode</c> because an enabled backdrop forces
///     BaseColor Mode without overwriting what the user picked.
/// </summary>
public class TexturingSettings
{
    // Default PaintMode = the pre-existing behavior: post-generation basecolor automation is a
    // strict no-op unless the user (or an enabled backdrop) opts into BaseColor Mode.
    public TerrainTexturingMode Mode { get; set; } = TerrainTexturingMode.PaintMode;

    /// <summary>
    ///     Overlay blend (percent) applied to every material NOT selected for road smoothing or
    ///     road painting; road materials are always pinned to 0 so no satellite texture bleeds
    ///     into the road system. 100 matches the backdrop, which is pure satellite imagery.
    /// </summary>
    public int NonRoadOverlayBlendPercent { get; set; } = 100;
}
