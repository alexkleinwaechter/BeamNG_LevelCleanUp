using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Helpers for building synthetic road networks in tests.
/// </summary>
internal static class RoadNetworkTestHelpers
{
    /// <summary>
    ///     Stamps a clean round road clearance (5 m, no structural depth) onto a rules instance so
    ///     decision-logic tests keep readable raise/dip arithmetic. Obstacle typing is unconditional
    ///     (doc 17 §4a); the real typed budgets (4.7 + span/20 depth, rail, water) are covered by
    ///     <c>BridgeObstacleTypingPlannerTests</c>. Returns the same instance for fluent use.
    /// </summary>
    public static BridgeRuleSystemOptions WithTestClearance(this BridgeRuleSystemOptions rules)
    {
        rules.RoadClearanceMeters = 5f;
        rules.StructuralDepthMinMeters = 0f;
        rules.StructuralDepthMaxMeters = 0f;
        return rules;
    }

    /// <summary>
    ///     Creates a straight road spline between two points.
    /// </summary>
    public static RoadSpline CreateStraightSpline(Vector2 start, Vector2 end, int pointCount = 5)
    {
        var points = new List<Vector2>();
        for (var i = 0; i < pointCount; i++)
        {
            var t = (float)i / (pointCount - 1);
            points.Add(Vector2.Lerp(start, end, t));
        }

        return new RoadSpline(points, SplineInterpolationType.LinearControlPoints);
    }

    /// <summary>
    ///     Creates a ParameterizedRoadSpline with default parameters.
    /// </summary>
    public static ParameterizedRoadSpline CreateParameterizedSpline(
        int splineId,
        Vector2 start,
        Vector2 end,
        string osmRoadType = "primary",
        int priority = 50,
        float roadWidth = 8f,
        bool isRoundabout = false,
        bool isBridge = false,
        bool isTunnel = false,
        bool excludeBridges = true,
        bool excludeTunnels = true,
        long? startOsmNodeId = null,
        long? endOsmNodeId = null,
        bool mergeStructuresIntoCorridor = false,
        List<StructureSegment>? structureSegments = null)
    {
        var spline = CreateStraightSpline(start, end);
        spline.StartOsmNodeId = startOsmNodeId;
        spline.EndOsmNodeId = endOsmNodeId;
        return new ParameterizedRoadSpline
        {
            Spline = spline,
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = roadWidth,
                TerrainAffectedRangeMeters = 6f,
                CrossSectionIntervalMeters = 0.5f,
                ExcludeBridgesFromTerrain = excludeBridges,
                ExcludeTunnelsFromTerrain = excludeTunnels,
                MergeStructuresIntoCorridor = mergeStructuresIntoCorridor
            },
            MaterialName = "asphalt",
            SplineId = splineId,
            OsmRoadType = osmRoadType,
            Priority = priority,
            IsRoundabout = isRoundabout,
            IsBridge = isBridge,
            IsTunnel = isTunnel,
            StartOsmNodeId = startOsmNodeId,
            EndOsmNodeId = endOsmNodeId,
            StructureSegments = structureSegments,
        };
    }

    /// <summary>
    ///     Adds a spline to the network and generates cross-sections for it.
    /// </summary>
    public static List<UnifiedCrossSection> AddSplineWithCrossSections(
        UnifiedRoadNetwork network,
        ParameterizedRoadSpline paramSpline,
        float crossSectionSpacing = 1.0f)
    {
        network.AddSpline(paramSpline);
        network.SplineMaterialMap[paramSpline.SplineId] = paramSpline.MaterialName;

        var samples = paramSpline.Spline.SampleByDistance(crossSectionSpacing);
        var crossSections = new List<UnifiedCrossSection>();
        var globalIndexStart = network.CrossSections.Count;

        for (var i = 0; i < samples.Count; i++)
        {
            var cs = UnifiedCrossSection.FromSplineSample(samples[i], paramSpline, globalIndexStart + i, i);
            cs.IsSplineStart = i == 0;
            cs.IsSplineEnd = i == samples.Count - 1;
            crossSections.Add(cs);
            network.AddCrossSection(cs);
        }

        return crossSections;
    }

    /// <summary>
    ///     Builds a network, detects junctions, and returns the network.
    /// </summary>
    public static UnifiedRoadNetwork BuildNetworkWithJunctions(
        params ParameterizedRoadSpline[] splines)
    {
        var network = new UnifiedRoadNetwork();
        foreach (var spline in splines)
            AddSplineWithCrossSections(network, spline);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);
        foreach (var j in junctions)
            network.Junctions.Add(j);

        return network;
    }

    /// <summary>
    ///     Creates a flat heightmap of the given size with uniform elevation.
    /// </summary>
    public static float[,] CreateFlatHeightmap(int size, float elevation = 100f)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            hm[y, x] = elevation;
        return hm;
    }

    /// <summary>
    ///     Creates a heightmap with a step function (elevation changes at a given x coordinate).
    /// </summary>
    public static float[,] CreateStepHeightmap(int size, int stepX, float lowElevation, float highElevation)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            hm[y, x] = x < stepX ? lowElevation : highElevation;
        return hm;
    }

    /// <summary>
    ///     Creates a heightmap with a linear slope in the X direction.
    /// </summary>
    public static float[,] CreateSlopeHeightmap(int size, float startElevation, float endElevation)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            hm[y, x] = startElevation + (endElevation - startElevation) * ((float)x / (size - 1));
        return hm;
    }

    /// <summary>
    ///     Builds elevation graph and chains, then runs chain-based smoothing on a network.
    /// </summary>
    public static (List<ElevationChain> chains, Dictionary<int, List<UnifiedCrossSection>> csBySpline)
        RunChainSmoothing(UnifiedRoadNetwork network, float[,] heightMap, float metersPerPixel = 1f,
            RoadSmoothingParameters? parameters = null)
    {
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        var csBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var smoother = new OptimizedElevationSmoother();
        var defaultParams = parameters ?? new RoadSmoothingParameters { CrossSectionIntervalMeters = 0.5f };

        foreach (var chain in chains)
        {
            var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(
                chain, csBySpline, defaultParams.CrossSectionIntervalMeters);
            smoother.CalculateChainElevations(chainCS, defaultParams, heightMap, metersPerPixel);
            OptimizedElevationSmoother.PropagateToDeduped(chain);
        }

        return (chains, csBySpline);
    }
}
