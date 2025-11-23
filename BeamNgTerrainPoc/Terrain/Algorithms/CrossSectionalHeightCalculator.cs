using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates target elevations for road cross-sections using cross-sectional leveling approach
/// </summary>
public class CrossSectionalHeightCalculator : IHeightCalculator
{
    private const int MaxSlopeIterations = 50;
    
    public void CalculateTargetElevations(
        RoadGeometry geometry, 
        float[,] heightMap,
        float metersPerPixel)
    {
        if (geometry.CrossSections.Count == 0)
        {
            Console.WriteLine("No cross-sections to process");
            return;
        }
        
        // Step 1: Calculate initial target elevations from heightmap
        CalculateInitialElevations(geometry.CrossSections, heightMap, metersPerPixel);
        
        // Step 2: Apply longitudinal slope constraints
        ApplySlopeConstraints(geometry.CrossSections, geometry.Parameters.RoadMaxSlopeDegrees);
        
        Console.WriteLine($"Calculated target elevations for {geometry.CrossSections.Count} cross-sections");
    }
    
    private void CalculateInitialElevations(
        List<CrossSection> crossSections, 
        float[,] heightMap,
        float metersPerPixel)
    {
        int heightMapHeight = heightMap.GetLength(0);
        int heightMapWidth = heightMap.GetLength(1);
        
        foreach (var crossSection in crossSections)
        {
            if (crossSection.IsExcluded)
                continue;
            
            // Sample heights along the cross-section
            var heights = SampleCrossSectionHeights(
                crossSection, 
                heightMap, 
                heightMapWidth,
                heightMapHeight,
                metersPerPixel);
            
            if (heights.Count == 0)
            {
                // Fallback: use nearest pixel
                int pixelX = (int)(crossSection.CenterPoint.X / metersPerPixel);
                int pixelY = (int)(crossSection.CenterPoint.Y / metersPerPixel);
                
                pixelX = Math.Clamp(pixelX, 0, heightMapWidth - 1);
                pixelY = Math.Clamp(pixelY, 0, heightMapHeight - 1);
                
                crossSection.TargetElevation = heightMap[pixelY, pixelX];
            }
            else
            {
                // Weighted average favoring center (70% center, 15% each edge)
                float centerWeight = 0.7f;
                float edgeWeight = 0.15f;
                
                float centerHeight = heights[heights.Count / 2];
                float leftHeight = heights[0];
                float rightHeight = heights[^1];
                
                crossSection.TargetElevation = 
                    centerHeight * centerWeight + 
                    leftHeight * edgeWeight + 
                    rightHeight * edgeWeight;
            }
        }
    }
    
    private List<float> SampleCrossSectionHeights(
        CrossSection crossSection,
        float[,] heightMap,
        int heightMapWidth,
        int heightMapHeight,
        float metersPerPixel)
    {
        var heights = new List<float>();
        
        // Sample at 5 points across the road width
        int numSamples = 5;
        float halfWidth = crossSection.WidthMeters / 2.0f;
        
        for (int i = 0; i < numSamples; i++)
        {
            float t = i / (float)(numSamples - 1); // 0 to 1
            float offset = (t - 0.5f) * 2.0f * halfWidth; // -halfWidth to +halfWidth
            
            // Calculate sample position
            var samplePos = crossSection.CenterPoint + crossSection.NormalDirection * offset;
            
            // Convert to pixel coordinates
            int pixelX = (int)(samplePos.X / metersPerPixel);
            int pixelY = (int)(samplePos.Y / metersPerPixel);
            
            // Check bounds
            if (pixelX >= 0 && pixelX < heightMapWidth && 
                pixelY >= 0 && pixelY < heightMapHeight)
            {
                heights.Add(heightMap[pixelY, pixelX]);
            }
        }
        
        return heights;
    }
    
    private void ApplySlopeConstraints(List<CrossSection> crossSections, float maxSlopeDegrees)
    {
        if (crossSections.Count < 2)
            return;
        
        // Filter out excluded sections for slope calculation
        var activeSections = crossSections.Where(cs => !cs.IsExcluded).ToList();
        
        if (activeSections.Count < 2)
            return;
        
        float maxSlopeRatio = MathF.Tan(maxSlopeDegrees * MathF.PI / 180.0f);
        
        // Iterative approach to enforce slope constraints
        for (int iteration = 0; iteration < MaxSlopeIterations; iteration++)
        {
            bool changed = false;
            
            for (int i = 1; i < activeSections.Count; i++)
            {
                var cs1 = activeSections[i - 1];
                var cs2 = activeSections[i];
                
                float distance = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
                
                if (distance < 0.001f)
                    continue;
                
                float currentSlope = (cs2.TargetElevation - cs1.TargetElevation) / distance;
                
                if (MathF.Abs(currentSlope) > maxSlopeRatio)
                {
                    // Adjust elevations to meet constraint
                    float targetSlope = MathF.Sign(currentSlope) * maxSlopeRatio;
                    float midpoint = (cs2.TargetElevation + cs1.TargetElevation) / 2.0f;
                    
                    cs1.TargetElevation = midpoint - distance * targetSlope / 2.0f;
                    cs2.TargetElevation = midpoint + distance * targetSlope / 2.0f;
                    changed = true;
                }
            }
            
            if (!changed)
                break;
        }
    }
}
