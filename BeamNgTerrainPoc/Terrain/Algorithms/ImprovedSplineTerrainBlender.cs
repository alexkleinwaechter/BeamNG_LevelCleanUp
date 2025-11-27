using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Improved terrain blender using virtual heightfield upsampling for smooth results.
/// 
/// KEY IMPROVEMENTS:
/// 1. Internal upsampling (4x resolution) for sub-pixel precision
/// 2. Proper SDF-based distance calculation
/// 3. Hermite/Cubic blend functions (C² continuous)
/// 4. Iterative shoulder smoothing
/// 5. Gaussian downsampling with anti-aliasing
/// 6. ADAPTIVE optimization: single-pass for dense networks, per-path for scattered roads
/// 
/// ELIMINATES: Jagged edges, stairs, blocky artifacts
/// </summary>
public class ImprovedSplineTerrainBlender
{
    private const int DefaultUpscaleFactor = 1; // 1x = no upsampling by default
    private const int GridCellSize = 32; // For spatial indexing

    public float[,] BlendRoadWithTerrain(
        float[,] originalHeightMap,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        if (geometry.CrossSections.Count == 0)
        {
            Console.WriteLine("No cross-sections to blend");
            return (float[,])originalHeightMap.Clone();
        }

        Console.WriteLine("=== IMPROVED SPLINE-BASED ROAD SMOOTHING ===");

        int originalHeight = originalHeightMap.GetLength(0);
        int originalWidth = originalHeightMap.GetLength(1);

        // Determine upscale factor (can be made configurable)
        int upscaleFactor = DefaultUpscaleFactor;

        if (upscaleFactor == 1)
        {
            Console.WriteLine($"No upsampling (factor=1), processing at original resolution");
            
            // No upsampling - process directly on original heightmap
            var result = (float[,])originalHeightMap.Clone();
            
            // Create a temporary virtual heightfield wrapper (no actual upsampling)
            var directVirtualField = Processing.VirtualHeightfield.CreateFromHeightmap(
                result,
                1,
                metersPerPixel);
            
            ProcessRoadSmoothingOnVirtualHeightfield(
                directVirtualField,
                geometry,
                parameters,
                originalHeightMap);
            
            // Shoulder smoothing
            int shoulderIterations = 3;
            Console.WriteLine($"Applying iterative shoulder smoothing ({shoulderIterations} passes)...");
            ApplyShoulderSmoothing(
                directVirtualField,
                geometry,
                parameters,
                shoulderIterations);
            
            Console.WriteLine("=== IMPROVED SMOOTHING COMPLETE ===");
            return directVirtualField.GetData();
        }
        else
        {
            Console.WriteLine($"Using {upscaleFactor}x internal upsampling for smooth results");
            
            // Step 1: Create virtual heightfield (upsample)
            var virtualHeightfield = Processing.VirtualHeightfield.CreateFromHeightmap(
                originalHeightMap,
                upscaleFactor,
                metersPerPixel);

            // Step 2: Process road smoothing on high-res virtual buffer
            Console.WriteLine($"Processing road smoothing on {virtualHeightfield.Width}x{virtualHeightfield.Height} virtual heightfield...");

            ProcessRoadSmoothingOnVirtualHeightfield(
                virtualHeightfield,
                geometry,
                parameters,
                originalHeightMap);

            // Step 3: Iterative shoulder smoothing (only on modified zones)
            int shoulderIterations = 3; // Configurable
            Console.WriteLine($"Applying iterative shoulder smoothing ({shoulderIterations} passes)...");
            ApplyShoulderSmoothing(
                virtualHeightfield,
                geometry,
                parameters,
                shoulderIterations);

            // Step 4: Downsample back to original resolution with anti-aliasing
            var result = virtualHeightfield.DownsampleToHeightmap(originalWidth, originalHeight);

            Console.WriteLine("=== IMPROVED SMOOTHING COMPLETE ===");

            return result;
        }
    }

