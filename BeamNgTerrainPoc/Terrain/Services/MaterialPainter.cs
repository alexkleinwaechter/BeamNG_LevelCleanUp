using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     Paints material layers based on spline ownership in the unified road network.
///     This is a separate phase from elevation smoothing, allowing:
///     - Different surface widths for painting vs. smoothing (RoadSurfaceWidthMeters vs RoadWidthMeters)
///     - Clean material boundaries without elevation artifacts
///     - Proper BeamNG material layer generation
///     Material overlap resolution follows BeamNG convention: last material in order wins.
///     Painting approach:
///     - Roads are painted by directly sampling the original spline at fine intervals
///     - This ensures accurate curve following even in tight curves
///     - Anti-aliased version uses distance-based alpha for smooth edges
/// </summary>
public class MaterialPainter
{
    /// <summary>
    ///     Minimum sampling interval for material painting in meters.
    ///     This ensures curves are accurately painted even when CrossSectionIntervalMeters is larger.
    /// </summary>
    private const float MaxPaintingSampleIntervalMeters = 0.25f;

    /// <summary>
    ///     Generates layer masks for each material based on spline ownership.
    ///     Uses RoadSurfaceWidthMeters for painting (may differ from RoadWidthMeters used for elevation).
    ///     Samples the original spline directly for accurate curve following.
    /// </summary>
    /// <param name="network">The unified road network with all splines.</param>
    /// <param name="width">Width of the output masks in pixels.</param>
    /// <param name="height">Height of the output masks in pixels.</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <returns>Dictionary mapping material name to layer mask (0-255).</returns>
    public Dictionary<string, byte[,]> PaintMaterials(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel)
    {
        TerrainLogger.Info("=== MATERIAL PAINTING ===");
        TerrainLogger.Info($"  Network: {network.Splines.Count} splines");
        TerrainLogger.Info($"  Output: {width}x{height} pixels");

        var layers = new Dictionary<string, byte[,]>();

        // Get unique material names (preserving order from splines)
        var materialNames = network.Splines
            .Select(s => s.MaterialName)
            .Distinct()
            .ToList();

        TerrainLogger.Info($"  Materials: {string.Join(", ", materialNames)}");

        // Initialize layer for each material
        foreach (var name in materialNames) layers[name] = new byte[height, width];

        // Group splines by material
        var splinesByMaterial = network.Splines
            .GroupBy(s => s.MaterialName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Paint each material's splines in order (last material wins for overlaps)
        foreach (var materialName in materialNames)
        {
            var layer = layers[materialName];
            var splines = splinesByMaterial[materialName];

            var paintedPixels = 0;

            foreach (var paramSpline in splines)
            {
                // Use RoadSurfaceWidthMeters if set, otherwise RoadWidthMeters
                var surfaceHalfWidth = paramSpline.Parameters.EffectiveRoadSurfaceWidthMeters / 2.0f;

                // Paint directly from the original spline with fine sampling for curve accuracy
                paintedPixels += PaintSplineDirectly(
                    layer, paramSpline.Spline, surfaceHalfWidth, metersPerPixel, width, height);
            }

            TerrainLogger.Info($"    {materialName}: {paintedPixels:N0} pixels painted from {splines.Count} spline(s)");
        }

        TerrainLogger.Info("=== MATERIAL PAINTING COMPLETE ===");
        return layers;
    }

    /// <summary>
    ///     Paints a road directly from its spline with fine sampling for accurate curve following.
    ///     This bypasses cross-sections and samples the spline at intervals fine enough to
    ///     accurately represent even tight curves.
    /// </summary>
    /// <param name="layer">The layer mask to paint on.</param>
    /// <param name="spline">The road spline to paint.</param>
    /// <param name="halfWidth">Half the surface width in meters.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="width">Layer width in pixels.</param>
    /// <param name="height">Layer height in pixels.</param>
    /// <returns>Number of pixels painted.</returns>
    private int PaintSplineDirectly(
        byte[,] layer,
        RoadSpline spline,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height)
    {
        // Calculate optimal sampling interval:
        // - Use at most MaxPaintingSampleIntervalMeters
        // - But also ensure we have at least one sample per pixel width
        var pixelSizeInMeters = metersPerPixel;
        var sampleInterval = MathF.Min(MaxPaintingSampleIntervalMeters, pixelSizeInMeters * 0.5f);

        // Sample the spline at fine intervals
        var samples = spline.SampleByDistance(sampleInterval);

        if (samples.Count < 2)
            return 0;

        var paintedPixels = 0;

        // Paint quads between consecutive samples
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var s1 = samples[i];
            var s2 = samples[i + 1];

            paintedPixels += PaintQuadBetweenSamples(
                layer, s1, s2, halfWidth, metersPerPixel, width, height);
        }

        // Paint end caps
        if (samples.Count > 0)
        {
            paintedPixels += PaintSampleCrossSection(
                layer, samples[0], halfWidth, metersPerPixel, width, height);

            if (samples.Count > 1)
                paintedPixels += PaintSampleCrossSection(
                    layer, samples[^1], halfWidth, metersPerPixel, width, height);
        }

        return paintedPixels;
    }

