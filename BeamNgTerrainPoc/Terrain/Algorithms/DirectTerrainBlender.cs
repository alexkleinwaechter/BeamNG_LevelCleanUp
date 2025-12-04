using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Direct terrain blending using road mask only (Option A).
/// Simple, fast, robust - works with intersections and complex road networks.
/// </summary>
public class DirectTerrainBlender
{
    public float[,] BlendRoadWithTerrain(
        float[,] originalHeightMap,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int height = originalHeightMap.GetLength(0);
        int width = originalHeightMap.GetLength(1);
        var modifiedHeightMap = (float[,])originalHeightMap.Clone();
        
        Console.WriteLine($"Processing {width}x{height} heightmap with direct road mask approach...");
        
        var startTime = DateTime.Now;
        
        // Step 1: Calculate target elevations for all road pixels
        var roadElevations = CalculateRoadElevations(
            originalHeightMap, 
            geometry.RoadMask, 
            parameters, 
            metersPerPixel);
        
        // Step 2: Apply road elevations and blend with terrain
        int modifiedPixels = ApplyRoadSmoothing(
            originalHeightMap,
            modifiedHeightMap,
            geometry.RoadMask,
            roadElevations,
            parameters,
            metersPerPixel);
        
        Console.WriteLine($"Blended {modifiedPixels:N0} pixels in {(DateTime.Now - startTime).TotalSeconds:F1}s");
        
        return modifiedHeightMap;
    }
    
    private float[,] CalculateRoadElevations(
        float[,] heightMap,
        byte[,] roadMask,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);
        var elevations = new float[height, width];
        
        Console.WriteLine("Calculating road elevations...");
        
        // For each road pixel, calculate target elevation from surrounding terrain
        for (int y = 0; y < height; y++)
        {
            if (y % 100 == 0 && y > 0)
            {
                Console.WriteLine($"  Calculating elevations: {(y / (float)height * 100):F1}%");
            }
            
            for (int x = 0; x < width; x++)
            {
                if (roadMask[y, x] > 128)
                {
                    // Sample surrounding heights (within road width)
                    float targetElevation = CalculateLocalRoadElevation(
                        heightMap, 
                        roadMask, 
                        x, 
                        y, 
                        parameters.RoadWidthMeters, 
                        metersPerPixel);
                    
                    elevations[y, x] = targetElevation;
                }
                else
                {
                    elevations[y, x] = heightMap[y, x];
                }
            }
        }
        
        // Apply slope constraints
        elevations = ApplySlopeConstraints(elevations, roadMask, parameters, metersPerPixel);
        