    /// <summary>
    /// Processes road smoothing on the virtual (upsampled) heightfield.
    /// Uses ADAPTIVE strategy: single-pass for dense networks, per-path for scattered roads.
    /// </summary>
    private void ProcessRoadSmoothingOnVirtualHeightfield(
        Processing.VirtualHeightfield virtualHeightfield,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float[,] originalHeightmap)
    {
        int height = virtualHeightfield.Height;
        int width = virtualHeightfield.Width;
        float metersPerPixel = virtualHeightfield.MetersPerPixel;

        float maxAffectedDistance = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;

        // Calculate per-path bounding boxes
        var pathBounds = CalculatePerPathBoundingBoxes(geometry.CrossSections, maxAffectedDistance, metersPerPixel, width, height);
        
        int totalPixels = width * height;
        int totalBoundingPixels = pathBounds.Sum(pb => pb.PixelCount);
        float coverageRatio = totalBoundingPixels / (float)totalPixels;
        
        Console.WriteLine($"  Per-path bounding box analysis:");
        Console.WriteLine($"    Full heightmap: {width}x{height} = {totalPixels:N0} pixels");
        Console.WriteLine($"    Number of paths (splines): {pathBounds.Count}");
        Console.WriteLine($"    Total bounding box pixels: {totalBoundingPixels:N0} ({(coverageRatio - 1) * 100:+0.0;-0.0}% overlap)");
        
        // Analyze largest path
        var largestPath = pathBounds.MaxBy(pb => pb.PixelCount);
        float largestCoverage = largestPath!.PixelCount / (float)totalPixels;
        
        // ADAPTIVE DECISION: Choose best strategy
        bool usePerPathOptimization = false;
        string strategyReason = "";
        
        if (coverageRatio > 1.2f)  // More than 20% overlap
        {
            strategyReason = $"bounding boxes overlap significantly ({coverageRatio:P0} total coverage)";
        }
        else if (largestCoverage > 0.35f)  // Single path covers > 35% of terrain
        {
            strategyReason = $"path {largestPath.PathId} covers {largestCoverage:P0} of terrain (mega-network)";
        }
        else if (pathBounds.Count > 20 && coverageRatio > 0.7f)
        {
            strategyReason = $"{pathBounds.Count} paths with {coverageRatio:P0} coverage (dense network)";
        }
        else if (pathBounds.Count == 1)
        {
            usePerPathOptimization = true;
            strategyReason = "single path detected";
        }
        else if (pathBounds.Count <= 5 && coverageRatio < 0.4f)
        {
            usePerPathOptimization = true;
            strategyReason = $"{pathBounds.Count} scattered paths ({coverageRatio:P0} coverage)";
        }
        else if (coverageRatio < 0.5f)
        {
            usePerPathOptimization = true;
            strategyReason = $"moderate coverage ({coverageRatio:P0}) with {pathBounds.Count} paths";
        }
        else
        {
            strategyReason = $"road network density ({pathBounds.Count} paths, {coverageRatio:P0} coverage)";
        }
        
        Console.WriteLine($"    Strategy: {(usePerPathOptimization ? "PER-PATH" : "SINGLE-PASS")} ({strategyReason})");

        // Build spatial index for cross-sections
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);

        int modifiedPixels = 0;
        int roadPixels = 0;
        int skippedPixels = 0;
        int pixelsProcessed = 0;

        var startTime = DateTime.Now;

        // Track original heights for proper blending
        var originalVirtualHeights = new float[height, width];

