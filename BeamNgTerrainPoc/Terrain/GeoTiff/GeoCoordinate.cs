namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Represents a geographic coordinate point with longitude, latitude, and optional altitude.
/// </summary>
public class GeoCoordinate
{
    /// <summary>
    /// Creates a new geographic coordinate.
    /// </summary>
    /// <param name="longitude">Longitude in degrees (X coordinate, -180 to 180)</param>
    /// <param name="latitude">Latitude in degrees (Y coordinate, -90 to 90)</param>
    /// <param name="altitude">Optional altitude in meters</param>
    public GeoCoordinate(double longitude, double latitude, double? altitude = null)
    {
        Longitude = longitude;
        Latitude = latitude;
        Altitude = altitude;
    }

    /// <summary>
    /// Longitude in degrees (X coordinate).
    /// Positive = East, Negative = West.
    /// Range: -180 to 180.
    /// </summary>
    public double Longitude { get; }

    /// <summary>
    /// Latitude in degrees (Y coordinate).
    /// Positive = North, Negative = South.
    /// Range: -90 to 90.
    /// </summary>
    public double Latitude { get; }

    /// <summary>
    /// Altitude/elevation in meters (optional).
    /// </summary>
    public double? Altitude { get; }

    /// <summary>
    /// Returns a string representation of this coordinate.
    /// </summary>
    public override string ToString()
    {
        var altStr = Altitude.HasValue ? $", Alt: {Altitude.Value:F2}m" : "";
        return $"Lon: {Longitude:F6}°, Lat: {Latitude:F6}°{altStr}";
    }

    /// <summary>
    /// Creates a coordinate with floor values (useful for grid alignment).
    /// </summary>
    public GeoCoordinate Floor()
    {
        return new GeoCoordinate(Math.Floor(Longitude), Math.Floor(Latitude), Altitude);
    }
}
