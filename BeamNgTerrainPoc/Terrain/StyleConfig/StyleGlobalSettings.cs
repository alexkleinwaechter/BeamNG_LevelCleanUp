namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// Global settings from OSM2World standard.properties.
/// These control rendering behavior and locale configuration.
/// </summary>
public class StyleGlobalSettings
{
    /// <summary>Active locale code (e.g., "DE", "PL"). Determines which locale files to load.</summary>
    public string Locale { get; set; } = "DE";

    /// <summary>Enable (true) or disable (false) creation of empty terrain.</summary>
    public bool CreateTerrain { get; set; } = true;

    /// <summary>Background color for PNG output (#hex format).</summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>Whether the PNG output should use a transparent background.</summary>
    public bool ExportAlpha { get; set; }

    /// <summary>Tree density in forests (trees per square meter).</summary>
    public float TreesPerSquareMeter { get; set; } = 0.02f;

    /// <summary>Enable parsing of building color and material tags.</summary>
    public bool UseBuildingColors { get; set; } = true;

    /// <summary>Enable replacing geometry with textured billboards.</summary>
    public bool UseBillboards { get; set; } = true;

    /// <summary>Enable rendering of world objects below the ground.</summary>
    public bool RenderUnderground { get; set; }

    /// <summary>Prevents PNG export from buffering primitives (reduces RAM, may increase render time).</summary>
    public bool ForceUnbufferedPNGRendering { get; set; }

    /// <summary>Maximum size for each dimension of the OpenGL canvas used for PNG output.</summary>
    public int CanvasLimit { get; set; } = 1024;

    /// <summary>Anti-aliasing sample count.</summary>
    public int Msaa { get; set; } = 4;

    /// <summary>Driving side for the locale ("right" or "left"). Merged from locale defaults.</summary>
    public string? DrivingSide { get; set; }
}
