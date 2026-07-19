using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

/// <summary>
///     Verifies generated bridge/tunnel filtering behavior for DecalRoad-related paths.
///     Terrain corridors still exclude generated bridges/tunnels, while DecalRoadGenerator
///     emits visual overlays for generated bridge decks with OverObjects enabled.
/// </summary>
public class BridgeDecalRoadFilterTests
{
    private static readonly DecalRoadLayerSet DefaultLayerSet = new()
    {
        Name = "unclassified",
        IsEnabled = true,
        DefaultLaneCount = 2,
        Layers =
        [
            new DecalRoadLayerDefinition
            {
                Name = "Surface",
                IsEnabled = true,
                IsTrackWidth = true,
                Material = "road_asphalt",
                Position = 0f
            }
        ]
    };

    private static readonly DecalRoadSettings DefaultSettings = new()
    {
        Enabled = true,
        NodeSpacingMeters = 2.0f,
        OsmLayerSets = new Dictionary<string, DecalRoadLayerSet>
        {
            ["unclassified"] = DefaultLayerSet
        }
    };

    private static readonly IReadOnlyDictionary<string, DecalRoadLayerSet> EmptyDefaults =
        new Dictionary<string, DecalRoadLayerSet>();

    private static DecalRoadSettings CreateGeneratorSettings(
        bool overObjects = false,
        bool renderOnRoads = true,
        bool renderOnBridges = true,
        bool renderOnTunnels = true)
    {
        return new DecalRoadSettings
        {
            Enabled = true,
            NodeSpacingMeters = 2.0f,
            OsmLayerSets = new Dictionary<string, DecalRoadLayerSet>
            {
                ["unclassified"] = new()
                {
                    Name = "unclassified",
                    IsEnabled = true,
                    DefaultLaneCount = 2,
                    Layers =
                    [
                        new DecalRoadLayerDefinition
                        {
                            Name = "Surface",
                            IsEnabled = true,
                            IsTrackWidth = true,
                            Material = "road_asphalt",
                            Position = 0f,
                            OverObjects = overObjects,
                            RenderOnRoads = renderOnRoads,
                            RenderOnBridges = renderOnBridges,
                            RenderOnTunnels = renderOnTunnels
                        }
                    ]
                }
            }
        };
    }

