using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Doc 10 — contiguous-span consolidation. OSM maps one physical bridge as many ways whose
///     <c>layer</c> tags differ along the deck (the tag only encodes LOCAL crossing order: Brooklyn
///     Bridge = 11 ways alternating layer 3/0/3/…/1). <c>StructureSegmentOps.Consolidate</c> joins only
///     identical type+layer, so the deck stays fragmented into per-way spans — each internal boundary
///     grows a fake abutment (overlap tongue + end wall + excavator strip = the interchange needle-wall
///     spikes). With <c>BridgeRuleSystemOptions.EnableContiguousSpanConsolidation</c> a final
///     station-based pass joins adjacent same-TYPE spans across layer differences, keeping the original
///     per-sub-range layers (<see cref="StructureSegment.LayerRanges"/>) so grade-separation
///     classification still sees the right relative layer at every crossing station.
/// </summary>
public class ContiguousSpanConsolidationTests
{
    // ---- StructureSegmentOps.ConsolidateByStation ------------------------------------------------------------

    private static StructureSegment Span(
        float start, float end, int layer, long wayId, StructureType type = StructureType.Bridge)
        => new()
        {
            StartDistance = start,
            EndDistance = end,
            Layer = layer,
            Type = type,
            OsmWayIds = [wayId],
            OriginalStartPoint = new Vector2(start, 0),
            OriginalEndPoint = new Vector2(end, 0),
        };

    [Fact]
    public void ConsolidateByStation_JoinsContiguousSpans_AcrossLayerDifferences()
    {
        // Brooklyn Bridge in miniature: 3 perfectly contiguous bridge ways, layers 3 / 0 / 1.
        var spans = new List<StructureSegment>
        {
            Span(0f, 330f, layer: 3, wayId: 101),
            Span(330f, 365f, layer: 0, wayId: 102),
            Span(365f, 1714f, layer: 1, wayId: 103),
        };

        var joined = Assert.Single(StructureSegmentOps.ConsolidateByStation(spans));

        Assert.Equal(0f, joined.StartDistance);
        Assert.Equal(1714f, joined.EndDistance);
        Assert.Equal(new HashSet<long> { 101, 102, 103 }, joined.OsmWayIds);
        Assert.Equal(new Vector2(0, 0), joined.OriginalStartPoint);
        Assert.Equal(new Vector2(1714, 0), joined.OriginalEndPoint);
        Assert.Equal(3, joined.Layer); // governing layer = max of the parts

        // The per-way layers survive as station sub-ranges for crossing classification.
        Assert.NotNull(joined.LayerRanges);
        Assert.Equal(3, joined.LayerRanges!.Count);
        Assert.Equal(3, joined.LayerAt(100f));
        Assert.Equal(0, joined.LayerAt(350f));
        Assert.Equal(1, joined.LayerAt(1000f));
    }

    [Fact]
    public void ConsolidateByStation_GapBeyondTolerance_DoesNotJoin()
    {
        // 10 m of real ground between two bridges — must stay two decks with real abutments.
        var spans = new List<StructureSegment>
        {
            Span(0f, 100f, layer: 1, wayId: 201),
            Span(110f, 200f, layer: 1, wayId: 202),
        };

        var result = StructureSegmentOps.ConsolidateByStation(spans);

        Assert.Equal(2, result.Count);
        Assert.Null(result[0].LayerRanges);
        Assert.Null(result[1].LayerRanges);
    }

    [Fact]
    public void ConsolidateByStation_DifferentType_DoesNotJoin()
    {
        var spans = new List<StructureSegment>
        {
            Span(0f, 100f, layer: 1, wayId: 301, type: StructureType.Bridge),
            Span(100f, 200f, layer: 1, wayId: 302, type: StructureType.Tunnel),
        };

        Assert.Equal(2, StructureSegmentOps.ConsolidateByStation(spans).Count);
    }

    [Fact]
    public void ConsolidateByStation_SingleSegment_IsUnchanged()
    {
        var only = Assert.Single(
            StructureSegmentOps.ConsolidateByStation([Span(5f, 50f, layer: 2, wayId: 401)]));

        Assert.Equal(5f, only.StartDistance);
        Assert.Equal(50f, only.EndDistance);
        Assert.Equal(2, only.Layer);
        Assert.Null(only.LayerRanges); // no join happened — LayerAt falls back to Layer
        Assert.Equal(2, only.LayerAt(20f));
    }

