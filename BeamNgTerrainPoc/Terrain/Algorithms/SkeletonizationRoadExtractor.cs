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
        int roadPixels = 0; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) if (binaryMask[y,x]) roadPixels++;
        Console.WriteLine($"Input: {roadPixels:N0} road pixels");
        var skeleton = ApplyZhangSuenThinning(binaryMask);
        int skeletonPixels = 0; for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) if (skeleton[y,x]) skeletonPixels++;
        Console.WriteLine($"Skeleton: {skeletonPixels:N0} centerline pixels");
        
        if (parameters?.BridgeEndpointMaxDistancePixels > 0)
        {
            BridgeEndpoints(skeleton, parameters.BridgeEndpointMaxDistancePixels);
        }
        
        var rawPaths = ExtractConnectedPaths(skeleton);
        Console.WriteLine($"Found {rawPaths.Count} raw connected components");
        var orderedPaths = new List<List<Vector2>>();
        
        foreach (var path in rawPaths)
        {
            var ordered = OrderPathGraph(path, parameters?.OrderingNeighborRadiusPixels ?? 2.5f);
            var densified = DensifyPath(ordered, parameters?.DensifyMaxSpacingPixels ?? 1.0f);
            if (densified.Count > 1) orderedPaths.Add(densified);
        }
        
        if (parameters?.ExportSkeletonDebugImage == true)
        {
            try { ExportSkeletonDebugImage(roadMask, skeleton, orderedPaths, parameters); } catch (Exception ex) { Console.WriteLine($"Skeleton debug export failed: {ex.Message}"); }
        }
        return orderedPaths;
    }
    
    private bool[,] ConvertToBinaryMask(byte[,] roadMask)
    { int h = roadMask.GetLength(0); int w = roadMask.GetLength(1); var b = new bool[h,w]; for(int y=0;y<h;y++) for(int x=0;x<w;x++) b[y,x]=roadMask[y,x]>128; return b; }
    
    private bool[,] ApplyZhangSuenThinning(bool[,] mask)
    { int h=mask.GetLength(0), w=mask.GetLength(1); var result=(bool[,])mask.Clone(); bool changed; int iteration=0; do { changed=false; iteration++; var remove=new List<(int x,int y)>(); for(int y=1;y<h-1;y++) for(int x=1;x<w-1;x++) if(result[y,x] && ShouldRemovePixel(result,x,y,true)) remove.Add((x,y)); foreach(var p in remove){result[p.y,p.x]=false; changed=true;} remove.Clear(); for(int y=1;y<h-1;y++) for(int x=1;x<w-1;x++) if(result[y,x] && ShouldRemovePixel(result,x,y,false)) remove.Add((x,y)); foreach(var p in remove){result[p.y,p.x]=false; changed=true;} if(iteration%10==0) Console.WriteLine($"  Thinning iteration {iteration}..."); } while(changed && iteration<100); Console.WriteLine($"Thinning complete after {iteration} iterations"); return result; }
    
    private bool ShouldRemovePixel(bool[,] img,int x,int y,bool first){ bool p2=img[y-1,x], p3=img[y-1,x+1], p4=img[y,x+1], p5=img[y+1,x+1], p6=img[y+1,x], p7=img[y+1,x-1], p8=img[y,x-1], p9=img[y-1,x-1]; int black=(p2?1:0)+(p3?1:0)+(p4?1:0)+(p5?1:0)+(p6?1:0)+(p7?1:0)+(p8?1:0)+(p9?1:0); if(black<2||black>6) return false; int transitions=0; bool[] n={p2,p3,p4,p5,p6,p7,p8,p9,p2}; for(int i=0;i<8;i++) if(!n[i]&&n[i+1]) transitions++; if(transitions!=1) return false; if(first){ if(p2&&p4&&p6) return false; if(p4&&p6&&p8) return false; } else { if(p2&&p4&&p8) return false; if(p2&&p6&&p8) return false; } return true; }
    
    private List<List<Vector2>> ExtractConnectedPaths(bool[,] skeleton)
    { int h=skeleton.GetLength(0), w=skeleton.GetLength(1); var visited=new bool[h,w]; var paths=new List<List<Vector2>>(); for(int y=0;y<h;y++) for(int x=0;x<w;x++) if(skeleton[y,x] && !visited[y,x]) { var path=new List<Vector2>(); TracePath(skeleton,visited,x,y,path); if(path.Count>=2) paths.Add(path); } return paths; }
    
    private void TracePath(bool[,] skeleton,bool[,] visited,int sx,int sy,List<Vector2> path)
    { var stack=new Stack<(int x,int y)>(); stack.Push((sx,sy)); while(stack.Count>0){ var (x,y)=stack.Pop(); if(x<0||x>=skeleton.GetLength(1)||y<0||y>=skeleton.GetLength(0)||visited[y,x]||!skeleton[y,x]) continue; visited[y,x]=true; path.Add(new Vector2(x,y)); for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++) if(!(dx==0&&dy==0)) stack.Push((x+dx,y+dy)); } }
    
    private List<Vector2> OrderPathGraph(List<Vector2> points, float neighborRadius)
    {
        if (points.Count <= 2) return points;
        var adj = BuildAdjacency(points);
        var endpoints = adj.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        
        if (endpoints.Count < 2)
        {
            // Fallback for loops or complex shapes without clear endpoints
            var startNode = points.FirstOrDefault(p => adj.ContainsKey(p) && adj[p].Any());
            if (startNode == default) return points; // No connections found
            return FindPathBfs(adj, startNode, adj[startNode].First());
        }

        // Find the pair of endpoints that are farthest apart
        Vector2 bestStart = endpoints[0], bestEnd = endpoints.Count > 1 ? endpoints[1] : endpoints[0];
        float maxDist = 0;
        for (int i = 0; i < endpoints.Count; i++)
        {
            for (int j = i + 1; j < endpoints.Count; j++)
            {
                float d = Vector2.Distance(endpoints[i], endpoints[j]);
                if (d > maxDist) { maxDist = d; bestStart = endpoints[i]; bestEnd = endpoints[j]; }
            }
        }

        // Find the path between the two farthest endpoints using BFS
        return FindPathBfs(adj, bestStart, bestEnd);
    }

    private Dictionary<Vector2, List<Vector2>> BuildAdjacency(List<Vector2> points)
    {
        var adj = new Dictionary<Vector2, List<Vector2>>();
        var pointSet = new HashSet<Vector2>(points);
        foreach(var p in points)
        {
            adj[p] = new List<Vector2>();
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var neighbor = new Vector2(p.X + dx, p.Y + dy);
                    if (pointSet.Contains(neighbor))
                    {
                        adj[p].Add(neighbor);
                    }
                }
            }
        }
        return adj;
    }

    private List<Vector2> FindPathBfs(Dictionary<Vector2, List<Vector2>> adj, Vector2 start, Vector2 end)
    {
        var queue = new Queue<Vector2>();
        var parents = new Dictionary<Vector2, Vector2>();
        var visited = new HashSet<Vector2>();
        queue.Enqueue(start);
        visited.Add(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == end) break;
            if (!adj.ContainsKey(current)) continue;
            foreach (var neighbor in adj[current])
            {
                if (!visited.Contains(neighbor))
                {
                    visited.Add(neighbor);
                    parents[neighbor] = current;
                    queue.Enqueue(neighbor);
                }
            }
        }
        var path = new List<Vector2>();
        var at = end;
        while (at != start)
        {
            path.Add(at);
            if (!parents.TryGetValue(at, out at)) return new List<Vector2>(); // Path not found
        }
        path.Add(start);
        path.Reverse();
        return path;
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
    
    private void BridgeEndpoints(bool[,] skeleton, float maxDist)
    {
        var endpoints = new List<Vector2>();
        int h=skeleton.GetLength(0), w=skeleton.GetLength(1);
        for(int y=1;y<h-1;y++) for(int x=1;x<w-1;x++) if(skeleton[y,x])
        {
            int n=0; for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++) if(!(dx==0&&dy==0) && skeleton[y+dy,x+dx]) n++;
            if(n==1) endpoints.Add(new Vector2(x,y));
        }
        Console.WriteLine($"Found {endpoints.Count} endpoints to consider for bridging.");
        for(int i=0;i<endpoints.Count;i++) for(int j=i+1;j<endpoints.Count;j++)
        {
            if(Vector2.Distance(endpoints[i],endpoints[j])<=maxDist)
            {
                Console.WriteLine($"Bridging gap between ({endpoints[i].X},{endpoints[i].Y}) and ({endpoints[j].X},{endpoints[j].Y})");
                DrawLineOnSkeleton(skeleton, (int)endpoints[i].X, (int)endpoints[i].Y, (int)endpoints[j].X, (int)endpoints[j].Y);
            }
        }
    }
    
    private void DrawLineOnSkeleton(bool[,] skeleton, int x0, int y0, int x1, int y1)
    { int dx=Math.Abs(x1-x0), sx=x0<x1?1:-1; int dy=-Math.Abs(y1-y0), sy=y0<y1?1:-1; int err=dx+dy; while(true){ if(x0>=0&&x0<skeleton.GetLength(1)&&y0>=0&&y0<skeleton.GetLength(0)) skeleton[y0,x0]=true; if(x0==x1&&y0==y1) break; int e2=2*err; if(e2>=dy){err+=dy;x0+=sx;} if(e2<=dx){err+=dx;y0+=sy;} } }
    
    private void ExportSkeletonDebugImage(byte[,] roadMask, bool[,] skeleton, List<List<Vector2>> orderedPaths, RoadSmoothingParameters p)
    { int h=roadMask.GetLength(0), w=roadMask.GetLength(1); var img=new Image<Rgba32>(w,h,new Rgba32(0,0,0,255)); for(int y=0;y<h;y++) for(int x=0;x<w;x++) if(roadMask[y,x]>128) img[x,h-1-y]=new Rgba32(25,25,25,255); for(int y=0;y<h;y++) for(int x=0;x<w;x++) if(skeleton[y,x]) img[x,h-1-y]=new Rgba32(60,60,60,255); var colors=new[]{new Rgba32(255,0,0,255),new Rgba32(0,255,0,255),new Rgba32(0,128,255,255),new Rgba32(255,128,0,255)}; int ci=0; foreach(var path in orderedPaths){ var col=colors[ci%colors.Length]; foreach(var pt in path){ int px=(int)pt.X, py=(int)pt.Y; if(px>=0&&px<w&&py>=0&&py<h) img[px,h-1-py]=col; } ci++; } var dir=p.DebugOutputDirectory; if(string.IsNullOrWhiteSpace(dir)) dir=Directory.GetCurrentDirectory(); Directory.CreateDirectory(dir); string fp=Path.Combine(dir,"skeleton_debug.png"); img.SaveAsPng(fp); Console.WriteLine($"Exported skeleton debug image: {fp}"); }
}
