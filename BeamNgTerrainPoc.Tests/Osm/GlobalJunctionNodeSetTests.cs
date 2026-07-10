using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Tests for <see cref="OsmGeometryProcessor.BuildGlobalJunctionNodeSet"/> and the end-to-end
///     ramp "hairpin" fix: two oneway *_link ramps meeting nearly head-to-tail at the node where
///     they touch a through road of a DIFFERENT highway type must not merge into one spline.
///     The through road lives in another merge partition, so only the cross-type global junction
///     set makes the connector's >90° deflection guard see that node as a junction.
/// </summary>
public class GlobalJunctionNodeSetTests
{
    private static OsmFeature Way(long id, List<long> nodeIds,
        List<GeoCoordinate>? coords = null, string highway = "primary", bool oneway = false)
    {
        var tags = new Dictionary<string, string> { ["highway"] = highway };
        if (oneway) tags["oneway"] = "yes";
        return new OsmFeature
        {
            Id = id,
            FeatureType = OsmFeatureType.Way,
            GeometryType = OsmGeometryType.LineString,
            Coordinates = coords ?? [],
            NodeIds = nodeIds,
            Tags = tags
        };
    }

    // ========================================================================================
    //  BuildGlobalJunctionNodeSet rules
    // ========================================================================================

    [Fact]
    public void InteriorNodeOfThroughRoad_TwoWays_IsJunction()
    {
        // Way 2 ends on an INTERIOR node of way 1 (the through road was not split there).
        var features = new List<OsmFeature>
        {
            Way(1, [10, 20, 30]),
            Way(2, [40, 20])
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);

        Assert.Contains(20L, junctions);
        Assert.Single(junctions);
    }

    [Fact]
    public void ThreeEndpointsMeeting_IsJunction()
    {
        // Classic fork/ramp node: three way endpoints share node 20.
        var features = new List<OsmFeature>
        {
            Way(1, [10, 20]),
            Way(2, [20, 30]),
            Way(3, [20, 40])
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);

        Assert.Contains(20L, junctions);
        Assert.Single(junctions);
    }

    [Fact]
    public void TwoEndpointsMeeting_IsNotJunction()
    {
        // A road split into two consecutive ways — plain continuation, not a junction.
        var features = new List<OsmFeature>
        {
            Way(1, [10, 20]),
            Way(2, [20, 30])
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);

        Assert.Empty(junctions);
    }

    [Fact]
    public void SingleWay_HasNoJunctions()
    {
        var features = new List<OsmFeature> { Way(1, [10, 20, 30, 40]) };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);

        Assert.Empty(junctions);
    }

    // ========================================================================================
    //  End-to-end: ramp hairpin across a cross-type junction (ways 690241580 + 387812799
    //  merging across their shared node with primary road 81541137 — the observed bug)
    // ========================================================================================

    [Fact]
    public void ConvertLinesToSplines_RampPairAtCrossTypeJunction_DoesNotMergeIntoHairpin()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // Off-ramp heading east, ending at junction node 200.
        var rampIn = Way(10, [100, 101, 200],
            [new(3.001, 42.402), new(3.0015, 42.402), new(3.002, 42.402)],
            highway: "primary_link", oneway: true);

        // On-ramp starting at junction node 200, heading back west with a slight offset (~170°).
        var rampOut = Way(20, [200, 102, 103],
            [new(3.002, 42.402), new(3.0015, 42.4021), new(3.001, 42.4022)],
            highway: "primary_link", oneway: true);

        // Through road of a DIFFERENT type starting at the same node — another merge partition.
        var through = Way(30, [200, 201, 202],
            [new(3.002, 42.402), new(3.002, 42.403), new(3.002, 42.404)],
            highway: "primary");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [rampIn, rampOut, through], bbox, terrainSize: 512, metersPerPixel: 1f);

        // The two ramps must stay separate splines — no hairpin.
        Assert.Equal(3, splines.Count);
        Assert.DoesNotContain(splines, s => s.OsmWayIds.Contains(10L) && s.OsmWayIds.Contains(20L));
    }
}
