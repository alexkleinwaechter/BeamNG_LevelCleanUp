using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using static BeamNG_LevelCleanUp.BlazorUI.Components.TerrainMaterialSettings;

namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>
/// Centralized state container for the Terrain Generation page.
/// Consolidates all form fields and computed properties to reduce code-behind complexity.
/// </summary>
public class TerrainGenerationState
{
    // ========================================
    // WORKING DIRECTORY & LEVEL INFO
    // ========================================
    
    public string WorkingDirectory { get; set; } = string.Empty;
    public string LevelName { get; set; } = string.Empty;
    public bool HasWorkingDirectory { get; set; }
    public bool HasExistingTerrainSettings { get; set; }
    
    // ========================================
    // TERRAIN PARAMETERS
    // ========================================
    
    public string TerrainName { get; set; } = "theTerrain";
    public int TerrainSize { get; set; } = 2048;
    public float MaxHeight { get; set; }
    public float MetersPerPixel { get; set; } = 1.0f;
    public float TerrainBaseHeight { get; set; }
    public bool UpdateTerrainBlock { get; set; } = true;
    public bool EnableCrossMaterialHarmonization { get; set; } = true;
    
    /// <summary>
    /// Global junction detection radius in meters.
    /// Used when a material's JunctionHarmonizationParameters.UseGlobalSettings is true.
    /// </summary>
    public float GlobalJunctionDetectionRadiusMeters { get; set; } = 10.0f;
    
    /// <summary>
    /// Global junction blend distance in meters.
    /// Used when a material's JunctionHarmonizationParameters.UseGlobalSettings is true.
    /// </summary>
    public float GlobalJunctionBlendDistanceMeters { get; set; } = 30.0f;
    
    // ========================================
    // HEIGHTMAP SOURCE
    // ========================================
    
    public HeightmapSourceType HeightmapSourceType { get; set; } = HeightmapSourceType.Png;
    public string? HeightmapPath { get; set; }
    public string? GeoTiffPath { get; set; }
    public string? GeoTiffDirectory { get; set; }
    
    // ========================================
    // GEOTIFF METADATA
    // ========================================
    
    public GeoBoundingBox? GeoBoundingBox { get; set; }
    public GeoBoundingBox? GeoTiffNativeBoundingBox { get; set; }
    public string? GeoTiffProjectionName { get; set; }
    public string? GeoTiffProjectionWkt { get; set; }
    public double[]? GeoTiffGeoTransform { get; set; }
    public int GeoTiffOriginalWidth { get; set; }
    public int GeoTiffOriginalHeight { get; set; }
    public double? GeoTiffMinElevation { get; set; }
    public double? GeoTiffMaxElevation { get; set; }
    
    // ========================================
    // CROP SETTINGS
    // ========================================
    
    public CropAnchor CropAnchor { get; set; } = CropAnchor.Center;
    public CropResult? CropResult { get; set; }
    
    /// <summary>
    /// Cached combined GeoTIFF path for directory mode (avoids re-combining on every crop change).
    /// </summary>
    public string? CachedCombinedGeoTiffPath { get; set; }
    
    // ========================================
    // OSM DATA AVAILABILITY
    // ========================================
    
    public bool CanFetchOsmData { get; set; }
    public string? OsmBlockedReason { get; set; }
    public GeoTiffValidationResult? GeoTiffValidationResult { get; set; }
    
    // ========================================
    // TERRAIN MATERIALS
    // ========================================
    
    public List<TerrainMaterialItemExtended> TerrainMaterials { get; } = new();
    
    // ========================================
    // UI STATE
    // ========================================
    
    public bool IsGenerating { get; set; }
    public bool IsLoading { get; set; }
    
    // ========================================
    // MESSAGES & LOGS
    // ========================================
    
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Messages { get; } = new();
    
    // ========================================
    // COMPUTED PROPERTIES
    // ========================================
    
    /// <summary>
    /// Gets the effective bounding box for OSM queries.
    /// Returns the cropped bounding box if cropping is enabled, otherwise returns the full bounding box.
    /// This MUST be used for all OSM-related operations to ensure correct geographic extent.
    /// </summary>
    public GeoBoundingBox? EffectiveBoundingBox =>
        CropResult is { NeedsCropping: true, CroppedBoundingBox: not null }
            ? CropResult.CroppedBoundingBox
            : GeoBoundingBox;
    
    /// <summary>
    /// Gets the output path for the terrain file.
    /// </summary>
    public string GetOutputPath()
    {
        if (string.IsNullOrEmpty(WorkingDirectory))
            return "Not set";
        return Path.Combine(WorkingDirectory, $"{TerrainName}.ter");
    }
    
