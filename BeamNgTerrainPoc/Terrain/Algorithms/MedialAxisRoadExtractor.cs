using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts road geometry using medial axis transform (skeleton) approach.
/// This is a basic implementation that can be enhanced with more sophisticated algorithms.
/// </summary>
public class MedialAxisRoadExtractor : IRoadExtractor
{
    public RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        
        // Step 1: Convert to binary mask (threshold at 128)
        var binaryMask = CreateBinaryMask(roadLayer);
        
        // Step 2: Find road pixels (any pixel with value > 0)
        var roadPixels = FindRoadPixels(binaryMask);
        
        if (roadPixels.Count == 0)
        {
            Console.WriteLine("Warning: No road pixels found in layer");
            return geometry;
        }
        
        // Step 3: Simple centerline extraction (can be enhanced with skeletonization)
        var centerlinePixels = ExtractCenterlineSimple(binaryMask, roadPixels);
        
        // Step 4: Convert pixel coordinates to world coordinates
        geometry.Centerline = ConvertToWorldCoordinates(centerlinePixels, metersPerPixel);
        
        // Step 5: Generate cross-sections at regular intervals
        if (geometry.Centerline.Count > 1)
        {
            geometry.CrossSections = GenerateCrossSections(
                geometry.Centerline, 
                parameters.RoadWidthMeters,
                parameters.CrossSectionIntervalMeters,
                metersPerPixel);
        }
        
        Console.WriteLine($"Extracted road geometry: {geometry.Centerline.Count} centerline points, " +
                         $"{geometry.CrossSections.Count} cross-sections");
        
