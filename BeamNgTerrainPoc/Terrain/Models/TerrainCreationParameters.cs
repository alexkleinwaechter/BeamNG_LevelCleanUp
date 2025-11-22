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
    /// 16-bit grayscale heightmap image
    /// </summary>
    public Image<L16> HeightmapImage { get; set; } = null!;
    
    /// <summary>
    /// List of material definitions. Each material has a name and optional layer image.
    /// Order matters - index in list = material index in terrain file.
    /// First material (index 0) is used as default/fallback where no other material is defined.
    /// </summary>
    public List<MaterialDefinition> Materials { get; set; } = new();
    
    /// <summary>
    /// Optional: Include layer texture data in the output file (currently not used by BeamNG)
    /// </summary>
    public bool IncludeLayerTextureData { get; set; } = false;
}
