using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Matches OSM bridge/tunnel structures to road splines and marks them accordingly.
/// This enables the pipeline to exclude bridge/tunnel segments from terrain smoothing
/// and material painting.
/// </summary>
public class BridgeTunnelSplineMatcher
{
    /// <summary>
    /// Maximum distance (in meters) between a structure point and spline centerline
    /// to consider them as a potential match.
    /// </summary>
    private const float MaxMatchDistanceMeters = 10.0f;

    /// <summary>
    /// Minimum overlap percentage required for a geometric match to be accepted.
    /// </summary>
    private const float MinOverlapPercent = 50.0f;

    private readonly OsmGeometryProcessor _geometryProcessor;
    private readonly StructureElevationCalculator _elevationCalculator;

    public BridgeTunnelSplineMatcher()
    {
        _geometryProcessor = new OsmGeometryProcessor();
        _elevationCalculator = new StructureElevationCalculator();
    }

    /// <summary>
    /// Creates a matcher with a custom geometry processor (for coordinate transformation).
    /// </summary>
    /// <param name="geometryProcessor">Geometry processor with transformer set.</param>
    public BridgeTunnelSplineMatcher(OsmGeometryProcessor geometryProcessor)
    {
        _geometryProcessor = geometryProcessor;
        _elevationCalculator = new StructureElevationCalculator();
    }

    /// <summary>
    /// Creates a matcher with custom geometry processor and elevation calculator.
    /// </summary>
    /// <param name="geometryProcessor">Geometry processor with transformer set.</param>
    /// <param name="elevationCalculator">Elevation calculator with configured parameters.</param>
    public BridgeTunnelSplineMatcher(
        OsmGeometryProcessor geometryProcessor,
        StructureElevationCalculator elevationCalculator)
    {
        _geometryProcessor = geometryProcessor;
        _elevationCalculator = elevationCalculator;
    }

    /// <summary>
    /// Matches bridge/tunnel structures to splines and marks the splines.
    /// </summary>
    /// <param name="splines">Road splines to annotate.</param>
    /// <param name="structuresResult">Bridge/tunnel structures from OSM query.</param>
    /// <param name="bbox">Bounding box for coordinate transformation.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel).</param>
    /// <returns>Statistics about matched structures.</returns>
    public BridgeTunnelMatchResult MatchAndAnnotate(
        List<ParameterizedRoadSpline> splines,
        OsmBridgeTunnelQueryResult structuresResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        return MatchAndAnnotate(splines, structuresResult, bbox, terrainSize, metersPerPixel, null);
    }

    /// <summary>
    /// Matches bridge/tunnel structures to splines, marks them, and calculates elevation profiles.
    /// </summary>
    /// <param name="splines">Road splines to annotate.</param>
    /// <param name="structuresResult">Bridge/tunnel structures from OSM query.</param>
    /// <param name="bbox">Bounding box for coordinate transformation.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel).</param>
    /// <param name="heightMap">Optional heightmap for elevation profile calculation. If null, profiles are not calculated.</param>
    /// <returns>Statistics about matched structures.</returns>
    public BridgeTunnelMatchResult MatchAndAnnotate(
        List<ParameterizedRoadSpline> splines,
        OsmBridgeTunnelQueryResult structuresResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        float[,]? heightMap)
    {
        var result = new BridgeTunnelMatchResult
        {
            TotalStructures = structuresResult.Structures.Count
        };

        if (structuresResult.Structures.Count == 0)
        {
            TerrainLogger.Info("BridgeTunnelSplineMatcher: No structures to match");
            return result;
        }

        if (splines.Count == 0)
        {
            TerrainLogger.Warning("BridgeTunnelSplineMatcher: No splines available for matching");
            result.UnmatchedList.AddRange(structuresResult.Structures);
            result.UnmatchedStructures = structuresResult.Structures.Count;
            return result;
        }

        TerrainLogger.Info($"BridgeTunnelSplineMatcher: Matching {structuresResult.Structures.Count} structures " +
                          $"against {splines.Count} splines");

        // Transform structure coordinates to world meters (same coordinate system as splines)
        foreach (var structure in structuresResult.Structures)
        {
            TransformStructureToMeterSpace(structure, bbox, terrainSize, metersPerPixel);
        }

        // Build spatial index for splines for faster lookup
        var splineIndex = BuildSplineSpatialIndex(splines, metersPerPixel);

        // Match each structure to the best-fitting spline
        foreach (var structure in structuresResult.Structures)
        {
            var match = FindBestMatch(structure, splines, splineIndex, metersPerPixel);

            if (match != null)
            {
                // Apply the match: mark the spline as bridge/tunnel
                var spline = splines.First(s => s.SplineId == match.SplineId);
                ApplyMatchToSpline(spline, structure);

                // Calculate and assign elevation profile if heightmap is available
                if (heightMap != null)
                {
                    CalculateAndAssignElevationProfile(spline, structure, heightMap, metersPerPixel);
                }

                result.MatchedList.Add(match);

                if (structure.IsBridge)
                    result.MatchedBridges++;
                else
                    result.MatchedTunnels++;

                TerrainLogger.Detail($"  Matched {structure.DisplayName} -> Spline {match.SplineId} " +
                                   $"(dist: {match.AverageDistanceMeters:F1}m, overlap: {match.OverlapPercent:F0}%)");
            }
            else
            {
                result.UnmatchedList.Add(structure);
                result.UnmatchedStructures++;
                TerrainLogger.Detail($"  Could not match: {structure.DisplayName}");
            }
        }

        // Log elevation profile statistics
        var splinesWithProfiles = splines.Count(s => s.ElevationProfile != null);
        if (splinesWithProfiles > 0)
        {
            TerrainLogger.Info($"BridgeTunnelSplineMatcher: Calculated {splinesWithProfiles} elevation profiles");
        }

        TerrainLogger.Info($"BridgeTunnelSplineMatcher: {result}");
        return result;
    }

