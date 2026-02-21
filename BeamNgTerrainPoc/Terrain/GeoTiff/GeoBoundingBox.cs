using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Logging;
using OSGeo.OSR;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Represents a geographic bounding box defined by two corner coordinates.
/// Used to store the geographic extent of GeoTIFF data for later OSM Overpass API queries.
/// </summary>
public class GeoBoundingBox
{
    /// <summary>
    /// Creates a bounding box from corner coordinates.
    /// </summary>
    /// <param name="lowerLeft">Lower-left (southwest) corner</param>
    /// <param name="upperRight">Upper-right (northeast) corner</param>
    public GeoBoundingBox(GeoCoordinate lowerLeft, GeoCoordinate upperRight)
    {
        LowerLeft = lowerLeft;
        UpperRight = upperRight;

        MinLongitude = lowerLeft.Longitude;
        MinLatitude = lowerLeft.Latitude;
        MaxLongitude = upperRight.Longitude;
        MaxLatitude = upperRight.Latitude;

        Width = MaxLongitude - MinLongitude;
        Height = MaxLatitude - MinLatitude;
    }

    /// <summary>
    /// Creates a bounding box from explicit min/max values.
    /// This constructor is used for JSON deserialization.
    /// </summary>
    [JsonConstructor]
    public GeoBoundingBox(double minLongitude, double minLatitude, double maxLongitude, double maxLatitude)
        : this(new GeoCoordinate(minLongitude, minLatitude), new GeoCoordinate(maxLongitude, maxLatitude))
    {
    }

    /// <summary>
    /// Lower-left (southwest) corner of the bounding box.
    /// </summary>
    public GeoCoordinate LowerLeft { get; }

    /// <summary>
    /// Upper-right (northeast) corner of the bounding box.
    /// </summary>
    public GeoCoordinate UpperRight { get; }

    /// <summary>
    /// Minimum longitude (westernmost edge).
    /// </summary>
    public double MinLongitude { get; }

    /// <summary>
    /// Minimum latitude (southernmost edge).
    /// </summary>
    public double MinLatitude { get; }

    /// <summary>
    /// Maximum longitude (easternmost edge).
    /// </summary>
    public double MaxLongitude { get; }

    /// <summary>
    /// Maximum latitude (northernmost edge).
    /// </summary>
    public double MaxLatitude { get; }

    /// <summary>
    /// Width of the bounding box in degrees longitude.
    /// </summary>
    public double Width { get; }

    /// <summary>
    /// Height of the bounding box in degrees latitude.
    /// </summary>
    public double Height { get; }

    /// <summary>
    /// Gets the center point of the bounding box.
    /// </summary>
    public GeoCoordinate Center => new(
        MinLongitude + Width / 2,
        MinLatitude + Height / 2);

    /// <summary>
    /// Checks if a point is within this bounding box.
    /// </summary>
    /// <param name="point">The point to check</param>
    /// <returns>True if the point is within the box</returns>
    public bool Contains(GeoCoordinate point)
    {
        return point.Longitude >= MinLongitude &&
               point.Longitude <= MaxLongitude &&
               point.Latitude >= MinLatitude &&
               point.Latitude <= MaxLatitude;
    }

    /// <summary>
    /// Checks if another bounding box is completely contained within this bounding box.
    /// </summary>
    /// <param name="other">The bounding box to check</param>
    /// <returns>True if the other box is completely within this box</returns>
    public bool Contains(GeoBoundingBox other)
    {
        return other.MinLongitude >= MinLongitude &&
               other.MaxLongitude <= MaxLongitude &&
               other.MinLatitude >= MinLatitude &&
               other.MaxLatitude <= MaxLatitude;
    }

    /// <summary>
    /// Checks if this bounding box intersects with another bounding box.
    /// </summary>
    /// <param name="other">The bounding box to check</param>
    /// <returns>True if the boxes overlap</returns>
    public bool Intersects(GeoBoundingBox other)
    {
        return MinLongitude <= other.MaxLongitude &&
               MaxLongitude >= other.MinLongitude &&
               MinLatitude <= other.MaxLatitude &&
               MaxLatitude >= other.MinLatitude;
    }

    /// <summary>
    /// Returns the bounding box in OSM Overpass API format: (south,west,north,east)
    /// </summary>
    /// <returns>Bounding box string for Overpass API queries</returns>
    public string ToOverpassBBox()
    {
        // Overpass format: (south, west, north, east) = (minLat, minLon, maxLat, maxLon)
        return $"({MinLatitude:F6},{MinLongitude:F6},{MaxLatitude:F6},{MaxLongitude:F6})";
    }

