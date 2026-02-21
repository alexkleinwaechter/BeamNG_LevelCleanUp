namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// A single texture layer within a material definition.
/// Represents material_NAME_textureN_* properties from OSM2World standard.properties.
/// Each material can have multiple texture layers (e.g., base texture + overlay markings).
/// </summary>
public class StyleTextureLayer
{
    /// <summary>Texture layer index (0, 1, 2...).</summary>
    public int Index { get; set; }

    // --- Texture source (one of dir or file) ---

    /// <summary>Directory containing PBR texture set (e.g., "./textures/cc0textures/Bricks029").</summary>
    public string? Dir { get; set; }

    /// <summary>Single texture file path (e.g., "./textures/DE19F1FreisingDoor00005_small.png").</summary>
    public string? File { get; set; }

    /// <summary>Alternate color texture file (e.g., "./textures/cc0textures/Facade005/Facade005_Color_Transparent.png").</summary>
    public string? ColorFile { get; set; }

    // --- Texture dimensions in meters ---

    /// <summary>Texture width in meters (texture repeat distance).</summary>
    public float? Width { get; set; }

    /// <summary>Texture height in meters (texture repeat distance).</summary>
    public float? Height { get; set; }

    /// <summary>Per-entity texture width (used for objects with fixed-size textures).</summary>
    public float? WidthPerEntity { get; set; }

    /// <summary>Per-entity texture height (used for objects with fixed-size textures).</summary>
    public float? HeightPerEntity { get; set; }

    // --- Rendering properties ---

    /// <summary>Texture coordinate function: "GLOBAL_X_Z", "STRIP_FIT_HEIGHT".</summary>
    public string? CoordFunction { get; set; }

    /// <summary>Whether the texture can be tinted by a color.</summary>
    public bool? Colorable { get; set; }

    /// <summary>Texture wrapping mode: "CLAMP", "CLAMP_TO_BORDER".</summary>
    public string? Wrap { get; set; }

    /// <summary>Texture padding (used with CLAMP_TO_BORDER).</summary>
    public float? Padding { get; set; }

    /// <summary>Per-layer transparency mode: "TRUE", "BINARY".</summary>
    public string? Transparency { get; set; }

    /// <summary>Per-layer color tint (#hex format).</summary>
    public string? Color { get; set; }

    // --- Text overlay properties (for signs with dynamic text) ---

    /// <summary>Layer type: "text" for text overlay layers.</summary>
    public string? Type { get; set; }

    /// <summary>Text template string (e.g., "%{maxspeed}").</summary>
    public string? Text { get; set; }

    /// <summary>Font specification (e.g., "DIN 1451 Mittelschrift,PLAIN").</summary>
    public string? Font { get; set; }

    /// <summary>Relative font size for text overlays.</summary>
    public float? RelativeFontSize { get; set; }

    /// <summary>Text color (#hex format).</summary>
    public string? TextColor { get; set; }
}
