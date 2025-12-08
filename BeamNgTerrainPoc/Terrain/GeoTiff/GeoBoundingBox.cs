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
    /// </summary>
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
}