    /// <summary>
    /// Returns a filename-safe string representation.
    /// </summary>
    public string ToFileNameString()
    {
        return $"{MinLongitude:F4}_{MinLatitude:F4}_{MaxLongitude:F4}_{MaxLatitude:F4}";
    }

    /// <summary>
    /// Returns a human-readable string representation.
    /// </summary>
    public override string ToString()
    {
        return $"BBox[SW: ({MinLatitude:F4}°, {MinLongitude:F4}°), NE: ({MaxLatitude:F4}°, {MaxLongitude:F4}°)]";
    }

    /// <summary>
    /// Creates a local coordinate bounding box (0,0 to width-1, height-1).
    /// Useful for pixel-space operations.
    /// </summary>
    public GeoBoundingBox ToLocal()
    {
        return new GeoBoundingBox(
            new GeoCoordinate(0, 0),
            new GeoCoordinate(Width, Height));
    }

    /// <summary>
    /// Checks if this bounding box has valid WGS84 coordinates.
    /// WGS84 requires latitude between -90 and 90, longitude between -180 and 180.
    /// </summary>
    public bool IsValidWgs84 =>
        MinLatitude >= -90 && MaxLatitude <= 90 &&
        MinLongitude >= -180 && MaxLongitude <= 180;

    /// <summary>
    /// Transforms a bounding box from a projected coordinate system to WGS84 (EPSG:4326).
    /// </summary>
    /// <param name="projectedBbox">The bounding box in the source projection.</param>
    /// <param name="sourceProjectionWkt">The WKT string of the source projection.</param>
    /// <returns>A new bounding box in WGS84 coordinates, or null if transformation failed.</returns>
    public static GeoBoundingBox? TransformToWgs84(GeoBoundingBox projectedBbox, string sourceProjectionWkt)
    {
        if (string.IsNullOrEmpty(sourceProjectionWkt))
        {
            TerrainLogger.Warning("Cannot transform coordinates: source projection is empty");
            return null;
        }

        try
        {
            // Create source spatial reference from WKT
            var sourceSrs = new SpatialReference(null);
            var importResult = sourceSrs.ImportFromWkt(ref sourceProjectionWkt);
            if (importResult != 0)
            {
                TerrainLogger.Error($"Failed to import source projection WKT (error code: {importResult})");
                return null;
            }

            // Create target spatial reference (WGS84)
            var targetSrs = new SpatialReference(null);
            targetSrs.ImportFromEPSG(4326);

            // Set axis mapping strategy to traditional GIS order (lon, lat)
            // This is important for GDAL 3.x+ which defaults to authority-compliant order
            sourceSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            targetSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            // Create coordinate transformation
            var transform = new CoordinateTransformation(sourceSrs, targetSrs);

            // Transform the four corners
            double[] minXY = [projectedBbox.MinLongitude, projectedBbox.MinLatitude, 0];
            double[] maxXY = [projectedBbox.MaxLongitude, projectedBbox.MaxLatitude, 0];

            transform.TransformPoint(minXY);
            transform.TransformPoint(maxXY);

            // Create transformed bounding box
            // Note: After transformation, minXY[0] is longitude, minXY[1] is latitude
            var transformedBbox = new GeoBoundingBox(
                minLongitude: minXY[0],
                minLatitude: minXY[1],
                maxLongitude: maxXY[0],
                maxLatitude: maxXY[1]
            );

            // Use Detail() for per-tile transformation messages to avoid UI spam during bulk operations
            TerrainLogger.Detail($"Transformed bbox from projected CRS to WGS84: {transformedBbox}");

            // Validate the result
            if (!transformedBbox.IsValidWgs84)
            {
                TerrainLogger.Warning($"Transformed coordinates are outside valid WGS84 range: {transformedBbox}");
                return null;
            }

            return transformedBbox;
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"Coordinate transformation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Approximate meters per degree of latitude (roughly constant).
    /// </summary>
    private const double MetersPerDegreeLat = 111_320.0;

    /// <summary>
    /// Splits this bounding box into a grid of roughly equal-sized chunks.
    /// Chunk sizes are approximate because degree-to-meter conversion varies with latitude.
    /// </summary>
    /// <param name="chunkSizeMeters">Target chunk size in meters (default 1000m).</param>
    /// <returns>List of chunk bounding boxes covering this bbox.</returns>
    public List<GeoBoundingBox> SplitIntoChunks(double chunkSizeMeters = 1000.0)
    {
        // Calculate meters-per-degree at the center latitude
        var centerLatRad = Center.Latitude * Math.PI / 180.0;
        var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(centerLatRad);

        // Convert chunk size from meters to degrees
        var chunkDegreesLat = chunkSizeMeters / MetersPerDegreeLat;
        var chunkDegreesLon = chunkSizeMeters / metersPerDegreeLon;

        // Calculate grid dimensions
        var cols = Math.Max(1, (int)Math.Ceiling(Width / chunkDegreesLon));
        var rows = Math.Max(1, (int)Math.Ceiling(Height / chunkDegreesLat));

        // If bbox fits in a single chunk, return it as-is
        if (cols == 1 && rows == 1)
            return [this];

        var chunks = new List<GeoBoundingBox>(cols * rows);

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var chunkMinLon = MinLongitude + col * chunkDegreesLon;
                var chunkMinLat = MinLatitude + row * chunkDegreesLat;
                var chunkMaxLon = Math.Min(MinLongitude + (col + 1) * chunkDegreesLon, MaxLongitude);
                var chunkMaxLat = Math.Min(MinLatitude + (row + 1) * chunkDegreesLat, MaxLatitude);

                chunks.Add(new GeoBoundingBox(chunkMinLon, chunkMinLat, chunkMaxLon, chunkMaxLat));
            }
        }

        return chunks;
    }