    /// <summary>
    /// Calculates and assigns an elevation profile to a matched spline.
    /// </summary>
    private void CalculateAndAssignElevationProfile(
        ParameterizedRoadSpline spline,
        OsmBridgeTunnel structure,
        float[,] heightMap,
        float metersPerPixel)
    {
        try
        {
            // Sample terrain elevations at entry and exit points
            float entryElevation = _elevationCalculator.SampleEntryElevation(structure, heightMap, metersPerPixel);
            float exitElevation = _elevationCalculator.SampleExitElevation(structure, heightMap, metersPerPixel);

            StructureElevationProfile profile;

            if (structure.IsBridge)
            {
                profile = _elevationCalculator.CalculateBridgeProfile(structure, entryElevation, exitElevation);
            }
            else if (structure.IsTunnel)
            {
                // For tunnels, also sample terrain along the path for clearance calculation
                var terrainSamples = _elevationCalculator.SampleTerrainAlongStructure(
                    structure, heightMap, metersPerPixel);
                profile = _elevationCalculator.CalculateTunnelProfile(
                    structure, entryElevation, exitElevation, terrainSamples);
            }
            else
            {
                // Unknown structure type - use linear profile
                profile = new StructureElevationProfile
                {
                    EntryElevation = entryElevation,
                    ExitElevation = exitElevation,
                    LengthMeters = structure.LengthMeters,
                    CurveType = StructureElevationCurveType.Linear,
                    CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation),
                    CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation)
                };
            }

            spline.ElevationProfile = profile;

