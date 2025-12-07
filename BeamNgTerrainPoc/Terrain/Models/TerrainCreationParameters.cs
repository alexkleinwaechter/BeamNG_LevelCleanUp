using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters for creating a BeamNG terrain file.
/// </summary>
public class TerrainCreationParameters
{
    /// <summary>
    /// Terrain size (must be power of 2: 256, 512, 1024, 2048, 4096, 8192, 16384)
    /// </summary>
    public int Size { get; set; }
    
    /// <summary>
    /// Maximum terrain height in world units
    /// </summary>
    public float MaxHeight { get; set; }
    
    /// <summary>
    /// Path to 16-bit grayscale heightmap image file.
    /// Use this OR HeightmapImage, not both.
    /// </summary>
    public string? HeightmapPath { get; set; }
    
    /// <summary>
    /// 16-bit grayscale heightmap image (for advanced scenarios where image is already loaded).
    /// Use this OR HeightmapPath, not both.
    /// If both are provided, HeightmapImage takes precedence.
    /// Note: When using HeightmapImage directly, caller is responsible for disposal.
    /// </summary>
    public Image<L16>? HeightmapImage { get; set; }
    
    /// <summary>
    /// List of material definitions. Each material has a name and optional layer image path.
    /// Order matters - index in list = material index in terrain file.
    /// First material (index 0) is used as default/fallback where no other material is defined.
    /// </summary>
    public List<MaterialDefinition> Materials { get; set; } = new();
    
    /// <summary>
    /// Optional: Include layer texture data in the output file (currently not used by BeamNG)
    /// </summary>
    public bool IncludeLayerTextureData { get; set; } = false;
    
    /// <summary>
    /// Optional: Name of the terrain (used for file naming conventions)
    /// Default is "theTerrain" if not specified.
    /// </summary>
    public string TerrainName { get; set; } = "theTerrain";
    
    /// <summary>
    /// Meters per pixel for the terrain (used for road smoothing calculations).
    /// Default is 2.0 (typical for BeamNG: 1024 terrain = 2048 meters).
    /// This defines the world-space scale of the terrain.
    /// </summary>
    public float MetersPerPixel { get; set; } = 2.0f;
    
    /// <summary>
    /// Enable cross-material junction harmonization.
    /// When enabled AND multiple materials have road smoothing, the system will detect
    /// where roads from different materials (e.g., highway and local road) meet and
    /// harmonize their elevations for smooth transitions.
    /// 
    /// This is a GLOBAL setting that applies to all road materials.
    /// Individual materials still control their own within-material junction harmonization
    /// via JunctionHarmonizationParameters.EnableJunctionHarmonization.
    /// 
    /// Default: true
    /// </summary>
    public bool EnableCrossMaterialHarmonization { get; set; } = true;
}
