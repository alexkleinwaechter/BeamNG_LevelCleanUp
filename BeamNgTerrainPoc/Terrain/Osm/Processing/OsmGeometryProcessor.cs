using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Processes OSM geometry for terrain generation.
/// Handles coordinate transformation, cropping, rasterization, and spline conversion.
/// </summary>
public class OsmGeometryProcessor
{
    private GeoCoordinateTransformer? _transformer;

    /// <summary>
    /// Sets the coordinate transformer for proper WGS84 to pixel conversion.
    /// When set, all coordinate transformations will use GDAL reprojection
    /// instead of simple linear interpolation.
    /// </summary>
    /// <param name="transformer">The transformer to use, or null to use linear interpolation.</param>
    public void SetCoordinateTransformer(GeoCoordinateTransformer? transformer)
    {
        _transformer = transformer;
        if (transformer != null)
        {
            TerrainLogger.Info($"OsmGeometryProcessor: Using GDAL coordinate transformer (reprojection: {transformer.UsesReprojection})");
        }
    }

    /// <summary>
    /// Transforms a geographic coordinate to pixel coordinates in BeamNG terrain-space.
    /// BeamNG uses bottom-left origin (Y=0 at bottom), matching how the heightmap is stored.
    /// Use this for road splines that will be used with the heightmap array.
    /// </summary>
    /// <param name="coord">Geographic coordinate (longitude, latitude).</param>
    /// <param name="bbox">The bounding box defining the terrain extent (used for linear interpolation fallback).</param>
    /// <param name="terrainSize">Size of the terrain in pixels.</param>
    /// <returns>Pixel coordinate (X, Y) where (0,0) is bottom-left (BeamNG coordinate system).</returns>
    public Vector2 TransformToTerrainCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize)
    {
        // Use proper transformer if available
        if (_transformer != null)
        {
            return _transformer.TransformToTerrainPixel(coord.Longitude, coord.Latitude);
        }

        // Fallback to linear interpolation (assumes no rotation/skew)
        // Normalize longitude/latitude to 0-1 range within bbox
        var normalizedX = (coord.Longitude - bbox.MinLongitude) / bbox.Width;
        var normalizedY = (coord.Latitude - bbox.MinLatitude) / bbox.Height;
        
        // Convert to pixel coordinates in BeamNG terrain-space (bottom-left origin)
        // No Y inversion needed - latitude increases northward, which corresponds to
        // increasing Y in BeamNG's bottom-up coordinate system
        var pixelX = (float)(normalizedX * terrainSize);
        var pixelY = (float)(normalizedY * terrainSize);
        
        return new Vector2(pixelX, pixelY);
    }
    
    /// <summary>
    /// Transforms a geographic coordinate to pixel coordinates in image-space.
    /// Image-space uses top-left origin (Y=0 at top), matching PNG/image files.
    /// Use this for layer maps that will be saved as images.
    /// </summary>
    /// <param name="coord">Geographic coordinate (longitude, latitude).</param>
    /// <param name="bbox">The bounding box defining the terrain extent (used for linear interpolation fallback).</param>
    /// <param name="terrainSize">Size of the terrain in pixels.</param>
    /// <returns>Pixel coordinate (X, Y) where (0,0) is top-left (image coordinate system).</returns>
    public Vector2 TransformToPixelCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize)
    {
        // Use proper transformer if available
        if (_transformer != null)
        {
            return _transformer.TransformToImagePixel(coord.Longitude, coord.Latitude);
        }

        // Fallback to linear interpolation (assumes no rotation/skew)
        // Normalize longitude/latitude to 0-1 range within bbox
        var normalizedX = (coord.Longitude - bbox.MinLongitude) / bbox.Width;
        var normalizedY = (coord.Latitude - bbox.MinLatitude) / bbox.Height;
        
        // Convert to pixel coordinates in image-space (top-left origin)
        // Y is inverted because image coordinates have Y increasing downward
        // while latitude increases northward
        var pixelX = (float)(normalizedX * terrainSize);
        var pixelY = (float)((1.0 - normalizedY) * terrainSize);
        
        return new Vector2(pixelX, pixelY);
    }
    
    /// <summary>
    /// Transforms a list of geographic coordinates to pixel coordinates in image-space.
    /// </summary>
    public List<Vector2> TransformToPixelCoordinates(
        List<GeoCoordinate> geoCoords, 
        GeoBoundingBox bbox, 
        int terrainSize)
    {
        return geoCoords
            .Select(c => TransformToPixelCoordinate(c, bbox, terrainSize))
            .ToList();
    }
    
    /// <summary>
    /// Transforms a list of geographic coordinates to terrain-space coordinates.
    /// </summary>
    public List<Vector2> TransformToTerrainCoordinates(
        List<GeoCoordinate> geoCoords, 
        GeoBoundingBox bbox, 
        int terrainSize)
    {
        return geoCoords
            .Select(c => TransformToTerrainCoordinate(c, bbox, terrainSize))
            .ToList();
    }
    
    /// <summary>
    /// Crops features to the terrain bounding box.
    /// Features fully outside are removed, features partially inside are clipped.
    /// </summary>
    public List<OsmFeature> CropToBoundingBox(List<OsmFeature> features, GeoBoundingBox bbox)
    {
        var result = new List<OsmFeature>();
        
        foreach (var feature in features)
        {
            // Check if feature has any coordinates inside bbox
            var hasInsideCoord = feature.Coordinates.Any(c => bbox.Contains(c));
            
            if (!hasInsideCoord)
            {
                // Check if the feature spans across the bbox (line crossing through)
                var minLon = feature.Coordinates.Min(c => c.Longitude);
                var maxLon = feature.Coordinates.Max(c => c.Longitude);
                var minLat = feature.Coordinates.Min(c => c.Latitude);
                var maxLat = feature.Coordinates.Max(c => c.Latitude);
                
                var crossesBbox = 
                    minLon <= bbox.MaxLongitude && maxLon >= bbox.MinLongitude &&
                    minLat <= bbox.MaxLatitude && maxLat >= bbox.MinLatitude;
                
                if (!crossesBbox)
                    continue; // Fully outside
            }
            
            // For now, include features that touch the bbox
            // More sophisticated clipping could be added later
            result.Add(feature);
        }
        
        TerrainLogger.Info($"Cropped {features.Count} features to {result.Count} within bbox");
        return result;
    }
    
    /// <summary>
    /// Clips a line to terrain bounds using Cohen-Sutherland-like approach.
    /// </summary>
    public List<Vector2> CropLineToTerrain(List<Vector2> coords, int terrainSize)
    {
        if (coords.Count < 2)
            return new List<Vector2>();
        
        var result = new List<Vector2>();
        var minBound = 0f;
        var maxBound = (float)terrainSize;
        
        for (int i = 0; i < coords.Count; i++)
        {
            var point = coords[i];
            var isInside = point.X >= minBound && point.X <= maxBound &&
                          point.Y >= minBound && point.Y <= maxBound;
            
            if (isInside)
            {
                result.Add(point);
            }
            else if (result.Count > 0)
            {
                // We just exited the bounds, clip to edge
                var lastInside = result[^1];
                var clipped = ClipToEdge(lastInside, point, minBound, maxBound);
                if (clipped.HasValue)
                {
                    result.Add(clipped.Value);
                }
                // Start a new segment if we re-enter later
            }
            else if (i > 0)
            {
                // Check if segment crosses into bounds
                var prev = coords[i - 1];
                var entry = ClipToEdge(point, prev, minBound, maxBound);
                if (entry.HasValue)
                {
                    result.Add(entry.Value);
                }
            }
        }
        
        return result;
    }
    
    private Vector2? ClipToEdge(Vector2 inside, Vector2 outside, float minBound, float maxBound)
    {
        var dx = outside.X - inside.X;
        var dy = outside.Y - inside.Y;
        
        float tMin = 0f, tMax = 1f;
        
        // Clip against each edge
        if (!ClipTest(-dx, inside.X - minBound, ref tMin, ref tMax)) return null;
        if (!ClipTest(dx, maxBound - inside.X, ref tMin, ref tMax)) return null;
        if (!ClipTest(-dy, inside.Y - minBound, ref tMin, ref tMax)) return null;
        if (!ClipTest(dy, maxBound - inside.Y, ref tMin, ref tMax)) return null;
        
        return new Vector2(inside.X + tMin * dx, inside.Y + tMin * dy);
    }
    
    private bool ClipTest(float p, float q, ref float tMin, ref float tMax)
    {
        if (Math.Abs(p) < 1e-10)
            return q >= 0;
        
        var t = q / p;
        if (p < 0)
        {
            if (t > tMax) return false;
            if (t > tMin) tMin = t;
        }
        else
        {
            if (t < tMin) return false;
            if (t < tMax) tMax = t;
        }
        return true;
    }
    
    /// <summary>
    /// Rasterizes polygon features to a binary layer map.
    /// Properly handles OSM multipolygon relations with:
    /// - Inner rings (holes) that are subtracted from the polygon
    /// - Multiple outer rings (Parts) for disjoint polygons in the same relation
    /// </summary>
    /// <param name="polygonFeatures">List of polygon features to rasterize.</param>
    /// <param name="bbox">Geographic bounding box.</param>
    /// <param name="terrainSize">Output image size.</param>
    /// <returns>Binary mask where 255 = inside polygon, 0 = outside.</returns>
    public byte[,] RasterizePolygonsToLayerMap(
        List<OsmFeature> polygonFeatures, 
        GeoBoundingBox bbox, 
        int terrainSize)
    {
        var result = new byte[terrainSize, terrainSize];
        int featuresWithHoles = 0;
        int featuresWithParts = 0;
        int totalInnerRings = 0;
        int totalParts = 0;
        
        foreach (var feature in polygonFeatures.Where(f => f.GeometryType == OsmGeometryType.Polygon))
        {
            // Rasterize the main outer ring
            var outerPixelCoords = TransformToPixelCoordinates(feature.Coordinates, bbox, terrainSize);
            
            // Transform inner rings (holes) if present
            List<List<Vector2>>? innerPixelRings = null;
            if (feature.InnerRings != null && feature.InnerRings.Count > 0)
            {
                featuresWithHoles++;
                totalInnerRings += feature.InnerRings.Count;
                innerPixelRings = feature.InnerRings
                    .Select(ring => TransformToPixelCoordinates(ring, bbox, terrainSize))
                    .ToList();
            }
            
            // Rasterize outer ring with holes subtracted
            RasterizePolygonWithHoles(result, outerPixelCoords, innerPixelRings);
            
            // Rasterize additional parts (disjoint outer rings from multipolygon relations)
            if (feature.Parts != null && feature.Parts.Count > 0)
            {
                featuresWithParts++;
                totalParts += feature.Parts.Count;
                foreach (var part in feature.Parts)
                {
                    var partPixelCoords = TransformToPixelCoordinates(part, bbox, terrainSize);
                    // Parts don't have their own inner rings in current implementation
                    // If needed, this could be extended to support inner rings per part
                    RasterizePolygonWithHoles(result, partPixelCoords, null);
                }
            }
        }
        
        if (featuresWithHoles > 0 || featuresWithParts > 0)
        {
            TerrainLogger.Info($"Rasterized multipolygons: {featuresWithHoles} features with {totalInnerRings} holes, {featuresWithParts} features with {totalParts} additional parts");
        }
        
        return result;
    }
    
    /// <summary>
    /// Rasterizes a polygon with optional inner rings (holes) using the even-odd fill rule.
    /// The outer ring is filled, then inner rings are subtracted (set to 0).
    /// This properly handles OSM multipolygon relations where inner rings represent holes.
    /// </summary>
    /// <param name="mask">The mask to draw on.</param>
    /// <param name="outerRing">The outer boundary of the polygon.</param>
    /// <param name="innerRings">Optional list of inner rings (holes) to subtract.</param>
    private void RasterizePolygonWithHoles(byte[,] mask, List<Vector2> outerRing, List<List<Vector2>>? innerRings)
    {
        if (outerRing.Count < 3)
            return;
        
        // First, fill the outer ring
        RasterizePolygon(mask, outerRing);
        
        // Then subtract inner rings (holes)
        if (innerRings != null)
        {
            foreach (var innerRing in innerRings)
            {
                if (innerRing.Count >= 3)
                {
                    SubtractPolygon(mask, innerRing);
                }
            }
        }
    }
    
    /// <summary>
    /// Subtracts (clears) a polygon area from the mask.
    /// Used for creating holes in multipolygon features.
    /// </summary>
    private void SubtractPolygon(byte[,] mask, List<Vector2> polygon)
    {
        if (polygon.Count < 3)
            return;
        
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        
        // Find Y bounds
        var minY = Math.Max(0, (int)polygon.Min(p => p.Y));
        var maxY = Math.Min(height - 1, (int)polygon.Max(p => p.Y));
        
        // Scanline fill with value 0 (subtract)
        for (var y = minY; y <= maxY; y++)
        {
            var intersections = new List<float>();
            
            // Find all intersections with this scanline
            for (var i = 0; i < polygon.Count - 1; i++)
            {
                var p1 = polygon[i];
                var p2 = polygon[i + 1];
                
                if ((p1.Y <= y && p2.Y > y) || (p2.Y <= y && p1.Y > y))
                {
                    var t = (y - p1.Y) / (p2.Y - p1.Y);
                    var x = p1.X + t * (p2.X - p1.X);
                    intersections.Add(x);
                }
            }
            
            // Close the polygon
            if (polygon.Count >= 2)
            {
                var p1 = polygon[^1];
                var p2 = polygon[0];
                
                if ((p1.Y <= y && p2.Y > y) || (p2.Y <= y && p1.Y > y))
                {
                    var t = (y - p1.Y) / (p2.Y - p1.Y);
                    var x = p1.X + t * (p2.X - p1.X);
                    intersections.Add(x);
                }
            }
            
            intersections.Sort();
            
            for (var i = 0; i < intersections.Count - 1; i += 2)
            {
                var xStart = Math.Max(0, (int)intersections[i]);
                var xEnd = Math.Min(width - 1, (int)intersections[i + 1]);
                
                for (var x = xStart; x <= xEnd; x++)
                {
                    mask[y, x] = 0; // Subtract (clear) instead of fill
                }
            }
        }
    }
    
    /// <summary>
    /// Rasterizes a single polygon onto the mask using scanline algorithm.
    /// </summary>
    private void RasterizePolygon(byte[,] mask, List<Vector2> polygon)
    {
        if (polygon.Count < 3)
            return;
        
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        
        // Find Y bounds
        var minY = Math.Max(0, (int)polygon.Min(p => p.Y));
        var maxY = Math.Min(height - 1, (int)polygon.Max(p => p.Y));
        
        // Scanline fill
        for (var y = minY; y <= maxY; y++)
        {
            var intersections = new List<float>();
            
            // Find all intersections with this scanline
            for (var i = 0; i < polygon.Count - 1; i++)
            {
                var p1 = polygon[i];
                var p2 = polygon[i + 1];
                
                // Check if edge crosses this scanline
                if ((p1.Y <= y && p2.Y > y) || (p2.Y <= y && p1.Y > y))
                {
                    // Calculate X intersection
                    var t = (y - p1.Y) / (p2.Y - p1.Y);
                    var x = p1.X + t * (p2.X - p1.X);
                    intersections.Add(x);
                }
            }
            
            // Close the polygon (last to first)
            if (polygon.Count >= 2)
            {
                var p1 = polygon[^1];
                var p2 = polygon[0];
                
                if ((p1.Y <= y && p2.Y > y) || (p2.Y <= y && p1.Y > y))
                {
                    var t = (y - p1.Y) / (p2.Y - p1.Y);
                    var x = p1.X + t * (p2.X - p1.X);
                    intersections.Add(x);
                }
            }
            
            // Sort intersections and fill between pairs
            intersections.Sort();
            
            for (var i = 0; i < intersections.Count - 1; i += 2)
            {
                var xStart = Math.Max(0, (int)intersections[i]);
                var xEnd = Math.Min(width - 1, (int)intersections[i + 1]);
                
                for (var x = xStart; x <= xEnd; x++)
                {
                    mask[y, x] = 255;
                }
            }
        }
    }
    
    /// <summary>
    /// Rasterizes line features to a binary mask with a specified width.
    /// NOTE: For roads, this rasterizes from ORIGINAL OSM geometry (control points).
    /// Use RasterizeSplinesFromLayerMap() instead for consistency with spline interpolation.
    /// </summary>
    public byte[,] RasterizeLinesToLayerMap(
        List<OsmFeature> lineFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float lineWidthPixels)
    {
        var result = new byte[terrainSize, terrainSize];
        var halfWidth = lineWidthPixels / 2f;
        
        foreach (var feature in lineFeatures.Where(f => f.GeometryType == OsmGeometryType.LineString))
        {
            var pixelCoords = TransformToPixelCoordinates(feature.Coordinates, bbox, terrainSize);
            var croppedCoords = CropLineToTerrain(pixelCoords, terrainSize);
            
            RasterizeLine(result, croppedCoords, halfWidth);
        }
        
        return result;
    }
    
    /// <summary>
    /// Rasterizes road splines to a binary layer map.
    /// This ensures the layer map matches the interpolated spline path used for elevation smoothing.
    /// Use this instead of RasterizeLinesToLayerMap() for road materials with pre-built splines.
    /// </summary>
    /// <param name="splines">Road splines (in meter coordinates)</param>
    /// <param name="terrainSize">Size of terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="roadSurfaceWidthMeters">Road surface width in meters</param>
    /// <returns>Binary mask where 255 = road surface, 0 = no road</returns>
    public byte[,] RasterizeSplinesToLayerMap(
        List<RoadSpline> splines,
        int terrainSize,
        float metersPerPixel,
        float roadSurfaceWidthMeters)
    {
        var result = new byte[terrainSize, terrainSize];
        var halfWidthMeters = roadSurfaceWidthMeters / 2f;
        
        // Fine sampling interval for accurate curve representation
        var sampleIntervalMeters = Math.Min(0.25f, metersPerPixel * 0.5f);
        
        foreach (var spline in splines)
        {
            // Sample the spline at fine intervals (using the INTERPOLATED path)
            var samples = spline.SampleByDistance(sampleIntervalMeters);
            
            if (samples.Count < 2)
                continue;
            
            // Rasterize quads between consecutive samples
            for (int i = 0; i < samples.Count - 1; i++)
            {
                var s1 = samples[i];
                var s2 = samples[i + 1];
                
                // Calculate quad corners in meters, then convert to image-space pixels
                var left1 = s1.Position - s1.Normal * halfWidthMeters;
                var right1 = s1.Position + s1.Normal * halfWidthMeters;
                var left2 = s2.Position - s2.Normal * halfWidthMeters;
                var right2 = s2.Position + s2.Normal * halfWidthMeters;
                
                // Convert from terrain-space meters to image-space pixels
                // Terrain-space: Y=0 at bottom, Image-space: Y=0 at top
                var corners = new Vector2[]
                {
                    new(left1.X / metersPerPixel, terrainSize - 1 - left1.Y / metersPerPixel),
                    new(right1.X / metersPerPixel, terrainSize - 1 - right1.Y / metersPerPixel),
                    new(right2.X / metersPerPixel, terrainSize - 1 - right2.Y / metersPerPixel),
                    new(left2.X / metersPerPixel, terrainSize - 1 - left2.Y / metersPerPixel)
                };
                
                FillConvexPolygon(result, corners, terrainSize, terrainSize);
            }
        }
        
        TerrainLogger.Info($"Rasterized {splines.Count} splines to layer map (width={roadSurfaceWidthMeters}m, samples every {sampleIntervalMeters:F2}m)");
        return result;
    }
    
    /// <summary>
    /// Fills a convex polygon using scanline rasterization.
    /// </summary>
    private void FillConvexPolygon(byte[,] mask, Vector2[] vertices, int width, int height)
    {
        if (vertices.Length < 3)
            return;
        
        // Find bounding box
        float minY = float.MaxValue, maxY = float.MinValue;
        
        foreach (var v in vertices)
        {
            minY = MathF.Min(minY, v.Y);
            maxY = MathF.Max(maxY, v.Y);
        }
        
        var startY = Math.Max(0, (int)MathF.Floor(minY));
        var endY = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
        
        // For each scanline
        for (var y = startY; y <= endY; y++)
        {
            var scanY = y + 0.5f;
            var intersections = new List<float>();
            
            // Find intersection points with polygon edges
            for (var i = 0; i < vertices.Length; i++)
            {
                var v1 = vertices[i];
                var v2 = vertices[(i + 1) % vertices.Length];
                
                if ((v1.Y <= scanY && v2.Y > scanY) || (v2.Y <= scanY && v1.Y > scanY))
                {
                    var t = (scanY - v1.Y) / (v2.Y - v1.Y);
                    var xIntersect = v1.X + t * (v2.X - v1.X);
                    intersections.Add(xIntersect);
                }
            }
            
            // Sort intersections and fill between pairs
            intersections.Sort();
            
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var xStart = Math.Max(0, (int)MathF.Floor(intersections[i]));
                var xEnd = Math.Min(width - 1, (int)MathF.Ceiling(intersections[i + 1]));
                
                for (var x = xStart; x <= xEnd; x++)
                {
                    mask[y, x] = 255;
                }
            }
        }
    }
    
    /// <summary>
    /// Rasterizes a line with given half-width onto the mask.
    /// </summary>
    private void RasterizeLine(byte[,] mask, List<Vector2> line, float halfWidth)
    {
        if (line.Count < 2)
            return;
        
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        
        for (var i = 0; i < line.Count - 1; i++)
        {
            var p1 = line[i];
            var p2 = line[i + 1];
            
            // Draw thick line using perpendicular offset
            var dir = Vector2.Normalize(p2 - p1);
            var perp = new Vector2(-dir.Y, dir.X);
            
            // Sample along the segment
            var segmentLength = Vector2.Distance(p1, p2);
            var samples = Math.Max(1, (int)(segmentLength * 2));
            
            for (var s = 0; s <= samples; s++)
            {
                var t = s / (float)samples;
                var center = Vector2.Lerp(p1, p2, t);
                
                // Fill perpendicular to line direction
                for (var w = -halfWidth; w <= halfWidth; w += 0.5f)
                {
                    var point = center + perp * w;
                    var px = (int)point.X;
                    var py = (int)point.Y;
                    
                    if (px >= 0 && px < width && py >= 0 && py < height)
                    {
                        mask[py, px] = 255;
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Converts line features directly to RoadSpline objects.
    /// This bypasses skeleton extraction when using OSM data.
    /// Uses terrain-space coordinates (bottom-left origin) since splines are used
    /// with the heightmap array which is in BeamNG coordinate system.
    /// 
    /// IMPORTANT: The returned splines are in METER coordinates (not pixels).
    /// This is required because RoadSpline.SampleByDistance() and RoadSmoothingService
    /// expect all positions to be in meters.
    /// 
    /// OSM ways are connected at shared endpoints before spline creation to avoid
    /// gaps that would cause elevation discontinuities.
    /// </summary>
    /// <param name="lineFeatures">OSM line features to convert.</param>
    /// <param name="bbox">Geographic bounding box (WGS84).</param>
    /// <param name="terrainSize">Size of terrain in pixels.</param>
    /// <param name="metersPerPixel">Scale factor for meters per pixel.</param>
    /// <param name="interpolationType">How to interpolate between control points (SmoothInterpolated or LinearControlPoints).</param>
    /// <param name="minPathLengthMeters">Minimum path length to include (default 1m). Paths shorter than this are skipped.</param>
    /// <param name="duplicatePointToleranceMeters">Tolerance for removing duplicate consecutive points (default 0.01m = 1cm).</param>
    /// <param name="endpointJoinToleranceMeters">Tolerance for joining endpoints of adjacent ways (default 1m).</param>
    /// <returns>List of road splines in meter coordinates.</returns>
    public List<RoadSpline> ConvertLinesToSplines(
        List<OsmFeature> lineFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated,
        float minPathLengthMeters = 1.0f,
        float duplicatePointToleranceMeters = 0.01f,
        float endpointJoinToleranceMeters = 1.0f)
    {
        var splines = new List<RoadSpline>();
        int skippedZeroLength = 0;
        int skippedTooFewPoints = 0;
        
        // Step 1: Transform all features to meter coordinates first
        var allPaths = new List<List<Vector2>>();
        
        foreach (var feature in lineFeatures.Where(f => f.GeometryType == OsmGeometryType.LineString))
        {
            // Transform to terrain-space coordinates (bottom-left origin for heightmap)
            var terrainCoords = TransformToTerrainCoordinates(feature.Coordinates, bbox, terrainSize);
            
            // Crop to terrain bounds
            var croppedCoords = CropLineToTerrain(terrainCoords, terrainSize);
            
            if (croppedCoords.Count < 2)
            {
                skippedTooFewPoints++;
                continue;
            }
            
            // Convert from pixel coordinates to meter coordinates
            var meterCoords = croppedCoords
                .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
                .ToList();
            
            // Remove duplicate consecutive points
            var uniqueCoords = RemoveDuplicateConsecutivePoints(meterCoords, duplicatePointToleranceMeters);
            
            if (uniqueCoords.Count >= 2)
            {
                allPaths.Add(uniqueCoords);
            }
            else
            {
                skippedTooFewPoints++;
            }
        }
        
        TerrainLogger.Info($"Prepared {allPaths.Count} paths from {lineFeatures.Count} OSM line features");
        
        // Step 2: Connect paths that share endpoints (OSM ways are often split at intersections)
        var connectedPaths = ConnectAdjacentPaths(allPaths, endpointJoinToleranceMeters);
        
        TerrainLogger.Info($"After connecting adjacent paths: {connectedPaths.Count} connected paths (was {allPaths.Count})");
        
        // Step 3: Create splines from connected paths
        foreach (var path in connectedPaths)
        {
            // Remove any duplicate points that might have been introduced by joining
            var cleanPath = RemoveDuplicateConsecutivePoints(path, duplicatePointToleranceMeters);
            
            if (cleanPath.Count < 2)
            {
                skippedTooFewPoints++;
                continue;
            }
            
            // Calculate total path length and skip if too short
            float totalLength = CalculatePathLength(cleanPath);
            if (totalLength < minPathLengthMeters)
            {
                skippedZeroLength++;
                continue;
            }
            
            try
            {
                var spline = new RoadSpline(cleanPath, interpolationType);
                splines.Add(spline);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to create spline from connected path: {ex.Message}");
            }
        }
        
        TerrainLogger.Info($"Created {splines.Count} splines from connected paths");
        if (skippedTooFewPoints > 0)
            TerrainLogger.Info($"  Skipped {skippedTooFewPoints} paths with too few points");
        if (skippedZeroLength > 0)
            TerrainLogger.Info($"  Skipped {skippedZeroLength} paths shorter than {minPathLengthMeters:F1}m");
        TerrainLogger.Info($"  Coordinate system: meters (metersPerPixel={metersPerPixel})");
        return splines;
    }
    
    /// <summary>
    /// Connects paths that share endpoints to form longer continuous paths.
    /// This is critical for OSM data where roads are split at intersections.
    /// Uses an iterative approach similar to the skeleton-based path joining.
    /// </summary>
    /// <param name="paths">List of individual paths to connect.</param>
    /// <param name="tolerance">Maximum distance between endpoints to consider them connected.</param>
    /// <returns>List of connected paths.</returns>
    private List<List<Vector2>> ConnectAdjacentPaths(List<List<Vector2>> paths, float tolerance)
    {
        if (paths.Count <= 1)
            return paths;
        
        var result = paths.Select(p => new List<Vector2>(p)).ToList();
        var toleranceSquared = tolerance * tolerance;
        bool didMerge;
        int iterations = 0;
        int totalMerges = 0;
        
        do
        {
            didMerge = false;
            iterations++;
            
            for (int i = 0; i < result.Count && !didMerge; i++)
            {
                var path1 = result[i];
                if (path1.Count < 2) continue;
                
                for (int j = i + 1; j < result.Count; j++)
                {
                    var path2 = result[j];
                    if (path2.Count < 2) continue;
                    
                    var p1Start = path1[0];
                    var p1End = path1[^1];
                    var p2Start = path2[0];
                    var p2End = path2[^1];
                    
                    // Check all 4 endpoint combinations
                    var dEndToStart = Vector2.DistanceSquared(p1End, p2Start);
                    var dEndToEnd = Vector2.DistanceSquared(p1End, p2End);
                    var dStartToEnd = Vector2.DistanceSquared(p1Start, p2End);
                    var dStartToStart = Vector2.DistanceSquared(p1Start, p2Start);
                    
                    if (dEndToStart <= toleranceSquared)
                    {
                        // path1.end connects to path2.start -> append path2 to path1
                        path1.AddRange(path2.Skip(1));
                        result.RemoveAt(j);
                        didMerge = true;
                        totalMerges++;
                        break;
                    }
                    
                    if (dEndToEnd <= toleranceSquared)
                    {
                        // path1.end connects to path2.end -> append reversed path2 to path1
                        for (int k = path2.Count - 2; k >= 0; k--)
                            path1.Add(path2[k]);
                        result.RemoveAt(j);
                        didMerge = true;
                        totalMerges++;
                        break;
                    }
                    
                    if (dStartToEnd <= toleranceSquared)
                    {
                        // path1.start connects to path2.end -> prepend path2 to path1
                        var merged = new List<Vector2>(path2);
                        merged.AddRange(path1.Skip(1));
                        result[i] = merged;
                        result.RemoveAt(j);
                        didMerge = true;
                        totalMerges++;
                        break;
                    }
                    
                    if (dStartToStart <= toleranceSquared)
                    {
                        // path1.start connects to path2.start -> prepend reversed path2 to path1
                        var merged = new List<Vector2>();
                        for (int k = path2.Count - 1; k >= 0; k--)
                            merged.Add(path2[k]);
                        merged.AddRange(path1.Skip(1));
                        result[i] = merged;
                        result.RemoveAt(j);
                        didMerge = true;
                        totalMerges++;
                        break;
                    }
                }
            }
        } while (didMerge && iterations < 1000); // Safety limit
        
        TerrainLogger.Info($"  Path joining: {totalMerges} merges in {iterations} iterations, tolerance={tolerance:F2}m");
        
        return result;
    }
    
    /// <summary>
    /// Removes consecutive duplicate points from a path.
    /// </summary>
    /// <param name="points">List of points to process.</param>
    /// <param name="tolerance">Minimum distance between points to keep them separate.</param>
    /// <returns>List with consecutive duplicates removed.</returns>
    private static List<Vector2> RemoveDuplicateConsecutivePoints(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;
        
        var result = new List<Vector2> { points[0] };
        var toleranceSquared = tolerance * tolerance;
        
        for (int i = 1; i < points.Count; i++)
        {
            var distSquared = Vector2.DistanceSquared(result[^1], points[i]);
            if (distSquared > toleranceSquared)
            {
                result.Add(points[i]);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Calculates the total length of a path.
    /// </summary>
    private static float CalculatePathLength(List<Vector2> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Vector2.Distance(points[i - 1], points[i]);
        }
        return length;
    }
    
    /// <summary>
    /// Detects roundabouts in the OSM query result using the RoundaboutDetector.
    /// This is a convenience method that creates a detector and runs detection.
    /// </summary>
    /// <param name="queryResult">The OSM query result to analyze.</param>
    /// <returns>List of detected roundabouts with merged ring geometry.</returns>
    public List<OsmRoundabout> DetectRoundabouts(OsmQueryResult queryResult)
    {
        var detector = new RoundaboutDetector();
        return detector.DetectRoundabouts(queryResult);
    }
    
    /// <summary>
    /// Converts line features to splines with roundabout detection and handling.
    /// This method:
    /// 1. Detects roundabouts from junction=roundabout tags
    /// 2. TRIMS connecting roads that overlap with roundabout rings (CRITICAL!)
    /// 3. Excludes roundabout segments from normal road processing
    /// 4. Creates closed-loop splines for roundabout rings
    /// 5. Creates normal splines for regular (trimmed) roads
    /// 
    /// Use this instead of ConvertLinesToSplines when you want roundabouts to be
    /// properly handled as closed loops rather than fragmented segments.
    /// </summary>
    /// <param name="lineFeatures">OSM line features to convert.</param>
    /// <param name="fullQueryResult">The complete OSM query result (needed for roundabout detection).</param>
    /// <param name="bbox">Geographic bounding box (WGS84).</param>
    /// <param name="terrainSize">Size of terrain in pixels.</param>
    /// <param name="metersPerPixel">Scale factor for meters per pixel.</param>
    /// <param name="interpolationType">How to interpolate between control points.</param>
    /// <param name="detectedRoundabouts">Output: List of detected roundabouts.</param>
    /// <param name="roundaboutWayIds">Output: Set of OSM way IDs that are part of roundabouts.</param>
    /// <param name="enableRoadTrimming">When true, trims roads that overlap with roundabouts (recommended).</param>
    /// <param name="overlapToleranceMeters">Tolerance for determining if a road point is on the roundabout ring.</param>
    /// <param name="minPathLengthMeters">Minimum path length to include.</param>
    /// <param name="duplicatePointToleranceMeters">Tolerance for removing duplicate points.</param>
    /// <param name="endpointJoinToleranceMeters">Tolerance for joining endpoints.</param>
    /// <returns>List of road splines including both regular roads and roundabout rings.</returns>
    public List<RoadSpline> ConvertLinesToSplinesWithRoundabouts(
        List<OsmFeature> lineFeatures,
        OsmQueryResult fullQueryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType,
        out List<OsmRoundabout> detectedRoundabouts,
        out HashSet<long> roundaboutWayIds,
        bool enableRoadTrimming = true,
        float overlapToleranceMeters = 2.0f,
        float minPathLengthMeters = 1.0f,
        float duplicatePointToleranceMeters = 0.01f,
        float endpointJoinToleranceMeters = 1.0f)
    {
        // Use the full overload and discard the roundabout processing result
        return ConvertLinesToSplinesWithRoundabouts(
            lineFeatures,
            fullQueryResult,
            bbox,
            terrainSize,
            metersPerPixel,
            interpolationType,
            out detectedRoundabouts,
            out roundaboutWayIds,
            out _,  // Discard roundaboutProcessingResult
            enableRoadTrimming,
            overlapToleranceMeters,
            minPathLengthMeters,
            duplicatePointToleranceMeters,
            endpointJoinToleranceMeters);
    }
    
    /// <summary>
    /// Converts line features to splines with roundabout detection and handling.
    /// This overload provides full roundabout processing result for junction detection.
    /// </summary>
    /// <param name="lineFeatures">OSM line features to convert.</param>
    /// <param name="fullQueryResult">The complete OSM query result (needed for roundabout detection).</param>
    /// <param name="bbox">Geographic bounding box (WGS84).</param>
    /// <param name="terrainSize">Size of terrain in pixels.</param>
    /// <param name="metersPerPixel">Scale factor for meters per pixel.</param>
    /// <param name="interpolationType">How to interpolate between control points.</param>
    /// <param name="detectedRoundabouts">Output: List of detected roundabouts.</param>
    /// <param name="roundaboutWayIds">Output: Set of OSM way IDs that are part of roundabouts.</param>
    /// <param name="roundaboutProcessingResult">Output: Full roundabout processing result for junction detection.</param>
    /// <param name="enableRoadTrimming">When true, trims roads that overlap with roundabouts (recommended).</param>
    /// <param name="overlapToleranceMeters">Tolerance for determining if a road point is on the roundabout ring.</param>
    /// <param name="minPathLengthMeters">Minimum path length to include.</param>
    /// <param name="duplicatePointToleranceMeters">Tolerance for removing duplicate points.</param>
    /// <param name="endpointJoinToleranceMeters">Tolerance for joining endpoints.</param>
    /// <param name="debugOutputPath">Optional path to save a debug image showing roundabout processing.</param>
    /// <returns>List of road splines including both regular roads and roundabout rings.</returns>
    public List<RoadSpline> ConvertLinesToSplinesWithRoundabouts(
        List<OsmFeature> lineFeatures,
        OsmQueryResult fullQueryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType,
        out List<OsmRoundabout> detectedRoundabouts,
        out HashSet<long> roundaboutWayIds,
        out RoundaboutMerger.RoundaboutProcessingResult roundaboutProcessingResult,
        bool enableRoadTrimming = true,
        float overlapToleranceMeters = 2.0f,
        float minPathLengthMeters = 1.0f,
        float duplicatePointToleranceMeters = 0.01f,
        float endpointJoinToleranceMeters = 1.0f,
        string? debugOutputPath = null)
    {
        // Build a set of feature IDs that belong to this material
        // This is CRITICAL for ensuring roundabout splines are only created once
        var materialFeatureIds = lineFeatures.Select(f => f.Id).ToHashSet();
        
        // Step 1: Detect ALL roundabouts in the full query result
        var detector = new RoundaboutDetector();
        var allDetectedRoundabouts = detector.DetectRoundabouts(fullQueryResult);
        
        // Step 2: Filter to only roundabouts whose ways overlap with THIS material's features
        // This prevents creating duplicate roundabout splines when multiple materials are processed
        detectedRoundabouts = allDetectedRoundabouts
            .Where(r => r.WayIds.Any(wayId => materialFeatureIds.Contains(wayId)))
            .ToList();
        
        var skippedRoundabouts = allDetectedRoundabouts.Count - detectedRoundabouts.Count;
        
        // Collect way IDs only from roundabouts that belong to this material
        var wayIdSet = new HashSet<long>();
        foreach (var roundabout in detectedRoundabouts)
        {
            foreach (var wayId in roundabout.WayIds)
            {
                wayIdSet.Add(wayId);
            }
        }
        roundaboutWayIds = wayIdSet;
        
        TerrainLogger.Info($"ConvertLinesToSplinesWithRoundabouts: Detected {allDetectedRoundabouts.Count} roundabout(s) total, " +
            $"{detectedRoundabouts.Count} belong to this material ({wayIdSet.Count} way segments)" +
            (skippedRoundabouts > 0 ? $", skipped {skippedRoundabouts} roundabout(s) belonging to other materials" : ""));
        
        // Capture pre-trim snapshot for debug visualization (if debug output requested)
        RoundaboutDebugImageExporter.PreTrimSnapshot? preTrimSnapshot = null;
        if (!string.IsNullOrEmpty(debugOutputPath) && detectedRoundabouts.Count > 0)
        {
            preTrimSnapshot = RoundaboutDebugImageExporter.CapturePreTrimSnapshot(lineFeatures, wayIdSet);
        }
        
        // Step 2: CRITICAL - Trim connecting roads that overlap with roundabouts
        // This removes the quirky high-angle segments that follow the roundabout ring
        var deletedFeatureIds = new HashSet<long>();
        if (enableRoadTrimming && detectedRoundabouts.Count > 0)
        {
            var trimmer = new ConnectingRoadTrimmer
            {
                OverlapToleranceMeters = overlapToleranceMeters
            };
            deletedFeatureIds = trimmer.TrimConnectingRoads(detectedRoundabouts, lineFeatures);
            
            // Update pre-trim snapshot with deleted feature IDs
            if (preTrimSnapshot != null)
            {
                preTrimSnapshot.DeletedFeatureIds = deletedFeatureIds;
            }
        }
        
        // Step 3: Create roundabout ring splines using RoundaboutMerger
        var merger = new RoundaboutMerger(this);
        roundaboutProcessingResult = merger.ProcessRoundabouts(
            detectedRoundabouts,
            bbox,
            terrainSize,
            metersPerPixel,
            interpolationType);
        
        var roundaboutSplines = roundaboutProcessingResult.RoundaboutSplines;
        
        // Step 4: Filter out roundabout ways AND deleted features from regular processing
        var regularFeatures = lineFeatures
            .Where(f => !wayIdSet.Contains(f.Id))
            .Where(f => !deletedFeatureIds.Contains(f.Id))
            .Where(f => f.Coordinates.Count >= 2) // Ensure still valid after trimming
            .ToList();
        
        TerrainLogger.Info($"  Regular roads after excluding roundabouts and deleted: {regularFeatures.Count} " +
            $"(excluded {lineFeatures.Count - regularFeatures.Count - deletedFeatureIds.Count} roundabout ways, " +
            $"{deletedFeatureIds.Count} deleted roads)");
        
        // Step 5: Process regular (now trimmed) roads
        var regularSplines = ConvertLinesToSplines(
            regularFeatures,
            bbox,
            terrainSize,
            metersPerPixel,
            interpolationType,
            minPathLengthMeters,
            duplicatePointToleranceMeters,
            endpointJoinToleranceMeters);
        
        // Step 6: Combine results
        var allSplines = new List<RoadSpline>();
        allSplines.AddRange(roundaboutSplines);
        allSplines.AddRange(regularSplines);
        
        TerrainLogger.Info($"ConvertLinesToSplinesWithRoundabouts: Total {allSplines.Count} splines " +
            $"({roundaboutSplines.Count} roundabout rings + {regularSplines.Count} regular roads)");
        
        // Step 7: Export debug image if requested and roundabouts were detected
        if (!string.IsNullOrEmpty(debugOutputPath) && detectedRoundabouts.Count > 0 && preTrimSnapshot != null)
        {
            try
            {
                var debugExporter = new RoundaboutDebugImageExporter();
                debugExporter.ExportDebugImage(
                    detectedRoundabouts,
                    regularFeatures,
                    preTrimSnapshot,
                    roundaboutSplines,
                    regularSplines,
                    bbox,
                    terrainSize,
                    metersPerPixel,
                    debugOutputPath,
                    _transformer);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export roundabout debug image: {ex.Message}");
            }
        }
        
        return allSplines;
    }
    
    /// <summary>
    /// Retrieves full OsmFeature objects by their IDs from a query result.
    /// </summary>
    public List<OsmFeature> GetFeaturesByIds(OsmQueryResult queryResult, IEnumerable<long> featureIds)
    {
        var idSet = featureIds.ToHashSet();
        return queryResult.Features.Where(f => idSet.Contains(f.Id)).ToList();
    }
    
    /// <summary>
    /// Retrieves full OsmFeature objects from selections.
    /// </summary>
    public List<OsmFeature> GetFeaturesFromSelections(
        OsmQueryResult queryResult, 
        IEnumerable<OsmFeatureSelection> selections)
    {
        return GetFeaturesByIds(queryResult, selections.Select(s => s.FeatureId));
    }
    
    /// <summary>
    /// Exports a debug image showing OSM splines overlaid on the terrain.
    /// This is useful for verifying coordinate transformation correctness.
    /// 
    /// The debug image shows:
    /// - Black background
    /// - White lines for each spline (control points connected)
    /// - Green dots at spline control points
    /// - Red dots at spline start points
    /// - Blue dots at spline end points
    /// </summary>
    /// <param name="splines">The splines to visualize (in meter coordinates)</param>
    /// <param name="terrainSize">Size of the terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="outputPath">Path to save the debug image</param>
    public void ExportOsmSplineDebugImage(
        List<RoadSpline> splines,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(0, 0, 0, 255));
        
        foreach (var spline in splines)
        {
            var controlPoints = spline.ControlPoints;
            
            // Draw lines between control points
            for (int i = 0; i < controlPoints.Count - 1; i++)
            {
                // Convert from meters back to pixels for drawing
                var p1 = controlPoints[i];
                var p2 = controlPoints[i + 1];
                
                int x1 = (int)(p1.X / metersPerPixel);
                int y1 = (int)(p1.Y / metersPerPixel);
                int x2 = (int)(p2.X / metersPerPixel);
                int y2 = (int)(p2.Y / metersPerPixel);
                
                // Flip Y for image coordinates (image Y=0 is top)
                y1 = terrainSize - 1 - y1;
                y2 = terrainSize - 1 - y2;
                
                DrawLine(image, x1, y1, x2, y2, new Rgba32(255, 255, 255, 255));
            }
            
            // Draw control points
            for (int i = 0; i < controlPoints.Count; i++)
            {
                var p = controlPoints[i];
                int x = (int)(p.X / metersPerPixel);
                int y = (int)(p.Y / metersPerPixel);
                
                // Flip Y for image coordinates
                y = terrainSize - 1 - y;
                
                // Choose color based on position
                Rgba32 color;
                if (i == 0)
                    color = new Rgba32(255, 0, 0, 255); // Red for start
                else if (i == controlPoints.Count - 1)
                    color = new Rgba32(0, 0, 255, 255); // Blue for end
                else
                    color = new Rgba32(0, 255, 0, 255); // Green for intermediate
                
                DrawPoint(image, x, y, color, 3);
            }
        }
        
        // Add text overlay with spline count
        // (ImageSharp doesn't have easy text drawing, so we'll just add info to the log)
        TerrainLogger.Info($"OSM Spline Debug Image: {splines.Count} splines, terrain {terrainSize}x{terrainSize}");
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        
        image.SaveAsPng(outputPath);
        TerrainLogger.Info($"Exported OSM spline debug image: {outputPath}");
    }
    
    /// <summary>
    /// Draws a line on an RGBA image using Bresenham's algorithm.
    /// </summary>
    private static void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        
        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            
            if (x0 == x1 && y0 == y1) break;
            
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
    
    /// <summary>
    /// Draws a point (filled circle) on an RGBA image.
    /// </summary>
    private static void DrawPoint(Image<Rgba32> img, int cx, int cy, Rgba32 color, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy <= radius * radius)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < img.Width && y >= 0 && y < img.Height)
                        img[x, y] = color;
                }
            }
        }
    }
}
