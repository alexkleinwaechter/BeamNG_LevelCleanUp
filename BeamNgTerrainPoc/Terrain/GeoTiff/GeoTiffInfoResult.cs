using System.Text.RegularExpressions;
using OSGeo.OSR;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Contains extended information about a GeoTIFF file, including both native and WGS84 bounding boxes.
/// </summary>
public class GeoTiffInfoResult
{
    /// <summary>
    /// Width of the GeoTIFF in pixels.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height of the GeoTIFF in pixels.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// The bounding box in the native coordinate reference system of the GeoTIFF.
    /// For projected CRS (UTM, etc.), these are NOT lat/lon values.
    /// </summary>
    public required GeoBoundingBox BoundingBox { get; init; }

    /// <summary>
    /// The bounding box transformed to WGS84 (EPSG:4326) coordinates.
    /// This is the bounding box to use for Overpass API queries.
    /// May be null if transformation failed or the GeoTIFF has no projection info.
    /// </summary>
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }

    /// <summary>
    /// The projection/CRS of the GeoTIFF in WKT format.
    /// </summary>
    public string? Projection { get; init; }

    /// <summary>
    /// The 6-element geotransform array from GDAL.
    /// [0] = top-left X, [1] = pixel width, [2] = row rotation,
    /// [3] = top-left Y, [4] = column rotation, [5] = pixel height (negative)
    /// </summary>
    public double[]? GeoTransform { get; init; }

    /// <summary>
    /// Minimum elevation value in the GeoTIFF (in meters or source units).
    /// May be null if elevation statistics could not be computed.
    /// </summary>
    public double? MinElevation { get; init; }

    /// <summary>
    /// Maximum elevation value in the GeoTIFF (in meters or source units).
    /// May be null if elevation statistics could not be computed.
    /// </summary>
    public double? MaxElevation { get; init; }

    /// <summary>
    /// Whether the GeoTIFF is in a geographic (lat/lon) coordinate system.
    /// </summary>
    public bool IsGeographic => Wgs84BoundingBox != null && 
                                 BoundingBox.MinLongitude == Wgs84BoundingBox.MinLongitude;

    /// <summary>
    /// Whether the GeoTIFF has valid WGS84 coordinates available for OSM queries.
    /// </summary>
    public bool HasValidWgs84Coordinates => Wgs84BoundingBox?.IsValidWgs84 == true;

    /// <summary>
    /// Gets a human-readable name for the projection (e.g., "WGS 84 / UTM zone 32N").
    /// Returns "Unknown" if projection cannot be determined.
    /// </summary>
    public string ProjectionName => ParseProjectionName(Projection);

    /// <summary>
    /// Gets the EPSG code if available (e.g., "EPSG:32632" for UTM zone 32N).
    /// Returns null if no EPSG code can be determined.
    /// </summary>
    public string? EpsgCode => ParseEpsgCode(Projection);

    /// <summary>
    /// Calculates the approximate meters per pixel for the GeoTIFF at a given target terrain size.
    /// Uses the WGS84 bounding box to calculate geographic extent, then divides by target pixel count.
    /// For projected CRS (UTM, etc.), uses the native pixel size directly.
    /// </summary>
    /// <param name="targetTerrainSize">The target terrain size in pixels (e.g., 4096)</param>
    /// <returns>Meters per pixel, or null if calculation is not possible</returns>
    public float? CalculateMetersPerPixel(int targetTerrainSize)
    {
        if (targetTerrainSize <= 0)
            return null;

        // For projected CRS (UTM, etc.), the GeoTransform pixel size is already in meters
        if (GeoTransform != null && !IsGeographic)
        {
            // GeoTransform[1] = pixel width in native units (meters for UTM)
            // We need to account for the resize from original to target size
            var nativePixelSizeX = Math.Abs(GeoTransform[1]);
            var nativePixelSizeY = Math.Abs(GeoTransform[5]);
            var avgNativePixelSize = (nativePixelSizeX + nativePixelSizeY) / 2.0;
            
            // Scale by the ratio of original size to target size
            var originalSize = Math.Max(Width, Height);
            var scaleFactor = (double)originalSize / targetTerrainSize;
            
            return (float)(avgNativePixelSize * scaleFactor);
        }

        // For geographic CRS, calculate from WGS84 bounding box
        if (Wgs84BoundingBox == null || !Wgs84BoundingBox.IsValidWgs84)
            return null;

        // Calculate approximate dimensions in meters
        // Using Haversine-like approximation
        var centerLat = (Wgs84BoundingBox.MinLatitude + Wgs84BoundingBox.MaxLatitude) / 2.0;
        var centerLatRad = centerLat * Math.PI / 180.0;
        
        // Meters per degree of latitude (roughly constant ~111km)
        const double MetersPerDegreeLatitude = 111320.0;
        
        // Meters per degree of longitude (varies with latitude)
        var metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(centerLatRad);
        
        // Calculate terrain extent in meters
        var latExtent = Wgs84BoundingBox.MaxLatitude - Wgs84BoundingBox.MinLatitude;
        var lonExtent = Wgs84BoundingBox.MaxLongitude - Wgs84BoundingBox.MinLongitude;
        
        var heightMeters = latExtent * MetersPerDegreeLatitude;
        var widthMeters = lonExtent * metersPerDegreeLongitude;
        
        // Use the average of width and height (terrain is square after resize)
        var avgExtentMeters = (widthMeters + heightMeters) / 2.0;
        
        return (float)(avgExtentMeters / targetTerrainSize);
    }

    /// <summary>
    /// Gets a description of the terrain's real-world size.
    /// </summary>
    /// <param name="targetTerrainSize">The target terrain size in pixels</param>
    /// <returns>Human-readable size description (e.g., "111.3 km × 91.2 km")</returns>
    public string? GetRealWorldSizeDescription(int targetTerrainSize)
    {
        var mpp = CalculateMetersPerPixel(targetTerrainSize);
        if (mpp == null)
            return null;

        var totalMeters = mpp.Value * targetTerrainSize;
        
        if (totalMeters >= 1000)
            return $"{totalMeters / 1000:F1} km × {totalMeters / 1000:F1} km";
        else
            return $"{totalMeters:F0} m × {totalMeters:F0} m";
    }

    /// <summary>
    /// Parses the projection WKT to extract a human-readable name.
    /// </summary>
    private static string ParseProjectionName(string? projectionWkt)
    {
        if (string.IsNullOrEmpty(projectionWkt))
            return "Unknown (no projection info)";

        try
        {
            // Use GDAL to parse the WKT and get the name
            var srs = new SpatialReference(null);
            var wktCopy = projectionWkt;
            if (srs.ImportFromWkt(ref wktCopy) == 0)
            {
                // Try to get the projection name
                var name = srs.GetAttrValue("PROJCS", 0);
                if (!string.IsNullOrEmpty(name))
                    return name;

                // If not a projected CRS, try geographic
                name = srs.GetAttrValue("GEOGCS", 0);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            // Fallback: try to extract name from WKT using regex
            // Pattern: PROJCS["Name",...] or GEOGCS["Name",...]
            var match = Regex.Match(projectionWkt, @"(?:PROJCS|GEOGCS)\[""([^""]+)""");
            if (match.Success)
                return match.Groups[1].Value;

            return "Unknown projection";
        }
        catch
        {
            return "Unknown (parse error)";
        }
    }

    /// <summary>
    /// Parses the projection WKT to extract the EPSG code.
    /// </summary>
    private static string? ParseEpsgCode(string? projectionWkt)
    {
        if (string.IsNullOrEmpty(projectionWkt))
            return null;

        try
        {
            var srs = new SpatialReference(null);
            var wktCopy = projectionWkt;
            if (srs.ImportFromWkt(ref wktCopy) == 0)
            {
                // Try to get the authority code
                var code = srs.GetAuthorityCode(null);
                var name = srs.GetAuthorityName(null);
                
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(name))
                    return $"{name}:{code}";

                // Try from PROJCS
                code = srs.GetAuthorityCode("PROJCS");
                if (!string.IsNullOrEmpty(code))
                    return $"EPSG:{code}";

                // Try from GEOGCS
                code = srs.GetAuthorityCode("GEOGCS");
                if (!string.IsNullOrEmpty(code))
                    return $"EPSG:{code}";
            }

            // Fallback: try to extract from WKT using regex
            // Pattern: AUTHORITY["EPSG","32632"]
            var match = Regex.Match(projectionWkt, @"AUTHORITY\[""EPSG"",""(\d+)""\]");
            if (match.Success)
                return $"EPSG:{match.Groups[1].Value}";

            return null;
        }
        catch
        {
            return null;
        }
    }
}
