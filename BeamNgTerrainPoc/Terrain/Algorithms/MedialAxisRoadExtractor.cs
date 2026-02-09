using System.Diagnostics;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

public class MedialAxisRoadExtractor : IRoadExtractor
{
    private readonly SkeletonizationRoadExtractor _skeletonExtractor;

    public MedialAxisRoadExtractor()
    {
        _skeletonExtractor = new SkeletonizationRoadExtractor();
    }

    public RoadGeometry ExtractRoadGeometry(byte[,] roadLayer, RoadSmoothingParameters parameters, float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        TerrainLogger.Info("Extracting road geometry with skeletonization-based spline approach...");
        perfLog?.LogSection($"MedialAxisRoadExtractor - {roadLayer.GetLength(1)}x{roadLayer.GetLength(0)}");

        // Check if we have pre-built splines from OSM or other sources
        if (parameters.UsePreBuiltSplines)
        {
            TerrainLogger.Info($"Using {parameters.PreBuiltSplines!.Count} pre-built splines from OSM");
            return ExtractFromPreBuiltSplines(geometry, parameters, metersPerPixel, perfLog);
        }

        // 1) Get raw paths from skeleton
        var sw = Stopwatch.StartNew();
        var centerlinePathsPixels = _skeletonExtractor.ExtractCenterlinePaths(roadLayer, parameters);
        perfLog?.Timing($"ExtractCenterlinePaths: {sw.Elapsed.TotalSeconds:F2}s, {centerlinePathsPixels.Count} paths");

        if (centerlinePathsPixels.Count == 0)
        {
            TerrainLogger.Warning("No centerline paths extracted");
            return geometry;
        }

        TerrainLogger.Info($"Extracted {centerlinePathsPixels.Count} raw skeleton path(s).");

        // 2) Merge broken curves inside the same corridor to avoid path splits in tight bends
        sw.Restart();
        var mergedPathPixels = MergeBrokenCurves(centerlinePathsPixels, roadLayer, parameters);
        perfLog?.Timing(
            $"MergeBrokenCurves: {sw.ElapsedMilliseconds}ms, {centerlinePathsPixels.Count} -> {mergedPathPixels.Count} paths");
        TerrainLogger.Info($"Merged to {mergedPathPixels.Count} path(s) after continuity pass.");

        // Convert back to Vector2 for the rest of the processing
        var mergedPathsVector = mergedPathPixels
            .Select(pixelPath => pixelPath.Select(p => new Vector2(p.X, p.Y)).ToList())
            .ToList();

        var totalCrossSections = 0;
        var allCrossSections = new List<CrossSection>();
        var globalIndex = 0;
        var pathId = 0;
        var skippedPaths = 0;

        sw.Restart();
        foreach (var pathPixels in mergedPathsVector)
        {
            if (pathPixels.Count < 2)
            {
                pathId++;
                continue;
            }

            var worldPoints = pathPixels.Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel)).ToList();
            try
            {
                var pathSpline = new RoadSpline(worldPoints);

                // FILTER: Skip paths that would generate too few cross-sections
                // This prevents isolated spikes from short skeleton fragments
                var estimatedCrossSections = (int)(pathSpline.TotalLength / parameters.CrossSectionIntervalMeters);
                const int MinCrossSectionsPerPath = 10; // Minimum to form a meaningful road segment
                if (estimatedCrossSections < MinCrossSectionsPerPath)
                {
                    skippedPaths++;
                    pathId++;
                    continue;
                }

                var splineSamples = pathSpline.SampleByDistance(parameters.CrossSectionIntervalMeters);
                var localIndex = 0;
                foreach (var sample in splineSamples)
                    allCrossSections.Add(new CrossSection
                    {
                        Index = globalIndex++,
                        PathId = pathId,
                        LocalIndex = localIndex++,
                        CenterPoint = sample.Position,
                        TangentDirection = sample.Tangent,
                        NormalDirection = sample.Normal,
                        WidthMeters = parameters.RoadWidthMeters,
                        IsExcluded = false
                    });
                totalCrossSections += splineSamples.Count;

                // Set a representative spline + centerline once
                if (geometry.Spline == null)
                {
                    geometry.Spline = pathSpline;
                    geometry.Centerline = worldPoints;
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to create spline for path {pathId}: {ex.Message}");
            }

            pathId++;
        }