        return elevations;
    }
    
    private float CalculateLocalRoadElevation(
        float[,] heightMap,
        byte[,] roadMask,
        int x,
        int y,
        float roadWidthMeters,
        float metersPerPixel)
    {
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);
        
        // Sample in a cross pattern along and perpendicular to road
        int sampleRadius = (int)(roadWidthMeters / (2 * metersPerPixel));
        sampleRadius = Math.Max(1, Math.Min(sampleRadius, 10));
        
        float sum = 0;
        int count = 0;
        
        // Sample along the road direction (horizontal and vertical)
        for (int dx = -sampleRadius; dx <= sampleRadius; dx++)
        {
            int sx = x + dx;
            if (sx >= 0 && sx < width && roadMask[y, sx] > 128)
            {
                sum += heightMap[y, sx];
                count++;
            }
        }
        
        for (int dy = -sampleRadius; dy <= sampleRadius; dy++)
        {
            int sy = y + dy;
            if (sy >= 0 && sy < height && roadMask[sy, x] > 128)
            {
                sum += heightMap[sy, x];
                count++;
            }
        }
        
        return count > 0 ? sum / count : heightMap[y, x];
    }
    
    private float[,] ApplySlopeConstraints(
        float[,] elevations,
        byte[,] roadMask,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int height = elevations.GetLength(0);
        int width = elevations.GetLength(1);
        var constrained = (float[,])elevations.Clone();
        
        Console.WriteLine("Applying slope constraints...");
        
        float maxSlopeRatio = MathF.Tan(parameters.RoadMaxSlopeDegrees * MathF.PI / 180.0f);
        float maxSlopeDiff = maxSlopeRatio * metersPerPixel;
        
        // Multiple passes to propagate constraints
        for (int pass = 0; pass < 5; pass++)
        {
            bool changed = false;
            
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (roadMask[y, x] == 0)
                        continue;
                    
                    float currentElev = constrained[y, x];
                    
                    // Check 4-connected neighbors
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            if (Math.Abs(dx) + Math.Abs(dy) != 1) // Only 4-connected
                                continue;
                            
                            int nx = x + dx;
                            int ny = y + dy;
                            
                            if (roadMask[ny, nx] > 128)
                            {
                                float neighborElev = constrained[ny, nx];
                                float diff = MathF.Abs(currentElev - neighborElev);
                                
                                if (diff > maxSlopeDiff)
                                {
                                    // Average to reduce slope
                                    float avg = (currentElev + neighborElev) / 2.0f;
                                    constrained[y, x] = avg;
                                    constrained[ny, nx] = avg;
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            }
            
            if (!changed)
                break;
        }
        
        return constrained;
    }
    
    private int ApplyRoadSmoothing(
        float[,] originalHeightMap,
        float[,] modifiedHeightMap,
        byte[,] roadMask,
        float[,] roadElevations,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int height = originalHeightMap.GetLength(0);
        int width = originalHeightMap.GetLength(1);
        int modifiedPixels = 0;
        
        Console.WriteLine("Applying road smoothing and blending...");
        
        float blendDistancePixels = parameters.TerrainAffectedRangeMeters / metersPerPixel;
        int maxSearchRadius = (int)Math.Ceiling(blendDistancePixels) + 1;
        
        for (int y = 0; y < height; y++)
        {
            if (y % 100 == 0 && y > 0)
            {
                Console.WriteLine($"  Blending: {(y / (float)height * 100):F1}%");
            }
            
            for (int x = 0; x < width; x++)
            {
                // Direct road pixel - always modify
                if (roadMask[y, x] > 128)
                {
                    modifiedHeightMap[y, x] = roadElevations[y, x];
                    modifiedPixels++;
                    continue;
                }
                
                // Check if this pixel is near enough to a road
                float distanceToRoad = CalculateDistanceToNearestRoad(roadMask, x, y, maxSearchRadius);
                
                // Skip if too far from any road
                if (distanceToRoad >= float.MaxValue || distanceToRoad > blendDistancePixels)
                    continue;
                
                // Transition zone - blend with terrain
                float roadElev = GetNearestRoadElevation(roadElevations, roadMask, x, y, maxSearchRadius);
                
                float blendFactor = distanceToRoad / blendDistancePixels;
                float t = BlendFunctions.Apply(blendFactor, parameters.BlendFunctionType);
                
                float originalHeight = originalHeightMap[y, x];
                float heightDiff = originalHeight - roadElev;
                
                // Apply side slope constraint
                float maxSlopeRatio = MathF.Tan(parameters.SideMaxSlopeDegrees * MathF.PI / 180.0f);
                float maxAllowedDiff = distanceToRoad * metersPerPixel * maxSlopeRatio;
                
                if (MathF.Abs(heightDiff) > maxAllowedDiff)
                {
                    heightDiff = MathF.Sign(heightDiff) * maxAllowedDiff;
                }
                
                modifiedHeightMap[y, x] = roadElev + heightDiff * t;
                modifiedPixels++;
            }
        }
        
        return modifiedPixels;
    }
    
    private float CalculateDistanceToNearestRoad(byte[,] roadMask, int x, int y, int maxRadius)
    {
        if (roadMask[y, x] > 128)
            return 0;
        
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        
        // Simple distance search in expanding square
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Only check perimeter of square
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;
                    
                    int nx = x + dx;
                    int ny = y + dy;
                    
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                    {
                        if (roadMask[ny, nx] > 128)
                        {
                            return MathF.Sqrt(dx * dx + dy * dy);
                        }
                    }
                }
            }
        }
        
        return float.MaxValue;
    }
    
    private float GetNearestRoadElevation(float[,] roadElevations, byte[,] roadMask, int x, int y, int maxRadius)
    {
        if (roadMask[y, x] > 128)
            return roadElevations[y, x];
        
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        
        // Find nearest road pixel elevation
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Only check perimeter of square
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;
                    
                    int nx = x + dx;
                    int ny = y + dy;
                    
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                    {
                        if (roadMask[ny, nx] > 128)
                        {
                            return roadElevations[ny, nx];
                        }
                    }
                }
            }
        }
        
        // Fallback - return original elevation if no road found nearby
        return roadElevations[y, x];
    }
}
