namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// Root model for locale default files (e.g., DE-defaults.json, PL-defaults.json).
/// Contains locale-specific settings, traffic sign mappings, and sign materials.
/// </summary>
public class Osm2WorldLocaleConfig
{
    /// <summary>Schema identifier for format versioning.</summary>
    public string Schema { get; set; } = "osm2world-locale-schema-v1";

    /// <summary>Configuration format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Locale code (e.g., "DE", "PL").</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>Source file this config was generated from.</summary>
    public string? GeneratedFrom { get; set; }

    /// <summary>
    /// Locale-specific settings (e.g., "drivingSide" → "right").
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps generic traffic sign names to locale-specific material keys.
    /// E.g., "SIGN_CITY_LIMIT" → "SIGN_DE_310".
    /// </summary>
    public Dictionary<string, string> TrafficSignMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Locale-specific material definitions (typically sign materials with text overlays).
    /// </summary>
    public Dictionary<string, StyleMaterial> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
