using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

public class MedialAxisRoadExtractor : IRoadExtractor
{
    private readonly SkeletonizationRoadExtractor _skeletonExtractor;
    public MedialAxisRoadExtractor(){ _skeletonExtractor = new SkeletonizationRoadExtractor(); }
    
    public RoadGeometry ExtractRoadGeometry(byte[,] roadLayer, RoadSmoothingParameters parameters, float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        Console.WriteLine("Extracting road geometry with skeletonization-based spline approach...");

        // 1) Get raw paths from skeleton
        var centerlinePathsPixels = _skeletonExtractor.ExtractCenterlinePaths(roadLayer, parameters);
        if(centerlinePathsPixels.Count==0){ Console.WriteLine("Warning: No centerline paths extracted"); return geometry; }
        Console.WriteLine($"Extracted {centerlinePathsPixels.Count} raw skeleton path(s).");

        // 2) Merge broken curves inside the same corridor to avoid path splits in tight bends
        var mergedPathPixels = MergeBrokenCurves(centerlinePathsPixels, roadLayer, parameters);
        Console.WriteLine($"Merged to {mergedPathPixels.Count} path(s) after continuity pass.");

        // Convert back to Vector2 for the rest of the processing
        var mergedPathsVector = mergedPathPixels
            .Select(pixelPath => pixelPath.Select(p => new Vector2(p.X, p.Y)).ToList())
            .ToList();

        int totalCrossSections=0;
        var allCrossSections=new List<CrossSection>();
        int globalIndex=0;
        int pathId=0;

        foreach(var pathPixels in mergedPathsVector)
        {
            if(pathPixels.Count < 2) { pathId++; continue; }

            var worldPoints = pathPixels.Select(p=> new Vector2(p.X*metersPerPixel, p.Y*metersPerPixel)).ToList();
            try 
            {
                var pathSpline = new RoadSpline(worldPoints);
                Console.WriteLine($"  Processed Path {pathId}: spline length {pathSpline.TotalLength:F1}m, points {worldPoints.Count}");
                
                var splineSamples = pathSpline.SampleByDistance(parameters.CrossSectionIntervalMeters);
                int localIndex=0;
                foreach(var sample in splineSamples)
                {
                    allCrossSections.Add(new CrossSection
                    { 
                        Index=globalIndex++, 
                        PathId=pathId, 
                        LocalIndex=localIndex++, 
                        CenterPoint=sample.Position, 
                        TangentDirection=sample.Tangent, 
                        NormalDirection=sample.Normal, 
                        WidthMeters=parameters.RoadWidthMeters, 
                        IsExcluded=false 
                    });
                }
                totalCrossSections += splineSamples.Count;

                // Set a representative spline + centerline once
                if (geometry.Spline == null)
                {
                    geometry.Spline = pathSpline;
                    geometry.Centerline = worldPoints;
                }
            } 
            catch(Exception ex)
            { 
                Console.WriteLine($"  Warning: Failed to create spline for path {pathId}: {ex.Message}"); 
            }
            pathId++;
        }
        
        geometry.CrossSections = allCrossSections;
        Console.WriteLine($"Generated {totalCrossSections} total cross-sections from {mergedPathPixels.Count} paths");
        return geometry;
    }

    // --------------------------
    // Path merging implementation
    // --------------------------

    private sealed record Pixel(int X, int Y);