            TerrainLogger.Detail($"    Elevation profile: {profile.CurveType}, " +
                               $"entry={entryElevation:F1}m, exit={exitElevation:F1}m");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to calculate elevation profile for {structure.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Transforms a structure's coordinates from geo to meter space.
    /// </summary>
    private void TransformStructureToMeterSpace(
        OsmBridgeTunnel structure,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        structure.PositionsMeters.Clear();

        foreach (var coord in structure.Coordinates)
        {
            // Transform to terrain pixel coordinates (bottom-left origin, like splines)
            var pixelCoord = _geometryProcessor.TransformToTerrainCoordinate(coord, bbox, terrainSize);

            // Convert to meters
            var metersCoord = new Vector2(
                pixelCoord.X * metersPerPixel,
                pixelCoord.Y * metersPerPixel);

            structure.PositionsMeters.Add(metersCoord);
        }

        // Calculate length from positions
        structure.LengthMeters = CalculatePolylineLength(structure.PositionsMeters);
    }

    /// <summary>
    /// Calculates the total length of a polyline.
    /// </summary>
    private static float CalculatePolylineLength(List<Vector2> points)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Vector2.Distance(points[i - 1], points[i]);
        }
        return length;
    }

    /// <summary>
    /// Builds a simple spatial index for faster spline lookup.
    /// Returns a dictionary mapping grid cells to spline IDs.
    /// </summary>
    private Dictionary<(int, int), List<int>> BuildSplineSpatialIndex(
        List<ParameterizedRoadSpline> splines,
        float metersPerPixel)
    {
        const float cellSizeMeters = 50f; // 50m grid cells
        var index = new Dictionary<(int, int), List<int>>();

        foreach (var spline in splines)
        {
            var samples = spline.Spline.SampleByDistance(10f); // Sample every 10m
            foreach (var sample in samples)
            {
                var cellX = (int)(sample.Position.X / cellSizeMeters);
                var cellY = (int)(sample.Position.Y / cellSizeMeters);
                var cell = (cellX, cellY);

                if (!index.TryGetValue(cell, out var splineIds))
                {
                    splineIds = [];
                    index[cell] = splineIds;
                }

                if (!splineIds.Contains(spline.SplineId))
                {
                    splineIds.Add(spline.SplineId);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Finds the best matching spline for a structure.
    /// </summary>
    private BridgeTunnelMatch? FindBestMatch(
        OsmBridgeTunnel structure,
        List<ParameterizedRoadSpline> splines,
        Dictionary<(int, int), List<int>> splineIndex,
        float metersPerPixel)
    {
        if (structure.PositionsMeters.Count < 2)
            return null;

        const float cellSizeMeters = 50f;
        BridgeTunnelMatch? bestMatch = null;
        float bestScore = float.MinValue;

        // Find candidate splines using spatial index
        var candidateSplineIds = new HashSet<int>();
        foreach (var pos in structure.PositionsMeters)
        {
            var cellX = (int)(pos.X / cellSizeMeters);
            var cellY = (int)(pos.Y / cellSizeMeters);

            // Check cell and neighbors
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var cell = (cellX + dx, cellY + dy);
                    if (splineIndex.TryGetValue(cell, out var ids))
                    {
                        foreach (var id in ids)
                            candidateSplineIds.Add(id);
                    }
                }
            }
        }

        // Evaluate each candidate
        foreach (var splineId in candidateSplineIds)
        {
            var spline = splines.First(s => s.SplineId == splineId);
            var matchResult = EvaluateMatch(structure, spline);

            if (matchResult == null)
                continue;

            // Calculate match score (higher is better)
            // Prefer: high overlap, low distance, way ID match
            float score = matchResult.OverlapPercent
                          - matchResult.AverageDistanceMeters * 5f
                          + (matchResult.MatchedByWayId ? 100f : 0f);

            if (score > bestScore && matchResult.OverlapPercent >= MinOverlapPercent)
            {
                bestScore = score;
                bestMatch = matchResult;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Evaluates how well a structure matches a spline.
    /// </summary>
    private BridgeTunnelMatch? EvaluateMatch(OsmBridgeTunnel structure, ParameterizedRoadSpline spline)
    {
        // Calculate average distance from structure points to spline centerline
        var distances = new List<float>();
        int pointsWithinThreshold = 0;

        foreach (var structurePos in structure.PositionsMeters)
        {
            float minDist = float.MaxValue;

            // Sample spline at regular intervals to find closest point
            var samples = spline.Spline.SampleByDistance(2f); // 2m intervals for accuracy
            foreach (var sample in samples)
            {
                var dist = Vector2.Distance(structurePos, sample.Position);
                if (dist < minDist)
                    minDist = dist;
            }

            distances.Add(minDist);
            if (minDist <= MaxMatchDistanceMeters)
                pointsWithinThreshold++;
        }

        if (distances.Count == 0)
            return null;

        float avgDistance = distances.Average();
        float overlapPercent = (float)pointsWithinThreshold / structure.PositionsMeters.Count * 100f;

        // Too far away - no match
        if (avgDistance > MaxMatchDistanceMeters * 2)
            return null;

        return new BridgeTunnelMatch
        {
            Structure = structure,
            SplineId = spline.SplineId,
            AverageDistanceMeters = avgDistance,
            OverlapPercent = overlapPercent,
            MatchedByWayId = false // TODO: Implement way ID matching when OsmFeature has WayId
        };
    }

    /// <summary>
    /// Applies the match to a spline, marking it as a bridge or tunnel.
    /// </summary>
    private static void ApplyMatchToSpline(ParameterizedRoadSpline spline, OsmBridgeTunnel structure)
    {
        spline.IsBridge = structure.IsBridge;
        spline.IsTunnel = structure.IsTunnel;
        spline.Layer = structure.Layer;
        spline.StructureData = structure;
    }
}
