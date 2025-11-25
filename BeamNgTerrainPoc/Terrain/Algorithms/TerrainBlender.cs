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
    
    // Cache for global index lookup
    private Dictionary<int, CrossSection>? _sectionsByIndex;
    // Cache for (PathId, LocalIndex) lookup
    private Dictionary<(int PathId, int LocalIndex), CrossSection>? _sectionsByPathLocal;
    
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
        
        _sectionsByIndex = geometry.CrossSections.ToDictionary(s => s.Index);
        _sectionsByPathLocal = geometry.CrossSections.ToDictionary(s => (s.PathId, s.LocalIndex));
        
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);
        Console.WriteLine($"Spatial index built with {spatialIndex.Count} grid cells");
        
        var sectionsWithElevation = geometry.CrossSections.Count(cs => cs.TargetElevation != 0);
        Console.WriteLine($"Cross-sections with non-zero elevation: {sectionsWithElevation}/{geometry.CrossSections.Count}");
        
        int modifiedPixels = 0;
        float maxAffectedDistance = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        
        int gridWidth = (width + GridCellSize - 1) / GridCellSize;
        int gridHeight = (height + GridCellSize - 1) / GridCellSize;
        
        Console.WriteLine($"Processing {width}x{height} heightmap with {gridWidth}x{gridHeight} grid...");
        Console.WriteLine($"Max affected distance: {maxAffectedDistance:F1} meters ({maxAffectedDistance / metersPerPixel:F0} pixels)");
        
        var startTime = DateTime.Now;
        
        for (int y = 0; y < height; y++)
        {
            if (y % 50 == 0 && y > 0)
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
                var nearestSection = FindNearestCrossSectionFast(worldPos, x, y, spatialIndex, gridWidth, gridHeight, maxAffectedDistance);
                if (nearestSection == null || nearestSection.IsExcluded) continue;
                
                var roadPoint = FindClosestPointOnRoad(worldPos, nearestSection);
                if (roadPoint == null) continue;
                
                float distanceToCenter = roadPoint.Value.DistanceFromRoad;
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
        _sectionsByIndex = null;
        _sectionsByPathLocal = null;
        return modifiedHeightMap;
    }
    
    private struct RoadPoint { public float DistanceFromRoad; public float Elevation; }
    
    private RoadPoint? FindClosestPointOnRoad(Vector2 worldPos, CrossSection nearestSection)
    {
        float minDistance = float.MaxValue;
        float interpolatedElevation = nearestSection.TargetElevation;
        
        if (_sectionsByPathLocal != null)
        {
            var prevKey = (nearestSection.PathId, nearestSection.LocalIndex - 1);
            var nextKey = (nearestSection.PathId, nearestSection.LocalIndex + 1);
            
            if (_sectionsByPathLocal.TryGetValue(prevKey, out var prevSection) && !prevSection.IsExcluded)
            {
                var r = GetDistanceAndElevationOnSegment(worldPos, prevSection, nearestSection);
                if (r.Distance < minDistance) { minDistance = r.Distance; interpolatedElevation = r.Elevation; }
            }
            if (_sectionsByPathLocal.TryGetValue(nextKey, out var nextSection) && !nextSection.IsExcluded)
            {
                var r = GetDistanceAndElevationOnSegment(worldPos, nearestSection, nextSection);
                if (r.Distance < minDistance) { minDistance = r.Distance; interpolatedElevation = r.Elevation; }
            }
        }
        
        float pointDistance = Vector2.Distance(worldPos, nearestSection.CenterPoint);
        if (pointDistance < minDistance)
        {
            minDistance = pointDistance;
            interpolatedElevation = nearestSection.TargetElevation;
        }
        
        return new RoadPoint { DistanceFromRoad = minDistance, Elevation = interpolatedElevation };
    }
    
    private (float Distance, float Elevation) GetDistanceAndElevationOnSegment(Vector2 point, CrossSection start, CrossSection end)
    {
        Vector2 segment = end.CenterPoint - start.CenterPoint;
        float segmentLength = segment.Length();
        if (segmentLength < 0.001f) return (Vector2.Distance(point, start.CenterPoint), start.TargetElevation);
        Vector2 toPoint = point - start.CenterPoint;
        float t = Vector2.Dot(toPoint, segment) / (segmentLength * segmentLength);
        t = Math.Clamp(t, 0.0f, 1.0f);
        Vector2 closestPoint = start.CenterPoint + segment * t;
        float distance = Vector2.Distance(point, closestPoint);
        float elevation = start.TargetElevation + (end.TargetElevation - start.TargetElevation) * t;
        return (distance, elevation);
    }
    
    private Dictionary<(int, int), List<CrossSection>> BuildSpatialIndex(List<CrossSection> crossSections, float metersPerPixel, int width, int height)
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
            if (!index.ContainsKey(key)) index[key] = new List<CrossSection>();
            index[key].Add(section);
        }
        return index;
    }
    
    private CrossSection? FindNearestCrossSectionFast(Vector2 worldPos, int pixelX, int pixelY, Dictionary<(int, int), List<CrossSection>> spatialIndex, int gridWidth, int gridHeight, float maxSearchDistance)
    {
        int gridX = pixelX / GridCellSize;
        int gridY = pixelY / GridCellSize;
        CrossSection? nearest = null; float minDistance = float.MaxValue; int searchRadius = 1; int maxSearchRadius = 3;
        while (searchRadius <= maxSearchRadius)
        {
            bool foundInThisRadius = false;
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    if (Math.Abs(dx) != searchRadius && Math.Abs(dy) != searchRadius) continue;
                    int checkGridX = gridX + dx; int checkGridY = gridY + dy;
                    if (checkGridX < 0 || checkGridX >= gridWidth || checkGridY < 0 || checkGridY >= gridHeight) continue;
                    var key = (checkGridX, checkGridY);
                    if (spatialIndex.TryGetValue(key, out var sections))
                    {
                        foreach (var section in sections)
                        {
                            float distance = Vector2.Distance(worldPos, section.CenterPoint);
                            if (distance < minDistance)
                            {
                                minDistance = distance; nearest = section; foundInThisRadius = true;
                            }
                        }
                    }
                }
            }
            if (foundInThisRadius && minDistance < maxSearchDistance) break;
            searchRadius++;
        }
        if (minDistance > maxSearchDistance) return null;
        return nearest;
    }
    
    private float CalculateBlendedHeight(Vector2 worldPos, float roadElevation, float originalHeight, float distanceToCenter, RoadSmoothingParameters parameters)
    {
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
        if (distanceToCenter <= halfRoadWidth) return roadElevation;
        float roadEdgeDistance = distanceToCenter - halfRoadWidth;
        float blendFactor = roadEdgeDistance / parameters.TerrainAffectedRangeMeters;
        float t = BlendFunctions.Apply(blendFactor, parameters.BlendFunctionType);
        float heightDiff = originalHeight - roadElevation;
        float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxAllowedDiff = roadEdgeDistance * maxSlopeRatio;
        if (MathF.Abs(heightDiff) > maxAllowedDiff) heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
        return roadElevation + heightDiff * t;
    }
}
