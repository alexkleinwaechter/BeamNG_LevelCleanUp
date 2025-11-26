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
        
        // CRITICAL: Check elevation range
        var minElev = geometry.CrossSections.Min(cs => cs.TargetElevation);
        var maxElev = geometry.CrossSections.Max(cs => cs.TargetElevation);
        Console.WriteLine($"Cross-section elevation range: {minElev:F2}m - {maxElev:F2}m");
        
        int modifiedPixels = 0;
        int roadPixels = 0;  // Count pixels strictly within road width
        float maxAffectedDistance = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        
        int gridWidth = (width + GridCellSize - 1) / GridCellSize;
        int gridHeight = (height + GridCellSize - 1) / GridCellSize;
        
        Console.WriteLine($"Processing {width}x{height} heightmap with {gridWidth}x{gridHeight} grid...");
        Console.WriteLine($"Road width: {parameters.RoadWidthMeters}m, Affected range: {maxAffectedDistance:F1}m");
        
        var startTime = DateTime.Now;
        
        // Track min/max heights being applied
        float minAppliedHeight = float.MaxValue;
        float maxAppliedHeight = float.MinValue;
        
        for (int y = 0; y < height; y++)
        {
            if (y % 200 == 0 && y > 0)
            {
                float progress = (y / (float)height) * 100f;
                var elapsed = DateTime.Now - startTime;
                var estimated = elapsed.TotalSeconds > 0 ? TimeSpan.FromSeconds(elapsed.TotalSeconds / (y / (float)height)) : TimeSpan.Zero;
                var remaining = estimated - elapsed;
                Console.WriteLine($"  Progress: {progress:F1}% (road:{roadPixels:N0}, blend:{modifiedPixels - roadPixels:N0}) - ETA: {remaining:mm\\:ss}");
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
                    
                    // Track if strictly within road
                    if (distanceToCenter <= halfRoadWidth)
                    {
                        roadPixels++;
                        minAppliedHeight = Math.Min(minAppliedHeight, newHeight);
                        maxAppliedHeight = Math.Max(maxAppliedHeight, newHeight);
                    }
                }
            }
        }
        
        Console.WriteLine($"Blending complete:");
        Console.WriteLine($"  Total pixels modified: {modifiedPixels:N0} ({(modifiedPixels / (float)(width * height) * 100):F2}%)");
        Console.WriteLine($"  Road surface pixels: {roadPixels:N0}");
        Console.WriteLine($"  Blend zone pixels: {modifiedPixels - roadPixels:N0}");
        
        if (roadPixels > 0)
        {
            Console.WriteLine($"  Road elevation range applied: {minAppliedHeight:F2}m - {maxAppliedHeight:F2}m");
            Console.WriteLine($"  Road elevation span: {maxAppliedHeight - minAppliedHeight:F2}m");
        }
        else
        {
            Console.WriteLine($"  ?? WARNING: NO ROAD PIXELS MODIFIED!");
            Console.WriteLine($"     Check: Road layer image has white pixels?");
            Console.WriteLine($"     Check: Cross-sections were created?");
            Console.WriteLine($"     Check: RoadWidthMeters ({parameters.RoadWidthMeters}m) is reasonable?");
        }
        
        Console.WriteLine($"  Processing time: {(DateTime.Now - startTime).TotalSeconds:F1}s");
        
        _sectionsByIndex = null;
        _sectionsByPathLocal = null;
        return modifiedHeightMap;
    }
    
    private struct RoadPoint { public float DistanceFromRoad; public float Elevation; }
    
    private RoadPoint? FindClosestPointOnRoad(Vector2 worldPos, CrossSection nearestSection)
    {
        float minDistance = float.MaxValue;
        float interpolatedElevation = nearestSection.TargetElevation;
        bool foundOnSegment = false;
        
        if (_sectionsByPathLocal != null)
        {
            var prevKey = (nearestSection.PathId, nearestSection.LocalIndex - 1);
            var nextKey = (nearestSection.PathId, nearestSection.LocalIndex + 1);
            
            if (_sectionsByPathLocal.TryGetValue(prevKey, out var prevSection) && !prevSection.IsExcluded)
            {
                var r = GetDistanceAndElevationOnSegment(worldPos, prevSection, nearestSection);
                if (r.Distance < minDistance) 
                { 
                    minDistance = r.Distance; 
                    interpolatedElevation = r.Elevation;
                    foundOnSegment = true;
                }
            }
            if (_sectionsByPathLocal.TryGetValue(nextKey, out var nextSection) && !nextSection.IsExcluded)
            {
                var r = GetDistanceAndElevationOnSegment(worldPos, nearestSection, nextSection);
                if (r.Distance < minDistance) 
                { 
                    minDistance = r.Distance; 
                    interpolatedElevation = r.Elevation;
                    foundOnSegment = true;
                }
            }
        }
        
        // Also check perpendicular distance to this cross-section's normal line
        // This is KEY for proper road width calculation on curves
        var perpendicularResult = GetPerpendicularDistanceToSection(worldPos, nearestSection);
        if (perpendicularResult.Distance < minDistance)
        {
            minDistance = perpendicularResult.Distance;
            interpolatedElevation = nearestSection.TargetElevation;
        }
        
        return new RoadPoint { DistanceFromRoad = minDistance, Elevation = interpolatedElevation };
    }
    
    /// <summary>
    /// Calculates perpendicular distance from point to cross-section's normal line.
    /// This ensures accurate distance measurement perpendicular to road direction.
    /// </summary>
    private (float Distance, float Elevation) GetPerpendicularDistanceToSection(Vector2 point, CrossSection section)
    {
        // Project point onto the normal line passing through section center
        Vector2 toPoint = point - section.CenterPoint;
        
        // Distance along the normal direction
        float alongNormal = Vector2.Dot(toPoint, section.NormalDirection);
        
        // Distance perpendicular to normal (along road direction)
        Vector2 tangent = new Vector2(-section.NormalDirection.Y, section.NormalDirection.X);
        float alongTangent = Vector2.Dot(toPoint, tangent);
        
        // The perpendicular distance is the component along the normal
        float perpendicularDistance = MathF.Abs(alongNormal);
        
        return (perpendicularDistance, section.TargetElevation);
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
        
        // Inside road: use pure road elevation (completely flat)
        if (distanceToCenter <= halfRoadWidth) 
            return roadElevation;
        
        // Outside affected range: use original terrain
        float totalAffectedRange = halfRoadWidth + parameters.TerrainAffectedRangeMeters;
        if (distanceToCenter >= totalAffectedRange)
            return originalHeight;
        
        // In blending zone: smooth transition
        float roadEdgeDistance = distanceToCenter - halfRoadWidth;
        float blendFactor = roadEdgeDistance / parameters.TerrainAffectedRangeMeters;
        blendFactor = Math.Clamp(blendFactor, 0.0f, 1.0f);
        
        // Apply blend function (cosine gives smoothest results)
        float t = BlendFunctions.Apply(blendFactor, parameters.BlendFunctionType);
        
        // Calculate height difference respecting embankment slope constraints
        float heightDiff = originalHeight - roadElevation;
        float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxAllowedDiff = roadEdgeDistance * maxSlopeRatio;
        
        // Clamp height difference to maximum slope
        if (MathF.Abs(heightDiff) > maxAllowedDiff)
        {
            heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
        }
        
        // Blend from road elevation to constrained terrain elevation
        return roadElevation + heightDiff * t;
    }
}
