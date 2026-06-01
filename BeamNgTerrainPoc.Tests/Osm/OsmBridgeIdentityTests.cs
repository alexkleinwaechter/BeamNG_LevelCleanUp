using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Tests that bridge/tunnel identity is preserved through OsmGeometryProcessor.ConvertLinesToSplines
///     when excludeBridges/excludeTunnels is false and spline merging is disabled.
/// </summary>
public class OsmBridgeIdentityTests
{
    /// <summary>
    ///     Creates an OsmFeature representing a road way with the given coordinates.
    /// </summary>
    private static OsmFeature CreateRoadFeature(
        long id, List<GeoCoordinate> coords, List<long> nodeIds,
        bool isBridge = false, bool isTunnel = false, int layer = 0)
    {
        var tags = new Dictionary<string, string>
        {
            ["highway"] = "primary",
            ["lanes"] = "2",
            ["surface"] = "asphalt"
        };
        if (isBridge)
        {
            tags["bridge"] = "yes";
            tags["layer"] = layer.ToString();
        }

        if (isTunnel)
        {
            tags["tunnel"] = "yes";
            tags["layer"] = layer.ToString();
        }

        return new OsmFeature
        {
            Id = id,
            FeatureType = OsmFeatureType.Way,
            GeometryType = OsmGeometryType.LineString,
            Coordinates = coords,
            NodeIds = nodeIds,
            Tags = tags
        };
    }

    /// <summary>
    ///     Creates a small bounding box and corresponding geo coordinates for a simple
    ///     3-way test scenario (road → bridge → road) well within terrain bounds.
    /// </summary>
    private static (GeoBoundingBox bbox, List<OsmFeature> features) CreateBridgeScenario(
        bool bridgeHasBridgeTag = true)
    {
        // Small bounding box (~500m x ~500m at mid-latitudes)
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // Three ways sharing nodes: road1 → bridge → road2
        // Coordinates placed well within the bounding box
        var road1Coords = new List<GeoCoordinate>
        {
            new(3.001, 42.402),
            new(3.0015, 42.402),
            new(3.002, 42.402) // shared node with bridge start
        };
        var bridgeCoords = new List<GeoCoordinate>
        {
            new(3.002, 42.402), // shared with road1 end
            new(3.0025, 42.402),
            new(3.003, 42.402) // shared with road2 start
        };
        var road2Coords = new List<GeoCoordinate>
        {
            new(3.003, 42.402), // shared with bridge end
            new(3.0035, 42.402),
            new(3.004, 42.402)
        };

        var road1 = CreateRoadFeature(1001, road1Coords,
            [100, 101, 102]);
        var bridge = CreateRoadFeature(1002, bridgeCoords,
            [102, 103, 104], // node 102 shared with road1
            isBridge: bridgeHasBridgeTag, layer: 1);
        var road2 = CreateRoadFeature(1003, road2Coords,
            [104, 105, 106]); // node 104 shared with bridge

        return (bbox, [road1, bridge, road2]);
    }

    [Fact]
    public void BridgeSpline_PreservesIsBridge_WhenExcludeBridgesFalse_AndMergingDisabled()
    {
        // Arrange: 3 ways — road, bridge, road — with excludeBridges=false and merging disabled
        var (bbox, features) = CreateBridgeScenario();
        var processor = new OsmGeometryProcessor();

        // Act: Convert with excludeBridges=false, disableSplineMerging=true
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: false, excludeTunnels: false,
            disableSplineMerging: true);

        // Assert: The bridge spline should still have IsBridge=true
        Assert.True(splines.Count >= 3, $"Expected at least 3 splines (got {splines.Count})");
        var bridgeSplines = splines.Where(s => s.IsBridge).ToList();
        Assert.Single(bridgeSplines);
    }

    [Fact]
    public void TunnelSpline_PreservesIsTunnel_WhenExcludeTunnelsFalse_AndMergingDisabled()
    {
        // Arrange: Same scenario but with tunnel instead of bridge
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        var road1 = CreateRoadFeature(1001, [
            new GeoCoordinate(3.001, 42.402),
            new GeoCoordinate(3.0015, 42.402),
            new GeoCoordinate(3.002, 42.402)
        ], [100, 101, 102]);

        var tunnel = CreateRoadFeature(1002, [
            new GeoCoordinate(3.002, 42.402),
            new GeoCoordinate(3.0025, 42.402),
            new GeoCoordinate(3.003, 42.402)
        ], [102, 103, 104], isTunnel: true, layer: -1);

        var road2 = CreateRoadFeature(1003, [
            new GeoCoordinate(3.003, 42.402),
            new GeoCoordinate(3.0035, 42.402),
            new GeoCoordinate(3.004, 42.402)
        ], [104, 105, 106]);

        var processor = new OsmGeometryProcessor();
        var splines = processor.ConvertLinesToSplines(
            [road1, tunnel, road2], bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: false, excludeTunnels: false,
            disableSplineMerging: true);

        Assert.True(splines.Count >= 3);
        var tunnelSplines = splines.Where(s => s.IsTunnel).ToList();
        Assert.Single(tunnelSplines);
    }

    [Fact]
    public void BridgeSpline_PreservesStructureMetadata_WhenExcludeBridgesFalse()
    {
        // Arrange
        var (bbox, features) = CreateBridgeScenario();
        var processor = new OsmGeometryProcessor();

        // Act
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: false, excludeTunnels: false,
            disableSplineMerging: true);

        // Assert: bridge should have layer=1 and StructureType set
        var bridgeSpline = splines.FirstOrDefault(s => s.IsBridge);
        Assert.NotNull(bridgeSpline);
        Assert.Equal(1, bridgeSpline.Layer);
    }

    [Fact]
    public void BridgeSpline_StillProtected_WhenExcludeBridgesTrue()
    {
        // Verify the existing behavior still works: with excludeBridges=true,
        // bridge goes through Step 5 (protected) and gets IsBridge=true
        var (bbox, features) = CreateBridgeScenario();
        var processor = new OsmGeometryProcessor();

        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: true, excludeTunnels: false,
            disableSplineMerging: true);

        var bridgeSplines = splines.Where(s => s.IsBridge).ToList();
        Assert.Single(bridgeSplines);
        Assert.Equal(1, bridgeSplines[0].Layer);
    }
}
