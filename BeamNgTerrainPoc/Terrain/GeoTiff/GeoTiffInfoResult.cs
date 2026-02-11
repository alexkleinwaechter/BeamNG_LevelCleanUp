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
    /// Returns the native DEM resolution in meters per pixel.
    /// For projected CRS (UTM, etc.), uses the native pixel size directly from the GeoTransform.
    /// For geographic CRS (lat/lon in degrees), calculates meters from the WGS84 bounding box.
    /// The terrain size parameter is unused but kept for API compatibility.
    /// </summary>
    /// <param name="targetTerrainSize">Unused. The native DEM resolution is independent of terrain size.</param>
    /// <returns>Meters per pixel, or null if calculation is not possible</returns>
    public float? CalculateMetersPerPixel(int targetTerrainSize = 0)
    {
        // For projected CRS (UTM, etc.), the GeoTransform pixel size is already in meters
        if (GeoTransform != null && !IsGeographic)
        {
            var nativePixelSizeX = Math.Abs(GeoTransform[1]);
            var nativePixelSizeY = Math.Abs(GeoTransform[5]);
            return (float)((nativePixelSizeX + nativePixelSizeY) / 2.0);
        }

        // For geographic CRS, calculate from WGS84 bounding box and pixel dimensions
        if (Wgs84BoundingBox == null || !Wgs84BoundingBox.IsValidWgs84 || Width <= 0 || Height <= 0)
            return null;

        // Calculate approximate dimensions in meters
        var centerLat = (Wgs84BoundingBox.MinLatitude + Wgs84BoundingBox.MaxLatitude) / 2.0;
        var centerLatRad = centerLat * Math.PI / 180.0;

        const double MetersPerDegreeLatitude = 111320.0;
        var metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(centerLatRad);

        var latExtent = Wgs84BoundingBox.MaxLatitude - Wgs84BoundingBox.MinLatitude;
        var lonExtent = Wgs84BoundingBox.MaxLongitude - Wgs84BoundingBox.MinLongitude;

        var heightMeters = latExtent * MetersPerDegreeLatitude;
        var widthMeters = lonExtent * metersPerDegreeLongitude;

        // Native resolution: total real-world extent divided by source pixel count
        var mppX = (float)(widthMeters / Width);
        var mppY = (float)(heightMeters / Height);

        return (mppX + mppY) / 2.0f;
    }

    /// <summary>
    /// Gets a description of the GeoTIFF's real-world size based on its native DEM resolution.
    /// </summary>
    /// <param name="targetTerrainSize">Unused. Kept for API compatibility.</param>
    /// <returns>Human-readable size description (e.g., "2.0 km × 2.0 km")</returns>
    public string? GetRealWorldSizeDescription(int targetTerrainSize = 0)
    {
        var mpp = CalculateMetersPerPixel();
        if (mpp == null)
            return null;

        var totalWidthMeters = mpp.Value * Width;
        var totalHeightMeters = mpp.Value * Height;

        if (totalWidthMeters >= 1000 || totalHeightMeters >= 1000)
            return $"{totalWidthMeters / 1000:F1} km × {totalHeightMeters / 1000:F1} km";
        else
            return $"{totalWidthMeters:F0} m × {totalHeightMeters:F0} m";
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
