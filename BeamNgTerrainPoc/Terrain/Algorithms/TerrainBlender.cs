using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Blends road surfaces smoothly with surrounding terrain using spline-based cross-sections
/// </summary>
public class TerrainBlender
{
    private const int GridCellSize = 32; // pixels per grid cell
    
    // Cache for index-based cross-section lookup
    private Dictionary<int, CrossSection>? _sectionsByIndex;
    
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
        
        int height = originalHeightMap.GetLength(0);
        int width = originalHeightMap.GetLength(1);
        var modifiedHeightMap = (float[,])originalHeightMap.Clone();
        
        Console.WriteLine($"Building spatial index for {geometry.CrossSections.Count} cross-sections...");
        
        // Build index-based lookup for O(1) access
        _sectionsByIndex = geometry.CrossSections.ToDictionary(s => s.Index);
        
        // Build spatial index for faster nearest cross-section lookup
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);
        
        Console.WriteLine($"Spatial index built with {spatialIndex.Count} grid cells");
        
        // Debug: Check if cross-sections have valid elevations
        var sectionsWithElevation = geometry.CrossSections.Count(cs => cs.TargetElevation != 0);
        Console.WriteLine($"Cross-sections with non-zero elevation: {sectionsWithElevation}/{geometry.CrossSections.Count}");
        
        int modifiedPixels = 0;
        float maxAffectedDistance = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        
        // Pre-calculate grid dimensions
        int gridWidth = (width + GridCellSize - 1) / GridCellSize;
        int gridHeight = (height + GridCellSize - 1) / GridCellSize;
        
        Console.WriteLine($"Processing {width}x{height} heightmap with {gridWidth}x{gridHeight} grid...");
        Console.WriteLine($"Max affected distance: {maxAffectedDistance:F1} meters ({maxAffectedDistance / metersPerPixel:F0} pixels)");
        
        var startTime = DateTime.Now;
        
        // Process each pixel
        for (int y = 0; y < height; y++)
        {
            if (y % 50 == 0 && y > 0)  // More frequent updates (was 100)
            {
                float progress = (y / (float)height) * 100f;
                var elapsed = DateTime.Now - startTime;
                var estimated = elapsed.TotalSeconds > 0 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / (y / (float)height)) : TimeSpan.Zero;
                var remaining = estimated - elapsed;
                Console.WriteLine($"  Progress: {progress:F1}% ({modifiedPixels:N0} pixels modified) - Elapsed: {elapsed:mm\\:ss}, ETA: {remaining:mm\\:ss}");
            }
            
            for (int x = 0; x < width; x++)
            {
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                
                // Find nearest cross-section using spatial index
                var nearestSection = FindNearestCrossSectionFast(
                    worldPos, 
                    x, 
                    y, 
                    spatialIndex, 
                    gridWidth, 
                    gridHeight,
                    maxAffectedDistance);
                
                if (nearestSection == null || nearestSection.IsExcluded)
                    continue;
                
                // Find the closest point on the road centerline and get interpolated data
                var roadPoint = FindClosestPointOnRoad(worldPos, nearestSection);
                
                if (roadPoint == null)
                    continue;
                
                // Calculate perpendicular distance from pixel to road centerline
                float distanceToCenter = roadPoint.Value.DistanceFromRoad;
                
                // Determine zone and blend
                float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
                float totalAffectedRange = halfRoadWidth + parameters.TerrainAffectedRangeMeters;
                
                if (distanceToCenter <= totalAffectedRange)
                {
                    float newHeight = CalculateBlendedHeight(
                        worldPos,
                        roadPoint.Value.Elevation,
                        originalHeightMap[y, x],
                        distanceToCenter,
                        parameters);
                    
                    modifiedHeightMap[y, x] = newHeight;
                    modifiedPixels++;
                }
            }
        }
        
        Console.WriteLine($"Blended {modifiedPixels:N0} pixels in {(DateTime.Now - startTime).TotalSeconds:F1}s");
        
        // Clear cache
        _sectionsByIndex = null;
        
        return modifiedHeightMap;
    }
    
    private struct RoadPoint
    {
        public float DistanceFromRoad;
        public float Elevation;
    }
    
    private RoadPoint? FindClosestPointOnRoad(Vector2 worldPos, CrossSection nearestSection)
    {
        float minDistance = float.MaxValue;
        float interpolatedElevation = nearestSection.TargetElevation;
        
        // Check segment before nearest cross-section
        if (nearestSection.Index > 0 && _sectionsByIndex!.TryGetValue(nearestSection.Index - 1, out var prevSection))
        {
            if (!prevSection.IsExcluded)
            {
                var result = GetDistanceAndElevationOnSegment(worldPos, prevSection, nearestSection);
                if (result.Distance < minDistance)
                {
                    minDistance = result.Distance;
                    interpolatedElevation = result.Elevation;
                }
            }
        }
        
        // Check segment after nearest cross-section
        if (_sectionsByIndex!.TryGetValue(nearestSection.Index + 1, out var nextSection))
        {
            if (!nextSection.IsExcluded)
            {
                var result = GetDistanceAndElevationOnSegment(worldPos, nearestSection, nextSection);
                if (result.Distance < minDistance)
                {
                    minDistance = result.Distance;
                    interpolatedElevation = result.Elevation;
                }
            }
        }
        
        // Also check distance to the nearest cross-section point itself
        float pointDistance = Vector2.Distance(worldPos, nearestSection.CenterPoint);
        if (pointDistance < minDistance)
        {
            minDistance = pointDistance;
            interpolatedElevation = nearestSection.TargetElevation;
        }
        
        return new RoadPoint
        {
            DistanceFromRoad = minDistance,
            Elevation = interpolatedElevation
        };
    }
    
    private (float Distance, float Elevation) GetDistanceAndElevationOnSegment(
        Vector2 point, 
        CrossSection start, 
        CrossSection end)
    {
        // Vector from segment start to end
        Vector2 segment = end.CenterPoint - start.CenterPoint;
        float segmentLength = segment.Length();
        
        if (segmentLength < 0.001f)
            return (Vector2.Distance(point, start.CenterPoint), start.TargetElevation);
        
        // Vector from segment start to point
        Vector2 toPoint = point - start.CenterPoint;
        
        // Project point onto segment to find parameter t [0, 1]
        float t = Vector2.Dot(toPoint, segment) / (segmentLength * segmentLength);
        
        // Clamp to segment bounds
        t = Math.Clamp(t, 0.0f, 1.0f);
        
        // Find closest point on segment
        Vector2 closestPoint = start.CenterPoint + segment * t;
        
        // Calculate perpendicular distance
        float distance = Vector2.Distance(point, closestPoint);
        
        // Interpolate elevation based on position along segment
        float elevation = start.TargetElevation + (end.TargetElevation - start.TargetElevation) * t;
        
        return (distance, elevation);
    }
    
    private Dictionary<(int, int), List<CrossSection>> BuildSpatialIndex(
        List<CrossSection> crossSections,
        float metersPerPixel,
        int width,
        int height)
    {
        var index = new Dictionary<(int, int), List<CrossSection>>();
        
        foreach (var section in crossSections)
        {
            if (section.IsExcluded)
                continue;
            
            // Convert world coordinates to pixel coordinates
            int pixelX = (int)(section.CenterPoint.X / metersPerPixel);
            int pixelY = (int)(section.CenterPoint.Y / metersPerPixel);
            
            // Calculate grid cell
            int gridX = pixelX / GridCellSize;
            int gridY = pixelY / GridCellSize;
            
            var key = (gridX, gridY);
            if (!index.ContainsKey(key))
                index[key] = new List<CrossSection>();
            
            index[key].Add(section);
        }
        
        return index;
    }
    
    private CrossSection? FindNearestCrossSectionFast(
        Vector2 worldPos,
        int pixelX,
        int pixelY,
        Dictionary<(int, int), List<CrossSection>> spatialIndex,
        int gridWidth,
        int gridHeight,
        float maxSearchDistance)
    {
        // Calculate grid cell for this pixel
        int gridX = pixelX / GridCellSize;
        int gridY = pixelY / GridCellSize;
        
        CrossSection? nearest = null;
        float minDistance = float.MaxValue;
        
        // Search in expanding radius
        int searchRadius = 1;
        int maxSearchRadius = 3;
        
        while (searchRadius <= maxSearchRadius)
        {
            bool foundInThisRadius = false;
            
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    // Only check cells in the current radius ring
                    if (Math.Abs(dx) != searchRadius && Math.Abs(dy) != searchRadius)
                        continue;
                    
                    int checkGridX = gridX + dx;
                    int checkGridY = gridY + dy;
                    
                    // Skip out-of-bounds cells
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
                                foundInThisRadius = true;
                            }
                        }
                    }
                }
            }
            
            // If we found something in this radius and it's close enough, stop searching
            if (foundInThisRadius && minDistance < maxSearchDistance)
                break;
            
            searchRadius++;
        }
        
        // Only return if within max search distance
        if (minDistance > maxSearchDistance)
            return null;
        
        return nearest;
    }
    
    private float CalculateBlendedHeight(
        Vector2 worldPos,
        float roadElevation,
        float originalHeight,
        float distanceToCenter,
        RoadSmoothingParameters parameters)
    {
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
        
        // Road Surface Zone - use interpolated road elevation
        if (distanceToCenter <= halfRoadWidth)
        {
            return roadElevation;
        }
        
        // Transition Zone
        float roadEdgeDistance = distanceToCenter - halfRoadWidth;
        float blendFactor = roadEdgeDistance / parameters.TerrainAffectedRangeMeters;
        
        // Apply blend function
        float t = BlendFunctions.Apply(blendFactor, parameters.BlendFunctionType);
        
        // Calculate height difference
        float heightDiff = originalHeight - roadElevation;
        
        // Apply side slope constraint
        float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxAllowedDiff = roadEdgeDistance * maxSlopeRatio;
        
        if (MathF.Abs(heightDiff) > maxAllowedDiff)
        {
            // Constrain to max slope (creates embankment or cutting)
            heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
        }
        
        // Blend between road elevation and constrained terrain
        return roadElevation + heightDiff * t;
    }
}