        return geometry;
    }
    
    private byte[,] CreateBinaryMask(byte[,] roadLayer)
    {
        int height = roadLayer.GetLength(0);
        int width = roadLayer.GetLength(1);
        var binary = new byte[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                binary[y, x] = roadLayer[y, x] > 128 ? (byte)255 : (byte)0;
            }
        }
        
        return binary;
    }
    
    private List<Vector2> FindRoadPixels(byte[,] binaryMask)
    {
        int height = binaryMask.GetLength(0);
        int width = binaryMask.GetLength(1);
        var pixels = new List<Vector2>();
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (binaryMask[y, x] > 0)
                {
                    pixels.Add(new Vector2(x, y));
                }
            }
        }
        
        return pixels;
    }
    
    private List<Vector2> ExtractCenterlineSimple(byte[,] binaryMask, List<Vector2> roadPixels)
    {
        // Simple approach: Use distance transform to find approximate centerline
        // For a more sophisticated implementation, use Zhang-Suen thinning or similar
        
        int height = binaryMask.GetLength(0);
        int width = binaryMask.GetLength(1);
        
        // Calculate distance transform (distance to nearest non-road pixel)
        var distanceMap = CalculateDistanceTransform(binaryMask);
        
        // Find local maxima in distance transform (centerline candidates)
        var centerlinePixels = new List<Vector2>();
        
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                if (binaryMask[y, x] > 0 && IsLocalMaximum(distanceMap, x, y))
                {
                    centerlinePixels.Add(new Vector2(x, y));
                }
            }
        }
        
        // Order centerline pixels into a path
        return OrderCenterlinePixels(centerlinePixels);
    }
    
    private float[,] CalculateDistanceTransform(byte[,] binaryMask)
    {
        int height = binaryMask.GetLength(0);
        int width = binaryMask.GetLength(1);
        var distance = new float[height, width];
        
        // Initialize with large values for road pixels, 0 for non-road
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                distance[y, x] = binaryMask[y, x] > 0 ? 10000f : 0f;
            }
        }
        
        // Forward pass
        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                if (binaryMask[y, x] > 0)
                {
                    distance[y, x] = Math.Min(distance[y, x],
                        Math.Min(distance[y - 1, x] + 1,
                        Math.Min(distance[y, x - 1] + 1,
                        distance[y - 1, x - 1] + 1.414f)));
                }
            }
        }
        
        // Backward pass
        for (int y = height - 2; y >= 0; y--)
        {
            for (int x = width - 2; x >= 0; x--)
            {
                if (binaryMask[y, x] > 0)
                {
                    distance[y, x] = Math.Min(distance[y, x],
                        Math.Min(distance[y + 1, x] + 1,
                        Math.Min(distance[y, x + 1] + 1,
                        distance[y + 1, x + 1] + 1.414f)));
                }
            }
        }
        
        return distance;
    }
    
    private bool IsLocalMaximum(float[,] distanceMap, int x, int y)
    {
        float center = distanceMap[y, x];
        if (center < 2) return false; // Skip pixels too close to edge
        
        // Check 8-neighborhood
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (distanceMap[y + dy, x + dx] > center)
                    return false;
            }
        }
        
        return true;
    }
    
    private List<Vector2> OrderCenterlinePixels(List<Vector2> pixels)
    {
        if (pixels.Count == 0) return pixels;
        
        // Simple ordering: start from one end and connect nearest neighbors
        // For complex road networks, a more sophisticated approach is needed
        
        var ordered = new List<Vector2>();
        var remaining = new HashSet<Vector2>(pixels);
        
        // Start with first pixel
        var current = pixels[0];
        ordered.Add(current);
        remaining.Remove(current);
        
        // Connect to nearest unvisited neighbors
        while (remaining.Count > 0)
        {
            Vector2? nearest = null;
            float minDist = float.MaxValue;
            
            foreach (var candidate in remaining)
            {
                float dist = Vector2.Distance(current, candidate);
                if (dist < minDist && dist < 10) // Only connect nearby pixels
                {
                    minDist = dist;
                    nearest = candidate;
                }
            }
            
            if (nearest.HasValue)
            {
                current = nearest.Value;
                ordered.Add(current);
                remaining.Remove(current);
            }
            else
            {
                // Disconnected segment - start new chain
                if (remaining.Count > 0)
                {
                    current = remaining.First();
                    ordered.Add(current);
                    remaining.Remove(current);
                }
            }
        }
        
        return ordered;
    }
    
    private List<Vector2> ConvertToWorldCoordinates(List<Vector2> pixelCoords, float metersPerPixel)
    {
        return pixelCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();
    }
    
    private List<CrossSection> GenerateCrossSections(
        List<Vector2> centerline,
        float roadWidthMeters,
        float intervalMeters,
        float metersPerPixel)
    {
        var crossSections = new List<CrossSection>();
        
        if (centerline.Count < 2) return crossSections;
        
        // Calculate total path length
        float totalLength = 0;
        for (int i = 1; i < centerline.Count; i++)
        {
            totalLength += Vector2.Distance(centerline[i - 1], centerline[i]);
        }
        
        // Generate cross-sections at regular intervals
        float currentDistance = 0;
        int sectionIndex = 0;
        
        for (int i = 1; i < centerline.Count; i++)
        {
            var p1 = centerline[i - 1];
            var p2 = centerline[i];
            float segmentLength = Vector2.Distance(p1, p2);
            
            while (currentDistance <= segmentLength)
            {
                // Interpolate position along segment
                float t = segmentLength > 0 ? currentDistance / segmentLength : 0;
                var position = Vector2.Lerp(p1, p2, t);
                
                // Calculate tangent direction
                var tangent = segmentLength > 0 
                    ? Vector2.Normalize(p2 - p1) 
                    : new Vector2(1, 0);
                
                // Calculate normal (perpendicular to tangent)
                var normal = new Vector2(-tangent.Y, tangent.X);
                
                crossSections.Add(new CrossSection
                {
                    CenterPoint = position,
                    TangentDirection = tangent,
                    NormalDirection = normal,
                    WidthMeters = roadWidthMeters,
                    Index = sectionIndex++
                });
                
                currentDistance += intervalMeters;
            }
            
            currentDistance -= segmentLength;
        }
        
        // Always add a cross-section at the end
        if (crossSections.Count == 0 || Vector2.Distance(crossSections[^1].CenterPoint, centerline[^1]) > 0.1f)
        {
            var lastSegment = centerline[^1] - centerline[^2];
            var tangent = Vector2.Normalize(lastSegment);
            var normal = new Vector2(-tangent.Y, tangent.X);
            
            crossSections.Add(new CrossSection
            {
                CenterPoint = centerline[^1],
                TangentDirection = tangent,
                NormalDirection = normal,
                WidthMeters = roadWidthMeters,
                Index = sectionIndex
            });
        }
        
        return crossSections;
    }
}
