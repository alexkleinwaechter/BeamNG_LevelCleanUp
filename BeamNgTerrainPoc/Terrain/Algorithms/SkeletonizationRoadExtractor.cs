using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts road centerline using Zhang-Suen morphological thinning/skeletonization.
/// Much more robust than distance transform - handles intersections naturally!
/// </summary>
public class SkeletonizationRoadExtractor
{
    /// <summary>
    /// Extract centerline from road mask using skeletonization
    /// </summary>
    public List<List<Vector2>> ExtractCenterlinePaths(byte[,] roadMask)
    {
        Console.WriteLine("Extracting road centerline using skeletonization...");
        
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        
        // Step 1: Convert to binary mask
        var binaryMask = ConvertToBinaryMask(roadMask);
        
        int roadPixels = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (binaryMask[y, x]) roadPixels++;
        
        Console.WriteLine($"Input: {roadPixels:N0} road pixels");
        
        // Step 2: Apply Zhang-Suen thinning to get skeleton
        var skeleton = ApplyZhangSuenThinning(binaryMask);
        
        int skeletonPixels = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (skeleton[y, x]) skeletonPixels++;
        
        Console.WriteLine($"Skeleton: {skeletonPixels:N0} centerline pixels");
        
        // Step 3: Extract connected components (path segments)
        var paths = ExtractConnectedPaths(skeleton);
        
        Console.WriteLine($"Found {paths.Count} path segments");
        for (int i = 0; i < Math.Min(5, paths.Count); i++)
        {
            Console.WriteLine($"  Path {i}: {paths[i].Count} points");
        }
        
        return paths;
    }
    
    /// <summary>
    /// Convert byte mask to boolean (threshold at 128)
    /// </summary>
    private bool[,] ConvertToBinaryMask(byte[,] roadMask)
    {
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        var binary = new bool[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                binary[y, x] = roadMask[y, x] > 128;
            }
        }
        