        perfLog?.Timing(
            $"SplineCreation + CrossSections: {sw.ElapsedMilliseconds}ms, {totalCrossSections} cross-sections from {mergedPathPixels.Count - skippedPaths} paths");

        geometry.CrossSections = allCrossSections;

        if (skippedPaths > 0)
            TerrainLogger.Info($"Skipped {skippedPaths} short path(s)");
        TerrainLogger.Info(
            $"Generated {totalCrossSections} total cross-sections from {mergedPathPixels.Count - skippedPaths} paths");

        totalSw.Stop();
        perfLog?.Timing($"=== MedialAxisRoadExtractor TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s ===");

        return geometry;
    }

    /// <summary>
    ///     Extracts road geometry from pre-built splines (e.g., from OSM Overpass API).
    ///     Bypasses skeleton extraction and path finding entirely.
    /// </summary>
    private RoadGeometry ExtractFromPreBuiltSplines(
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float metersPerPixel,
        TerrainCreationLogger? perfLog)
    {
        var sw = Stopwatch.StartNew();
        var allCrossSections = new List<CrossSection>();
        var globalIndex = 0;
        var pathId = 0;
        var totalCrossSections = 0;
        var skippedPaths = 0;

        foreach (var spline in parameters.PreBuiltSplines!)
        {
            try
            {
                // FILTER: Skip paths that would generate too few cross-sections
                var estimatedCrossSections = (int)(spline.TotalLength / parameters.CrossSectionIntervalMeters);
                const int MinCrossSectionsPerPath = 10;
                if (estimatedCrossSections < MinCrossSectionsPerPath)
                {
                    skippedPaths++;
                    pathId++;
                    continue;
                }

                var splineSamples = spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
                var localIndex = 0;

                foreach (var sample in splineSamples)
                    allCrossSections.Add(new CrossSection
                    {
                        Index = globalIndex++,
                        PathId = pathId,
                        LocalIndex = localIndex++,
                        CenterPoint = sample.Position,
                        TangentDirection = sample.Tangent,
                        NormalDirection = sample.Normal,
                        WidthMeters = parameters.RoadWidthMeters,
                        IsExcluded = false
                    });

                totalCrossSections += splineSamples.Count;

                // Set a representative spline + centerline once
                if (geometry.Spline == null)
                {
                    geometry.Spline = spline;
                    geometry.Centerline = spline.ControlPoints;
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to process pre-built spline {pathId}: {ex.Message}");
            }

            pathId++;
        }

        perfLog?.Timing(
            $"PreBuiltSplines -> CrossSections: {sw.ElapsedMilliseconds}ms, {totalCrossSections} cross-sections from {parameters.PreBuiltSplines.Count - skippedPaths} splines");

        geometry.CrossSections = allCrossSections;

        if (skippedPaths > 0)
            TerrainLogger.Info($"Skipped {skippedPaths} short pre-built spline(s)");
        TerrainLogger.Info(
            $"Generated {totalCrossSections} total cross-sections from {parameters.PreBuiltSplines.Count - skippedPaths} pre-built splines");

        return geometry;
    }

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
        var maxGap = (int)Math.Max(2, Math.Round(sp.BridgeEndpointMaxDistancePixels));
        var maxAngleDeg = Math.Max(10f, sp.JunctionAngleThreshold); // smaller angle -> stricter straight-through

        // Main merge loop: greedily connect endpoints that are close, angle-consistent, and connected through road mask
        bool merged;
        var pass = 0;
        do
        {
            pass++;
            merged = false;

            for (var i = 0; i < paths.Count && !merged; i++)
            for (var j = i + 1; j < paths.Count && !merged; j++)
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
        } while (merged && paths.Count > 1);

        if (pass > 1)
            TerrainLogger.Info($"  Path continuity: {pass - 1} merge(s), remaining {paths.Count} paths");

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
        var dist = Math.Sqrt(dx * dx + dy * dy);
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
        var w = mask.GetLength(1);
        var h = mask.GetLength(0);
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        int total = 0, inside = 0;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                total++;
                if (mask[y0, x0] > 0) inside++;
            }

            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }

        // allow small gaps but mostly on-road
        return total == 0 ? false : inside / (float)total >= 0.6f;
    }

    // --------------------------
    // Path merging implementation
    // --------------------------

    private sealed record Pixel(int X, int Y);
}