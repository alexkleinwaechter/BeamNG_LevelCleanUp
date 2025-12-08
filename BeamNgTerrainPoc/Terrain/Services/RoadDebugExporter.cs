using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Centralized debug and production image exporter for road smoothing.
/// Used by both RoadSmoothingService and MultiMaterialRoadSmoother to avoid code duplication.
/// </summary>
public static class RoadDebugExporter
{
    /// <summary>
    /// Exports all applicable debug images based on parameters settings.
    /// Call this after geometry extraction and elevation calculation.
    /// </summary>
    /// <param name="geometry">Road geometry with cross-sections</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="parameters">Road smoothing parameters (controls which exports are enabled)</param>
    /// <param name="smoothedHeightMap">Optional: smoothed heightmap for outline export</param>
    /// <param name="distanceField">Optional: distance field for outline export</param>
    public static void ExportAllDebugImages(
        RoadGeometry geometry,
        float metersPerPixel,
        RoadSmoothingParameters parameters,
        float[,]? smoothedHeightMap = null,
        float[,]? distanceField = null)
    {
        if (parameters.Approach != RoadSmoothingApproach.Spline)
            return;
        
        if (geometry.CrossSections.Count == 0)
            return;
        
        // Always export spline masks (production output)
        try
        {
            ExportSplineMasks(geometry, metersPerPixel, parameters);
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Spline mask export failed: {ex.Message}");
        }
        
        // Optional spline debug export
        if (parameters.ExportSplineDebugImage)
        {
            try
            {
                ExportSplineDebugImage(geometry, metersPerPixel, parameters, "spline_debug.png");
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Spline debug export failed: {ex.Message}");
            }
        }
        
        // Optional smoothed elevation debug image
        if (parameters.ExportSmoothedElevationDebugImage)
        {
            try
            {
                ExportSmoothedElevationDebugImage(geometry, metersPerPixel, parameters);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Smoothed elevation debug export failed: {ex.Message}");
            }
        }
        
        // Optional heightmap with road outlines (requires smoothed heightmap and distance field)
        if (parameters.ExportSmoothedHeightmapWithOutlines && smoothedHeightMap != null && distanceField != null)
        {
            try
            {
                ExportSmoothedHeightmapWithRoadOutlines(smoothedHeightMap, geometry, distanceField, metersPerPixel, parameters);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Smoothed heightmap with outlines export failed: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Exports spline masks as PNG images for use in BeamNG.
    /// Creates:
    /// - One PNG per detected path (spline) with white road on black background
    /// - One combined PNG with all splines
    /// 
    /// Images are exported as 16-bit grayscale PNGs for BeamNG compatibility.
    /// 
    /// Output folder: {DebugOutputDirectory}/splines/
    /// Files: path_001.png, path_002.png, ..., all_splines.png
    /// </summary>
    public static void ExportSplineMasks(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        float halfWidth = parameters.RoadWidthMeters / 2.0f;
        
        // Create output directory
        var baseDir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Directory.GetCurrentDirectory();
        var splinesDir = Path.Combine(baseDir, "splines");
        Directory.CreateDirectory(splinesDir);
        
        // Group cross-sections by PathId
        // Note: Don't filter by TargetElevation - we want to export geometry regardless of elevation status
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .OrderBy(g => g.Key)
            .ToList();
        
        if (pathGroups.Count == 0)
        {
            TerrainLogger.Warning("No valid paths to export as spline masks.");
            return;
        }
        
        TerrainLogger.Info($"Exporting {pathGroups.Count} spline mask(s) to: {splinesDir}");
        
        // Create combined image (16-bit grayscale)
        using var combinedImage = new Image<L16>(width, height, new L16(0));
        
        int pathIndex = 0;
        foreach (var pathGroup in pathGroups)
        {
            pathIndex++;
            var pathSections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();
            
            if (pathSections.Count < 2) continue;
            
            // Create individual path image (16-bit grayscale)
            using var pathImage = new Image<L16>(width, height, new L16(0));
            
            // Draw the road with full width using cross-sections
            foreach (var cs in pathSections)
            {
                var center = cs.CenterPoint;
                var left = center - cs.NormalDirection * halfWidth;
                var right = center + cs.NormalDirection * halfWidth;
                
                int lx = (int)(left.X / metersPerPixel);
                int ly = (int)(left.Y / metersPerPixel);
                int rx = (int)(right.X / metersPerPixel);
                int ry = (int)(right.Y / metersPerPixel);
                
                // Draw on individual path image (white = 65535 for 16-bit)
                DrawThickLineL16(pathImage, lx, ly, rx, ry, new L16(ushort.MaxValue), height);
                
                // Draw on combined image
                DrawThickLineL16(combinedImage, lx, ly, rx, ry, new L16(ushort.MaxValue), height);
            }
            
            // Fill gaps between cross-sections by drawing connecting quads
            for (int i = 0; i < pathSections.Count - 1; i++)
            {
                var cs1 = pathSections[i];
                var cs2 = pathSections[i + 1];
                
                // Get the four corners of the road segment
                var left1 = cs1.CenterPoint - cs1.NormalDirection * halfWidth;
                var right1 = cs1.CenterPoint + cs1.NormalDirection * halfWidth;
                var left2 = cs2.CenterPoint - cs2.NormalDirection * halfWidth;
                var right2 = cs2.CenterPoint + cs2.NormalDirection * halfWidth;
                
                // Draw filled quad
                FillQuadL16(pathImage, 
                    (int)(left1.X / metersPerPixel), (int)(left1.Y / metersPerPixel),
                    (int)(right1.X / metersPerPixel), (int)(right1.Y / metersPerPixel),
                    (int)(right2.X / metersPerPixel), (int)(right2.Y / metersPerPixel),
                    (int)(left2.X / metersPerPixel), (int)(left2.Y / metersPerPixel),
                    new L16(ushort.MaxValue), height);
                
                FillQuadL16(combinedImage,
                    (int)(left1.X / metersPerPixel), (int)(left1.Y / metersPerPixel),
                    (int)(right1.X / metersPerPixel), (int)(right1.Y / metersPerPixel),
                    (int)(right2.X / metersPerPixel), (int)(right2.Y / metersPerPixel),
                    (int)(left2.X / metersPerPixel), (int)(left2.Y / metersPerPixel),
                    new L16(ushort.MaxValue), height);
            }
            
            // Save individual path image
            string pathFileName = $"path_{pathIndex:D3}.png";
            string pathFilePath = Path.Combine(splinesDir, pathFileName);
            pathImage.SaveAsPng(pathFilePath);
        }
        
        // Save combined image
        string combinedFilePath = Path.Combine(splinesDir, "all_splines.png");
        combinedImage.SaveAsPng(combinedFilePath);
        
        TerrainLogger.Info($"Exported {pathIndex} individual spline mask(s) + combined mask (16-bit grayscale)");
        TerrainLogger.Info($"  Road width: {parameters.RoadWidthMeters}m ({parameters.RoadWidthMeters / metersPerPixel:F1} pixels)");
        TerrainLogger.Info($"  Combined mask: {combinedFilePath}");
    }
    
    /// <summary>
    /// Exports a debug image showing spline centerlines and cross-section widths.
    /// </summary>
    public static void ExportSplineDebugImage(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters, string fileName)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
        
        // Draw road mask faintly
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(32, 32, 32, 255); // flip Y for visualization
                }
            }
        }
        