        return binary;
    }
    
    /// <summary>
    /// Zhang-Suen thinning algorithm - reduces shapes to 1-pixel wide skeleton
    /// </summary>
    private bool[,] ApplyZhangSuenThinning(bool[,] mask)
    {
        int height = mask.GetLength(0);
        int width = mask.GetLength(1);
        var result = (bool[,])mask.Clone();
        bool hasChanged;
        int iteration = 0;
        
        do
        {
            hasChanged = false;
            iteration++;
            
            // Sub-iteration 1
            var toRemove = new List<(int x, int y)>();
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (result[y, x] && ShouldRemovePixel(result, x, y, true))
                    {
                        toRemove.Add((x, y));
                    }
                }
            }
            
            foreach (var (x, y) in toRemove)
            {
                result[y, x] = false;
                hasChanged = true;
            }
            
            // Sub-iteration 2
            toRemove.Clear();
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (result[y, x] && ShouldRemovePixel(result, x, y, false))
                    {
                        toRemove.Add((x, y));
                    }
                }
            }
            
            foreach (var (x, y) in toRemove)
            {
                result[y, x] = false;
                hasChanged = true;
            }
            
            if (iteration % 10 == 0)
            {
                Console.WriteLine($"  Thinning iteration {iteration}...");
            }
            
        } while (hasChanged && iteration < 100); // Safety limit
        
        Console.WriteLine($"Thinning complete after {iteration} iterations");
        
        return result;
    }
    
    /// <summary>
    /// Check if pixel should be removed (Zhang-Suen conditions)
    /// </summary>
    private bool ShouldRemovePixel(bool[,] image, int x, int y, bool firstSubIteration)
    {
        // Get 8 neighbors (P2-P9 in clockwise order starting from top)
        bool p2 = image[y - 1, x];     // N
        bool p3 = image[y - 1, x + 1]; // NE
        bool p4 = image[y, x + 1];     // E
        bool p5 = image[y + 1, x + 1]; // SE
        bool p6 = image[y + 1, x];     // S
        bool p7 = image[y + 1, x - 1]; // SW
        bool p8 = image[y, x - 1];     // W
        bool p9 = image[y - 1, x - 1]; // NW
        
        // Count black neighbors
        int blackNeighbors = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) +
                            (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
        
        // Condition 1: 2 <= B(P1) <= 6
        if (blackNeighbors < 2 || blackNeighbors > 6)
            return false;
        
        // Condition 2: A(P1) = 1 (number of 0-1 transitions)
        int transitions = 0;
        bool[] neighbors = { p2, p3, p4, p5, p6, p7, p8, p9, p2 }; // Wrap around
        for (int i = 0; i < 8; i++)
        {
            if (!neighbors[i] && neighbors[i + 1])
                transitions++;
        }
        
        if (transitions != 1)
            return false;
        
        // Conditions 3 & 4 differ between sub-iterations
        if (firstSubIteration)
        {
            // Condition 3: P2 * P4 * P6 = 0
            if (p2 && p4 && p6)
                return false;
            
            // Condition 4: P4 * P6 * P8 = 0
            if (p4 && p6 && p8)
                return false;
        }
        else
        {
            // Condition 3: P2 * P4 * P8 = 0
            if (p2 && p4 && p8)
                return false;
            
            // Condition 4: P2 * P6 * P8 = 0
            if (p2 && p6 && p8)
                return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Extract connected paths from skeleton using connected component analysis
    /// </summary>
    private List<List<Vector2>> ExtractConnectedPaths(bool[,] skeleton)
    {
        int height = skeleton.GetLength(0);
        int width = skeleton.GetLength(1);
        var visited = new bool[height, width];
        var paths = new List<List<Vector2>>();
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (skeleton[y, x] && !visited[y, x])
                {
                    var path = new List<Vector2>();
                    TracePath(skeleton, visited, x, y, path);
                    
                    if (path.Count >= 3) // Minimum points for a valid path
                    {
                        // Order points to form continuous path
                        var orderedPath = OrderPathPoints(path);
                        paths.Add(orderedPath);
                    }
                }
            }
        }
        
        return paths;
    }
    
    /// <summary>
    /// Trace a connected path using depth-first search
    /// </summary>
    private void TracePath(bool[,] skeleton, bool[,] visited, int startX, int startY, List<Vector2> path)
    {
        var stack = new Stack<(int x, int y)>();
        stack.Push((startX, startY));
        
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            
            if (x < 0 || x >= skeleton.GetLength(1) ||
                y < 0 || y >= skeleton.GetLength(0) ||
                visited[y, x] || !skeleton[y, x])
            {
                continue;
            }
            
            visited[y, x] = true;
            path.Add(new Vector2(x, y));
            
            // Check 8-connected neighbors
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    stack.Push((x + dx, y + dy));
                }
            }
        }
    }
    
    /// <summary>
    /// Order path points to form a continuous line
    /// </summary>
    private List<Vector2> OrderPathPoints(List<Vector2> points)
    {
        if (points.Count <= 2)
            return points;
        
        // Find an endpoint (point with only one neighbor in skeleton)
        var start = FindEndpoint(points) ?? points[0];
        
        var ordered = new List<Vector2> { start };
        var remaining = new HashSet<Vector2>(points);
        remaining.Remove(start);
        
        var current = start;
        
        // Greedy nearest neighbor (within small radius)
        while (remaining.Count > 0)
        {
            var nearest = remaining
                .Where(p => Vector2.Distance(current, p) <= 2.0f) // 8-connectivity
                .OrderBy(p => Vector2.Distance(current, p))
                .FirstOrDefault();
            
            if (nearest == default(Vector2))
            {
                // Disconnected - shouldn't happen in well-connected skeleton
                break;
            }
            
            ordered.Add(nearest);
            remaining.Remove(nearest);
            current = nearest;
        }
        
        return ordered;
    }
    
    /// <summary>
    /// Find endpoint of a path (point with only one neighbor)
    /// </summary>
    private Vector2? FindEndpoint(List<Vector2> points)
    {
        foreach (var point in points)
        {
            int neighbors = points.Count(p =>
            {
                if (p == point) return false;
                float dist = Vector2.Distance(p, point);
                return dist <= 2.0f; // 8-connectivity
            });
            
            if (neighbors == 1)
                return point;
        }
        
        return null;
    }
}
