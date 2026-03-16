using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Generates DecalRoad objects from a UnifiedRoadNetwork.
/// Uses cross-section data (same as MasterSplineExporter) for accurate centerline alignment,
/// then applies lateral offsets, corridor-based junction suppression, and chunking.
/// Two-pass architecture: Pass 1 builds road corridors, Pass 2 generates DecalRoads
/// with per-node corridor overlap checking.
/// </summary>
public class DecalRoadGenerator
{
    /// <summary>
    /// Generate all DecalRoad objects for the given network.
    /// </summary>
    public static List<GeneratedDecalRoad> Generate(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults)
    {
        var results = new List<GeneratedDecalRoad>();

        // Pass 1: Build road corridors for all eligible splines
        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, settings, appDataDefaults, settings.NodeSpacingMeters);

        // Build junction influence zones for proximity filter
        var junctionZones = RoadCorridorOverlapChecker.BuildJunctionInfluenceZones(
            network.Junctions, corridors);

        // Build continuity lookup for Phase 2 centerline preservation
        var continuityLookup = BuildContinuityLookup(network.Junctions);

        // Pass 2: Generate DecalRoads with corridor overlap checking
        foreach (var spline in network.Splines)
        {
            if (spline.IsBridge || spline.IsTunnel)
                continue;

            var layerSet = DecalRoadLayerSetResolver.Resolve(
                spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            var splineResults = GenerateForSpline(
                spline, layerSet, crossSections,
                corridors, junctionZones, continuityLookup,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                settings.NodeSpacingMeters);
            results.AddRange(splineResults);
        }

        return results;
    }