    private List<List<Pixel>> MergeBrokenCurves(
        List<List<Vector2>> rawPaths, // Vector2 type from skeleton extractor
        byte[,] roadMask,
        RoadSmoothingParameters parameters)
    {
        // Convert Vector2 to local Pixel type
        var paths = rawPaths
            .Select(list => list.Select(v => new Pixel((int)v.X, (int)v.Y)).ToList())
            .Where(l => l.Count >= 2)
            .ToList();

        if (paths.Count <= 1) return paths;

        var sp = parameters.SplineParameters ?? new SplineRoadParameters();
        int maxGap = (int)Math.Max(2, Math.Round(sp.BridgeEndpointMaxDistancePixels));
        float maxAngleDeg = Math.Max(10f, sp.JunctionAngleThreshold); // smaller angle -> stricter straight-through

        // Main merge loop: greedily connect endpoints that are close, angle-consistent, and connected through road mask
        bool merged;
        int pass = 0;
        do
        {
            pass++;
            merged = false;

            for (int i = 0; i < paths.Count && !merged; i++)
            {
                for (int j = i + 1; j < paths.Count && !merged; j++)
                {
                    var a = paths[i];
                    var b = paths[j];

                    // Try all four endpoint pairings with auto reverse of B when needed
                    if (TryMerge(a, b, maxGap, maxAngleDeg, roadMask, out var mergedPath) ||
                        TryMerge(a, Reverse(b), maxGap, maxAngleDeg, roadMask, out mergedPath) ||
                        TryMerge(Reverse(a), b, maxGap, maxAngleDeg, roadMask, out mergedPath) ||
                        TryMerge(Reverse(a), Reverse(b), maxGap, maxAngleDeg, roadMask, out mergedPath))
                    {
                        // Replace i with merged, remove j
                        paths[i] = mergedPath;
                        paths.RemoveAt(j);
                        merged = true;
                    }
                }
            }

            if (merged)
                Console.WriteLine($"  Path continuity pass {pass}: merged, remaining {paths.Count} paths");

        } while (merged && paths.Count > 1);

        return paths;
    }

    private static List<Pixel> Reverse(List<Pixel> p)
    {
        var r = new List<Pixel>(p);
        r.Reverse();
        return r;
    }

    private bool TryMerge(
        List<Pixel> a,
        List<Pixel> b,
        int maxGap,
        float maxAngleDeg,
        byte[,] roadMask,
        out List<Pixel> merged)
    {
        merged = null!;
        if (a.Count == 0 || b.Count == 0) return false;

        var aEnd = a[^1];
        var bStart = b[0];

        // 1) Proximity
        var dx = aEnd.X - bStart.X;
        var dy = aEnd.Y - bStart.Y;
        var dist = Math.Sqrt(dx*dx + dy*dy);
        if (dist > maxGap) return false;

        // 2) Direction continuity (favor straight-through)
        var aDir = TangentAtEnd(a, true);
        var bDir = TangentAtEnd(b, false);
        if (aDir.Length() < 1e-3f || bDir.Length() < 1e-3f) return false;

        var ang = AngleBetween(aDir, bDir);
        if (ang > maxAngleDeg) return false;

        // 3) Connectivity inside mask (line-of-sight)
        if (!IsBridgeInsideRoadMask(aEnd, bStart, roadMask)) return false;

        // OK, merge (avoid duplicating the junction pixel)
        merged = new List<Pixel>(a.Count + b.Count - 1);
        merged.AddRange(a);
        if (b.Count > 0)
            merged.AddRange(b.Skip(1));
        return true;
    }

    private static Vector2 TangentAtEnd(List<Pixel> p, bool atEnd)
    {
        // use last/first 3 points if available
        if (p.Count < 2) return Vector2.Zero;

        if (atEnd)
        {
            var a = p[Math.Max(0, p.Count - 3)];
            var b = p[^1];
            return new Vector2(b.X - a.X, b.Y - a.Y);
        }
        else
        {
            var a = p[0];
            var b = p[Math.Min(p.Count - 1, 2)];
            return new Vector2(b.X - a.X, b.Y - a.Y);
        }
    }

    private static float AngleBetween(Vector2 v1, Vector2 v2)
    {
        v1 = Vector2.Normalize(v1);
        v2 = Vector2.Normalize(v2);
        var dot = Math.Clamp(Vector2.Dot(v1, v2), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    private static bool IsBridgeInsideRoadMask(Pixel a, Pixel b, byte[,] mask)
    {
        // Bresenham; require majority of samples to be inside mask
        int w = mask.GetLength(1);
        int h = mask.GetLength(0);
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        int total = 0, inside = 0;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                total++;
                if (mask[y0, x0] > 0) inside++;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }

        // allow small gaps but mostly on-road
        return total == 0 ? false : (inside / (float)total) >= 0.6f;
    }
}
