using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Blends road surfaces smoothly with surrounding terrain
/// </summary>
public class TerrainBlender
{
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
        
        // Build spatial index for faster nearest cross-section lookup
        var spatialIndex = BuildSpatialIndex(geometry.CrossSections, metersPerPixel, width, height);
        
        int modifiedPixels = 0;
        
        // Process each pixel
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                
                // Find nearest cross-section
                var nearestSection = FindNearestCrossSection(worldPos, geometry.CrossSections, spatialIndex);
                
                if (nearestSection == null || nearestSection.IsExcluded)
                    continue;
                
                // Calculate distance from pixel to road centerline
                float distanceToCenter = CalculateDistanceToRoad(worldPos, nearestSection);
                
                // Determine zone and blend
                float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
                float totalAffectedRange = halfRoadWidth + parameters.TerrainAffectedRangeMeters;
                
                if (distanceToCenter <= totalAffectedRange)
                {
                    float newHeight = CalculateBlendedHeight(
                        worldPos,
                        nearestSection,
                        originalHeightMap[y, x],
                        distanceToCenter,
                        parameters);
                    
                    modifiedHeightMap[y, x] = newHeight;
                    modifiedPixels++;
                }
            }
        }
        
        Console.WriteLine($"Blended {modifiedPixels} pixels");
        
        return modifiedHeightMap;
    }
    
    private Dictionary<(int, int), List<CrossSection>> BuildSpatialIndex(
        List<CrossSection> crossSections,
        float metersPerPixel,
        int width,
        int height)
    {
        // Simple grid-based spatial index
        int gridSize = 32; // pixels
        var index = new Dictionary<(int, int), List<CrossSection>>();
        
        foreach (var section in crossSections)
        {
            if (section.IsExcluded)
                continue;
            
            int gridX = (int)(section.CenterPoint.X / metersPerPixel / gridSize);
            int gridY = (int)(section.CenterPoint.Y / metersPerPixel / gridSize);
            
            var key = (gridX, gridY);
            if (!index.ContainsKey(key))
                index[key] = new List<CrossSection>();
            
            index[key].Add(section);
        }
        
        return index;
    }
    
    private CrossSection? FindNearestCrossSection(
        Vector2 worldPos,
        List<CrossSection> crossSections,
        Dictionary<(int, int), List<CrossSection>> spatialIndex)
    {
        // For simplicity, search nearby grid cells
        // In production, use a proper KD-tree or R-tree
        
        CrossSection? nearest = null;
        float minDistance = float.MaxValue;
        
        foreach (var section in crossSections)
        {
            if (section.IsExcluded)
                continue;
            
            float distance = Vector2.Distance(worldPos, section.CenterPoint);
            
            if (distance < minDistance)
            {
                minDistance = distance;
                nearest = section;
            }
        }
        
        return nearest;
    }
    
    private float CalculateDistanceToRoad(Vector2 worldPos, CrossSection crossSection)
    {
        // Calculate perpendicular distance to the cross-section line
        var toPoint = worldPos - crossSection.CenterPoint;
        
        // Project onto normal direction to get perpendicular distance
        float perpendicularDistance = MathF.Abs(Vector2.Dot(toPoint, crossSection.NormalDirection));
        
        return perpendicularDistance;
    }
    
    private float CalculateBlendedHeight(
        Vector2 worldPos,
        CrossSection crossSection,
        float originalHeight,
        float distanceToCenter,
        RoadSmoothingParameters parameters)
    {
        float halfRoadWidth = parameters.RoadWidthMeters / 2.0f;
        
        // Road Surface Zone
        if (distanceToCenter <= halfRoadWidth)
        {
            return crossSection.TargetElevation;
        }
        
        // Transition Zone
        float roadEdgeDistance = distanceToCenter - halfRoadWidth;
        float blendFactor = roadEdgeDistance / parameters.TerrainAffectedRangeMeters;
        
        // Apply blend function
        float t = BlendFunctions.Apply(blendFactor, parameters.BlendFunctionType);
        
        // Calculate height difference
        float heightDiff = originalHeight - crossSection.TargetElevation;
        
        // Apply side slope constraint
        float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxAllowedDiff = roadEdgeDistance * maxSlopeRatio;
        
        if (MathF.Abs(heightDiff) > maxAllowedDiff)
        {
            // Constrain to max slope (creates embankment or cutting)
            heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
        }
        
        // Blend between road elevation and constrained terrain
        return crossSection.TargetElevation + heightDiff * t;
    }
}