    private static void SetCrossSections(
        UnifiedRoadNetwork network,
        int splineId,
        float targetElevation,
        bool isExcluded)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            cs.TargetElevation = targetElevation;
            cs.IsExcluded = isExcluded;
        }
    }

    /// <summary>
    ///     When ExcludeBridgesFromTerrain=true (default), bridge splines must NOT produce corridors.
    /// </summary>
    [Fact]
    public void BuildCorridors_BridgeExcluded_NoCorridor()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: true);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Empty(corridors);
    }

    [Fact]
    public void Generate_BridgeExcluded_ProducesDeckOverlayDecalRoads()
    {
        const float bridgeElevation = 125f;
        const float terrainBaseHeight = 7f;
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: true);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        SetCrossSections(network, bridge.SplineId, bridgeElevation, isExcluded: true);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight,
            CreateGeneratorSettings(),
            EmptyDefaults);

        Assert.NotEmpty(roads);
        Assert.All(roads, road => Assert.Equal(bridge.SplineId, road.SplineId));
        Assert.All(roads, road => Assert.True(road.OverObjects));
        Assert.All(roads.SelectMany(road => road.Nodes), node =>
            Assert.Equal(bridgeElevation + terrainBaseHeight, node[2], precision: 3));
    }

    [Fact]
    public void Generate_RegularRoad_PreservesLayerOverObjectsFalse()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road);
        SetCrossSections(network, road.SplineId, targetElevation: 50f, isExcluded: false);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(overObjects: false),
            EmptyDefaults);

        Assert.NotEmpty(roads);
        Assert.All(roads, generatedRoad => Assert.False(generatedRoad.OverObjects));
    }

    [Fact]
    public void Generate_RegularRoad_PreservesLayerOverObjectsTrue()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road);
        SetCrossSections(network, road.SplineId, targetElevation: 50f, isExcluded: false);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(overObjects: true),
            EmptyDefaults);

        Assert.NotEmpty(roads);
        Assert.All(roads, generatedRoad => Assert.True(generatedRoad.OverObjects));
    }

    /// <summary>
    ///     When ExcludeBridgesFromTerrain=false, bridge splines MUST produce corridors
    ///     (they're rendered as flat terrain roads).
    /// </summary>
    [Fact]
    public void BuildCorridors_BridgeNotExcluded_ProducesCorridor()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: false);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Single(corridors);
        Assert.True(corridors.ContainsKey(bridge.SplineId));
    }

    /// <summary>
    ///     Same test for tunnels: ExcludeTunnelsFromTerrain=false must produce a corridor.
    /// </summary>
    [Fact]
    public void BuildCorridors_TunnelNotExcluded_ProducesCorridor()
    {
        var tunnel = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isTunnel: true, excludeTunnels: false);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, tunnel);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Single(corridors);
    }

    /// <summary>
    ///     Tunnel with ExcludeTunnelsFromTerrain=true must NOT produce a corridor.
    /// </summary>
    [Fact]
    public void BuildCorridors_TunnelExcluded_NoCorridor()
    {
        var tunnel = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isTunnel: true, excludeTunnels: true);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, tunnel);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Empty(corridors);
    }

    /// <summary>
    ///     Mixed network: road + bridge (not excluded) + road should produce 3 corridors.
    ///     This simulates the Road→Bridge→Road scenario from OSM data.
    /// </summary>
    [Fact]
    public void BuildCorridors_RoadBridgeRoad_NotExcluded_AllThreeGetCorridors()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(100, 150), new Vector2(130, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: false);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new Vector2(130, 150), new Vector2(250, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road2);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Equal(3, corridors.Count);
        Assert.True(corridors.ContainsKey(road1.SplineId));
        Assert.True(corridors.ContainsKey(bridge.SplineId));
        Assert.True(corridors.ContainsKey(road2.SplineId));
    }

    /// <summary>
    ///     Mixed network: road + bridge (excluded) + road should produce only 2 corridors.
    /// </summary>
    [Fact]
    public void BuildCorridors_RoadBridgeRoad_BridgeExcluded_OnlyRoadsGetCorridors()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(100, 150), new Vector2(130, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new Vector2(130, 150), new Vector2(250, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road2);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Equal(2, corridors.Count);
        Assert.True(corridors.ContainsKey(road1.SplineId));
        Assert.False(corridors.ContainsKey(bridge.SplineId));
        Assert.True(corridors.ContainsKey(road2.SplineId));
    }

    /// <summary>
    ///     Regular (non-bridge, non-tunnel) splines are never affected by the exclusion parameters.
    /// </summary>
    [Fact]
    public void BuildCorridors_RegularSpline_AlwaysProducesCorridor()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road);

        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, DefaultSettings, EmptyDefaults, 2.0f);

        Assert.Single(corridors);
    }

    // ─── Bridge-tight OverObjects: deck/ground run partitioning ───────────────────────────────

    private static StructureSegment CreateStructureSegment(
        StructureType type, float startDistance, float endDistance, long wayId = 1234)
    {
        return new StructureSegment
        {
            Type = type,
            StartDistance = startDistance,
            EndDistance = endDistance,
            OsmWayIds = [wayId]
        };
    }

    private static void TagSpanSections(
        UnifiedRoadNetwork network, int splineId, StructureSegment segment)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            if (cs.DistanceAlongSpline >= segment.StartDistance &&
                cs.DistanceAlongSpline <= segment.EndDistance)
                cs.StructureSpanId = segment.SpanId;
    }

    /// <summary>
    ///     Merged corridor road→bridge→road: only the deck stretch gets OverObjects, cut at the
    ///     span boundaries with a shared boundary node into each approach run.
    /// </summary>
    [Fact]
    public void Generate_MergedCorridorBridgeSpan_OverObjectsOnlyOnDeckRun()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f, isExcluded: false);
        TagSpanSections(network, corridor.SplineId, segment);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(),
            EmptyDefaults);

        // One layer, one bridge span in the middle → ground / deck / ground
        Assert.Equal(3, roads.Count);
        var deck = Assert.Single(roads, r => r.OverObjects);
        var grounds = roads.Where(r => !r.OverObjects).ToList();
        Assert.Equal(2, grounds.Count);

        // The deck road covers the 20 m span plus the one-node extension into each approach —
        // far shorter than the 90 m spline the flag used to cover.
        var deckLength = 0f;
        for (var i = 1; i < deck.Nodes.Count; i++)
            deckLength += Vector2.Distance(
                new Vector2(deck.Nodes[i - 1][0], deck.Nodes[i - 1][1]),
                new Vector2(deck.Nodes[i][0], deck.Nodes[i][1]));
        Assert.InRange(deckLength, 19.5f, 28f);

        // One-span overlap: the deck's first two nodes coincide with one approach run's last two,
        // and its last two with the other approach run's first two (curve-gap fix).
        Assert.Contains(grounds, g =>
            NodesEqual(g.Nodes[^1], deck.Nodes[1]) && NodesEqual(g.Nodes[^2], deck.Nodes[0]));
        Assert.Contains(grounds, g =>
            NodesEqual(g.Nodes[0], deck.Nodes[^2]) && NodesEqual(g.Nodes[1], deck.Nodes[^1]));
    }

    private static bool NodesEqual(float[] a, float[] b)
    {
        return MathF.Abs(a[0] - b[0]) < 1e-3f &&
               MathF.Abs(a[1] - b[1]) < 1e-3f &&
               MathF.Abs(a[2] - b[2]) < 1e-3f;
    }

    /// <summary>
    ///     Tunnel spans are tagged with a StructureSpanId too, but must NOT force OverObjects
    ///     (only bridge decks have a mesh to project onto). Tunnels get their own treatment
    ///     when tunnel decal behavior is designed.
    /// </summary>
    [Fact]
    public void Generate_MergedCorridorTunnelSpan_NoOverObjects()
    {
        var segment = CreateStructureSegment(StructureType.Tunnel, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f, isExcluded: false);
        TagSpanSections(network, corridor.SplineId, segment);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(),
            EmptyDefaults);

        // No bridge span → no OverObjects anywhere (the road may still be length-chunked).
        Assert.NotEmpty(roads);
        Assert.All(roads, r => Assert.False(r.OverObjects));
    }

    private static List<UnifiedCrossSection> CreateTaggedSections(params int[] spanIds)
    {
        return spanIds.Select(id => new UnifiedCrossSection { StructureSpanId = id }).ToList();
    }

    [Fact]
    public void PartitionSectionsByStructure_TunnelSegments_TunnelRunsWithoutDeck()
    {
        var segment = CreateStructureSegment(StructureType.Tunnel, 0f, 10f);
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            structureSegments: [segment]);
        var spanId = segment.SpanId;
        var sections = CreateTaggedSections(-1, -1, spanId, spanId, spanId, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 1, DecalRoadGenerator.StructureRunContext.Road, false), runs[0]);
        // tunnel extended two sections into each neighbour (one-span overlap), clamped at index 0
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 6, DecalRoadGenerator.StructureRunContext.Tunnel, false), runs[1]);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            5, 7, DecalRoadGenerator.StructureRunContext.Road, false), runs[2]);
    }

    [Fact]
    public void PartitionSectionsByStructure_MiddleSpan_ThreeRunsWithSharedBoundary()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 0f, 10f);
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            structureSegments: [segment]);
        var spanId = segment.SpanId;
        var sections = CreateTaggedSections(-1, -1, -1, spanId, spanId, spanId, spanId, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 2, DecalRoadGenerator.StructureRunContext.Road, false), runs[0]);
        // deck extended by two sections on each side — one-span overlap with both road runs
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            1, 8, DecalRoadGenerator.StructureRunContext.Bridge, true), runs[1]);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            7, 9, DecalRoadGenerator.StructureRunContext.Road, false), runs[2]);
    }

    [Fact]
    public void PartitionSectionsByStructure_SpanAtStart_TwoRuns()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 0f, 10f);
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            structureSegments: [segment]);
        var spanId = segment.SpanId;
        var sections = CreateTaggedSections(spanId, spanId, spanId, spanId, -1, -1, -1, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 5, DecalRoadGenerator.StructureRunContext.Bridge, true), runs[0]);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            4, 9, DecalRoadGenerator.StructureRunContext.Road, false), runs[1]);
    }

    [Fact]
    public void PartitionSectionsByStructure_NoStructureSegments_SingleRoadRun()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0));
        var sections = CreateTaggedSections(-1, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        var run = Assert.Single(runs);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 3, DecalRoadGenerator.StructureRunContext.Road, false), run);
    }

    [Fact]
    public void PartitionSectionsByStructure_LegacyWholeSplineBridge_SingleDeckRun()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            isBridge: true, excludeBridges: true);
        var sections = CreateTaggedSections(-1, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, true);

        var run = Assert.Single(runs);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 3, DecalRoadGenerator.StructureRunContext.Bridge, true), run);
    }

    [Fact]
    public void PartitionSectionsByStructure_LegacyWholeSplineTunnel_SingleTunnelRunWithoutDeck()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            isTunnel: true, excludeTunnels: false);
        var sections = CreateTaggedSections(-1, -1, -1, -1);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        var run = Assert.Single(runs);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 3, DecalRoadGenerator.StructureRunContext.Tunnel, false), run);
    }

    [Fact]
    public void PartitionSectionsByStructure_SingleSectionGroundGap_CoveredByNeighbourDecks()
    {
        var segA = CreateStructureSegment(StructureType.Bridge, 0f, 10f, wayId: 1);
        var segB = CreateStructureSegment(StructureType.Bridge, 11f, 20f, wayId: 2);
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            structureSegments: [segA, segB]);
        var sections = CreateTaggedSections(
            segA.SpanId, segA.SpanId, segA.SpanId, -1, segB.SpanId, segB.SpanId, segB.SpanId);

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(spline, sections, false);

        // Both spans are deck; the 1-section ground gap between them is absorbed by the
        // neighbours' extension and emits no own (unbuildable) road.
        Assert.All(runs, r => Assert.True(r.OnDeck));
        Assert.Equal(2, runs.Count);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            0, 4, DecalRoadGenerator.StructureRunContext.Bridge, true), runs[0]);
        Assert.Equal(new DecalRoadGenerator.SectionRun(
            2, 6, DecalRoadGenerator.StructureRunContext.Bridge, true), runs[1]);
    }

    // ─── Render scope (RenderOnRoads / RenderOnBridges / RenderOnTunnels) ─────────────────────

    /// <summary>"Only on bridges": road runs skipped, only the deck road remains.</summary>
    [Fact]
    public void Generate_BridgeOnlyLayer_EmitsOnlyDeckRoad()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f, isExcluded: false);
        TagSpanSections(network, corridor.SplineId, segment);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(renderOnRoads: false, renderOnTunnels: false),
            EmptyDefaults);

        var deck = Assert.Single(roads);
        Assert.True(deck.OverObjects);
    }

    /// <summary>"Not on bridges": the deck run is skipped, both approach roads remain.</summary>
    [Fact]
    public void Generate_NotOnBridgesLayer_SkipsDeckRun()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f, isExcluded: false);
        TagSpanSections(network, corridor.SplineId, segment);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(renderOnBridges: false),
            EmptyDefaults);

        Assert.Equal(2, roads.Count);
        Assert.All(roads, r => Assert.False(r.OverObjects));
    }

    /// <summary>"Not on tunnels": the tunnel run is skipped, both approach roads remain.</summary>
    [Fact]
    public void Generate_NotOnTunnelsLayer_SkipsTunnelRun()
    {
        var segment = CreateStructureSegment(StructureType.Tunnel, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f, isExcluded: false);
        TagSpanSections(network, corridor.SplineId, segment);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(renderOnTunnels: false),
            EmptyDefaults);

        Assert.Equal(2, roads.Count);
        Assert.All(roads, r => Assert.False(r.OverObjects));
    }

    /// <summary>"Not on bridges" on a legacy whole-spline generated bridge suppresses the layer entirely.</summary>
    [Fact]
    public void Generate_LegacyBridge_NotOnBridges_EmitsNothing()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: true);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        SetCrossSections(network, bridge.SplineId, targetElevation: 125f, isExcluded: true);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateGeneratorSettings(renderOnBridges: false),
            EmptyDefaults);

        Assert.Empty(roads);
    }
}
