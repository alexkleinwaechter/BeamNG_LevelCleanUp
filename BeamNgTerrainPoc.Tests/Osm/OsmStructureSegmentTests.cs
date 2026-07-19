using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     End-to-end tests for the bridge sub-range tracking introduced by the "merged-corridor bridge"
///     refactor (plan doc 11, Phase 1). A road→bridge→road OSM scenario is run through
///     <see cref="OsmGeometryProcessor.ConvertLinesToSplines"/>; we assert the resulting (merged) spline
///     carries a <c>StructureSegment</c> over the correct interior arc-range, anchored by StartDistance/
///     EndDistance. Phase 1 is purely additive — nothing consumes these segments yet — so these tests
///     guard the seed → merge → propagate plumbing only.
/// </summary>
public class OsmStructureSegmentTests
{
    private static OsmFeature Road(long id, List<GeoCoordinate> coords, List<long> nodeIds,
        bool isBridge = false, int layer = 0)
    {
        var tags = new Dictionary<string, string> { ["highway"] = "primary", ["lanes"] = "2" };
        if (isBridge) { tags["bridge"] = "yes"; tags["layer"] = layer.ToString(); }
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

    // road1 (nodes 100-101-102) → bridge (102-103-104) → road2 (104-105-106), collinear, shared nodes.
    private static (GeoBoundingBox bbox, List<OsmFeature> features) Scenario()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        var road1 = Road(1001,
            [new(3.001, 42.402), new(3.0015, 42.402), new(3.002, 42.402)], [100, 101, 102]);
        var bridge = Road(1002,
            [new(3.002, 42.402), new(3.0025, 42.402), new(3.003, 42.402)], [102, 103, 104],
            isBridge: true, layer: 1);
        var road2 = Road(1003,
            [new(3.003, 42.402), new(3.0035, 42.402), new(3.004, 42.402)], [104, 105, 106]);

        return (bbox, [road1, bridge, road2]);
    }

    [Fact]
    public void MergedCorridor_CarriesBridgeStructureSegment_InTheInterior()
    {
        var (bbox, features) = Scenario();
        var processor = new OsmGeometryProcessor();

        // excludeBridges=false + merging ENABLED => the bridge merges INTO the through-road corridor.
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: false, excludeTunnels: false,
            disableSplineMerging: false);

        // Find the spline that carries the bridge sub-range.
        var withSpan = splines.FirstOrDefault(s =>
            s.StructureSegments != null && s.StructureSegments.Any(seg => seg.IsBridge));
        Assert.NotNull(withSpan);

        // It must be the fully-merged corridor (road1 + bridge + road2 all in one spline).
        Assert.Contains(1001L, withSpan!.OsmWayIds);
        Assert.Contains(1002L, withSpan.OsmWayIds);
        Assert.Contains(1003L, withSpan.OsmWayIds);

        var span = Assert.Single(withSpan.StructureSegments!, s => s.IsBridge);

        // The bridge way id is recorded on the span (stable per-span identity key, plan §10.2).
        Assert.Contains(1002L, span.OsmWayIds);

        // The span is strictly INTERIOR — road exists on both sides (this is the whole point: continuity).
        Assert.True(span.StartDistance > 0f, $"expected interior start, got {span.StartDistance}");
        Assert.True(span.EndDistance < withSpan.TotalLength,
            $"expected interior end, got {span.EndDistance} of {withSpan.TotalLength}");
        Assert.True(span.EndDistance > span.StartDistance);

        // Roughly the middle third (three equal-length ways). Loose bounds tolerate Chaikin/dedup.
        Assert.InRange(span.StartDistance / withSpan.TotalLength, 0.15f, 0.5f);
        Assert.InRange(span.EndDistance / withSpan.TotalLength, 0.5f, 0.85f);
    }

    [Fact]
    public void MergeFlag_ForcesBridgeIntoCorridor_EvenWhenExcludeBridgesTrue()
    {
        var (bbox, features) = Scenario();
        var processor = new OsmGeometryProcessor();

        // The realistic bridge-generation config: excludeBridges=TRUE (so today the bridge would be held out
        // as its own spline), but mergeStructuresIntoCorridor=TRUE forces it INTO the corridor anyway.
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: true, excludeTunnels: false,
            disableSplineMerging: false,
            mergeStructuresIntoCorridor: true);

        // The bridge must NOT be a standalone spline — it is now interior to one merged corridor.
        var withSpan = Assert.Single(splines, s =>
            s.StructureSegments != null && s.StructureSegments.Any(seg => seg.IsBridge));
        Assert.Contains(1001L, withSpan.OsmWayIds);
        Assert.Contains(1002L, withSpan.OsmWayIds);
        Assert.Contains(1003L, withSpan.OsmWayIds);

        var span = Assert.Single(withSpan.StructureSegments!, s => s.IsBridge);
        Assert.Contains(1002L, span.OsmWayIds);
        Assert.True(span.StartDistance > 0f, $"expected interior start, got {span.StartDistance}");
        Assert.True(span.EndDistance < withSpan.TotalLength,
            $"expected interior end, got {span.EndDistance} of {withSpan.TotalLength}");
    }

    [Fact]
    public void MergeFlagOff_ExcludeBridgesTrue_BridgeStaysSeparate()
    {
        var (bbox, features) = Scenario();
        var processor = new OsmGeometryProcessor();

        // Flag OFF (default) + excludeBridges=true => legacy behaviour: the bridge is its own spline.
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: true, excludeTunnels: false,
            disableSplineMerging: false,
            mergeStructuresIntoCorridor: false);

        var bridgeSpline = Assert.Single(splines, s => s.IsBridge);
        // It carries ONLY the bridge way — the approaches did not merge in.
        Assert.Contains(1002L, bridgeSpline.OsmWayIds);
        Assert.DoesNotContain(1001L, bridgeSpline.OsmWayIds);
        Assert.DoesNotContain(1003L, bridgeSpline.OsmWayIds);
    }

    [Fact]
    public void SeparatedBridgeSpline_CarriesFullSpanStructureSegment()
    {
        var (bbox, features) = Scenario();
        var processor = new OsmGeometryProcessor();

        // excludeBridges=true => bridge stays its OWN spline (today's behaviour). It should still carry a
        // StructureSegment, covering ~the whole spline (so a derived IsBridge would agree in a later phase).
        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: true, excludeTunnels: false,
            disableSplineMerging: false);

        var bridgeSpline = splines.FirstOrDefault(s => s.IsBridge);
        Assert.NotNull(bridgeSpline);
        Assert.NotNull(bridgeSpline!.StructureSegments);

        var span = Assert.Single(bridgeSpline.StructureSegments!);
        Assert.True(span.IsBridge);
        Assert.Contains(1002L, span.OsmWayIds);

        // Full-span: starts at ~0, ends at ~TotalLength.
        Assert.Equal(0f, span.StartDistance, 1f);
        Assert.InRange(span.EndDistance, bridgeSpline.TotalLength - 1f, bridgeSpline.TotalLength + 0.01f);
    }

    [Fact]
    public void PlainRoad_HasNoStructureSegments()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));
        var road = Road(1001,
            [new(3.001, 42.402), new(3.0015, 42.402), new(3.002, 42.402)], [100, 101, 102]);

        var processor = new OsmGeometryProcessor();
        var splines = processor.ConvertLinesToSplines(
            [road], bbox, terrainSize: 512, metersPerPixel: 1f,
            excludeBridges: false, excludeTunnels: false,
            disableSplineMerging: false);

        var spline = Assert.Single(splines);
        Assert.True(spline.StructureSegments == null || spline.StructureSegments.Count == 0);
    }
}
