namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Result of validating a GeoTIFF file for terrain generation and OSM integration.
/// </summary>
public class GeoTiffValidationResult
{
    /// <summary>
    /// Whether the GeoTIFF is valid for terrain generation.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Whether OSM data can be fetched for this GeoTIFF.
    /// This may be false even if IsValid is true (terrain can still be generated without OSM).
    /// </summary>
    public bool CanFetchOsmData { get; init; }

    /// <summary>
    /// List of critical errors that prevent terrain generation.
    /// </summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>
    /// List of warnings about potential issues (terrain can still be generated).
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Detailed diagnostic information for debugging.
    /// </summary>
    public List<string> DiagnosticInfo { get; init; } = [];

    /// <summary>
    /// The reason OSM data cannot be fetched (if applicable).
    /// </summary>
    public string? OsmBlockedReason { get; init; }

    /// <summary>
    /// The native bounding box (may be in projected coordinates like meters).
    /// </summary>
    public GeoBoundingBox? NativeBoundingBox { get; init; }

    /// <summary>
    /// The WGS84 bounding box (lat/lon degrees) - null if transformation failed.
    /// </summary>
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }

    /// <summary>
    /// The projection name for display.
    /// </summary>
    public string? ProjectionName { get; init; }

    /// <summary>
    /// Whether the native coordinates appear to be in a projected CRS (meters, not degrees).
    /// </summary>
    public bool IsProjectedCrs { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static GeoTiffValidationResult Success(
        GeoBoundingBox nativeBbox,
        GeoBoundingBox? wgs84Bbox,
        string? projectionName,
        bool isProjectedCrs)
    {
        var result = new GeoTiffValidationResult
        {
            IsValid = true,
            CanFetchOsmData = wgs84Bbox?.IsValidWgs84 == true,
            NativeBoundingBox = nativeBbox,
            Wgs84BoundingBox = wgs84Bbox,
            ProjectionName = projectionName,
            IsProjectedCrs = isProjectedCrs,
            OsmBlockedReason = wgs84Bbox?.IsValidWgs84 != true 
                ? "Could not determine valid WGS84 coordinates for this GeoTIFF" 
                : null
        };

        if (!result.CanFetchOsmData)
        {
            result.Warnings.Add("OSM road data will not be available for this terrain.");
        }

        return result;
    }

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static GeoTiffValidationResult Failure(string error)
    {
        return new GeoTiffValidationResult
        {
            IsValid = false,
            CanFetchOsmData = false,
            Errors = [error],
            OsmBlockedReason = error
        };
    }
}