    /// <summary>
    ///     Paints a filled quad between two consecutive spline samples.
    /// </summary>
    private int PaintQuadBetweenSamples(
        byte[,] layer,
        SplineSample s1,
        SplineSample s2,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height)
    {
        // Calculate the four corners of the quad in world coordinates
        var left1 = s1.Position - s1.Normal * halfWidth;
        var right1 = s1.Position + s1.Normal * halfWidth;
        var left2 = s2.Position - s2.Normal * halfWidth;
        var right2 = s2.Position + s2.Normal * halfWidth;

        // Convert to pixel coordinates
        var corners = new Vector2[]
        {
            new(left1.X / metersPerPixel, left1.Y / metersPerPixel),
            new(right1.X / metersPerPixel, right1.Y / metersPerPixel),
            new(right2.X / metersPerPixel, right2.Y / metersPerPixel),
            new(left2.X / metersPerPixel, left2.Y / metersPerPixel)
        };

        return FillConvexPolygon(layer, corners, width, height);
    }

    /// <summary>
    ///     Paints a single spline sample as an end cap.
    /// </summary>
    private int PaintSampleCrossSection(
        byte[,] layer,
        SplineSample sample,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height)
    {
        var left = sample.Position - sample.Normal * halfWidth;
        var right = sample.Position + sample.Normal * halfWidth;

        var x0 = (int)(left.X / metersPerPixel);
        var y0 = (int)(left.Y / metersPerPixel);
        var x1 = (int)(right.X / metersPerPixel);
        var y1 = (int)(right.Y / metersPerPixel);

        return DrawThickLine(layer, x0, y0, x1, y1, width, height, metersPerPixel);
    }

