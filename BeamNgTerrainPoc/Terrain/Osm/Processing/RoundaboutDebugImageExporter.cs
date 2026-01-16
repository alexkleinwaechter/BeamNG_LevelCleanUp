using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Exports debug images visualizing roundabout detection and road trimming.
/// 
/// The debug image shows:
/// - Original road paths in gray (semi-transparent) for comparison
/// - Roundabout rings in yellow
/// - Connection/trim points marked with circles (white outline, green fill)
/// - Trimmed/deleted road portions in red
/// - Connecting roads (after trimming) in cyan
/// - Roundabout centers marked with crosshairs
/// </summary>
public class RoundaboutDebugImageExporter
{
    /// <summary>
    /// Data captured before trimming for comparison visualization.
    /// </summary>
    public class PreTrimSnapshot
    {
        /// <summary>
        /// Original road coordinates before trimming, keyed by feature ID.
        /// </summary>
        public Dictionary<long, List<GeoCoordinate>> OriginalRoadCoordinates { get; set; } = new();
        
        /// <summary>
        /// Feature IDs that were completely deleted during trimming.
        /// </summary>
        public HashSet<long> DeletedFeatureIds { get; set; } = [];
    }
    
    /// <summary>
    /// Captures a snapshot of road coordinates before trimming.
    /// Call this before calling ConnectingRoadTrimmer.TrimConnectingRoads().
    /// </summary>
    /// <param name="lineFeatures">The OSM line features before trimming.</param>
    /// <param name="roundaboutWayIds">Set of way IDs that are part of roundabouts (to exclude from snapshot).</param>
    /// <returns>Snapshot of original coordinates for later comparison.</returns>
    public static PreTrimSnapshot CapturePreTrimSnapshot(
        List<OsmFeature> lineFeatures,
        HashSet<long> roundaboutWayIds)
    {
        var snapshot = new PreTrimSnapshot();
        
        foreach (var feature in lineFeatures)
        {
            // Skip roundabout ways themselves
            if (roundaboutWayIds.Contains(feature.Id))
                continue;
            
            // Skip non-highway features
            if (feature.Category != "highway")
                continue;
            
            if (feature.GeometryType != OsmGeometryType.LineString)
                continue;
            
            // Make a deep copy of the coordinates
            snapshot.OriginalRoadCoordinates[feature.Id] = feature.Coordinates
                .Select(c => new GeoCoordinate(c.Longitude, c.Latitude))
                .ToList();
        }
        
        return snapshot;
    }
    
    /// <summary>
    /// Exports a debug image showing roundabout detection and road trimming results.
    /// </summary>
    /// <param name="roundabouts">Detected roundabouts.</param>
    /// <param name="trimmedFeatures">Road features after trimming.</param>
    /// <param name="preTrimSnapshot">Snapshot of original roads before trimming.</param>
    /// <param name="roundaboutSplines">Roundabout ring splines (in meter coordinates).</param>
    /// <param name="regularSplines">Regular road splines after processing (in meter coordinates).</param>
    /// <param name="bbox">Geographic bounding box.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="outputPath">Path to save the debug image.</param>
    /// <param name="coordinateTransformer">Optional coordinate transformer for accurate projection.</param>
    public void ExportDebugImage(
        List<OsmRoundabout> roundabouts,
        List<OsmFeature> trimmedFeatures,
        PreTrimSnapshot preTrimSnapshot,
        List<RoadSpline> roundaboutSplines,
        List<RoadSpline> regularSplines,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        string outputPath,
        GeoCoordinateTransformer? coordinateTransformer = null)
    {
        if (roundabouts.Count == 0)
        {
            TerrainLogger.Info("RoundaboutDebugImageExporter: No roundabouts to visualize, skipping debug image");
            return;
        }
        
        TerrainLogger.Info($"RoundaboutDebugImageExporter: Creating debug image for {roundabouts.Count} roundabout(s)...");
        
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(20, 20, 20, 255));
        
        // Create a helper for coordinate transformation
        var transformHelper = new TransformHelper(bbox, terrainSize, coordinateTransformer);
        
        // Layer 1: Original road paths in gray (semi-transparent) - BEFORE trimming
        DrawOriginalRoads(image, preTrimSnapshot, transformHelper, terrainSize);
        
        // Layer 2: Trimmed/deleted portions in red
        DrawTrimmedPortions(image, preTrimSnapshot, trimmedFeatures, transformHelper, terrainSize);
        
