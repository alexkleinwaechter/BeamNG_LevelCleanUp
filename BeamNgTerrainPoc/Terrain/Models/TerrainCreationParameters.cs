using BeamNgTerrainPoc.Terrain.GeoTiff;
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

    /// <summary>
    /// Path to a GeoTIFF heightmap file.
    /// Use this as an alternative to HeightmapPath for importing elevation data with geographic coordinates.
    /// When set, the GeoTIFF will be read and the bounding box will be extracted.
    /// Priority: HeightmapImage > HeightmapPath > GeoTiffPath
    /// </summary>
    public string? GeoTiffPath { get; set; }

    /// <summary>
    /// Path to a directory containing multiple GeoTIFF tiles to combine.
    /// When set, all .tif, .tiff, and .geotiff files in the directory will be combined.
    /// Use this for terrain data that spans multiple tiles (e.g., SRTM tiles).
    /// Priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory
    /// </summary>
    public string? GeoTiffDirectory { get; set; }

    /// <summary>
    /// Geographic bounding box of the terrain.
    /// Automatically populated when importing from GeoTIFF.
    /// Can be used for OSM Overpass API queries to fetch roads, buildings, etc.
    /// </summary>
    public GeoBoundingBox? GeoBoundingBox { get; set; }

    /// <summary>
    /// Minimum elevation from GeoTIFF data (in meters).
    /// Automatically populated when importing from GeoTIFF.
    /// This is the base elevation - terrain heights are relative to this.
    /// </summary>
    public double? GeoTiffMinElevation { get; set; }

    /// <summary>
    /// Maximum elevation from GeoTIFF data (in meters).
    /// Automatically populated when importing from GeoTIFF.
    /// </summary>
    public double? GeoTiffMaxElevation { get; set; }

    /// <summary>
    /// Base height (Z position) for the terrain in world units.
    /// When importing from GeoTIFF, this should be set to the minimum elevation
    /// so the terrain sits at the correct world height.
    /// Default is 0.
    /// </summary>
    public float TerrainBaseHeight { get; set; } = 0.0f;

    /// <summary>
    /// When true and importing from GeoTIFF with MaxHeight=0, the TerrainBaseHeight
    /// will be automatically set to the minimum elevation from the GeoTIFF.
    /// Default is true.
    /// </summary>
    public bool AutoSetBaseHeightFromGeoTiff { get; set; } = true;
}
