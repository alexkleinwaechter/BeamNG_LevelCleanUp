using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

/// <summary>
///     Tunnel plan Phase 5: DecalRoads through the tunnel. With <c>EnableTunnelMesh</c> on, tunnel
///     runs project onto the tube floor collision (<c>OverObjects</c>) and the legacy whole-spline
///     tunnel skip is lifted; with it off, today's behavior stands (no decals on excluded tunnel
///     splines, tunnel runs never OverObjects).
/// </summary>
public class TunnelDecalRoadTests
{
    private static readonly IReadOnlyDictionary<string, DecalRoadLayerSet> EmptyDefaults =
        new Dictionary<string, DecalRoadLayerSet>();

    private static DecalRoadSettings Settings() => new()
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
                        RenderOnTunnels = true
                    }
                ]
            }
        }
    };

    private static StructureSegment TunnelSegment(float start, float end) => new()
    {
        Type = StructureType.Tunnel,
        StartDistance = start,
        EndDistance = end,
        OsmWayIds = [777L]
    };

    private static UnifiedRoadNetwork BuildMergedCorridor(bool meshOn, out StructureSegment segment)
    {
        segment = TunnelSegment(40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);
        corridor.Parameters.TunnelRules = new TunnelRuleSystemOptions { EnableTunnelMesh = meshOn };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = 50f;
            if (cs.DistanceAlongSpline >= segment.StartDistance &&
                cs.DistanceAlongSpline <= segment.EndDistance)
            {
                cs.StructureSpanId = segment.SpanId;
                cs.StructureSpanType = StructureType.Tunnel;
            }
        }

        return network;
    }

    [Fact]
    public void MergedCorridor_MeshOn_TunnelRunProjectsOntoFloorCollision()
    {
        var network = BuildMergedCorridor(meshOn: true, out _);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            Settings(),
            EmptyDefaults);

        // ground / tunnel / ground — the tunnel run gets OverObjects (tube floor collision).
        Assert.Equal(3, roads.Count);
        var tunnelRun = Assert.Single(roads, r => r.OverObjects);
        var grounds = roads.Where(r => !r.OverObjects).ToList();
        Assert.Equal(2, grounds.Count);
        Assert.True(tunnelRun.Nodes.Count >= 2);
    }

    [Fact]
    public void MergedCorridor_MeshOff_NoOverObjects()
    {
        var network = BuildMergedCorridor(meshOn: false, out _);

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            Settings(),
            EmptyDefaults);

        Assert.NotEmpty(roads);
        Assert.All(roads, r => Assert.False(r.OverObjects));
    }

    [Fact]
    public void WholeSplineTunnel_MeshOff_StaysSkipped()
    {
        var tunnel = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isTunnel: true, excludeTunnels: true);
        tunnel.Parameters.TunnelRules = new TunnelRuleSystemOptions(); // mesh off

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, tunnel);
        foreach (var cs in network.GetCrossSectionsForSpline(tunnel.SplineId))
            cs.TargetElevation = 50f;

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            Settings(),
            EmptyDefaults);

        Assert.Empty(roads); // legacy skip preserved
    }

    [Fact]
    public void WholeSplineTunnel_MeshOn_ProducesDecals()
    {
        var tunnel = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isTunnel: true, excludeTunnels: true);
        tunnel.Parameters.TunnelRules = new TunnelRuleSystemOptions { EnableTunnelMesh = true };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, tunnel);
        foreach (var cs in network.GetCrossSectionsForSpline(tunnel.SplineId))
            cs.TargetElevation = 50f;

        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            Settings(),
            EmptyDefaults);

        Assert.NotEmpty(roads); // the skip (and its merged-corridor decal-loss bug) is lifted
    }

    [Fact]
    public void PartitionSectionsByStructure_TunnelHasMesh_TunnelRunOnDeck()
    {
        var segment = TunnelSegment(0f, 10f);
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0),
            structureSegments: [segment]);
        var spanId = segment.SpanId;
        var sections = new[] { -1, -1, spanId, spanId, spanId, -1, -1, -1 }
            .Select(id => new UnifiedCrossSection { StructureSpanId = id })
            .ToList();

        var runs = DecalRoadGenerator.PartitionSectionsByStructure(
            spline, sections, isGeneratedBridge: false, tunnelHasMesh: true);

        var tunnelRun = Assert.Single(runs,
            r => r.Context == DecalRoadGenerator.StructureRunContext.Tunnel);
        Assert.True(tunnelRun.OnDeck);

        // Without the mesh, byte-identical to today: never OnDeck.
        var legacyRuns = DecalRoadGenerator.PartitionSectionsByStructure(
            spline, sections, isGeneratedBridge: false);
        var legacyTunnelRun = Assert.Single(legacyRuns,
            r => r.Context == DecalRoadGenerator.StructureRunContext.Tunnel);
        Assert.False(legacyTunnelRun.OnDeck);
    }
}
