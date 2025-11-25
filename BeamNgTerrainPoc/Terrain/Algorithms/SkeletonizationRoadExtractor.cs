using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

public class SkeletonizationRoadExtractor
{
    public List<List<Vector2>> ExtractCenterlinePaths(byte[,] roadMask, RoadSmoothingParameters? parameters = null)
    {
        Console.WriteLine("Extracting road centerline using skeletonization...");
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        
        var binaryMask = ConvertToBinaryMask(roadMask);
        int roadPixels = 0; 
        for (int y = 0; y < height; y++) 
            for (int x = 0; x < width; x++) 
                if (binaryMask[y,x]) roadPixels++;
        Console.WriteLine($"Input: {roadPixels:N0} road pixels");
        
        // DILATE the mask before skeletonization (BeamNG technique)
        binaryMask = DilateMask(binaryMask, 3);
        Console.WriteLine($"After dilation: mask prepared for skeletonization");
        
        var skeleton = ApplyZhangSuenThinning(binaryMask);
        int skeletonPixels = 0; 
        for (int y = 0; y < height; y++) 
            for (int x = 0; x < width; x++) 
                if (skeleton[y,x]) skeletonPixels++;
        Console.WriteLine($"Skeleton: {skeletonPixels:N0} centerline pixels");
        
        // Detect keypoints (endpoints and junctions)
        var (endpoints, junctions, classifications) = DetectKeypoints(skeleton);
        Console.WriteLine($"Found {endpoints.Count} endpoints and {junctions.Count} junctions");
        
        // Extract paths using BeamNG-style algorithm
        var rawPaths = ExtractPathsFromEndpointsAndJunctions(skeleton, endpoints, junctions, classifications);
        Console.WriteLine($"Extracted {rawPaths.Count} raw paths");
        
        // Join close paths (much more aggressive than before)
        rawPaths = JoinClosePaths(rawPaths, 40.0f); // 40 pixels like BeamNG
        Console.WriteLine($"After joining: {rawPaths.Count} paths");
        
        // Filter short paths
        rawPaths = FilterShortPaths(rawPaths, 20.0f);
        Console.WriteLine($"After filtering: {rawPaths.Count} paths");
        
        var orderedPaths = new List<List<Vector2>>();
        foreach (var path in rawPaths)
        {
            var densified = DensifyPath(path, parameters?.DensifyMaxSpacingPixels ?? 1.0f);
            if (densified.Count > 1) orderedPaths.Add(densified);
        }
        
        if (parameters?.ExportSkeletonDebugImage == true)
        {
            try { ExportSkeletonDebugImage(roadMask, skeleton, orderedPaths, parameters); } 
            catch (Exception ex) { Console.WriteLine($"Skeleton debug export failed: {ex.Message}"); }
        }
        
        return orderedPaths;
    }
    
