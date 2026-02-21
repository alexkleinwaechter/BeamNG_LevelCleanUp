using System.Text.Json.Serialization;

namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// BeamNG-specific extensions for materials used in the building pipeline.
/// These properties are NOT in OSM2World's standard.properties — they are our additions
/// that map OSM2World materials to BeamNG's material system (Texture Cooker naming,
/// PBR material properties, etc.).
/// </summary>
public class BeamNgMaterialExtension
{
    /// <summary>
    /// BeamNG material name (e.g., "mtb_plaster").
    /// Becomes the material name in .materials.json and DAE files.
    /// </summary>
    public required string MaterialName { get; set; }

    /// <summary>Whether this is a roof material (affects UV mapping approach).</summary>
    public bool IsRoofMaterial { get; set; }

    /// <summary>
    /// Whether this material supports per-instance color tinting via BeamNG's instanceDiffuse system.
    /// When true, each TSStatic can specify its own instanceColor to tint the texture.
    /// </summary>
    public bool InstanceDiffuse { get; set; }

    /// <summary>
    /// Default RGB color (0-255 per channel). Used for placeholder texture generation
    /// and as diffuse color fallback. Array of [R, G, B].
    /// </summary>
    public float[] DefaultColor { get; set; } = [180, 180, 180];

    /// <summary>Material opacity (0 = fully transparent, 1 = fully opaque).</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>Whether the material should be rendered double-sided.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>
    /// Color/albedo texture filename with BeamNG Texture Cooker naming
    /// (e.g., "Bricks029_Color.color.png").
    /// </summary>
    public required string ColorMapFile { get; set; }

    /// <summary>
    /// Normal map texture filename with BeamNG Texture Cooker naming
    /// (e.g., "Bricks029_Normal.normal.png").
    /// </summary>
    public string? NormalMapFile { get; set; }

    /// <summary>
    /// ORM (Occlusion/Roughness/Metallic) packed texture filename
    /// (e.g., "Bricks029_ORM.data.png"). Used as source for automatic channel splitting.
    /// </summary>
    public string? OrmMapFile { get; set; }

    /// <summary>
    /// Optional explicit ambient occlusion texture filename (e.g., "Bricks029_AO.data.png").
    /// When set, overrides the auto-derived name from OrmMapFile.
    /// When null, auto-derived from OrmMapFile by replacing _ORM with _AO.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AoMapFile { get; set; }

    /// <summary>
    /// Optional explicit roughness texture filename (e.g., "Bricks029_Roughness.data.png").
    /// When set, overrides the auto-derived name from OrmMapFile.
    /// When null, auto-derived from OrmMapFile by replacing _ORM with _Roughness.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RoughnessMapFile { get; set; }

    /// <summary>
    /// Optional explicit metallic texture filename (e.g., "Bricks029_Metallic.data.png").
    /// When set, overrides the auto-derived name from OrmMapFile.
    /// When null, auto-derived from OrmMapFile by replacing _ORM with _Metallic.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MetallicMapFile { get; set; }
}