        // Draw spline centerline samples (if spline is available)
        if (geometry.Spline != null)
        {
            float sampleInterval = parameters.CrossSectionIntervalMeters;
            for (float d = 0; d <= geometry.Spline.TotalLength; d += sampleInterval)
            {
                var p = geometry.Spline.GetPointAtDistance(d);
                int px = (int)(p.X / metersPerPixel);
                int py = (int)(p.Y / metersPerPixel);
                if (px >= 0 && px < width && py >= 0 && py < height)
                {
                    image[px, height - 1 - py] = new Rgba32(255, 255, 0, 255); // yellow centerline
                }
            }
        }
        
        // Draw road width (perpendicular segments) for a subset of cross-sections
        int step = Math.Max(1, (int)(2.0f * parameters.CrossSectionIntervalMeters));
        foreach (var cs in geometry.CrossSections.Where(c => c.LocalIndex % step == 0))
        {
            var center = cs.CenterPoint;
            float halfWidth = parameters.RoadWidthMeters / 2.0f;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, new Rgba32(0, 255, 0, 255)); // green road width
        }
        
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, fileName);
        image.SaveAsPng(filePath);
        TerrainLogger.Info($"Exported spline debug image: {filePath}");
    }

    /// <summary>
    /// Exports a debug image color-coded by smoothed target elevations.
    /// </summary>
    public static void ExportSmoothedElevationDebugImage(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));

        var elevations = geometry.CrossSections.Select(cs => cs.TargetElevation).Where(e => e > 0).ToList();
        if (!elevations.Any())
        {
            TerrainLogger.Warning("No valid elevations to create smoothed debug image.");
            return;
        }
        float minElev = elevations.Min();
        float maxElev = elevations.Max();
        float range = maxElev - minElev;
        if (range < 0.01f) range = 1f;

        // Draw road mask
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(32, 32, 32, 255);
                }
            }
        }

        // Color code the road based on smoothed target elevations
        foreach (var cs in geometry.CrossSections)
        {
            if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation <= -1000f) continue;

            float normalizedElevation = (cs.TargetElevation - minElev) / range;
            var color = GetColorForValue(normalizedElevation);

            var center = cs.CenterPoint;
            float halfWidth = parameters.RoadWidthMeters / 2.0f;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, color);
        }

        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "spline_smoothed_elevation_debug.png");
        image.SaveAsPng(filePath);
        TerrainLogger.Info($"Exported smoothed elevation debug image: {filePath}");
        TerrainLogger.Info($"  Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)");
    }

    /// <summary>
    /// Exports the smoothed heightmap as a grayscale image with road outlines overlaid.
    /// Shows:
    /// - Smoothed heightmap as grayscale background
    /// - Thin cyan outline at road edges (± roadWidth/2)
    /// - Thin magenta outline at terrain blending edges (± roadWidth/2 + terrainAffectedRange)
    /// </summary>
    public static void ExportSmoothedHeightmapWithRoadOutlines(
        float[,] smoothedHeightMap, 
        RoadGeometry geometry, 
        float[,] distanceField,
        float metersPerPixel, 
        RoadSmoothingParameters parameters)
    {
        int width = smoothedHeightMap.GetLength(1);
        int height = smoothedHeightMap.GetLength(0);
        
        TerrainLogger.Info($"Exporting smoothed heightmap with road outlines ({width}x{height})...");

        // Step 1: Find height range for grayscale normalization
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float h = smoothedHeightMap[y, x];
                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }
        float heightRange = maxHeight - minHeight;
        if (heightRange < 0.01f) heightRange = 1f;

        // Step 2: Create grayscale heightmap image
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float normalizedHeight = (smoothedHeightMap[y, x] - minHeight) / heightRange;
                byte gray = (byte)(normalizedHeight * 255f);
                image[x, height - 1 - y] = new Rgba32(gray, gray, gray, 255);
            }
        }

        // Step 3: Draw road outlines
        float roadHalfWidth = parameters.RoadWidthMeters / 2.0f;
        float blendZoneMaxDist = roadHalfWidth + parameters.TerrainAffectedRangeMeters;
        float edgeTolerance = metersPerPixel * 0.75f;

        int roadEdgePixels = 0;
        int blendEdgePixels = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dist = distanceField[y, x];

                if (Math.Abs(dist - roadHalfWidth) < edgeTolerance)
                {
                    image[x, height - 1 - y] = new Rgba32(0, 255, 255, 255); // Cyan
                    roadEdgePixels++;
                }
                else if (Math.Abs(dist - blendZoneMaxDist) < edgeTolerance)
                {
                    image[x, height - 1 - y] = new Rgba32(255, 0, 255, 255); // Magenta
                    blendEdgePixels++;
                }
            }
        }

        // Step 4: Save image
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "smoothed_heightmap_with_road_outlines.png");
        image.SaveAsPng(filePath);
        
        TerrainLogger.Info($"Exported smoothed heightmap with outlines: {filePath}");
        TerrainLogger.Info($"  Height range: {minHeight:F2}m (black) to {maxHeight:F2}m (white)");
        TerrainLogger.Info($"  Road edge outline (cyan): {roadEdgePixels:N0} pixels at ±{roadHalfWidth:F1}m from centerline");
        TerrainLogger.Info($"  Blend zone edge outline (magenta): {blendEdgePixels:N0} pixels at ±{blendZoneMaxDist:F1}m from centerline");
    }

    #region Drawing Helpers
    
    /// <summary>
    /// Draws a thick line (road cross-section) on a 16-bit grayscale image using Bresenham's algorithm.
    /// </summary>
    private static void DrawThickLineL16(Image<L16> img, int x0, int y0, int x1, int y1, L16 color, int imgHeight)
    {
        // Flip Y for image coordinates
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
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
    /// Fills a quadrilateral on a 16-bit grayscale image by checking each pixel in the bounding box.
    /// </summary>
    private static void FillQuadL16(Image<L16> img, int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3, L16 color, int imgHeight)
    {
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        y2 = imgHeight - 1 - y2;
        y3 = imgHeight - 1 - y3;
        
        int minY = Math.Max(0, Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)));
        int maxY = Math.Min(img.Height - 1, Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
        int minX = Math.Max(0, Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)));
        int maxX = Math.Min(img.Width - 1, Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)));
        
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (IsPointInQuad(x, y, x0, y0, x1, y1, x2, y2, x3, y3))
                {
                    img[x, y] = color;
                }
            }
        }
    }
    
    /// <summary>
    /// Draws a thick line (road cross-section) on an image using Bresenham's algorithm.
    /// </summary>
    private static void DrawThickLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int imgHeight)
    {
        // Flip Y for image coordinates
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
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
    /// Draws a line on an image with Y-flipping for coordinate conversion.
    /// </summary>
    private static void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int height = img.Height;
        y0 = height - 1 - y0;
        y1 = height - 1 - y1;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
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
    /// Fills a quadrilateral by checking each pixel in the bounding box.
    /// </summary>
    private static void FillQuad(Image<Rgba32> img, int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3, Rgba32 color, int imgHeight)
    {
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        y2 = imgHeight - 1 - y2;
        y3 = imgHeight - 1 - y3;
        
        int minY = Math.Max(0, Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)));
        int maxY = Math.Min(img.Height - 1, Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
        int minX = Math.Max(0, Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)));
        int maxX = Math.Min(img.Width - 1, Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)));
        
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (IsPointInQuad(x, y, x0, y0, x1, y1, x2, y2, x3, y3))
                {
                    img[x, y] = color;
                }
            }
        }
    }
    
    /// <summary>
    /// Checks if a point is inside a convex quadrilateral using cross product test.
    /// </summary>
    private static bool IsPointInQuad(int px, int py, int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3)
    {
        int sign0 = Sign((x1 - x0) * (py - y0) - (y1 - y0) * (px - x0));
        int sign1 = Sign((x2 - x1) * (py - y1) - (y2 - y1) * (px - x1));
        int sign2 = Sign((x3 - x2) * (py - y2) - (y3 - y2) * (px - x2));
        int sign3 = Sign((x0 - x3) * (py - y3) - (y0 - y3) * (px - x3));
        
        return (sign0 >= 0 && sign1 >= 0 && sign2 >= 0 && sign3 >= 0) ||
               (sign0 <= 0 && sign1 <= 0 && sign2 <= 0 && sign3 <= 0);
    }
    
    private static int Sign(int value) => value > 0 ? 1 : (value < 0 ? -1 : 0);
    
    /// <summary>
    /// Gets a color for elevation visualization (blue=low, green=mid, red=high).
    /// </summary>
    private static Rgba32 GetColorForValue(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        float r = Math.Clamp(value * 2.0f, 0f, 1f);
        float b = Math.Clamp((1.0f - value) * 2.0f, 0f, 1f);
        float g = 1.0f - Math.Abs(value - 0.5f) * 2.0f;
        return new Rgba32(r, g, b);
    }
    
    #endregion
}
