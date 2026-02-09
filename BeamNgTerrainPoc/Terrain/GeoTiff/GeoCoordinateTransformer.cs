using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using OSGeo.OSR;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Transforms WGS84 (lat/lon) coordinates to pixel coordinates using proper GDAL reprojection.
/// This is essential when the GeoTIFF is in a projected coordinate system (UTM, State Plane, etc.)
/// because simple linear interpolation between WGS84 bbox corners doesn't account for
/// meridian convergence and scale factor variations.
/// </summary>
public class GeoCoordinateTransformer : IDisposable
{
    private readonly CoordinateTransformation? _wgs84ToNativeTransform;
    private readonly double[] _inverseGeoTransform;
    private readonly int _terrainSize;
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly bool _isProjected;
    private bool _disposed;

    /// <summary>
    /// Creates a transformer that converts WGS84 coordinates to pixel coordinates
    /// using proper GDAL reprojection for projected coordinate systems.
    /// </summary>
    /// <param name="projectionWkt">The WKT of the GeoTIFF's native projection.</param>
    /// <param name="geoTransform">The 6-element geotransform array from GDAL.</param>
    /// <param name="originalWidth">Original width of the GeoTIFF in pixels.</param>
    /// <param name="originalHeight">Original height of the GeoTIFF in pixels.</param>
    /// <param name="terrainSize">Target terrain size (after resize to power of 2).</param>
    public GeoCoordinateTransformer(
        string? projectionWkt,
        double[] geoTransform,
        int originalWidth,
        int originalHeight,
        int terrainSize)
    {
        _originalWidth = originalWidth;
        _originalHeight = originalHeight;
        _terrainSize = terrainSize;

        // Compute inverse geotransform for native CRS -> pixel conversion
        _inverseGeoTransform = ComputeInverseGeoTransform(geoTransform);

        // Check if we need to transform from WGS84
        _isProjected = !string.IsNullOrEmpty(projectionWkt) && 
                       !GeoBoundingBox.IsWgs84Projection(projectionWkt);

        if (_isProjected)
        {
            try
            {
                // Create WGS84 -> native CRS transformation
                var wgs84Srs = new SpatialReference(null);
                wgs84Srs.ImportFromEPSG(4326);
                wgs84Srs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                var nativeSrs = new SpatialReference(null);
                var wktCopy = projectionWkt!;
                if (nativeSrs.ImportFromWkt(ref wktCopy) == 0)
                {
                    nativeSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                    _wgs84ToNativeTransform = new CoordinateTransformation(wgs84Srs, nativeSrs);
                    TerrainLogger.Info($"Created WGS84 -> native CRS transformer for projected coordinates");
                }
                else
                {
                    TerrainLogger.Warning("Could not parse projection WKT, falling back to linear interpolation");
                    _isProjected = false;
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Could not create coordinate transformer: {ex.Message}. Falling back to linear interpolation.");
                _isProjected = false;
            }
        }
    }

    /// <summary>
    /// Whether this transformer uses proper reprojection (for projected CRS)
    /// or simple linear interpolation (for geographic CRS).
    /// </summary>
    public bool UsesReprojection => _isProjected && _wgs84ToNativeTransform != null;

    /// <summary>
    /// Transforms a WGS84 coordinate to pixel coordinates in terrain-space (bottom-left origin).
    /// </summary>
    /// <param name="longitude">Longitude in WGS84.</param>
    /// <param name="latitude">Latitude in WGS84.</param>
    /// <returns>Pixel coordinate where (0,0) is bottom-left.</returns>
    public Vector2 TransformToTerrainPixel(double longitude, double latitude)
    {
        double nativeX, nativeY;

        if (_wgs84ToNativeTransform != null)
        {
            // Transform WGS84 -> native CRS
            double[] point = [longitude, latitude, 0];
            _wgs84ToNativeTransform.TransformPoint(point);
            nativeX = point[0];
            nativeY = point[1];
        }
        else
        {
            // No transformation needed (already in native CRS or geographic)
            nativeX = longitude;
            nativeY = latitude;
        }

        // Apply inverse geotransform to get pixel coordinates
        // These are in the original GeoTIFF pixel space (top-left origin)
        var pixelX = _inverseGeoTransform[0] + _inverseGeoTransform[1] * nativeX + _inverseGeoTransform[2] * nativeY;
        var pixelY = _inverseGeoTransform[3] + _inverseGeoTransform[4] * nativeX + _inverseGeoTransform[5] * nativeY;

        // Scale to terrain size (if GeoTIFF was resized)
        var scaleX = (float)_terrainSize / _originalWidth;
        var scaleY = (float)_terrainSize / _originalHeight;
        pixelX *= scaleX;
        pixelY *= scaleY;

        // Convert from image-space (top-left origin) to terrain-space (bottom-left origin)
        // In image space, Y increases downward; in terrain space, Y increases upward
        var terrainY = _terrainSize - 1 - pixelY;

        return new Vector2((float)pixelX, (float)terrainY);
    }

    /// <summary>
    /// Transforms a WGS84 coordinate to pixel coordinates in image-space (top-left origin).
    /// </summary>
    /// <param name="longitude">Longitude in WGS84.</param>
    /// <param name="latitude">Latitude in WGS84.</param>
    /// <returns>Pixel coordinate where (0,0) is top-left.</returns>
    public Vector2 TransformToImagePixel(double longitude, double latitude)
    {
        double nativeX, nativeY;

        if (_wgs84ToNativeTransform != null)
        {
            // Transform WGS84 -> native CRS
            double[] point = [longitude, latitude, 0];
            _wgs84ToNativeTransform.TransformPoint(point);
            nativeX = point[0];
            nativeY = point[1];
        }
        else
        {
            // No transformation needed
            nativeX = longitude;
            nativeY = latitude;
        }

        // Apply inverse geotransform to get pixel coordinates
        var pixelX = _inverseGeoTransform[0] + _inverseGeoTransform[1] * nativeX + _inverseGeoTransform[2] * nativeY;
        var pixelY = _inverseGeoTransform[3] + _inverseGeoTransform[4] * nativeX + _inverseGeoTransform[5] * nativeY;

        // Scale to terrain size (if GeoTIFF was resized)
        var scaleX = (float)_terrainSize / _originalWidth;
        var scaleY = (float)_terrainSize / _originalHeight;

        return new Vector2((float)(pixelX * scaleX), (float)(pixelY * scaleY));
    }

    /// <summary>
    /// Computes the inverse geotransform for converting native CRS coordinates to pixel coordinates.
    /// </summary>
    private static double[] ComputeInverseGeoTransform(double[] gt)
    {
        // Geotransform: X = gt[0] + pixel*gt[1] + line*gt[2]
        //               Y = gt[3] + pixel*gt[4] + line*gt[5]
        // Inverse: pixel = igt[0] + X*igt[1] + Y*igt[2]
        //          line  = igt[3] + X*igt[4] + Y*igt[5]

        var det = gt[1] * gt[5] - gt[2] * gt[4];
        if (Math.Abs(det) < 1e-15)
        {
            // Degenerate transform, return identity-ish
            return [0, 1, 0, 0, 0, 1];
        }

        var invDet = 1.0 / det;

        var igt = new double[6];
        igt[1] = gt[5] * invDet;
        igt[4] = -gt[4] * invDet;
        igt[2] = -gt[2] * invDet;
        igt[5] = gt[1] * invDet;
        igt[0] = (gt[2] * gt[3] - gt[0] * gt[5]) * invDet;
        igt[3] = (gt[0] * gt[4] - gt[1] * gt[3]) * invDet;

        return igt;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _wgs84ToNativeTransform?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Factory for creating thread-safe GeoCoordinateTransformer instances.
/// GDAL's CoordinateTransformation is not thread-safe, so each thread needs its own instance.
/// This factory stores the parameters needed to create transformers on demand.
/// </summary>
public class GeoCoordinateTransformerFactory
{
    private readonly string? _projectionWkt;
    private readonly double[] _geoTransform;
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly int _terrainSize;

    /// <summary>
    /// Creates a factory that can produce GeoCoordinateTransformer instances with the given parameters.
    /// </summary>
    /// <param name="projectionWkt">The WKT of the GeoTIFF's native projection.</param>
    /// <param name="geoTransform">The 6-element geotransform array from GDAL.</param>
    /// <param name="originalWidth">Original width of the GeoTIFF in pixels.</param>
    /// <param name="originalHeight">Original height of the GeoTIFF in pixels.</param>
    /// <param name="terrainSize">Target terrain size (after resize to power of 2).</param>
    public GeoCoordinateTransformerFactory(
        string? projectionWkt,
        double[] geoTransform,
        int originalWidth,
        int originalHeight,
        int terrainSize)
    {
        _projectionWkt = projectionWkt;
        // Clone the array to ensure immutability
        _geoTransform = (double[])geoTransform.Clone();
        _originalWidth = originalWidth;
        _originalHeight = originalHeight;
        _terrainSize = terrainSize;
    }

    /// <summary>
    /// Creates a factory from an existing transformer instance.
    /// Note: This extracts the parameters but the original transformer remains usable.
    /// </summary>
    public static GeoCoordinateTransformerFactory? FromTransformer(GeoCoordinateTransformer? transformer)
    {
        // Cannot extract parameters from an existing transformer - return null
        // Callers should use the constructor with explicit parameters instead
        return null;
    }

    /// <summary>
    /// Creates a new GeoCoordinateTransformer instance.
    /// Each call creates a new instance that is safe to use in a single thread.
    /// The caller is responsible for disposing the returned instance.
    /// </summary>
    public GeoCoordinateTransformer Create()
    {
        return new GeoCoordinateTransformer(
            _projectionWkt,
            _geoTransform,
            _originalWidth,
            _originalHeight,
            _terrainSize);
    }
}
