namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// A single material definition from OSM2World properties.
/// Mirrors the structure of material_NAME_* properties with optional BeamNG extensions.
/// </summary>
public class StyleMaterial
{
    /// <summary>Material-level color (#hex format, e.g., "#ffe68c").</summary>
    public string? Color { get; set; }

    /// <summary>Material-level transparency mode: "TRUE" or "BINARY".</summary>
    public string? Transparency { get; set; }

    /// <summary>Whether the material should be rendered double-sided.</summary>
    public bool? DoubleSided { get; set; }

    /// <summary>Texture layers (base texture + optional overlays).</summary>
    public List<StyleTextureLayer> TextureLayers { get; set; } = new();

    /// <summary>
    /// BeamNG-specific extension properties. Null for materials we don't use in BeamNG
    /// (e.g., terrain, road markings, traffic signs).
    /// </summary>
    public BeamNgMaterialExtension? Beamng { get; set; }
}