        if (usePerPathOptimization)
        {
            // PER-PATH MODE: Process each path's bounding box separately
            Console.WriteLine($"    Processing {pathBounds.Count} paths with separate bounding boxes:");
            
            foreach (var pathBound in pathBounds.OrderByDescending(pb => pb.PixelCount))
            {
                Console.WriteLine($"      Path {pathBound.PathId}: ({pathBound.Bounds.MinX},{pathBound.Bounds.MinY})-({pathBound.Bounds.MaxX},{pathBound.Bounds.MaxY}) = {pathBound.PixelCount:N0} pixels, {pathBound.SectionCount} sections");
            }
            
            foreach (var pathBound in pathBounds)
            {
                var bounds = pathBound.Bounds;
                
                // Pre-load original heights for this path's bounding box
                for (int y = bounds.MinY; y <= bounds.MaxY; y++)
                    for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                        originalVirtualHeights[y, x] = virtualHeightfield[y, x];

                int pathPixelsModified = 0;

                // Process only this path's bounding box
                for (int y = bounds.MinY; y <= bounds.MaxY; y++)
                {
                    for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                    {
                        pixelsProcessed++;
                        
                        var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);

                        var nearestSection = FindNearestCrossSection(
                            worldPos, x, y, spatialIndex,
                            width, height, maxAffectedDistance, metersPerPixel);

                        if (nearestSection == null || nearestSection.IsExcluded)
                        {
                            skippedPixels++;
                            continue;
                        }

                        // CRITICAL: Only process if nearest section belongs to THIS path
                        if (nearestSection.PathId != pathBound.PathId)
                        {
                            skippedPixels++;
                            continue;
                        }

                        var roadPoint = CalculateSignedDistanceAndElevation(
                            worldPos, nearestSection, geometry.CrossSections, parameters);

                        if (roadPoint == null)
                        {
                            skippedPixels++;
                            continue;
                        }

                        float distanceToCenter = roadPoint.Value.Distance;

                        if (distanceToCenter > maxAffectedDistance)
                        {
                            skippedPixels++;
                            continue;
                        }

                        float newHeight = CalculateBlendedHeightImproved(
                            roadPoint.Value.Elevation,
                            originalVirtualHeights[y, x],
                            distanceToCenter,
                            parameters);

                        virtualHeightfield[y, x] = newHeight;
                        modifiedPixels++;
                        pathPixelsModified++;

                        if (distanceToCenter <= halfRoadWidth)
                        {
                            roadPixels++;
                        }
                    }
                }
                
                if (pathPixelsModified > 0)
                {
                    Console.WriteLine($"        → Modified {pathPixelsModified:N0} pixels");
                }
            }
        }
        else
        {
            // SINGLE-PASS MODE: Process entire heightmap in one pass
            Console.WriteLine($"    Processing entire {width}x{height} heightmap in single pass...");
            
            // Pre-load all original heights
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    originalVirtualHeights[y, x] = virtualHeightfield[y, x];
            
            for (int y = 0; y < height; y++)
            {
                if (y % 1000 == 0 && y > 0)
                {
                    float progress = (y / (float)height) * 100f;
                    Console.WriteLine($"      Progress: {progress:F1}% (modified: {modifiedPixels:N0})");
                }

                for (int x = 0; x < width; x++)
                {
                    pixelsProcessed++;
                    
                    var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);

                    var nearestSection = FindNearestCrossSection(
                        worldPos, x, y, spatialIndex,
                        width, height, maxAffectedDistance, metersPerPixel);

                    if (nearestSection == null || nearestSection.IsExcluded)
                    {
                        skippedPixels++;
                        continue;
                    }

                    var roadPoint = CalculateSignedDistanceAndElevation(
                        worldPos, nearestSection, geometry.CrossSections, parameters);

                    if (roadPoint == null)
                    {
                        skippedPixels++;
                        continue;
                    }

                    float distanceToCenter = roadPoint.Value.Distance;

                    if (distanceToCenter > maxAffectedDistance)
                    {
                        skippedPixels++;
                        continue;
                    }

                    float newHeight = CalculateBlendedHeightImproved(
                        roadPoint.Value.Elevation,
                        originalVirtualHeights[y, x],
                        distanceToCenter,
                        parameters);

                    virtualHeightfield[y, x] = newHeight;
                    modifiedPixels++;

                    if (distanceToCenter <= halfRoadWidth)
                    {
                        roadPixels++;
                    }
                }
            }
        }

        var elapsed = DateTime.Now - startTime;
        Console.WriteLine($"  Blending complete in {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"    Pixels processed: {pixelsProcessed:N0} (vs {totalPixels:N0} full heightmap)");
        Console.WriteLine($"    Modified pixels: {modifiedPixels:N0} ({(modifiedPixels / (float)pixelsProcessed * 100):F2}% of processed)");
        Console.WriteLine($"    Road pixels: {roadPixels:N0}");
        Console.WriteLine($"    Skipped pixels: {skippedPixels:N0}");
        Console.WriteLine($"    Performance: {pixelsProcessed / elapsed.TotalSeconds:F0} pixels/sec");
    }

    /// <summary>
    /// Bounding box for a single path (spline).
    /// </summary>
    private class PathBoundingBox
    {
        public int PathId { get; set; }
        public (int MinX, int MinY, int MaxX, int MaxY) Bounds { get; set; }
        public int SectionCount { get; set; }
        public int PixelCount => (Bounds.MaxX - Bounds.MinX + 1) * (Bounds.MaxY - Bounds.MinY + 1);
    }

    /// <summary>
    /// Calculates separate bounding boxes for each path (spline).
    /// This is MUCH more efficient than one giant bounding box when you have multiple scattered roads!
    /// </summary>
    private List<PathBoundingBox> CalculatePerPathBoundingBoxes(
        List<CrossSection> crossSections,
        float maxAffectedDistanceMeters,
        float metersPerPixel,
        int width,
        int height)
    {
        var result = new List<PathBoundingBox>();
        
        // Convert margin to pixels
        int marginPixels = (int)Math.Ceiling(maxAffectedDistanceMeters / metersPerPixel);

        // Group cross-sections by PathId
        var pathGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();

        foreach (var pathGroup in pathGroups)
        {
            var sections = pathGroup.ToList();
            
            if (sections.Count == 0)
                continue;

            // Find min/max for THIS path only
            float minWorldX = sections.Min(cs => cs.CenterPoint.X);
            float maxWorldX = sections.Max(cs => cs.CenterPoint.X);
            float minWorldY = sections.Min(cs => cs.CenterPoint.Y);
            float maxWorldY = sections.Max(cs => cs.CenterPoint.Y);

            // Convert to pixel coordinates with margin
            int minX = Math.Max(0, (int)((minWorldX / metersPerPixel) - marginPixels));
            int maxX = Math.Min(width - 1, (int)((maxWorldX / metersPerPixel) + marginPixels));
            int minY = Math.Max(0, (int)((minWorldY / metersPerPixel) - marginPixels));
            int maxY = Math.Min(height - 1, (int)((maxWorldY / metersPerPixel) + marginPixels));

            result.Add(new PathBoundingBox
            {
                PathId = pathGroup.Key,
                Bounds = (minX, minY, maxX, maxY),
                SectionCount = sections.Count
            });
        }

        return result;
    }

    /// <summary>
    /// Calculates tight bounding box around road cross-sections including affected range margin.
    /// NOTE: This is now primarily used by shoulder smoothing. Main blending uses per-path boxes.
    /// </summary>
    private (int MinX, int MinY, int MaxX, int MaxY) CalculateRoadBoundingBox(
        List<CrossSection> crossSections,
        float maxAffectedDistanceMeters,
        float metersPerPixel,
        int width,
        int height)
    {
        if (crossSections.Count == 0)
            return (0, 0, width - 1, height - 1);

        // Convert margin to pixels
        int marginPixels = (int)Math.Ceiling(maxAffectedDistanceMeters / metersPerPixel);

        // Find min/max in world coordinates
        float minWorldX = crossSections.Min(cs => cs.CenterPoint.X);
        float maxWorldX = crossSections.Max(cs => cs.CenterPoint.X);
        float minWorldY = crossSections.Min(cs => cs.CenterPoint.Y);
        float maxWorldY = crossSections.Max(cs => cs.CenterPoint.Y);

        // Convert to pixel coordinates with margin
        int minX = Math.Max(0, (int)((minWorldX / metersPerPixel) - marginPixels));
        int maxX = Math.Min(width - 1, (int)((maxWorldX / metersPerPixel) + marginPixels));
        int minY = Math.Max(0, (int)((minWorldY / metersPerPixel) - marginPixels));
        int maxY = Math.Min(height - 1, (int)((maxWorldY / metersPerPixel) + marginPixels));

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Processes road smoothing on the virtual (upsampled) heightfield.
    /// Uses SDF-based distance calculation for accurate blending.
    /// OPTIMIZED: Only processes pixels near the road (based on bounding box + margin)
    /// </summary>
    private void ProcessRoadSmoothingOnVirtualHeightfield_Old(
        Processing.VirtualHeightfield virtualHeightfield,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float[,] originalHeightmap)
    {
        int height = virtualHeightfield.Height;
        int width = virtualHeightfield.Width;
        float metersPerPixel = virtualHeightfield.MetersPerPixel;

        float maxAffectedDistance = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;

        // OPTIMIZATION 1: Calculate bounding box around all cross-sections
        var bounds = CalculateRoadBoundingBox(geometry.CrossSections, maxAffectedDistance, metersPerPixel, width, height);
        
        int totalPixels = width * height;
        int boundingBoxPixels = (bounds.MaxX - bounds.MinX + 1) * (bounds.MaxY - bounds.MinY + 1);
        float reductionPercent = (1 - boundingBoxPixels / (float)totalPixels) * 100f;
        
        Console.WriteLine($"  Bounding box optimization:");
        Console.WriteLine($"    Full heightmap: {width}x{height} = {totalPixels:N0} pixels");
        Console.WriteLine($"    Road bounding box: ({bounds.MinX},{bounds.MinY}) to ({bounds.MaxX},{bounds.MaxY})");
        Console.WriteLine($"    Processing only: {boundingBoxPixels:N0} pixels ({reductionPercent:F1}% reduction)");

        // Build spatial index for cross-sections (only within bounding box)
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);

        int modifiedPixels = 0;
        int roadPixels = 0;
        int skippedPixels = 0;

        var startTime = DateTime.Now;

        // Track original heights for proper blending
        var originalVirtualHeights = new float[height, width];
        for (int y = bounds.MinY; y <= bounds.MaxY; y++)
            for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                originalVirtualHeights[y, x] = virtualHeightfield[y, x];

        // OPTIMIZATION 2: Only process pixels within bounding box
        for (int y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            if ((y - bounds.MinY) % 500 == 0 && y > bounds.MinY)
            {
                float progress = ((y - bounds.MinY) / (float)(bounds.MaxY - bounds.MinY + 1)) * 100f;
                Console.WriteLine($"  Progress: {progress:F1}% (modified: {modifiedPixels:N0}, skipped: {skippedPixels:N0})");
            }

            for (int x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);

                // Find nearest cross-section using spatial index
                var nearestSection = FindNearestCrossSection(
                    worldPos, x, y, spatialIndex,
                    width, height, maxAffectedDistance, metersPerPixel);

                if (nearestSection == null || nearestSection.IsExcluded)
                {
                    skippedPixels++;
                    continue;
                }

                // Calculate SDF-based distance and elevation
                var roadPoint = CalculateSignedDistanceAndElevation(
                    worldPos, nearestSection, geometry.CrossSections, parameters);

                if (roadPoint == null)
                {
                    skippedPixels++;
                    continue;
                }

                float distanceToCenter = roadPoint.Value.Distance;

                // OPTIMIZATION 3: Early skip if outside affected range
                if (distanceToCenter > maxAffectedDistance)
                {
                    skippedPixels++;
                    continue;
                }

                float newHeight = CalculateBlendedHeightImproved(
                    roadPoint.Value.Elevation,
                    originalVirtualHeights[y, x],
                    distanceToCenter,
                    parameters);

                virtualHeightfield[y, x] = newHeight;
                modifiedPixels++;

                if (distanceToCenter <= halfRoadWidth)
                {
                    roadPixels++;
                }
            }
        }

        var elapsed = DateTime.Now - startTime;
        Console.WriteLine($"  Blending complete in {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Pixels in bounding box: {boundingBoxPixels:N0}");
        Console.WriteLine($"  Modified pixels: {modifiedPixels:N0} ({(modifiedPixels / (float)boundingBoxPixels * 100):F2}% of box)");
        Console.WriteLine($"  Road pixels: {roadPixels:N0}");
        Console.WriteLine($"  Skipped (outside road): {skippedPixels:N0}");
        Console.WriteLine($"  Performance: {modifiedPixels / elapsed.TotalSeconds:F0} pixels/sec");
    }

    /// <summary>
    /// Calculates tight bounding box around road cross-sections including affected range margin.
    /// </summary>
    private (int MinX, int MinY, int MaxX, int MaxY) CalculateRoadBoundingBox_Old(
        List<CrossSection> crossSections,
        float maxAffectedDistanceMeters,
        float metersPerPixel,
        int width,
        int height)
    {
        if (crossSections.Count == 0)
            return (0, 0, width - 1, height - 1);

        // Convert margin to pixels
        int marginPixels = (int)Math.Ceiling(maxAffectedDistanceMeters / metersPerPixel);

        // Find min/max in world coordinates
        float minWorldX = crossSections.Min(cs => cs.CenterPoint.X);
        float maxWorldX = crossSections.Max(cs => cs.CenterPoint.X);
        float minWorldY = crossSections.Min(cs => cs.CenterPoint.Y);
        float maxWorldY = crossSections.Max(cs => cs.CenterPoint.Y);

        // Convert to pixel coordinates with margin
        int minX = Math.Max(0, (int)((minWorldX / metersPerPixel) - marginPixels));
        int maxX = Math.Min(width - 1, (int)((maxWorldX / metersPerPixel) + marginPixels));
        int minY = Math.Max(0, (int)((minWorldY / metersPerPixel) - marginPixels));
        int maxY = Math.Min(height - 1, (int)((maxWorldY / metersPerPixel) + marginPixels));

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Calculates signed distance from world position to road spline and interpolated elevation.
    /// This is the KEY improvement - proper SDF instead of grid-based approximation.
    /// </summary>
    private struct RoadPointInfo
    {
        public float Distance; // Perpendicular distance from road center
        public float Elevation; // Interpolated elevation at nearest point
    }

    private RoadPointInfo? CalculateSignedDistanceAndElevation(
        Vector2 worldPos,
        CrossSection nearestSection,
        List<CrossSection> allSections,
        RoadSmoothingParameters parameters)
    {
        float minDistance = float.MaxValue;
        float interpolatedElevation = nearestSection.TargetElevation;

        // METHOD 1: Perpendicular distance to nearest cross-section's normal line
        var perpDist = GetPerpendicularDistance(worldPos, nearestSection);
        if (perpDist < minDistance)
        {
            minDistance = perpDist;
            interpolatedElevation = nearestSection.TargetElevation;
        }

        // METHOD 2: Check segments to adjacent cross-sections
        var prevSection = allSections.FirstOrDefault(s =>
            s.PathId == nearestSection.PathId &&
            s.LocalIndex == nearestSection.LocalIndex - 1 &&
            !s.IsExcluded);

        if (prevSection != null)
        {
            var segmentResult = GetDistanceToSegment(worldPos, prevSection, nearestSection);
            if (segmentResult.Distance < minDistance)
            {
                minDistance = segmentResult.Distance;
                interpolatedElevation = segmentResult.Elevation;
            }
        }

        var nextSection = allSections.FirstOrDefault(s =>
            s.PathId == nearestSection.PathId &&
            s.LocalIndex == nearestSection.LocalIndex + 1 &&
            !s.IsExcluded);

        if (nextSection != null)
        {
            var segmentResult = GetDistanceToSegment(worldPos, nearestSection, nextSection);
            if (segmentResult.Distance < minDistance)
            {
                minDistance = segmentResult.Distance;
                interpolatedElevation = segmentResult.Elevation;
            }
        }

        return new RoadPointInfo
        {
            Distance = minDistance,
            Elevation = interpolatedElevation
        };
    }

    /// <summary>
    /// Calculates perpendicular distance from point to cross-section's normal line.
    /// </summary>
    private float GetPerpendicularDistance(Vector2 point, CrossSection section)
    {
        Vector2 toPoint = point - section.CenterPoint;
        float alongNormal = MathF.Abs(Vector2.Dot(toPoint, section.NormalDirection));
        return alongNormal;
    }

    /// <summary>
    /// Calculates distance to road segment between two cross-sections with elevation interpolation.
    /// </summary>
    private (float Distance, float Elevation) GetDistanceToSegment(
        Vector2 point,
        CrossSection start,
        CrossSection end)
    {
        Vector2 segment = end.CenterPoint - start.CenterPoint;
        float segmentLengthSq = segment.LengthSquared();

        if (segmentLengthSq < 0.0001f)
            return (Vector2.Distance(point, start.CenterPoint), start.TargetElevation);

        Vector2 toPoint = point - start.CenterPoint;
        float t = Vector2.Dot(toPoint, segment) / segmentLengthSq;
        t = Math.Clamp(t, 0.0f, 1.0f);

        Vector2 closestPoint = start.CenterPoint + segment * t;
        float distance = Vector2.Distance(point, closestPoint);
        float elevation = start.TargetElevation + (end.TargetElevation - start.TargetElevation) * t;

        return (distance, elevation);
    }

    /// <summary>
    /// Improved blending function using Hermite interpolation for C² continuity.
    /// Creates smoother transitions than simple smoothstep.
    /// </summary>
    private float CalculateBlendedHeightImproved(
        float roadElevation,
        float originalHeight,
        float distanceToCenter,
        RoadSmoothingParameters parameters)
    {
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;

        // Zone 1: Inside road bed - force to road elevation (perfectly flat)
        if (distanceToCenter <= halfRoadWidth)
            return roadElevation;

        // Zone 3: Beyond affected range - use original terrain
        float totalAffectedRange = halfRoadWidth + parameters.TerrainAffectedRangeMeters;
        if (distanceToCenter >= totalAffectedRange)
            return originalHeight;

        // Zone 2: Shoulder/blend zone - smooth transition
        float blendZoneDistance = distanceToCenter - halfRoadWidth;
        float blendFactor = blendZoneDistance / parameters.TerrainAffectedRangeMeters;
        blendFactor = Math.Clamp(blendFactor, 0.0f, 1.0f);

        // Apply blend function (Hermite for extra smoothness)
        float t = HermiteBlend(blendFactor);

        // Apply embankment slope constraints
        float heightDiff = originalHeight - roadElevation;
        float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxAllowedDiff = blendZoneDistance * maxSlopeRatio;

        if (MathF.Abs(heightDiff) > maxAllowedDiff)
        {
            heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
        }

        // Blend from road to constrained terrain
        return roadElevation + heightDiff * t;
    }

    /// <summary>
    /// Hermite blend function (C² continuous - smooth value AND smooth derivative).
    /// Formula: 3t² - 2t³
    /// Even smoother than smoothstep.
    /// </summary>
    private float HermiteBlend(float t)
    {
        // SmoothStep: 3t² - 2t³
        return t * t * (3.0f - 2.0f * t);

        // Alternative: SmootherStep (C³ continuous): 6t⁵ - 15t⁴ + 10t³
        // return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
    }

    /// <summary>
    /// Applies iterative Gaussian smoothing ONLY to shoulder zone.
    /// Road bed is protected (kept perfectly flat).
    /// ADAPTIVE: Uses per-path boxes for scattered roads, full scan for dense networks
    /// </summary>
    private void ApplyShoulderSmoothing(
        Processing.VirtualHeightfield virtualHeightfield,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        int iterations)
    {
        int height = virtualHeightfield.Height;
        int width = virtualHeightfield.Width;
        float metersPerPixel = virtualHeightfield.MetersPerPixel;

        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
        float totalAffectedRange = halfRoadWidth + parameters.TerrainAffectedRangeMeters;

        // Calculate per-path bounding boxes
        var pathBounds = CalculatePerPathBoundingBoxes(geometry.CrossSections, totalAffectedRange, metersPerPixel, width, height);

        int totalPixels = width * height;
        int totalBoundingBoxPixels = pathBounds.Sum(pb => pb.PixelCount);
        float coverageRatio = totalBoundingBoxPixels / (float)totalPixels;
        var largestPath = pathBounds.MaxBy(pb => pb.PixelCount);
        float largestCoverage = largestPath!.PixelCount / (float)totalPixels;
        
        // ADAPTIVE DECISION: Same logic as main processing
        bool usePerPathOptimization = !(coverageRatio > 1.2f || largestCoverage > 0.35f || (pathBounds.Count > 20 && coverageRatio > 0.7f));
        
        if (usePerPathOptimization && pathBounds.Count > 1)
        {
            Console.WriteLine($"  Shoulder smoothing using per-path bounding boxes ({pathBounds.Count} paths, {totalBoundingBoxPixels:N0} pixels total)");
        }
        else
        {
            Console.WriteLine($"  Shoulder smoothing using single-pass mode (dense network: {coverageRatio:P0} coverage)");
        }

        // Build spatial index
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);

        // Identify shoulder pixels
        var shoulderMask = new bool[height, width];
        int shoulderPixelCount = 0;

        Console.WriteLine($"  Identifying shoulder zone pixels...");

        if (usePerPathOptimization && pathBounds.Count > 1)
        {
            // PER-PATH: Only scan within bounding boxes
            foreach (var pathBound in pathBounds)
            {
                var bounds = pathBound.Bounds;
                int pathShoulderPixels = 0;

                for (int y = bounds.MinY; y <= bounds.MaxY; y++)
                {
                    for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                    {
                        var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                        var nearestSection = FindNearestCrossSection(
                            worldPos, x, y, spatialIndex,
                            width, height, totalAffectedRange, metersPerPixel);

                        if (nearestSection == null || nearestSection.IsExcluded)
                            continue;

                        // Only process if nearest section belongs to THIS path
                        if (nearestSection.PathId != pathBound.PathId)
                            continue;

                        var roadPoint = CalculateSignedDistanceAndElevation(
                            worldPos, nearestSection, geometry.CrossSections, parameters);

                        if (roadPoint == null)
                            continue;

                        float dist = roadPoint.Value.Distance;

                        // Mark as shoulder if: outside road bed BUT inside affected range
                        if (dist > halfRoadWidth && dist <= totalAffectedRange)
                        {
                            shoulderMask[y, x] = true;
                            shoulderPixelCount++;
                            pathShoulderPixels++;
                        }
                    }
                }
                
                if (pathShoulderPixels > 0)
                {
                    Console.WriteLine($"      Path {pathBound.PathId}: {pathShoulderPixels:N0} shoulder pixels");
                }
            }
        }
        else
        {
            // SINGLE-PASS: Scan entire heightmap
            for (int y = 0; y < height; y++)
            {
                if (y % 1000 == 0 && y > 0)
                {
                    float progress = (y / (float)height) * 100f;
                    Console.WriteLine($"      Progress: {progress:F1}% (found: {shoulderPixelCount:N0} shoulder pixels)");
                }
                
                for (int x = 0; x < width; x++)
                {
                    var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                    var nearestSection = FindNearestCrossSection(
                        worldPos, x, y, spatialIndex,
                        width, height, totalAffectedRange, metersPerPixel);

                    if (nearestSection == null || nearestSection.IsExcluded)
                        continue;

                    var roadPoint = CalculateSignedDistanceAndElevation(
                        worldPos, nearestSection, geometry.CrossSections, parameters);

                    if (roadPoint == null)
                        continue;

                    float dist = roadPoint.Value.Distance;

                    if (dist > halfRoadWidth && dist <= totalAffectedRange)
                    {
                        shoulderMask[y, x] = true;
                        shoulderPixelCount++;
                    }
                }
            }
        }

        Console.WriteLine($"  Total shoulder zone: {shoulderPixelCount:N0} pixels ({(shoulderPixelCount / (float)totalPixels * 100):F2}% of heightmap)");

        if (shoulderPixelCount == 0)
        {
            Console.WriteLine("  No shoulder pixels to smooth, skipping smoothing iterations");
            return;
        }

        // Apply 5x5 Gaussian smoothing ONLY to shoulder pixels
        var temp = new float[height, width];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Console.WriteLine($"  Shoulder smoothing iteration {iteration + 1}/{iterations}...");

            // Copy current state
            if (usePerPathOptimization && pathBounds.Count > 1)
            {
                // Only copy bounding box regions
                foreach (var pathBound in pathBounds)
                {
                    var bounds = pathBound.Bounds;
                    for (int y = bounds.MinY; y <= bounds.MaxY; y++)
                        for (int x = bounds.MinX; x <= bounds.MaxX; x++)
                            temp[y, x] = virtualHeightfield[y, x];
                }
            }
            else
            {
                // Copy entire heightmap
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        temp[y, x] = virtualHeightfield[y, x];
            }

            // Smooth shoulder pixels only
            int smoothedCount = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!shoulderMask[y, x])
                        continue;

                    // Apply 5x5 Gaussian kernel
                    float smoothed = Apply5x5GaussianKernel(temp, x, y, width, height);
                    virtualHeightfield[y, x] = smoothed;
                    smoothedCount++;
                }
            }

            Console.WriteLine($"    Smoothed {smoothedCount:N0} shoulder pixels");
        }
    }

    /// <summary>
    /// Applies a 5x5 Gaussian kernel for local smoothing.
    /// Weights: Center=0.25, Adjacent=0.125, Diagonal=0.0625
    /// </summary>
    private float Apply5x5GaussianKernel(float[,] data, int x, int y, int width, int height)
    {
        // Simplified 5x5 Gaussian kernel
        // [1  4  6  4  1]
        // [4  16 24 16 4]
        // [6  24 36 24 6]
        // [4  16 24 16 4]
        // [1  4  6  4  1]
        // Divided by 256

        float sum = 0;
        float weightSum = 0;

        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, width - 1);
                int sy = Math.Clamp(y + dy, 0, height - 1);

                float weight = GetGaussian5x5Weight(dx, dy);
                sum += data[sy, sx] * weight;
                weightSum += weight;
            }
        }

        return sum / weightSum;
    }

    /// <summary>
    /// Gets weight for 5x5 Gaussian kernel.
    /// </summary>
    private float GetGaussian5x5Weight(int dx, int dy)
    {
        int adx = Math.Abs(dx);
        int ady = Math.Abs(dy);

        // Approximate Gaussian weights
        if (adx == 0 && ady == 0) return 36; // Center
        if (adx + ady == 1) return 24; // Adjacent (up/down/left/right)
        if (adx == 1 && ady == 1) return 16; // Diagonal
        if (adx + ady == 2) return 6; // Edge
        if ((adx == 2 && ady == 0) || (adx == 0 && ady == 2)) return 6;
        if (adx == 2 && ady == 1) return 4;
        if (adx == 1 && ady == 2) return 4;
        if (adx == 2 && ady == 2) return 1;

        return 1; // Corner
    }

    /// <summary>
    /// Builds spatial index for fast cross-section lookup.
    /// </summary>
    private Dictionary<(int, int), List<CrossSection>> BuildSpatialIndex(
        List<CrossSection> crossSections,
        float metersPerPixel,
        int width,
        int height)
    {
        var index = new Dictionary<(int, int), List<CrossSection>>();

        foreach (var section in crossSections)
        {
            if (section.IsExcluded) continue;

            int pixelX = (int)(section.CenterPoint.X / metersPerPixel);
            int pixelY = (int)(section.CenterPoint.Y / metersPerPixel);
            int gridX = pixelX / GridCellSize;
            int gridY = pixelY / GridCellSize;

            var key = (gridX, gridY);
            if (!index.ContainsKey(key))
                index[key] = new List<CrossSection>();

            index[key].Add(section);
        }

        return index;
    }

    /// <summary>
    /// Finds nearest cross-section using spatial index.
    /// </summary>
    private CrossSection? FindNearestCrossSection(
        Vector2 worldPos,
        int pixelX,
        int pixelY,
        Dictionary<(int, int), List<CrossSection>> spatialIndex,
        int width,
        int height,
        float maxDistance,
        float metersPerPixel)
    {
        int gridX = pixelX / GridCellSize;
        int gridY = pixelY / GridCellSize;
        int gridWidth = (width + GridCellSize - 1) / GridCellSize;
        int gridHeight = (height + GridCellSize - 1) / GridCellSize;

        CrossSection? nearest = null;
        float minDistance = float.MaxValue;

        // Search in expanding radius
        for (int radius = 0; radius <= 3; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Only check cells at current radius
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    int checkGridX = gridX + dx;
                    int checkGridY = gridY + dy;

                    if (checkGridX < 0 || checkGridX >= gridWidth ||
                        checkGridY < 0 || checkGridY >= gridHeight)
                        continue;

                    var key = (checkGridX, checkGridY);
                    if (spatialIndex.TryGetValue(key, out var sections))
                    {
                        foreach (var section in sections)
                        {
                            float distance = Vector2.Distance(worldPos, section.CenterPoint);
                            if (distance < minDistance)
                            {
                                minDistance = distance;
                                nearest = section;
                            }
                        }
                    }
                }
            }

            // Early exit if found something close
            if (nearest != null && minDistance < maxDistance)
                break;
        }

        return minDistance <= maxDistance ? nearest : null;
    }
}