    /// <summary>
    /// Gets the debug output directory path.
    /// </summary>
    public string GetDebugPath()
    {
        if (string.IsNullOrEmpty(WorkingDirectory))
            return "Not set";
        return Path.Combine(WorkingDirectory, "MT_TerrainGeneration");
    }
    
    /// <summary>
    /// Gets the working directory title for display.
    /// </summary>
    public string GetWorkingDirectoryTitle()
    {
        if (!string.IsNullOrEmpty(LevelName))
            return $"Working Directory > {LevelName}";
        if (!string.IsNullOrEmpty(WorkingDirectory))
            return $"Working Directory > {WorkingDirectory}";
        return "Select Level Folder";
    }
    
    /// <summary>
    /// Checks if terrain generation can proceed.
    /// </summary>
    public bool CanGenerate()
    {
        var hasValidHeightmapSource = HeightmapSourceType switch
        {
            HeightmapSourceType.Png => !string.IsNullOrEmpty(HeightmapPath) && File.Exists(HeightmapPath),
            HeightmapSourceType.GeoTiffFile => !string.IsNullOrEmpty(GeoTiffPath) && File.Exists(GeoTiffPath),
            HeightmapSourceType.GeoTiffDirectory => !string.IsNullOrEmpty(GeoTiffDirectory) &&
                                                    Directory.Exists(GeoTiffDirectory),
            _ => false
        };

        return hasValidHeightmapSource &&
               TerrainMaterials.Any() &&
               !string.IsNullOrEmpty(TerrainName);
    }
    
    /// <summary>
    /// Gets the helper text for meters per pixel field.
    /// </summary>
    public string GetMetersPerPixelHelperText()
    {
        var terrainSizeKm = MetersPerPixel * TerrainSize / 1000f;
        return $"Terrain = {terrainSizeKm:F1}km × {terrainSizeKm:F1}km in-game";
    }
    
    /// <summary>
    /// Gets the heightmap source description for display.
    /// </summary>
    public string GetHeightmapSourceDescription()
    {
        return HeightmapSourceType switch
        {
            HeightmapSourceType.Png => "16-bit grayscale PNG heightmap",
            HeightmapSourceType.GeoTiffFile => "Single GeoTIFF elevation file with geographic coordinates",
            HeightmapSourceType.GeoTiffDirectory => "Directory with multiple GeoTIFF tiles to combine",
            _ => "Unknown"
        };
    }
    
    // ========================================
    // STATE MANAGEMENT
    // ========================================
    
    /// <summary>
    /// Clears all GeoTIFF metadata fields.
    /// </summary>
    public void ClearGeoMetadata()
    {
        GeoBoundingBox = null;
        GeoTiffNativeBoundingBox = null;
        GeoTiffProjectionName = null;
        GeoTiffProjectionWkt = null;
        GeoTiffGeoTransform = null;
        GeoTiffOriginalWidth = 0;
        GeoTiffOriginalHeight = 0;
        GeoTiffMinElevation = null;
        GeoTiffMaxElevation = null;
        
        CleanupCachedCombinedGeoTiff();
    }
    
    /// <summary>
    /// Cleans up the cached combined GeoTIFF file if it exists.
    /// </summary>
    public void CleanupCachedCombinedGeoTiff()
    {
        if (!string.IsNullOrEmpty(CachedCombinedGeoTiffPath))
        {
            try
            {
                if (File.Exists(CachedCombinedGeoTiffPath))
                {
                    File.Delete(CachedCombinedGeoTiffPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            CachedCombinedGeoTiffPath = null;
        }
    }
    
    /// <summary>
    /// Clears all messages, warnings, and errors.
    /// </summary>
    public void ClearMessages()
    {
        Errors.Clear();
        Warnings.Clear();
        Messages.Clear();
    }
    
    /// <summary>
    /// Resets all state to initial values.
    /// </summary>
    public void Reset()
    {
        WorkingDirectory = string.Empty;
        LevelName = string.Empty;
        HasWorkingDirectory = false;
        HasExistingTerrainSettings = false;
        TerrainMaterials.Clear();
        ClearMessages();
        
        HeightmapPath = null;
        TerrainSize = 2048;
        MaxHeight = 500.0f;
        MetersPerPixel = 1.0f;
        TerrainName = "theTerrain";
        TerrainBaseHeight = 0.0f;
        UpdateTerrainBlock = true;
        EnableCrossMaterialHarmonization = true;
        GlobalJunctionDetectionRadiusMeters = 10.0f;
        GlobalJunctionBlendDistanceMeters = 30.0f;
        
        HeightmapSourceType = HeightmapSourceType.Png;
        GeoTiffPath = null;
        GeoTiffDirectory = null;
        CropAnchor = CropAnchor.Center;
        CropResult = null;
        CanFetchOsmData = false;
        OsmBlockedReason = null;
        GeoTiffValidationResult = null;
        
        ClearGeoMetadata();
    }
}
