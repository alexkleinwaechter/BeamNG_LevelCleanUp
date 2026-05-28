using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

/// <summary>
///     Verifies that bridge/tunnel splines are only excluded from DecalRoad generation
///     when the corresponding ExcludeBridgesFromTerrain / ExcludeTunnelsFromTerrain parameter is true.
///     Regression tests for the bug where RoadCorridorBuilder and DecalRoadGenerator
///     unconditionally skipped all bridge/tunnel splines regardless of the parameter.
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
}