    /// <summary>
    ///     Fills a convex polygon using scanline rasterization.
    ///     Polygon vertices should be in order (clockwise or counter-clockwise).
    /// </summary>
    private int FillConvexPolygon(byte[,] layer, Vector2[] vertices, int width, int height)
    {
        if (vertices.Length < 3)
            return 0;

        var count = 0;

        // Find bounding box
        float minY = float.MaxValue, maxY = float.MinValue;
        float minX = float.MaxValue, maxX = float.MinValue;

        foreach (var v in vertices)
        {
            minY = MathF.Min(minY, v.Y);
            maxY = MathF.Max(maxY, v.Y);
            minX = MathF.Min(minX, v.X);
            maxX = MathF.Max(maxX, v.X);
        }

        var startY = Math.Max(0, (int)MathF.Floor(minY));
        var endY = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
        var startX = Math.Max(0, (int)MathF.Floor(minX));
        var endX = Math.Min(width - 1, (int)MathF.Ceiling(maxX));

        // For each scanline
        for (var y = startY; y <= endY; y++)
        {
            var scanY = y + 0.5f;

            // Find intersection points with polygon edges
            var intersections = new List<float>();

            for (var i = 0; i < vertices.Length; i++)
            {
                var v1 = vertices[i];
                var v2 = vertices[(i + 1) % vertices.Length];

                // Check if edge crosses this scanline
                if ((v1.Y <= scanY && v2.Y > scanY) || (v2.Y <= scanY && v1.Y > scanY))
                {
                    // Calculate x intersection using linear interpolation
                    var t = (scanY - v1.Y) / (v2.Y - v1.Y);
                    var xIntersect = v1.X + t * (v2.X - v1.X);
                    intersections.Add(xIntersect);
                }
            }

            // Sort intersections
            intersections.Sort();

            // Fill between pairs of intersections
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var xStart = Math.Max(startX, (int)MathF.Floor(intersections[i]));
                var xEnd = Math.Min(endX, (int)MathF.Ceiling(intersections[i + 1]));

                for (var x = xStart; x <= xEnd; x++)
                    if (layer[y, x] == 0)
                    {
                        layer[y, x] = 255;
                        count++;
                    }
            }
        }

