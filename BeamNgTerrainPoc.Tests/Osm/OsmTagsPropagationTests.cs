using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Tests that the full OSM tag bag (D-6) flows from <see cref="RoadSpline.OsmTags" />
///     through <see cref="UnifiedRoadNetworkBuilder" /> onto
///     <see cref="ParameterizedRoadSpline.OsmTags" />.
/// </summary>
public class OsmTagsPropagationTests
{
    [Fact]
    public void BuildNetwork_CopiesOsmTags_FromRoadSpline_ToParameterizedRoadSpline()
    {
        // Arrange: a pre-built RoadSpline carrying a full OSM tag dict, including a raw
        // bridge= value that is NOT promoted to a dedicated field.
        var controlPoints = new List<Vector2>
        {
            new(100, 100),
            new(150, 100),
            new(200, 100)
        };
        var roadSpline = new RoadSpline(controlPoints)
        {
            IsBridge = true,
            OsmRoadType = "primary",
            OsmTags = new Dictionary<string, string>
            {
                ["bridge"] = "viaduct",
                ["highway"] = "primary",
                ["maxheight"] = "4.5"
            }
        };

        var parameters = new RoadSmoothingParameters
        {
            CrossSectionIntervalMeters = 0.5f,
            PreBuiltSplines = new List<RoadSpline> { roadSpline }
        };
        var material = new MaterialDefinition("asphalt", roadParameters: parameters);

        var heightMap = new float[256, 256];
        var builder = new UnifiedRoadNetworkBuilder();

        // Act
        var network = builder.BuildNetwork(
            new List<MaterialDefinition> { material },
            heightMap,
            metersPerPixel: 1f,
            terrainSize: 256);

        // Assert: the ParameterizedRoadSpline carries the same tag bag (reference passed through).
        var paramSpline = network.Splines.FirstOrDefault(s => s.IsBridge);
        Assert.NotNull(paramSpline);
        Assert.NotNull(paramSpline.OsmTags);
        Assert.Equal("viaduct", paramSpline.OsmTags!["bridge"]);
        Assert.Equal("primary", paramSpline.OsmTags["highway"]);
        Assert.Equal("4.5", paramSpline.OsmTags["maxheight"]);
    }
}
