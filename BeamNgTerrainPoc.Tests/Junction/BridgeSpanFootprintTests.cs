using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
/// Phase A3 (plan doc 14 §5): the <see cref="BridgeSpanFootprint"/> XY containment query — the robust
/// "what is under the bridge" test that replaces the sparse mid-spline-crossing sampler.
/// </summary>
public class BridgeSpanFootprintTests
{
    // A 30 m ground corridor (width 8 → half-width 4) carrying a bridge span over arc-range [10,20] m.
    private static UnifiedRoadNetwork MergedCorridorWithSpan(float roadWidth = 8f)
    {
        var span = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 10f,
            EndDistance = 20f,
            Layer = 1,
            OsmWayIds = { 555L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(0, 0), new(30, 0), roadWidth: roadWidth, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor, crossSectionSpacing: 1f);
        return network;
    }

    [Fact]
    public void BuildAll_ProducesOneFootprint_ForOneBridgeSpan()
    {
        var network = MergedCorridorWithSpan();

        var footprints = BridgeSpanFootprint.BuildAll(network);

        var fp = Assert.Single(footprints);
        Assert.Equal(1, fp.OwnerSplineId);
        Assert.Equal(1, fp.Layer);
    }

    [Theory]
    [InlineData(15f, 0f, true)]   // dead-center of the span
    [InlineData(15f, 3.5f, true)] // within half-width 4
    [InlineData(15f, 6f, false)]  // beyond the deck width
    [InlineData(5f, 0f, false)]   // before the span start (10 m)
    [InlineData(25f, 0f, false)]  // after the span end (20 m)
    public void Contains_MatchesSweptDeckPolygon(float x, float y, bool expected)
    {
        var fp = Assert.Single(BridgeSpanFootprint.BuildAll(MergedCorridorWithSpan()));

        Assert.Equal(expected, fp.Contains(new Vector2(x, y)));
    }

    [Fact]
    public void BuildAll_MergedSpanWithLayerRanges_OneFootprintPerSubRange()
    {
        // Doc 10: a consolidated span keeps per-sub-range layers so the footprint grade-separation
        // pass still compares the RIGHT local layer at each crossing — one footprint per sub-range,
        // all carrying the SAME merged SpanId (deck identity).
        var span = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 10f,
            EndDistance = 20f,
            Layer = 3,
            OsmWayIds = { 555L, 556L },
            LayerRanges = [new StructureLayerRange(10f, 15f, 3), new StructureLayerRange(15f, 20f, 1)]
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(0, 0), new(30, 0), isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor, crossSectionSpacing: 1f);

        var footprints = BridgeSpanFootprint.BuildAll(network);

        Assert.Equal(2, footprints.Count);
        Assert.All(footprints, fp => Assert.Equal(span.SpanId, fp.SpanId));
        var layer3 = Assert.Single(footprints, fp => fp.Layer == 3);
        var layer1 = Assert.Single(footprints, fp => fp.Layer == 1);
        Assert.True(layer3.Contains(new Vector2(12f, 0f)));
        Assert.False(layer3.Contains(new Vector2(18f, 0f)));
        Assert.True(layer1.Contains(new Vector2(18f, 0f)));
        Assert.False(layer1.Contains(new Vector2(12f, 0f)));
    }

    [Fact]
    public void BuildAll_FlagOff_IsEmpty()
    {
        // Legacy separated bridge spline (flag off): no merged-corridor footprints.
        var network = new UnifiedRoadNetwork();
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(0, 0), new(30, 0), isBridge: true,
            structureSegments:
            [
                new StructureSegment { Type = StructureType.Bridge, StartDistance = 0, EndDistance = 30, Layer = 1, OsmWayIds = { 7L } }
            ]);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        Assert.Empty(BridgeSpanFootprint.BuildAll(network));
    }

    [Fact]
    public void BuildAll_PlainCorridor_IsEmpty()
    {
        var network = new UnifiedRoadNetwork();
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(0, 0), new(30, 0), mergeStructuresIntoCorridor: true); // no StructureSegments
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        Assert.Empty(BridgeSpanFootprint.BuildAll(network));
    }
}
