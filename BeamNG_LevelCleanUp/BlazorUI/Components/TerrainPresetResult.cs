namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
/// Result of importing a BeamNG terrain preset file.
/// Contains the extracted settings that should be applied to the terrain generation page.
/// </summary>
public class TerrainPresetResult
{
    /// <summary>
    /// The terrain name from the preset.
    /// </summary>
    public string? TerrainName { get; set; }

    /// <summary>
    /// The maximum height (from heightScale in preset).
    /// </summary>
    public float? MaxHeight { get; set; }

    /// <summary>
    /// Meters per pixel (from squareSize in preset).
    /// </summary>
    public float? MetersPerPixel { get; set; }

    /// <summary>
    /// Terrain base height (from pos.z in preset).
    /// </summary>
    public float? TerrainBaseHeight { get; set; }

    /// <summary>
    /// Resolved path to the heightmap file.
    /// </summary>
    public string? HeightmapPath { get; set; }

    /// <summary>
    /// Resolved path to the hole map file.
    /// </summary>
    public string? HoleMapPath { get; set; }

    /// <summary>
    /// Number of layer maps that were successfully assigned to materials.
    /// </summary>
    public int AssignedLayerMapsCount { get; set; }
}