    [Fact]
    public void ConsolidateByStation_SmallReprojectionSeam_StillJoins()
    {
        // Reprojected neighbours can disagree by well under the tolerance; a sub-metre seam or a
        // small overlap is not a real ground gap.
        var spans = new List<StructureSegment>
        {
            Span(0f, 100.0f, layer: 2, wayId: 501),
            Span(100.6f, 200f, layer: 0, wayId: 502),  // 0.6 m seam
            Span(199.2f, 300f, layer: 1, wayId: 503),  // 0.8 m overlap
        };

        var joined = Assert.Single(StructureSegmentOps.ConsolidateByStation(spans));
        Assert.Equal(0f, joined.StartDistance);
        Assert.Equal(300f, joined.EndDistance);
    }

    // ---- LayerAt fallback -------------------------------------------------------------------------------------

    [Fact]
    public void LayerAt_OutsideAllRanges_FallsBackToGoverningLayer()
    {
        var seg = Span(0f, 100f, layer: 3, wayId: 601);
        seg.LayerRanges = [new StructureLayerRange(0f, 40f, 3), new StructureLayerRange(40f, 100f, 1)];

        Assert.Equal(3, seg.LayerAt(10f));
        Assert.Equal(1, seg.LayerAt(70f));
        Assert.Equal(3, seg.LayerAt(500f)); // off the span → governing layer
    }

    // ---- End-to-end through OsmGeometryProcessor ---------------------------------------------------------------

    private static OsmFeature Way(long id, List<GeoCoordinate> coords, List<long> nodeIds,
        bool isBridge = false, int? layer = null)
    {
        var tags = new Dictionary<string, string> { ["highway"] = "primary" };
        if (isBridge) tags["bridge"] = "yes";
        if (layer.HasValue) tags["layer"] = layer.Value.ToString();
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
    ///     One straight west→east corridor: approach road, then THREE contiguous bridge ways whose layer
    ///     tags differ (3 / none / 1 — the Brooklyn Bridge pattern), then an exit road.
    /// </summary>
    private static (GeoBoundingBox bbox, List<OsmFeature> features) FragmentedViaductScenario()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(2.999, 42.401),
            new GeoCoordinate(3.008, 42.405));
        const double lat = 42.4025;

        var road1 = Way(3001, [new(3.000, lat), new(3.001, lat)], [300, 301]);
        var bridgeA = Way(3002, [new(3.001, lat), new(3.002, lat)], [301, 302], isBridge: true, layer: 3);
        var bridgeB = Way(3003, [new(3.002, lat), new(3.0025, lat)], [302, 303], isBridge: true); // no layer tag
        var bridgeC = Way(3004, [new(3.0025, lat), new(3.004, lat)], [303, 304], isBridge: true, layer: 1);
        var road2 = Way(3005, [new(3.004, lat), new(3.005, lat)], [304, 305]);

        return (bbox, [road1, bridgeA, bridgeB, bridgeC, road2]);
    }

    [Fact]
    public void FlagOff_ContiguousBridgeWaysWithDifferentLayers_StayFragmented()
    {
        var (bbox, features) = FragmentedViaductScenario();

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            features, bbox, terrainSize: 1024, metersPerPixel: 1f,
            excludeBridges: true, mergeStructuresIntoCorridor: true,
            consolidateContiguousSpans: false);

        var corridor = Assert.Single(splines, s => s.StructureSegments is { Count: > 0 });
        Assert.Equal(3, corridor.StructureSegments!.Count); // today's behaviour — one span per way
    }

    [Fact]
    public void FlagOn_ContiguousBridgeWaysWithDifferentLayers_BecomeOneSpan()
    {
        var (bbox, features) = FragmentedViaductScenario();

        var splines = new OsmGeometryProcessor().ConvertLinesToSplines(
            features, bbox, terrainSize: 1024, metersPerPixel: 1f,
            excludeBridges: true, mergeStructuresIntoCorridor: true,
            consolidateContiguousSpans: true);

        var corridor = Assert.Single(splines, s => s.StructureSegments is { Count: > 0 });
        var span = Assert.Single(corridor.StructureSegments!);

        Assert.Equal(new HashSet<long> { 3002, 3003, 3004 }, span.OsmWayIds);
        Assert.Equal(3, span.Layer);
        Assert.NotNull(span.LayerRanges);
        Assert.Equal(3, span.LayerRanges!.Count);

        // Sub-range layers sit in way order along the corridor (3 → 0 → 1).
        var ordered = span.LayerRanges!.OrderBy(r => r.StartDistance).Select(r => r.Layer).ToArray();
        Assert.Equal([3, 0, 1], ordered);

        // The joined span covers the whole viaduct: its ends project onto the original outer abutments.
        Assert.NotNull(span.OriginalStartPoint);
        Assert.NotNull(span.OriginalEndPoint);
        Assert.True(span.EndDistance - span.StartDistance > 200f,
            $"span [{span.StartDistance:F1},{span.EndDistance:F1}] too short for the 3-way viaduct");
    }
}
