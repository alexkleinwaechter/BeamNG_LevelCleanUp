using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Result of importing a GeoTIFF file, containing the heightmap data and geographic metadata.
/// </summary>
public class GeoTiffImportResult
{
    /// <summary>
    /// The heightmap image in 16-bit grayscale format, ready for terrain creation.
    /// Note: Caller is responsible for disposing this image when done.
    /// </summary>
    public required Image<L16> HeightmapImage { get; init; }

    /// <summary>
    /// The geographic bounding box of the imported terrain.
    /// This can be used for OSM Overpass API queries.
    /// </summary>
    public required GeoBoundingBox BoundingBox { get; init; }

    /// <summary>
    /// Width of the heightmap in pixels.
    /// </summary>
    public int Width => HeightmapImage.Width;

    /// <summary>
    /// Height of the heightmap in pixels.
    /// </summary>
    public int Height => HeightmapImage.Height;

    /// <summary>
    /// Minimum elevation found in the GeoTIFF data (in meters).
    /// </summary>
    public double MinElevation { get; init; }

    /// <summary>
    /// Maximum elevation found in the GeoTIFF data (in meters).
    /// </summary>
    public double MaxElevation { get; init; }

    /// <summary>
    /// Elevation range (max - min) in meters.
    /// This is typically used as MaxHeight in terrain creation.
    /// </summary>
    public double ElevationRange => MaxElevation - MinElevation;

    /// <summary>
    /// Pixel size in the X direction (degrees per pixel).
    /// </summary>
    public double PixelSizeX { get; init; }

    /// <summary>
    /// Pixel size in the Y direction (degrees per pixel).
    /// </summary>
    public double PixelSizeY { get; init; }

    /// <summary>
    /// Coordinate Reference System / Projection information (WKT format).
    /// </summary>
    public string? Projection { get; init; }

    /// <summary>
    /// Path to the source GeoTIFF file.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Returns a summary of the import result.
    /// </summary>
    public override string ToString()
    {
        return $"GeoTIFF Import: {Width}x{Height} px, " +
               $"Elevation: {MinElevation:F1}m - {MaxElevation:F1}m (range: {ElevationRange:F1}m), " +
               $"BBox: {BoundingBox}";
    }
}