        return count;
    }

    /// <summary>
    ///     Paints material layers with anti-aliased edges for smoother boundaries.
    ///     Uses direct spline sampling for accurate curve following and distance-based alpha for edge pixels.
    /// </summary>
    /// <param name="network">The unified road network.</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="antiAliasEdgeWidth">Width of anti-aliased edge in meters.</param>
    /// <returns>Dictionary mapping material name to layer mask (0-255).</returns>
    public Dictionary<string, byte[,]> PaintMaterialsAntiAliased(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel,
        float antiAliasEdgeWidth = 0.5f)
    {
        TerrainLogger.Info("=== MATERIAL PAINTING (Anti-Aliased) ===");

        var layers = new Dictionary<string, byte[,]>();

        var materialNames = network.Splines
            .Select(s => s.MaterialName)
            .Distinct()
            .ToList();

        foreach (var name in materialNames) layers[name] = new byte[height, width];

        var splinesByMaterial = network.Splines
            .GroupBy(s => s.MaterialName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var materialName in materialNames)
        {
            var layer = layers[materialName];
            var splines = splinesByMaterial[materialName];

            foreach (var paramSpline in splines)
            {
                var surfaceHalfWidth = paramSpline.Parameters.EffectiveRoadSurfaceWidthMeters / 2.0f;

                // Paint directly from the original spline with fine sampling for curve accuracy
                PaintSplineDirectlyAntiAliased(
                    layer, paramSpline.Spline, surfaceHalfWidth, metersPerPixel,
                    width, height, antiAliasEdgeWidth);
            }

            TerrainLogger.Info($"    {materialName}: painted from {splines.Count} spline(s)");
        }

        TerrainLogger.Info("=== ANTI-ALIASED PAINTING COMPLETE ===");
        return layers;
    }

    /// <summary>
    ///     Paints a road directly from its spline with anti-aliased edges.
    ///     Uses fine sampling for accurate curve following.
    /// </summary>
    private void PaintSplineDirectlyAntiAliased(
        byte[,] layer,
        RoadSpline spline,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height,
        float antiAliasEdgeWidth)
    {
        // Calculate optimal sampling interval
        var pixelSizeInMeters = metersPerPixel;
        var sampleInterval = MathF.Min(MaxPaintingSampleIntervalMeters, pixelSizeInMeters * 0.5f);

        // Sample the spline at fine intervals
        var samples = spline.SampleByDistance(sampleInterval);

        if (samples.Count < 2)
            return;

        // Paint anti-aliased quads between consecutive samples
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var s1 = samples[i];
            var s2 = samples[i + 1];

            PaintQuadBetweenSamplesAntiAliased(
                layer, s1, s2, halfWidth, metersPerPixel, width, height, antiAliasEdgeWidth);
        }

        // Paint anti-aliased end caps
        if (samples.Count > 0)
        {
            PaintSampleCrossSectionAntiAliased(
                layer, samples[0], halfWidth, metersPerPixel, width, height, antiAliasEdgeWidth);

            if (samples.Count > 1)
                PaintSampleCrossSectionAntiAliased(
                    layer, samples[^1], halfWidth, metersPerPixel, width, height, antiAliasEdgeWidth);
        }
    }

    /// <summary>
    ///     Paints an anti-aliased quad between two consecutive spline samples.
    /// </summary>
    private void PaintQuadBetweenSamplesAntiAliased(
        byte[,] layer,
        SplineSample s1,
        SplineSample s2,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height,
        float antiAliasEdgeWidth)
    {
        var totalHalfWidth = halfWidth + antiAliasEdgeWidth;

        // Calculate bounding box
        var points = new[]
        {
            s1.Position - s1.Normal * totalHalfWidth,
            s1.Position + s1.Normal * totalHalfWidth,
            s2.Position - s2.Normal * totalHalfWidth,
            s2.Position + s2.Normal * totalHalfWidth
        };

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (var p in points)
        {
            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxY = MathF.Max(maxY, p.Y);
        }

        var pxMinX = Math.Max(0, (int)(minX / metersPerPixel - 1));
        var pxMaxX = Math.Min(width - 1, (int)(maxX / metersPerPixel + 1));
        var pxMinY = Math.Max(0, (int)(minY / metersPerPixel - 1));
        var pxMaxY = Math.Min(height - 1, (int)(maxY / metersPerPixel + 1));

        // Direction along the road segment
        var segmentDir = s2.Position - s1.Position;
        var segmentLength = segmentDir.Length();
        if (segmentLength < 0.0001f) return;
        segmentDir /= segmentLength;

        for (var py = pxMinY; py <= pxMaxY; py++)
        for (var px = pxMinX; px <= pxMaxX; px++)
        {
            var pixelWorld = new Vector2(px * metersPerPixel, py * metersPerPixel);

            // Project pixel onto segment to find parameter t
            var toPixel = pixelWorld - s1.Position;
            var t = Vector2.Dot(toPixel, segmentDir) / segmentLength;
            t = Math.Clamp(t, 0, 1);

            // Interpolate sample properties at this t
            var interpolatedCenter = Vector2.Lerp(s1.Position, s2.Position, t);
            var interpolatedNormal = Vector2.Normalize(
                Vector2.Lerp(s1.Normal, s2.Normal, t));

            // Distance from interpolated centerline (perpendicular)
            var toPixelFromCenter = pixelWorld - interpolatedCenter;
            var distFromCenter = MathF.Abs(Vector2.Dot(toPixelFromCenter, interpolatedNormal));

            if (distFromCenter > totalHalfWidth)
                continue;

            // Calculate alpha based on distance from edge
            byte alpha;
            if (distFromCenter <= halfWidth)
            {
                alpha = 255; // Full coverage inside road
            }
            else
            {
                // Anti-alias zone
                var edgeDist = distFromCenter - halfWidth;
                var normalizedDist = edgeDist / antiAliasEdgeWidth;
                alpha = (byte)(255 * (1.0f - normalizedDist));
            }

            // Use max blending (paint over if our alpha is higher)
            if (alpha > layer[py, px]) layer[py, px] = alpha;
        }
    }

    /// <summary>
    ///     Paints a spline sample as an anti-aliased end cap.
    /// </summary>
    private void PaintSampleCrossSectionAntiAliased(
        byte[,] layer,
        SplineSample sample,
        float halfWidth,
        float metersPerPixel,
        int width,
        int height,
        float antiAliasEdgeWidth)
    {
        var center = sample.Position;
        var normal = sample.Normal;
        var tangent = sample.Tangent;

        // Paint a small region around the sample (end cap)
        var totalHalfWidth = halfWidth + antiAliasEdgeWidth;
        var capExtent = metersPerPixel * 2; // Small extent along tangent for end cap

        // Bounding box for the end cap region
        var minX = Math.Max(0, (int)((center.X - totalHalfWidth) / metersPerPixel) - 1);
        var maxX = Math.Min(width - 1, (int)((center.X + totalHalfWidth) / metersPerPixel) + 1);
        var minY = Math.Max(0, (int)((center.Y - totalHalfWidth) / metersPerPixel) - 1);
        var maxY = Math.Min(height - 1, (int)((center.Y + totalHalfWidth) / metersPerPixel) + 1);

        for (var py = minY; py <= maxY; py++)
        for (var px = minX; px <= maxX; px++)
        {
            var pixelWorld = new Vector2(px * metersPerPixel, py * metersPerPixel);

            // Distance from cross-section centerline (along normal direction)
            var toPixel = pixelWorld - center;
            var distFromCenter = MathF.Abs(Vector2.Dot(toPixel, normal));
            var distAlongTangent = MathF.Abs(Vector2.Dot(toPixel, tangent));

            // Only paint near the cross-section line (small extent along tangent)
            if (distAlongTangent > capExtent)
                continue;

            if (distFromCenter > totalHalfWidth)
                continue;

            // Calculate alpha based on distance from edge
            byte alpha;
            if (distFromCenter <= halfWidth)
            {
                alpha = 255; // Full coverage
            }
            else
            {
                // Anti-alias zone
                var t = (distFromCenter - halfWidth) / antiAliasEdgeWidth;
                alpha = (byte)(255 * (1.0f - t));
            }

            // Use max blending (paint over if our alpha is higher)
            if (alpha > layer[py, px]) layer[py, px] = alpha;
        }
    }

    /// <summary>
    ///     Draws a thick line (single pixel width in practice, but ensures coverage).
    /// </summary>
    private int DrawThickLine(byte[,] layer, int x0, int y0, int x1, int y1, int width, int height,
        float metersPerPixel)
    {
        var count = 0;

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            // Paint the pixel and its neighbors to ensure coverage
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var px = x0 + offsetX;
                var py = y0 + offsetY;

                if (px >= 0 && px < width && py >= 0 && py < height)
                    if (layer[py, px] == 0)
                    {
                        layer[py, px] = 255;
                        count++;
                    }
            }

            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }

        return count;
    }

    /// <summary>
    ///     Combines multiple material layers into a single indexed layer.
    ///     Uses the BeamNG convention: last material in order wins for overlaps.
    /// </summary>
    /// <param name="layers">Dictionary of material name to layer mask.</param>
    /// <param name="materialOrder">Ordered list of material names (first = index 0).</param>
    /// <param name="threshold">Minimum alpha value to consider as painted (default 128).</param>
    /// <returns>Indexed layer where each pixel contains the material index (0-based), or -1 if no material.</returns>
    public int[,] CombineLayersToIndexed(
        Dictionary<string, byte[,]> layers,
        List<string> materialOrder,
        byte threshold = 128)
    {
        if (layers.Count == 0 || materialOrder.Count == 0)
            throw new ArgumentException("Must have at least one layer and material");

        var firstLayer = layers.Values.First();
        var height = firstLayer.GetLength(0);
        var width = firstLayer.GetLength(1);

        var result = new int[height, width];

        // Initialize to -1 (no material)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            result[y, x] = -1;

        // Apply materials in order (last wins)
        for (var i = 0; i < materialOrder.Count; i++)
        {
            var materialName = materialOrder[i];
            if (!layers.TryGetValue(materialName, out var layer))
                continue;

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (layer[y, x] >= threshold)
                    result[y, x] = i;
        }

        return result;
    }

    /// <summary>
    ///     Exports material layers as a combined debug image.
    ///     Each material is shown in a different color.
    /// </summary>
    /// <param name="layers">Dictionary of material layers.</param>
    /// <param name="outputPath">Path to save the debug image.</param>
    public void ExportDebugImage(
        Dictionary<string, byte[,]> layers,
        string outputPath)
    {
        if (layers.Count == 0)
            return;

        var firstLayer = layers.Values.First();
        var height = firstLayer.GetLength(0);
        var width = firstLayer.GetLength(1);

        // Define colors for materials (cycle through if more materials than colors)
        var colors = new (byte R, byte G, byte B)[]
        {
            (255, 0, 0), // Red
            (0, 255, 0), // Green
            (0, 0, 255), // Blue
            (255, 255, 0), // Yellow
            (255, 0, 255), // Magenta
            (0, 255, 255), // Cyan
            (255, 128, 0), // Orange
            (128, 0, 255) // Purple
        };

        using var image = new Image<Rgba32>(width, height);

        // Fill with black background
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = new Rgba32(0, 0, 0, 255);

        // Paint each material in its color (last wins for overlaps)
        var colorIndex = 0;
        foreach (var (materialName, layer) in layers)
        {
            var color = colors[colorIndex % colors.Length];

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = layer[y, x];
                if (alpha > 0)
                {
                    // Blend with alpha
                    var a = alpha / 255.0f;
                    var existing = image[x, y];

                    var r = (byte)(existing.R * (1 - a) + color.R * a);
                    var g = (byte)(existing.G * (1 - a) + color.G * a);
                    var b = (byte)(existing.B * (1 - a) + color.B * a);

                    image[x, y] = new Rgba32(r, g, b, 255);
                }
            }

            colorIndex++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        image.SaveAsPng(outputPath);

        TerrainLogger.Info($"  Exported material debug image: {outputPath}");
    }

    /// <summary>
    ///     Gets statistics about painted layers.
    /// </summary>
    public Dictionary<string, PaintStatistics> GetPaintStatistics(Dictionary<string, byte[,]> layers)
    {
        var stats = new Dictionary<string, PaintStatistics>();

        foreach (var (materialName, layer) in layers)
        {
            var height = layer.GetLength(0);
            var width = layer.GetLength(1);

            var fullCoveragePixels = 0;
            var partialCoveragePixels = 0;
            long totalAlpha = 0;

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = layer[y, x];
                if (alpha == 255)
                    fullCoveragePixels++;
                else if (alpha > 0)
                    partialCoveragePixels++;

                totalAlpha += alpha;
            }

            stats[materialName] = new PaintStatistics
            {
                FullCoveragePixels = fullCoveragePixels,
                PartialCoveragePixels = partialCoveragePixels,
                TotalPixels = width * height,
                AverageAlpha = totalAlpha / (float)(width * height)
            };
        }

        return stats;
    }
}

/// <summary>
///     Statistics about a painted material layer.
/// </summary>
public class PaintStatistics
{
    /// <summary>
    ///     Number of pixels with full coverage (alpha = 255).
    /// </summary>
    public int FullCoveragePixels { get; init; }

    /// <summary>
    ///     Number of pixels with partial coverage (0 &lt; alpha &lt; 255).
    /// </summary>
    public int PartialCoveragePixels { get; init; }

    /// <summary>
    ///     Total number of pixels in the layer.
    /// </summary>
    public int TotalPixels { get; init; }

    /// <summary>
    ///     Average alpha value across all pixels.
    /// </summary>
    public float AverageAlpha { get; init; }

    /// <summary>
    ///     Percentage of pixels with any coverage.
    /// </summary>
    public float CoveragePercentage =>
        (FullCoveragePixels + PartialCoveragePixels) / (float)TotalPixels * 100f;

    public override string ToString()
    {
        return
            $"Full: {FullCoveragePixels:N0}, Partial: {PartialCoveragePixels:N0}, Coverage: {CoveragePercentage:F2}%";
    }
}