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

    // ========================================================================================
    //  BuildJunctionNodeMaxContinuingClassMap rules (cross-class chaining guard input).
    //  Only bands that CONTINUE through the node count: interior node of a way, or >= 2
    //  way-ends of the band. A higher-class road that merely terminates never blocks.
    // ========================================================================================

    [Fact]
    public void MaxContinuingClass_InteriorHigherClassWay_Counts()
    {
        // Tertiary passes THROUGH node 20 (interior); residential ends there.
        var features = new List<OsmFeature>
        {
            Way(1, [40, 20, 50], highway: "tertiary"),
            Way(2, [10, 20], highway: "residential")
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);
        var maxClass = OsmGeometryProcessor.BuildJunctionNodeMaxContinuingClassMap(features, junctions);

        Assert.Equal(2, maxClass[20L]); // tertiary continues through → its band rules the node
    }

    [Fact]
    public void MaxContinuingClass_TwoHigherClassWayEnds_Count()
    {
        // Tertiary SPLIT at node 20 (two way-ends — the node-5429248736 shape); residential through.
        var features = new List<OsmFeature>
        {
            Way(1, [40, 20], highway: "tertiary"),
            Way(2, [20, 50], highway: "tertiary"),
            Way(3, [10, 20, 30], highway: "residential")
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);
        var maxClass = OsmGeometryProcessor.BuildJunctionNodeMaxContinuingClassMap(features, junctions);

        Assert.Equal(2, maxClass[20L]); // split tertiary still continues through
    }

    [Fact]
    public void MaxContinuingClass_TerminatingHigherClassArm_DoesNotCount()
    {
        // Tertiary merely ENDS on an interior node of the residential — a plain T-junction mouth.
        // The residential (through road) band rules the node, so its own pair may chain through.
        var features = new List<OsmFeature>
        {
            Way(1, [10, 20, 30], highway: "residential"),
            Way(2, [40, 20], highway: "tertiary")
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);
        var maxClass = OsmGeometryProcessor.BuildJunctionNodeMaxContinuingClassMap(features, junctions);

        Assert.Equal(1, maxClass[20L]); // only the residential continues through
    }

    [Fact]
    public void MaxContinuingClass_LinkInheritsParentClass()
    {
        var features = new List<OsmFeature>
        {
            Way(1, [40, 20, 50], highway: "primary_link"),
            Way(2, [10, 20], highway: "residential")
        };

        var junctions = OsmGeometryProcessor.BuildGlobalJunctionNodeSet(features);
        var maxClass = OsmGeometryProcessor.BuildJunctionNodeMaxContinuingClassMap(features, junctions);

        Assert.Equal(3, maxClass[20L]); // primary_link inherits primary's band
    }

    // ========================================================================================
    //  End-to-end: cross-class chaining guard (node 5429248736 — residential "Im Herrengarten"
    //  + "Wedenhofstraße" chained straight across tertiary "Maiweg", swallowing the crossroads
    //  into two spline interiors so only a MidSplineCrossing hump remained)
    // ========================================================================================

    [Fact]
    public void ConvertLinesToSplines_ResidentialPairAcrossTertiary_DoesNotChainThrough()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // Residential street heading north, ending at crossroads node 200.
        var resIn = Way(10, [100, 101, 200],
            [new(3.002, 42.401), new(3.002, 42.4015), new(3.002, 42.402)],
            highway: "residential");

        // Residential street continuing DEAD STRAIGHT north from node 200 (deflection ~0° — the
        // >90° junction guard alone would allow this merge).
        var resOut = Way(20, [200, 102, 103],
            [new(3.002, 42.402), new(3.002, 42.4025), new(3.002, 42.403)],
            highway: "residential");

        // Tertiary road crossing east-west THROUGH node 200 (interior node — not split there).
        var through = Way(30, [300, 200, 301],
            [new(3.001, 42.402), new(3.002, 42.402), new(3.003, 42.402)],
            highway: "tertiary");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [resIn, resOut, through], bbox, terrainSize: 512, metersPerPixel: 1f);

        // The residential arms must TERMINATE at the crossroads (real junction detection downstream),
        // not chain through into one spline that swallows the node.
        Assert.Equal(3, splines.Count);
        Assert.DoesNotContain(splines, s => s.OsmWayIds.Contains(10L) && s.OsmWayIds.Contains(20L));
    }

    [Fact]
    public void ConvertLinesToSplines_SameClassCrossing_StillChainsThrough()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // Identical geometry to the guard test, but the crossing road is residential too —
        // same class band, so the pair may chain through (legacy MidSplineCrossing behavior).
        var resIn = Way(10, [100, 101, 200],
            [new(3.002, 42.401), new(3.002, 42.4015), new(3.002, 42.402)],
            highway: "residential");
        var resOut = Way(20, [200, 102, 103],
            [new(3.002, 42.402), new(3.002, 42.4025), new(3.002, 42.403)],
            highway: "residential");
        var through = Way(30, [300, 200, 301],
            [new(3.001, 42.402), new(3.002, 42.402), new(3.003, 42.402)],
            highway: "residential");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [resIn, resOut, through], bbox, terrainSize: 512, metersPerPixel: 1f);

        Assert.Equal(2, splines.Count);
        Assert.Contains(splines, s => s.OsmWayIds.Contains(10L) && s.OsmWayIds.Contains(20L));
    }

    [Fact]
    public void ConvertLinesToSplines_HigherClassArmTerminating_ThroughRoadStillChains()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // Regression (render 2026-07-19): an ongoing residential road split at a T-node where a
        // TERMINATING tertiary arm ends. The first guard version counted the terminating tertiary
        // toward the node's max class and split the residential into two endpoint splines — the
        // through road must keep chaining; the tertiary arm terminating on it IS the T-junction.
        var resIn = Way(10, [100, 101, 200],
            [new(3.002, 42.401), new(3.002, 42.4015), new(3.002, 42.402)],
            highway: "residential");
        var resOut = Way(20, [200, 102, 103],
            [new(3.002, 42.402), new(3.002, 42.4025), new(3.002, 42.403)],
            highway: "residential");
        var tertiaryArm = Way(30, [300, 200],
            [new(3.001, 42.402), new(3.002, 42.402)],
            highway: "tertiary");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [resIn, resOut, tertiaryArm], bbox, terrainSize: 512, metersPerPixel: 1f);

        Assert.Equal(2, splines.Count);
        Assert.Contains(splines, s => s.OsmWayIds.Contains(10L) && s.OsmWayIds.Contains(20L));
    }

    [Fact]
    public void ConvertLinesToSplines_BothPairsSplitAtCrossing_OnlyDominantChains()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // The exact node-5429248736 shape: residential pair AND tertiary pair all meet as way-ends
        // at one crossroads. The tertiary (2 ends = continues) chains; the residential pair is
        // blocked and terminates in two arms.
        var resIn = Way(10, [100, 101, 200],
            [new(3.002, 42.401), new(3.002, 42.4015), new(3.002, 42.402)],
            highway: "residential");
        var resOut = Way(20, [200, 102, 103],
            [new(3.002, 42.402), new(3.002, 42.4025), new(3.002, 42.403)],
            highway: "residential");
        var t1 = Way(30, [300, 200],
            [new(3.001, 42.402), new(3.002, 42.402)], highway: "tertiary");
        var t2 = Way(31, [200, 301],
            [new(3.002, 42.402), new(3.003, 42.402)], highway: "tertiary");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [resIn, resOut, t1, t2], bbox, terrainSize: 512, metersPerPixel: 1f);

        Assert.Equal(3, splines.Count);
        Assert.Contains(splines, s => s.OsmWayIds.Contains(30L) && s.OsmWayIds.Contains(31L));
        Assert.DoesNotContain(splines, s => s.OsmWayIds.Contains(10L) && s.OsmWayIds.Contains(20L));
    }

    [Fact]
    public void ConvertLinesToSplines_DominantRoadSplitAtCrossing_StillChainsThrough()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // The tertiary IS split at the junction node; a residential arm also ends there.
        // The tertiary pair's band equals the node's max band → it must still chain through.
        var t1 = Way(30, [300, 200],
            [new(3.001, 42.402), new(3.002, 42.402)], highway: "tertiary");
        var t2 = Way(31, [200, 301],
            [new(3.002, 42.402), new(3.003, 42.402)], highway: "tertiary");
        var resArm = Way(40, [100, 200],
            [new(3.002, 42.401), new(3.002, 42.402)], highway: "residential");

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            [t1, t2, resArm], bbox, terrainSize: 512, metersPerPixel: 1f);

        Assert.Equal(2, splines.Count);
        Assert.Contains(splines, s => s.OsmWayIds.Contains(30L) && s.OsmWayIds.Contains(31L));
    }

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