        // Layer 3: Connecting roads after trimming in cyan
        DrawTrimmedRoads(image, regularSplines, metersPerPixel, terrainSize);
        
        // Layer 4: Roundabout rings in yellow
        DrawRoundaboutRings(image, roundaboutSplines, metersPerPixel, terrainSize);
        
        // Layer 5: Connection points with circles
        DrawConnectionPoints(image, roundabouts, transformHelper, terrainSize);
        
        // Layer 6: Roundabout centers with crosshairs
        DrawRoundaboutCenters(image, roundabouts, transformHelper, terrainSize);
        
        // Save the image
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        
        image.SaveAsPng(outputPath);
        
        TerrainLogger.Info($"RoundaboutDebugImageExporter: Saved debug image to {outputPath}");
        TerrainLogger.Info($"  - {roundabouts.Count} roundabout ring(s) (yellow)");
        TerrainLogger.Info($"  - {roundabouts.Sum(r => r.Connections.Count)} connection point(s) (green circles)");
        TerrainLogger.Info($"  - {preTrimSnapshot.DeletedFeatureIds.Count} completely deleted road(s) (red)");
    }
    
    /// <summary>
    /// Exports a simplified debug image showing only roundabouts and their connections.
    /// Use this when pre-trim snapshot is not available.
    /// </summary>
    public void ExportSimplifiedDebugImage(
        List<OsmRoundabout> roundabouts,
        List<RoadSpline> roundaboutSplines,
        List<RoadSpline> regularSplines,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        if (roundabouts.Count == 0)
        {
            TerrainLogger.Info("RoundaboutDebugImageExporter: No roundabouts to visualize, skipping debug image");
            return;
        }
        
        TerrainLogger.Info($"RoundaboutDebugImageExporter: Creating simplified debug image for {roundabouts.Count} roundabout(s)...");
        
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(20, 20, 20, 255));
        
        // Layer 1: Regular roads in cyan
        DrawTrimmedRoads(image, regularSplines, metersPerPixel, terrainSize);
        
        // Layer 2: Roundabout rings in yellow
        DrawRoundaboutRings(image, roundaboutSplines, metersPerPixel, terrainSize);
        
        // Save the image
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        
        image.SaveAsPng(outputPath);
        
        TerrainLogger.Info($"RoundaboutDebugImageExporter: Saved simplified debug image to {outputPath}");
    }
    
    #region Drawing Methods
    
    /// <summary>
    /// Draws original roads before trimming in semi-transparent gray.
    /// </summary>
    private void DrawOriginalRoads(
        Image<Rgba32> image,
        PreTrimSnapshot snapshot,
        TransformHelper transform,
        int terrainSize)
    {
        var grayColor = new Rgba32(128, 128, 128, 100); // Semi-transparent gray
        
        foreach (var (featureId, coords) in snapshot.OriginalRoadCoordinates)
        {
            if (coords.Count < 2)
                continue;
            
            // Transform to pixel coordinates
            var pixelCoords = coords
                .Select(c => transform.ToImagePixel(c))
                .ToList();
            
            // Draw line segments
            for (int i = 0; i < pixelCoords.Count - 1; i++)
            {
                var p1 = pixelCoords[i];
                var p2 = pixelCoords[i + 1];
                
                DrawLine(image, 
                    (int)p1.X, terrainSize - 1 - (int)p1.Y,
                    (int)p2.X, terrainSize - 1 - (int)p2.Y,
                    grayColor);
            }
        }
    }
    
    /// <summary>
    /// Draws trimmed/deleted road portions in red.
    /// </summary>
    private void DrawTrimmedPortions(
        Image<Rgba32> image,
        PreTrimSnapshot snapshot,
        List<OsmFeature> trimmedFeatures,
        TransformHelper transform,
        int terrainSize)
    {
        var redColor = new Rgba32(255, 50, 50, 200); // Bright red
        
        // Build lookup of current coordinates
        var currentCoords = new Dictionary<long, HashSet<(double lon, double lat)>>();
        foreach (var feature in trimmedFeatures)
        {
            if (!snapshot.OriginalRoadCoordinates.ContainsKey(feature.Id))
                continue;
            
            var coordSet = new HashSet<(double lon, double lat)>();
            foreach (var c in feature.Coordinates)
            {
                coordSet.Add((Math.Round(c.Longitude, 7), Math.Round(c.Latitude, 7)));
            }
            currentCoords[feature.Id] = coordSet;
        }
        
        // Find and draw removed portions
        foreach (var (featureId, originalCoords) in snapshot.OriginalRoadCoordinates)
        {
            // Check if feature was completely deleted
            if (snapshot.DeletedFeatureIds.Contains(featureId))
            {
                // Draw entire road in red
                var pixelCoords = originalCoords
                    .Select(c => transform.ToImagePixel(c))
                    .ToList();
                
                for (int i = 0; i < pixelCoords.Count - 1; i++)
                {
                    var p1 = pixelCoords[i];
                    var p2 = pixelCoords[i + 1];
                    
                    DrawThickLine(image, 
                        (int)p1.X, terrainSize - 1 - (int)p1.Y,
                        (int)p2.X, terrainSize - 1 - (int)p2.Y,
                        redColor, 2);
                }
                continue;
            }
            
            // Check if feature was trimmed (coordinates removed)
            if (!currentCoords.TryGetValue(featureId, out var currentSet))
                continue;
            
            // Draw removed segments
            var pixelCoordsList = originalCoords
                .Select(c => (coord: c, pixel: transform.ToImagePixel(c)))
                .ToList();
            
            for (int i = 0; i < pixelCoordsList.Count - 1; i++)
            {
                var c1 = pixelCoordsList[i].coord;
                var c2 = pixelCoordsList[i + 1].coord;
                var p1 = pixelCoordsList[i].pixel;
                var p2 = pixelCoordsList[i + 1].pixel;
                
                var c1Rounded = (Math.Round(c1.Longitude, 7), Math.Round(c1.Latitude, 7));
                var c2Rounded = (Math.Round(c2.Longitude, 7), Math.Round(c2.Latitude, 7));
                
                // If either endpoint was removed, draw this segment in red
                if (!currentSet.Contains(c1Rounded) || !currentSet.Contains(c2Rounded))
                {
                    DrawThickLine(image, 
                        (int)p1.X, terrainSize - 1 - (int)p1.Y,
                        (int)p2.X, terrainSize - 1 - (int)p2.Y,
                        redColor, 2);
                }
            }
        }
    }
    
    /// <summary>
    /// Draws roads after trimming in cyan.
    /// </summary>
    private void DrawTrimmedRoads(
        Image<Rgba32> image,
        List<RoadSpline> splines,
        float metersPerPixel,
        int terrainSize)
    {
        var cyanColor = new Rgba32(0, 200, 255, 255); // Bright cyan
        const float sampleInterval = 0.5f; // Sample every 0.5 meters for smooth curves
        
        foreach (var spline in splines)
        {
            if (spline.TotalLength < 1f)
                continue;
            
            Vector2? prevPoint = null;
            
            for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
            {
                var point = spline.GetPointAtDistance(d);
                var px = (int)(point.X / metersPerPixel);
                var py = terrainSize - 1 - (int)(point.Y / metersPerPixel);
                
                if (prevPoint.HasValue)
                {
                    var prevPx = (int)(prevPoint.Value.X / metersPerPixel);
                    var prevPy = terrainSize - 1 - (int)(prevPoint.Value.Y / metersPerPixel);
                    DrawLine(image, prevPx, prevPy, px, py, cyanColor);
                }
                
                prevPoint = point;
            }
        }
    }
    
    /// <summary>
    /// Draws roundabout rings in yellow.
    /// </summary>
    private void DrawRoundaboutRings(
        Image<Rgba32> image,
        List<RoadSpline> roundaboutSplines,
        float metersPerPixel,
        int terrainSize)
    {
        var yellowColor = new Rgba32(255, 220, 0, 255); // Bright yellow
        const float sampleInterval = 0.3f; // Sample more frequently for smooth ring curves
        
        foreach (var spline in roundaboutSplines)
        {
            if (spline.TotalLength < 1f)
                continue;
            
            Vector2? prevPoint = null;
            
            for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
            {
                var point = spline.GetPointAtDistance(d);
                var px = (int)(point.X / metersPerPixel);
                var py = terrainSize - 1 - (int)(point.Y / metersPerPixel);
                
                if (prevPoint.HasValue)
                {
                    var prevPx = (int)(prevPoint.Value.X / metersPerPixel);
                    var prevPy = terrainSize - 1 - (int)(prevPoint.Value.Y / metersPerPixel);
                    DrawThickLine(image, prevPx, prevPy, px, py, yellowColor, 2);
                }
                
                prevPoint = point;
            }
        }
    }
    
    /// <summary>
    /// Draws connection points as circles.
    /// </summary>
    private void DrawConnectionPoints(
        Image<Rgba32> image,
        List<OsmRoundabout> roundabouts,
        TransformHelper transform,
        int terrainSize)
    {
        var greenFill = new Rgba32(50, 200, 50, 255);
        var whiteOutline = new Rgba32(255, 255, 255, 255);
        
        foreach (var roundabout in roundabouts)
        {
            foreach (var connection in roundabout.Connections)
            {
                var pixel = transform.ToImagePixel(connection.ConnectionPoint);
                var px = (int)pixel.X;
                var py = terrainSize - 1 - (int)pixel.Y;
                
                // Draw white outline
                DrawCircleOutline(image, px, py, 6, whiteOutline);
                // Draw green fill
                DrawFilledCircle(image, px, py, 4, greenFill);
            }
        }
    }
    
    /// <summary>
    /// Draws roundabout centers with crosshairs.
    /// </summary>
    private void DrawRoundaboutCenters(
        Image<Rgba32> image,
        List<OsmRoundabout> roundabouts,
        TransformHelper transform,
        int terrainSize)
    {
        var magentaColor = new Rgba32(255, 0, 200, 255);
        
        foreach (var roundabout in roundabouts)
        {
            var pixel = transform.ToImagePixel(roundabout.Center);
            var px = (int)pixel.X;
            var py = terrainSize - 1 - (int)pixel.Y;
            
            // Draw crosshair
            const int crosshairSize = 8;
            DrawLine(image, px - crosshairSize, py, px + crosshairSize, py, magentaColor);
            DrawLine(image, px, py - crosshairSize, px, py + crosshairSize, magentaColor);
            
            // Draw small circle at center
            DrawCircleOutline(image, px, py, 3, magentaColor);
        }
    }
    
    #endregion
    
    #region Drawing Primitives
    
    private static void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        
        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                BlendPixel(img, x0, y0, color);
            
            if (x0 == x1 && y0 == y1) break;
            
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
    
    private static void DrawThickLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int thickness)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        int halfThick = thickness / 2;
        
        while (true)
        {
            // Draw thick point
            for (int ox = -halfThick; ox <= halfThick; ox++)
            {
                for (int oy = -halfThick; oy <= halfThick; oy++)
                {
                    var px = x0 + ox;
                    var py = y0 + oy;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        BlendPixel(img, px, py, color);
                }
            }
            
            if (x0 == x1 && y0 == y1) break;
            
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
    
    private static void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius * radius)
                {
                    var px = cx + x;
                    var py = cy + y;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        BlendPixel(img, px, py, color);
                }
            }
        }
    }
    
    private static void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 3)
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                BlendPixel(img, px, py, color);
        }
    }
    
    private static void BlendPixel(Image<Rgba32> img, int x, int y, Rgba32 color)
    {
        if (color.A == 255)
        {
            img[x, y] = color;
            return;
        }
        
        var existing = img[x, y];
        float alpha = color.A / 255f;
        float invAlpha = 1f - alpha;
        
        var blended = new Rgba32(
            (byte)(color.R * alpha + existing.R * invAlpha),
            (byte)(color.G * alpha + existing.G * invAlpha),
            (byte)(color.B * alpha + existing.B * invAlpha),
            255);
        
        img[x, y] = blended;
    }
    
    #endregion
    
    #region Helper Classes
    
    /// <summary>
    /// Helper for coordinate transformation.
    /// </summary>
    private class TransformHelper
    {
        private readonly GeoBoundingBox _bbox;
        private readonly int _terrainSize;
        private readonly GeoCoordinateTransformer? _transformer;
        
        public TransformHelper(GeoBoundingBox bbox, int terrainSize, GeoCoordinateTransformer? transformer)
        {
            _bbox = bbox;
            _terrainSize = terrainSize;
            _transformer = transformer;
        }
        
        public Vector2 ToImagePixel(GeoCoordinate coord)
        {
            if (_transformer != null)
            {
                return _transformer.TransformToTerrainPixel(coord.Longitude, coord.Latitude);
            }
            
            // Fallback to linear interpolation
            var normalizedX = (coord.Longitude - _bbox.MinLongitude) / _bbox.Width;
            var normalizedY = (coord.Latitude - _bbox.MinLatitude) / _bbox.Height;
            
            return new Vector2(
                (float)(normalizedX * _terrainSize),
                (float)(normalizedY * _terrainSize));
        }
    }
    
    #endregion
}