    internal static List<GeneratedDecalRoad> GenerateForSpline(
        ParameterizedRoadSpline spline,
        DecalRoadLayerSet layerSet,
        IReadOnlyList<UnifiedCrossSection> crossSections,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeSpacingMeters)
    {
        var results = new List<GeneratedDecalRoad>();
        // Use master spline width for lateral offsets (cascade: MasterSplineWidth → RoadSurfaceWidth → RoadWidth)
        // MasterSplineWidth is intentionally narrower than RoadSurfaceWidth to account for material dither;
        // DecalRoad edge blends are designed to visually improve the dither area at road edges.
        var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
        var laneCount = GetLaneCount(spline, layerSet);
        var splineName = GetSplineName(spline);

        // Sub-sample cross-sections at desired node spacing
        var sampledSections = SubSampleCrossSections(crossSections, nodeSpacingMeters);
        if (sampledSections.Count < 2) return results;

        // Expand layers (mirroring, per-lane replication)
        var expandedLayers = ExpandLayers(layerSet.Layers, laneCount);

        foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)
        {
            if (!layer.IsEnabled) continue;

            float nodeWidth;
            if (layer.IsTrackWidth)
                nodeWidth = roadWidth;
            else if (layer.IsLaneWidth)
                nodeWidth = roadWidth / Math.Max(1, laneCount);
            else
                nodeWidth = layer.Width;

            // Calculate laterally offset nodes using cross-section normals
            var offsetNodes2D = new List<Vector2>(sampledSections.Count);
            foreach (var cs in sampledSections)
            {
                var offset = position * 0.5f * roadWidth;
                var offsetPos = cs.CenterPoint + cs.NormalDirection * offset;
                offsetNodes2D.Add(offsetPos);
            }

            // Build segments using corridor overlap suppression
            List<List<(Vector2 Pos, int SectionIndex)>> segments;
            if (layer.InterruptAtJunctions)
            {
                segments = BuildSegmentsWithCorridorCheck(
                    offsetNodes2D, spline.SplineId, corridors, junctionZones,
                    layer.LayerType, continuityLookup);
            }
            else
            {
                // No interruption — single segment with all nodes
                var allNodes = new List<(Vector2, int)>();
                for (int i = 0; i < offsetNodes2D.Count; i++)
                    allNodes.Add((offsetNodes2D[i], i));
                segments = [allNodes];
            }

            // Process each segment
            int chunkIndex = 0;
            foreach (var segment in segments)
            {
                // Convert to world coordinates with elevation from cross-sections
                var worldNodesSegment = new List<float[]>(segment.Count);
                foreach (var (pos, sectionIdx) in segment)
                {
                    var cs = sampledSections[sectionIdx];

                    // Use TargetElevation from unified pipeline (smoothed/harmonized),
                    // matching MasterSplineExporter behavior exactly
                    float elevation;
                    if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                    {
                        elevation = cs.TargetElevation;
                    }
                    else
                    {
                        elevation = GetHeightMapElevation(heightMap, pos.X, pos.Y, metersPerPixel);
                    }

                    var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                        pos.X, pos.Y, elevation + terrainBaseHeight,
                        terrainSizePixels, metersPerPixel);
                    worldNodesSegment.Add([worldPos.X, worldPos.Y, worldPos.Z, nodeWidth]);
                }

                // Reverse node order for flipped layers (right-side mirrored).
                // This flips the texture UV along the spline direction, matching
                // BeamNG's layerMgr.lua isFlip behavior for edge blend textures
                // that need opposite orientation on each side of the road.
                if (isFlipped)
                    worldNodesSegment.Reverse();

                // Chunk into ≤100 nodes with shared boundary nodes (overlap by 1)
                var chunks = ChunkNodes(worldNodesSegment, maxNodesPerChunk: 100);
                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    chunkIndex++;
                    var name = $"{splineName}_{layer.Name}_{side}_{chunkIndex:D3}";

                    // Per-chunk fade: first chunk gets fadeIn, last gets fadeOut,
                    // middle chunks get no fade (matching BeamNG layerMgr behavior).
                    // When flipped, swap fade directions since nodes are reversed.
                    var startFade = (ci == 0) ? (isFlipped ? layer.FadeOut : layer.FadeIn) : 0f;
                    var endFade = (ci == chunks.Count - 1) ? (isFlipped ? layer.FadeIn : layer.FadeOut) : 0f;

                    results.Add(new GeneratedDecalRoad
                    {
                        Name = name,
                        ParentGroupName = splineName,
                        Material = layer.Material,
                        TextureLength = layer.TextureLength,
                        RenderPriority = layer.RenderPriority,
                        StartEndFade = [startFade, endFade],
                        DistanceFade = layer.DistanceFade,
                        Drivability = layer.Drivability,
                        IsAIRoad = layer.LayerType == DecalRoadLayerType.AIRoad,
                        LanesLeft = layer.LanesLeft,
                        LanesRight = layer.LanesRight,
                        OneWay = layer.OneWay,
                        FlipDirection = layer.FlipDirection,
                        Nodes = chunks[ci]
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Sub-samples cross-sections at the desired node spacing interval.
    /// Matches MasterSplineExporter.SampleNodesFromUnifiedCrossSections step logic.
    /// Always includes first and last cross-sections.
    /// </summary>
    internal static List<UnifiedCrossSection> SubSampleCrossSections(
        IReadOnlyList<UnifiedCrossSection> crossSections,
        float nodeSpacingMeters)
    {
        if (crossSections.Count <= 2)
            return crossSections.ToList();

        // Estimate total path length from cross-section positions
        float totalLength = 0;
        for (int i = 1; i < crossSections.Count; i++)
            totalLength += Vector2.Distance(crossSections[i - 1].CenterPoint, crossSections[i].CenterPoint);

        var nodeCount = Math.Max(2, (int)Math.Ceiling(totalLength / nodeSpacingMeters) + 1);
        var step = Math.Max(1, crossSections.Count / nodeCount);

        var sampled = new List<UnifiedCrossSection>();
        for (int i = 0; i < crossSections.Count; i += step)
            sampled.Add(crossSections[i]);

        // Always include the last cross-section
        if (sampled.Count == 0 || sampled[^1] != crossSections[^1])
            sampled.Add(crossSections[^1]);

        return sampled;
    }

    /// <summary>
    /// Calculates lane boundary positions as normalized values in [-1, +1].
    /// For N lanes, returns N-1 boundary positions.
    /// </summary>
    public static float[] CalculateLaneBoundaryPositions(int laneCount)
    {
        if (laneCount <= 1) return [];

        var positions = new float[laneCount - 1];
        for (int i = 1; i < laneCount; i++)
            positions[i - 1] = -1.0f + 2.0f * i / laneCount;

        return positions;
    }

    /// <summary>
    /// Calculates lane center positions as normalized values in [-1, +1].
    /// For N lanes, returns N positions (one per lane).
    /// Matches BeamNG layerMgr.lua tread mark positioning:
    ///   Left lane i:  ((-i * laneWidth) + laneWidth/2) / halfWidth
    ///   Right lane i: ((i * laneWidth) - laneWidth/2) / halfWidth
    /// Simplified: lane center[k] = -1.0 + (2*k + 1) / N  for k in 0..N-1
    /// </summary>
    public static float[] CalculateLaneCenterPositions(int laneCount)
    {
        if (laneCount <= 0) return [];

        var positions = new float[laneCount];
        for (int k = 0; k < laneCount; k++)
            positions[k] = -1.0f + (2 * k + 1.0f) / laneCount;

        return positions;
    }

    /// <summary>
    /// Splits a node list into chunks of at most maxNodesPerChunk.
    /// Adjacent chunks share a boundary node (overlap by 1) to ensure
    /// seamless spline continuity, matching BeamNG's chunking behavior.
    /// </summary>
    public static List<List<float[]>> ChunkNodes(List<float[]> nodes, int maxNodesPerChunk = 100)
    {
        var chunks = new List<List<float[]>>();
        int i = 0;
        while (i < nodes.Count)
        {
            var count = Math.Min(maxNodesPerChunk, nodes.Count - i);
            chunks.Add(nodes.GetRange(i, count));
            // Advance by (maxNodesPerChunk - 1) so last node becomes first of next chunk
            i += maxNodesPerChunk - 1;
        }
        return chunks;
    }

    /// <summary>
    /// Expands layers by mirroring and per-lane replication.
    /// Returns tuples of (layer, normalizedPosition, sideLabel, laneIndex, isFlipped).
    /// When IsFlipped is true, nodes must be reversed to flip texture UV along the spline,
    /// matching BeamNG's layerMgr.lua isFlip behavior for right-side mirrored layers.
    /// </summary>
    internal static List<(DecalRoadLayerDefinition Layer, float Position, string Side, int LaneIndex, bool IsFlipped)>
        ExpandLayers(IReadOnlyList<DecalRoadLayerDefinition> layers, int laneCount)
    {
        var expanded = new List<(DecalRoadLayerDefinition, float, string, int, bool)>();

        foreach (var layer in layers)
        {
            if (layer.LayerType == DecalRoadLayerType.TreadMarks)
            {
                // Tread marks: one DecalRoad per lane, centered in each lane.
                // Covers all lanes including center lane for odd counts (e.g. 3 lanes).
                // isFlip = false for all (tire tracks are symmetric).
                var centers = CalculateLaneCenterPositions(laneCount);
                int leftNum = 0, rightNum = 0;
                for (int i = 0; i < centers.Length; i++)
                {
                    string side;
                    if (centers[i] < -0.01f)
                        side = $"L{++leftNum}";
                    else if (centers[i] > 0.01f)
                        side = $"R{++rightNum}";
                    else
                        side = "C";
                    expanded.Add((layer, centers[i], side, i, false));
                }
            }
            else if (layer.IsPerLane)
            {
                // Replicate at each lane boundary
                var boundaries = CalculateLaneBoundaryPositions(laneCount);
                for (int i = 0; i < boundaries.Length; i++)
                {
                    expanded.Add((layer, boundaries[i], $"C{i + 1}", i, false));
                }
            }
            else if (layer.IsMirrored)
            {
                // Left: original node order, Right: reversed nodes (flips texture UV)
                expanded.Add((layer, -MathF.Abs(layer.Position), "L", 0, false));
                expanded.Add((layer, MathF.Abs(layer.Position), "R", 0, true));
            }
            else
            {
                // Single placement at declared position
                var side = layer.Position < -0.01f ? "L" : layer.Position > 0.01f ? "R" : "C";
                expanded.Add((layer, layer.Position, side, 0, false));
            }
        }

        return expanded;
    }

    /// <summary>
    /// Builds continuous segments by suppressing nodes that fall inside another road's corridor.
    /// Each node's actual 2D position (after lateral offset) is checked — this naturally
    /// handles side-specific suppression without L/R classification.
    /// </summary>
    private static List<List<(Vector2 Pos, int SectionIndex)>> BuildSegmentsWithCorridorCheck(
        IReadOnlyList<Vector2> offsetNodes,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        DecalRoadLayerType layerType,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup,
        int minSegmentNodes = 3)
    {
        var segments = new List<List<(Vector2, int)>>();
        var current = new List<(Vector2, int)>();

        // Phase 2: check if this spline is continuous somewhere
        HashSet<int>? terminatorsWeCanIgnore = null;
        if (layerType == DecalRoadLayerType.CenterLine && continuityLookup != null)
            continuityLookup.TryGetValue(ownSplineId, out terminatorsWeCanIgnore);

        for (int i = 0; i < offsetNodes.Count; i++)
        {
            var result = RoadCorridorOverlapChecker.CheckWithJunctionFilter(
                offsetNodes[i], ownSplineId, corridors, junctionZones);

            bool suppress = result.IsOverlapping;

            // Phase 2: Continuous road centerline preservation
            if (suppress && terminatorsWeCanIgnore != null &&
                result.OverlappingSplineId.HasValue &&
                terminatorsWeCanIgnore.Contains(result.OverlappingSplineId.Value))
            {
                suppress = false; // This spline is continuous, overlapping road terminates here
            }

            if (suppress)
            {
                if (current.Count >= minSegmentNodes)
                    segments.Add(current);
                current = [];
            }
            else
            {
                current.Add((offsetNodes[i], i));
            }
        }

        if (current.Count >= minSegmentNodes)
            segments.Add(current);

        return segments;
    }

    /// <summary>
    /// For Phase 2: lookup of which splines are continuous at which junctions.
    /// Key = splineId, Value = set of splineIds that terminate at junctions where key is continuous.
    /// If spline A is continuous at a junction where spline B terminates,
    /// then ContinuityLookup[A] contains B.
    /// </summary>
    private static Dictionary<int, HashSet<int>> BuildContinuityLookup(
        IReadOnlyList<NetworkJunction> junctions)
    {
        var lookup = new Dictionary<int, HashSet<int>>();

        foreach (var junction in junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type == JunctionType.Endpoint) continue;

            var continuousIds = junction.GetContinuousRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();
            var terminatingIds = junction.GetTerminatingRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();

            // For each continuous road, record which terminating roads it can ignore
            foreach (var contId in continuousIds)
            {
                if (!lookup.TryGetValue(contId, out var set))
                {
                    set = [];
                    lookup[contId] = set;
                }
                foreach (var termId in terminatingIds)
                    set.Add(termId);
            }
        }

        return lookup;
    }

    private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
    {
        // 1. OSM tags
        if (spline.OsmTags != null &&
            spline.OsmTags.TryGetValue("lanes", out var lanesStr) &&
            int.TryParse(lanesStr, out var lanes) && lanes > 0)
            return lanes;

        // 2. Layer set default
        return layerSet.DefaultLaneCount;
    }

    private static string GetSplineName(ParameterizedRoadSpline spline)
    {
        // Use material name + ID for unique naming (matches MasterSplineExporter pattern)
        return $"{spline.MaterialName}_{spline.SplineId:D3}";
    }

    private static float GetHeightMapElevation(
        float[,] heightMap, float terrainX, float terrainY, float metersPerPixel)
    {
        var pixelX = (int)(terrainX / metersPerPixel);
        var pixelY = (int)(terrainY / metersPerPixel);
        var size = heightMap.GetLength(0);
        pixelX = Math.Clamp(pixelX, 0, size - 1);
        pixelY = Math.Clamp(pixelY, 0, size - 1);
        return heightMap[pixelY, pixelX]; // [y, x] row-major
    }
}