    /// <summary>
    /// Estimates the approximate width of this bounding box in meters.
    /// </summary>
    public double ApproximateWidthMeters
    {
        get
        {
            var centerLatRad = Center.Latitude * Math.PI / 180.0;
            return Width * MetersPerDegreeLat * Math.Cos(centerLatRad);
        }
    }

    /// <summary>
    /// Estimates the approximate height of this bounding box in meters.
    /// </summary>
    public double ApproximateHeightMeters => Height * MetersPerDegreeLat;

    /// <summary>
    /// Checks if the given projection WKT represents WGS84 (EPSG:4326) or a similar geographic CRS.
    /// Note: This checks if it's a GEOGRAPHIC coordinate system (lat/lon in degrees),
    /// not just if the datum is WGS84 (which UTM projections also use).
    /// </summary>
    /// <param name="projectionWkt">The WKT string of the projection.</param>
    /// <returns>True if the projection is a geographic CRS (coordinates in degrees), false if it's a projected CRS (coordinates in meters/feet).</returns>
    public static bool IsWgs84Projection(string? projectionWkt)
    {
        if (string.IsNullOrEmpty(projectionWkt))
            return false;

        try
        {
            // Use GDAL to properly parse and check
            // IMPORTANT: Don't use simple string matching because "WGS 84" appears in both:
            // - Geographic CRS: "WGS 84" (EPSG:4326) - coordinates in degrees
            // - Projected CRS: "WGS 84 / UTM zone 32N" (EPSG:32632) - coordinates in meters!
            var srs = new SpatialReference(null);
            var wktCopy = projectionWkt;
            if (srs.ImportFromWkt(ref wktCopy) != 0)
            {
                TerrainLogger.Warning("Failed to parse projection WKT");
                return false;
            }

            // Check if it's a geographic coordinate system (not projected)
            // IsGeographic() returns 1 if it's geographic, 0 if projected
            // IsProjected() returns 1 if it's projected, 0 otherwise
            if (srs.IsProjected() == 1)
            {
                // It's a projected CRS (UTM, State Plane, etc.) - coordinates are in meters/feet, NOT degrees
                // Use Detail() to avoid spam during bulk tile analysis
                TerrainLogger.Detail("GeoTIFF uses a projected CRS (coordinates in meters/feet) - will transform to WGS84");
                return false;
            }

            if (srs.IsGeographic() == 1)
            {
                // It's a geographic CRS - coordinates are in degrees
                var authorityCode = srs.GetAuthorityCode("GEOGCS");
                TerrainLogger.Detail($"Geographic CRS detected (EPSG:{authorityCode ?? "unknown"}) - coordinates are in degrees");
                return true;
            }

            // Unknown type - be conservative and assume it needs transformation
            TerrainLogger.DetailWarning("Unknown CRS type - assuming projected coordinates");
            return false;
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Could not parse projection WKT: {ex.Message}");
            // If we can't parse it, be conservative - assume it might need transformation
            return false;
        }
    }
}