    // Dilate mask to expand black regions (helps with intersection connectivity)
    private bool[,] DilateMask(bool[,] mask, int radius)
    {
        int h = mask.GetLength(0), w = mask.GetLength(1);
        var result = new bool[h, w];
        
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool found = false;
                for (int dy = -radius; dy <= radius && !found; dy++)
                {
                    for (int dx = -radius; dx <= radius && !found; dx++)
                    {
                        int ny = y + dy, nx = x + dx;
                        if (ny >= 0 && ny < h && nx >= 0 && nx < w && mask[ny, nx])
                        {
                            found = true;
                        }
                    }
                }
                result[y, x] = found;
            }
        }
        return result;
    }
    
    // Detect endpoints (degree 1) and junctions (degree >= 3) in skeleton
    private (List<Vector2> endpoints, List<Vector2> junctions, Dictionary<int, string> classifications) 
        DetectKeypoints(bool[,] skeleton)
    {
        int h = skeleton.GetLength(0), w = skeleton.GetLength(1);
        var endpoints = new List<Vector2>();
        var junctions = new List<Vector2>();
        var classifications = new Dictionary<int, string>();
        
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                if (!skeleton[y, x]) continue;
                
                int neighbors = CountNeighbors(skeleton, x, y);
                int transitions = CountTransitions(skeleton, x, y);
                int idx = y * w + x;
                
                if (neighbors == 1)
                {
                    endpoints.Add(new Vector2(x, y));
                    classifications[idx] = "end";
                }
                else if (transitions >= 3 && neighbors >= 3)
                {
                    junctions.Add(new Vector2(x, y));
                    classifications[idx] = "junction";
                }
            }
        }
        return (endpoints, junctions, classifications);
    }
    
    // Count 8-connected black neighbors
    private int CountNeighbors(bool[,] skeleton, int x, int y)
    {
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (!(dx == 0 && dy == 0) && skeleton[y + dy, x + dx])
                    count++;
        return count;
    }
    
    // Count transitions (0->1) in circular neighborhood (for junction detection)
    private int CountTransitions(bool[,] skeleton, int x, int y)
    {
        bool[] n = new bool[9];
        n[0] = skeleton[y - 1, x];     // N
        n[1] = skeleton[y - 1, x + 1]; // NE
        n[2] = skeleton[y, x + 1];     // E
        n[3] = skeleton[y + 1, x + 1]; // SE
        n[4] = skeleton[y + 1, x];     // S
        n[5] = skeleton[y + 1, x - 1]; // SW
        n[6] = skeleton[y, x - 1];     // W
        n[7] = skeleton[y - 1, x - 1]; // NW
        n[8] = n[0]; // Wrap around
        
        int transitions = 0;
        for (int i = 0; i < 8; i++)
            if (!n[i] && n[i + 1])
                transitions++;
        return transitions;
    }
    
    // Extract paths using BeamNG-style approach
    private List<List<Vector2>> ExtractPathsFromEndpointsAndJunctions(
        bool[,] skeleton, 
        List<Vector2> endpoints, 
        List<Vector2> junctions,
        Dictionary<int, string> classifications)
    {
        int h = skeleton.GetLength(0), w = skeleton.GetLength(1);
        var visited = new HashSet<int>();
        var paths = new List<List<Vector2>>();
        var walkedArms = new Dictionary<int, HashSet<int>>();
        
        // Mark arm as walked
        void MarkArmWalked(int fromIdx, int toIdx)
        {
            if (!walkedArms.ContainsKey(fromIdx))
                walkedArms[fromIdx] = new HashSet<int>();
            walkedArms[fromIdx].Add(toIdx);
        }
        
        bool HasArmBeenWalked(int fromIdx, int toIdx)
        {
            return walkedArms.ContainsKey(fromIdx) && walkedArms[fromIdx].Contains(toIdx);
        }
        
        // Get all control points (endpoints + junctions)
        var controlPoints = new List<Vector2>();
        controlPoints.AddRange(endpoints);
        controlPoints.AddRange(junctions);
        
        // From each control point, walk in all unwalked directions
        foreach (var pt in controlPoints)
        {
            int fromIdx = (int)(pt.Y * w + pt.X);
            
            // Get unvisited neighbors
            var neighbors = GetUnvisitedNeighbors(skeleton, (int)pt.X, (int)pt.Y, visited, w, h);
            
            foreach (var nb in neighbors)
            {
                int toIdx = (int)(nb.Y * w + nb.X);
                
                if (HasArmBeenWalked(fromIdx, toIdx))
                    continue;
                
                // Walk from this control point through the neighbor
                var path = WalkPath(skeleton, pt, nb, visited, classifications, w, h);
                
                if (path.Count > 1)
                {
                    paths.Add(path);
                    
                    // Mark both directions as walked
                    if (path.Count >= 2)
                    {
                        var lastPt = path[path.Count - 1];
                        int lastIdx = (int)(lastPt.Y * w + lastPt.X);
                        MarkArmWalked(fromIdx, toIdx);
                        MarkArmWalked(lastIdx, (int)(path[path.Count - 2].Y * w + path[path.Count - 2].X));
                    }
                }
            }
        }
        
        return paths;
    }
    
    // Walk a path from start through first neighbor until hitting another control point
    private List<Vector2> WalkPath(
        bool[,] skeleton,
        Vector2 start,
        Vector2 firstNeighbor,
        HashSet<int> globalVisited,
        Dictionary<int, string> classifications,
        int w, int h)
    {
        var path = new List<Vector2> { start };
        var localVisited = new HashSet<int>();
        localVisited.Add((int)(start.Y * w + start.X));
        
        var current = firstNeighbor;
        
        while (true)
        {
            int idx = (int)(current.Y * w + current.X);
            
            if (globalVisited.Contains(idx) || localVisited.Contains(idx))
                break;
            
            path.Add(current);
            globalVisited.Add(idx);
            localVisited.Add(idx);
            
            // Stop if we hit another control point
            if (path.Count > 2 && classifications.ContainsKey(idx))
                break;
            
            // Find next unvisited neighbor
            var neighbors = GetUnvisitedNeighbors(skeleton, (int)current.X, (int)current.Y, localVisited, w, h);
            if (neighbors.Count == 0)
                break;
            
            current = neighbors[0];
        }
        
        return path;
    }
    
    // Get unvisited skeleton neighbors
    private List<Vector2> GetUnvisitedNeighbors(bool[,] skeleton, int x, int y, HashSet<int> visited, int w, int h)
    {
        var result = new List<Vector2>();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && nx < w && ny >= 0 && ny < h && skeleton[ny, nx])
                {
                    int idx = ny * w + nx;
                    if (!visited.Contains(idx))
                        result.Add(new Vector2(nx, ny));
                }
            }
        }
        return result;
    }
    
    // Join paths with close endpoints (BeamNG-style)
    private List<List<Vector2>> JoinClosePaths(List<List<Vector2>> paths, float joinThreshold)
    {
        float thresholdSq = joinThreshold * joinThreshold;
        bool didMerge = true;
        
        while (didMerge)
        {
            didMerge = false;
            
            for (int i = 0; i < paths.Count && !didMerge; i++)
            {
                var path1 = paths[i];
                if (path1.Count < 2) continue;
                
                for (int j = i + 1; j < paths.Count; j++)
                {
                    var path2 = paths[j];
                    if (path2.Count < 2) continue;
                    
                    var p1s = path1[0];
                    var p1e = path1[path1.Count - 1];
                    var p2s = path2[0];
                    var p2e = path2[path2.Count - 1];
                    
                    float d_es = Vector2.DistanceSquared(p1e, p2s);
                    float d_ee = Vector2.DistanceSquared(p1e, p2e);
                    float d_se = Vector2.DistanceSquared(p1s, p2e);
                    float d_ss = Vector2.DistanceSquared(p1s, p2s);
                    
                    if (d_es < thresholdSq)
                    {
                        // Join end1 to start2
                        path1.AddRange(path2.Skip(1));
                        paths.RemoveAt(j);
                        didMerge = true;
                        break;
                    }
                    else if (d_ee < thresholdSq)
                    {
                        // Join end1 to end2 (reverse path2)
                        for (int k = path2.Count - 2; k >= 0; k--)
                            path1.Add(path2[k]);
                        paths.RemoveAt(j);
                        didMerge = true;
                        break;
                    }
                    else if (d_se < thresholdSq)
                    {
                        // Join start1 to end2
                        path2.AddRange(path1.Skip(1));
                        paths[i] = path2;
                        paths.RemoveAt(j);
                        didMerge = true;
                        break;
                    }
                    else if (d_ss < thresholdSq)
                    {
                        // Join start1 to start2 (reverse path1)
                        var newPath = new List<Vector2>();
                        for (int k = path1.Count - 1; k >= 0; k--)
                            newPath.Add(path1[k]);
                        newPath.AddRange(path2.Skip(1));
                        paths[i] = newPath;
                        paths.RemoveAt(j);
                        didMerge = true;
                        break;
                    }
                }
            }
        }
        
        return paths;
    }
    
    // Filter out short paths
    private List<List<Vector2>> FilterShortPaths(List<List<Vector2>> paths, float minLength)
    {
        var filtered = new List<List<Vector2>>();
        foreach (var path in paths)
        {
            float length = 0;
            for (int i = 1; i < path.Count; i++)
                length += Vector2.Distance(path[i - 1], path[i]);
            
            if (length >= minLength)
                filtered.Add(path);
        }
        return filtered;
    }
    
    private bool[,] ConvertToBinaryMask(byte[,] roadMask)
    { 
        int h = roadMask.GetLength(0); 
        int w = roadMask.GetLength(1); 
        var b = new bool[h,w]; 
        for(int y=0;y<h;y++) 
            for(int x=0;x<w;x++) 
                b[y,x]=roadMask[y,x]>128; 
        return b; 
    }
    
    private bool[,] ApplyZhangSuenThinning(bool[,] mask)
    { 
        int h=mask.GetLength(0), w=mask.GetLength(1); 
        var result=(bool[,])mask.Clone(); 
        bool changed; 
        int iteration=0; 
        do 
        { 
            changed=false; 
            iteration++; 
            var remove=new List<(int x,int y)>(); 
            for(int y=1;y<h-1;y++) 
                for(int x=1;x<w-1;x++) 
                    if(result[y,x] && ShouldRemovePixel(result,x,y,true)) 
                        remove.Add((x,y)); 
            foreach(var p in remove)
            {
                result[p.y,p.x]=false; 
                changed=true;
            } 
            remove.Clear(); 
            for(int y=1;y<h-1;y++) 
                for(int x=1;x<w-1;x++) 
                    if(result[y,x] && ShouldRemovePixel(result,x,y,false)) 
                        remove.Add((x,y)); 
            foreach(var p in remove)
            {
                result[p.y,p.x]=false; 
                changed=true;
            } 
            if(iteration%10==0) 
                Console.WriteLine($"  Thinning iteration {iteration}..."); 
        } while(changed && iteration<100); 
        Console.WriteLine($"Thinning complete after {iteration} iterations"); 
        return result; 
    }
    
    private bool ShouldRemovePixel(bool[,] img,int x,int y,bool first)
    { 
        bool p2=img[y-1,x], p3=img[y-1,x+1], p4=img[y,x+1], p5=img[y+1,x+1], p6=img[y+1,x], p7=img[y+1,x-1], p8=img[y,x-1], p9=img[y-1,x-1]; 
        int black=(p2?1:0)+(p3?1:0)+(p4?1:0)+(p5?1:0)+(p6?1:0)+(p7?1:0)+(p8?1:0)+(p9?1:0); 
        if(black<2||black>6) return false; 
        int transitions=0; 
        bool[] n={p2,p3,p4,p5,p6,p7,p8,p9,p2}; 
        for(int i=0;i<8;i++) 
            if(!n[i]&&n[i+1]) 
                transitions++; 
        if(transitions!=1) return false; 
        if(first)
        { 
            if(p2&&p4&&p6) return false; 
            if(p4&&p6&&p8) return false; 
        } 
        else 
        { 
            if(p2&&p4&&p8) return false; 
            if(p2&&p6&&p8) return false; 
        } 
        return true; 
    }
    
    private List<Vector2> DensifyPath(List<Vector2> ordered, float maxSpacing)
    { 
        if(ordered.Count < 2) return ordered; 
        var result = new List<Vector2>(); 
        for(int i = 0; i < ordered.Count - 1; i++)
        { 
            var a = ordered[i]; 
            var b = ordered[i + 1]; 
            result.Add(a); 
            float dist = Vector2.Distance(a, b); 
            if(dist > maxSpacing)
            { 
                int extra = (int)MathF.Ceiling(dist / maxSpacing) - 1; 
                for(int k = 1; k <= extra; k++)
                { 
                    float t = k / (float)(extra + 1); 
                    result.Add(Vector2.Lerp(a, b, t)); 
                } 
            } 
        } 
        result.Add(ordered[^1]); 
        return result; 
    }
    
    private void ExportSkeletonDebugImage(byte[,] roadMask, bool[,] skeleton, List<List<Vector2>> orderedPaths, RoadSmoothingParameters p)
    { 
        int h=roadMask.GetLength(0), w=roadMask.GetLength(1); 
        var img=new Image<Rgba32>(w,h,new Rgba32(0,0,0,255)); 
        for(int y=0;y<h;y++) 
            for(int x=0;x<w;x++) 
                if(roadMask[y,x]>128) 
                    img[x,h-1-y]=new Rgba32(25,25,25,255); 
        for(int y=0;y<h;y++) 
            for(int x=0;x<w;x++) 
                if(skeleton[y,x]) 
                    img[x,h-1-y]=new Rgba32(60,60,60,255); 
        var colors=new[]{new Rgba32(255,0,0,255),new Rgba32(0,255,0,255),new Rgba32(0,128,255,255),new Rgba32(255,128,0,255),new Rgba32(255,255,0,255),new Rgba32(255,0,255,255)}; 
        int ci=0; 
        foreach(var path in orderedPaths)
        { 
            var col=colors[ci%colors.Length]; 
            foreach(var pt in path)
            { 
                int px=(int)pt.X, py=(int)pt.Y; 
                if(px>=0&&px<w&&py>=0&&py<h) 
                    img[px,h-1-py]=col; 
            } 
            ci++; 
        } 
        var dir=p.DebugOutputDirectory; 
        if(string.IsNullOrWhiteSpace(dir)) 
            dir=Directory.GetCurrentDirectory(); 
        Directory.CreateDirectory(dir); 
        string fp=Path.Combine(dir,"skeleton_debug.png"); 
        img.SaveAsPng(fp); 
        Console.WriteLine($"Exported skeleton debug image: {fp}"); 
    }
}
