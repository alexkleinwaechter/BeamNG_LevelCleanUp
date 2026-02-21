namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// Root model for traffic sign catalogue files (e.g., DE-trafficSigns.json, PL-trafficSigns.json).
/// Contains hundreds of sign material definitions and sign-specific defaults.
/// </summary>
public class Osm2WorldTrafficSignsConfig
{
    /// <summary>Schema identifier for format versioning.</summary>
    public string Schema { get; set; } = "osm2world-trafficsigns-schema-v1";

    /// <summary>Configuration format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Locale code (e.g., "DE", "PL").</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>Source file this config was generated from.</summary>
    public string? GeneratedFrom { get; set; }

    /// <summary>
    /// Global traffic sign defaults (e.g., "defaultTrafficSignHeight" → 2.6, "standardPoleRadius" → 0.038).
    /// </summary>
    public Dictionary<string, float> TrafficSignDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-sign properties keyed by sign name (e.g., "SIGN_DE_600_30" → { "numPosts": "2" }).
    /// Values are string dictionaries since properties can be numeric or text.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> TrafficSignProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Traffic sign material definitions (typically hundreds of entries with transparency and texture layers).
    /// </summary>
    public Dictionary<string, StyleMaterial> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
