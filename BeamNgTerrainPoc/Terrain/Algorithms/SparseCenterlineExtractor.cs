using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts sparse, clean centerline points suitable for spline fitting.
/// Generates hundreds (not thousands) of points for efficient spline creation.
/// </summary>
public class SparseCenterlineExtractor
{
    private const int SampleGridSize = 64; // Sample every N pixels
    private const float SimplificationTolerance = 10.0f; // Douglas-Peucker tolerance
    
    public List<Vector2> ExtractCenterlinePoints(byte[,] roadMask, float metersPerPixel)
    {
        Console.WriteLine("Extracting sparse centerline for spline fitting...");
        
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        
        // Step 1: Calculate distance transform
        var distanceMap = CalculateDistanceTransform(roadMask);
        
        // Step 2: Sample at regular intervals to find centerline candidates
        var candidates = SampleCenterlineCandidates(roadMask, distanceMap, width, height);
        
        Console.WriteLine($"Found {candidates.Count} centerline candidates");
        
        if (candidates.Count < 2)
        {
            Console.WriteLine("Warning: Too few centerline points found");
            return candidates;
        }
        
        // Step 3: Order points into a path
        var orderedPath = OrderPoints(candidates);
        
        Console.WriteLine($"Ordered path: {orderedPath.Count} points");
        
        // Step 4: Simplify path using Douglas-Peucker
        var simplified = SimplifyPath(orderedPath, SimplificationTolerance);
        
        Console.WriteLine($"Simplified to {simplified.Count} points for spline");
        
        return simplified;
    }
    
    private List<Vector2> SampleCenterlineCandidates(
        byte[,] roadMask,
        float[,] distanceMap,
        int width,
        int height)
    {
        var candidates = new List<Vector2>();
        
        // Sample at grid intervals
        for (int y = SampleGridSize / 2; y < height; y += SampleGridSize)
        {
            for (int x = SampleGridSize / 2; x < width; x += SampleGridSize)
            {
                // Find best centerline point in this grid cell
                var localMax = FindLocalMaxInRegion(
                    distanceMap,
                    roadMask,
                    x, y,
                    SampleGridSize / 2);
                
                if (localMax.HasValue && distanceMap[(int)localMax.Value.Y, (int)localMax.Value.X] >= 2)
                {
                    candidates.Add(localMax.Value);
                }
            }
        }
        
        return candidates;
    }
    
    private Vector2? FindLocalMaxInRegion(
        float[,] distanceMap,
        byte[,] roadMask,
        int centerX,
        int centerY,
        int radius)
    {
        int height = distanceMap.GetLength(0);
        int width = distanceMap.GetLength(1);
        
        Vector2? maxPos = null;
        float maxValue = 0;
        
        int startY = Math.Max(0, centerY - radius);
        int endY = Math.Min(height - 1, centerY + radius);
        int startX = Math.Max(0, centerX - radius);
        int endX = Math.Min(width - 1, centerX + radius);
        
        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                if (roadMask[y, x] > 128 && distanceMap[y, x] > maxValue)
                {
                    maxValue = distanceMap[y, x];
                    maxPos = new Vector2(x, y);
                }
            }
        }
        
        return maxPos;
    }
    
    private float[,] CalculateDistanceTransform(byte[,] binaryMask)
    {
        int height = binaryMask.GetLength(0);
        int width = binaryMask.GetLength(1);
        var distance = new float[height, width];
        
        // Initialize
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                distance[y, x] = binaryMask[y, x] > 128 ? 10000f : 0f;
            }
        }
        
        // Forward pass
        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                if (binaryMask[y, x] > 128)
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
                if (binaryMask[y, x] > 128)
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
    
    private List<Vector2> OrderPoints(List<Vector2> points)
    {
        if (points.Count < 2)
            return points;
        
        var ordered = new List<Vector2>();
        var remaining = new HashSet<Vector2>(points);
        
        // Start with first point
        var current = points[0];
        ordered.Add(current);
        remaining.Remove(current);
        
        // Greedy nearest neighbor
        while (remaining.Count > 0)
        {
            Vector2? nearest = null;
            float minDist = float.MaxValue;
            
            foreach (var candidate in remaining)
            {
                float dist = Vector2.Distance(current, candidate);
                if (dist < minDist && dist < SampleGridSize * 2)
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
                // Disconnected - start new chain or stop
                break;
            }
        }
        
        return ordered;
    }
    
    private List<Vector2> SimplifyPath(List<Vector2> points, float tolerance)
    {
        if (points.Count < 3)
            return points;
        
        return DouglasPeucker(points, 0, points.Count - 1, tolerance);
    }
    
    private List<Vector2> DouglasPeucker(List<Vector2> points, int startIndex, int endIndex, float tolerance)
    {
        if (endIndex <= startIndex + 1)
        {
            return new List<Vector2> { points[startIndex], points[endIndex] };
        }
        
        // Find point with maximum distance
        float maxDistance = 0;
        int maxIndex = startIndex;
        
        var lineStart = points[startIndex];
        var lineEnd = points[endIndex];
        
        for (int i = startIndex + 1; i < endIndex; i++)
        {
            float distance = PerpendicularDistance(points[i], lineStart, lineEnd);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                maxIndex = i;
            }
        }
        
        // Recursively simplify
        if (maxDistance > tolerance)
        {
            var left = DouglasPeucker(points, startIndex, maxIndex, tolerance);
            var right = DouglasPeucker(points, maxIndex, endIndex, tolerance);
            
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }
        else
        {
            return new List<Vector2> { points[startIndex], points[endIndex] };
        }
    }
    
    private float PerpendicularDistance(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
    {
        var line = lineEnd - lineStart;
        float lineLength = line.Length();
        
        if (lineLength < 0.001f)
            return Vector2.Distance(point, lineStart);
        
        var toPoint = point - lineStart;
        float t = Vector2.Dot(toPoint, line) / (lineLength * lineLength);
        t = Math.Clamp(t, 0, 1);
        
        var closestPoint = lineStart + line * t;
        return Vector2.Distance(point, closestPoint);
    }
}
