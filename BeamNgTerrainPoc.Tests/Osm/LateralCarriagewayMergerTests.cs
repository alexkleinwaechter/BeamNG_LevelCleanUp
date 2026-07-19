using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Tests for the lateral dual-carriageway merge (ai_docs/2026-07-10_lateral_spline_merge): two
///     antiparallel oneway chains of the same road (OSM dual carriageway, e.g. the A61 direction
///     lanes — ways 132678377/1448505388 in the Winningen dataset) combine into ONE bidirectional
///     path so both directions get a single elevation solve. Covers pair detection guards, centerline
///     averaging, lane/tag synthesis, structure-span union and residual tails, plus the
///     ConvertLinesToSplines integration.
/// </summary>
public class LateralCarriagewayMergerTests
{
    private static readonly IReadOnlySet<string> MotorwayOnly = new HashSet<string> { "motorway" };

    /// <summary>A straight polyline from <paramref name="from"/> to <paramref name="to"/> with ~20 m spacing.</summary>
    private static List<Vector2> Line(Vector2 from, Vector2 to, float spacing = 20f)
    {
        var length = Vector2.Distance(from, to);
        var steps = Math.Max(2, (int)MathF.Ceiling(length / spacing));
        var points = new List<Vector2>(steps + 1);
        for (var i = 0; i <= steps; i++)
            points.Add(Vector2.Lerp(from, to, i / (float)steps));
        return points;
    }

    private static PathWithMetadata Carriageway(
        long wayId,
        List<Vector2> points,
        string highway = "motorway",
        bool oneway = true,
        int lanes = 2,
        string? refTag = "A 61")
    {
        var tags = new Dictionary<string, string> { ["highway"] = highway, ["lanes"] = lanes.ToString() };
        if (oneway) tags["oneway"] = "yes";
        if (refTag != null) tags["ref"] = refTag;

        var path = new PathWithMetadata(points, null, null, wayId, tags,
            isBridge: false, isTunnel: false, StructureType.None, layer: 0, bridgeStructureType: null)
        {
            LaneSegments =
            [
                new LaneSegment { StartPointIndex = 0, LaneInfo = OsmLaneInfo.TryParse(tags) ?? new OsmLaneInfo() }
            ],
        };
        return path;
    }

    // ---------------------------------------------------------------------------------------------
    // Pair detection guards
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AntiparallelOnewayPair_MergesIntoOneBidirectionalPath()
    {
        var forward = Carriageway(1, Line(new(0, 0), new(1000, 0)));
        var backward = Carriageway(2, Line(new(1000, 14), new(0, 14)));

        var result = LateralCarriagewayMerger.Merge([forward, backward], MotorwayOnly);

        var merged = Assert.Single(result);
        Assert.Contains(1L, merged.AllWayIds);
        Assert.Contains(2L, merged.AllWayIds);

        // Marks the corridor for width-aware T-junction admission (ramps that OSM-connected to one
        // carriageway now sit ~half the separation away from the merged centerline).
        Assert.True(merged.IsLaterallyMerged);

        // 2+2 lanes, bidirectional; the stale oneway tag must be gone.
        var lane = Assert.Single(merged.LaneSegments).LaneInfo;
        Assert.False(lane.IsOneWay);
        Assert.Equal(4, lane.TotalLanes);
        Assert.Equal(2, lane.LanesForward);
        Assert.Equal(2, lane.LanesBackward);
        Assert.False(merged.Tags.ContainsKey("oneway"));
        Assert.Equal("4", merged.Tags["lanes"]);

        // Centerline runs midway between the carriageways (full mutual overlap ⇒ no taper).
        foreach (var point in merged.Points)
            Assert.InRange(point.Y, 6.5f, 7.5f);
    }

    [Fact]
    public void SameDirectionParallel_DoesNotMerge()
    {
        var a = Carriageway(1, Line(new(0, 0), new(1000, 0)));
        var b = Carriageway(2, Line(new(0, 14), new(1000, 14)));

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void BidirectionalPartner_DoesNotMerge()
    {
        var a = Carriageway(1, Line(new(0, 0), new(1000, 0)));
        var b = Carriageway(2, Line(new(1000, 14), new(0, 14)), oneway: false);

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DifferentRef_DoesNotMerge_FrontageRoadGuard()
    {
        var a = Carriageway(1, Line(new(0, 0), new(1000, 0)));
        var b = Carriageway(2, Line(new(1000, 14), new(0, 14)), refTag: "L 52");

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TooFarApart_DoesNotMerge()
    {
        var a = Carriageway(1, Line(new(0, 0), new(1000, 0)));
        var b = Carriageway(2, Line(new(1000, 50), new(0, 50)));

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void IneligibleRoadType_DoesNotMerge()
    {
        var a = Carriageway(1, Line(new(0, 0), new(1000, 0)), highway: "primary");
        var b = Carriageway(2, Line(new(1000, 14), new(0, 14)), highway: "primary");

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        Assert.Equal(2, result.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Merge construction details
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PartnerWithoutLanesTag_ContributesOsmDefaultOfOneLane()
    {
        // Way 2 has oneway=yes but no lanes tag — OsmLaneInfo priority 7 assigns the OSM default
        // of 1 lane. Way 1 (strictly longer ⇒ merge base, its direction wins) has 3.
        var a = Carriageway(1, Line(new(-40, 0), new(1000, 0)), lanes: 3);
        var bTags = new Dictionary<string, string>
        {
            ["highway"] = "motorway", ["oneway"] = "yes", ["ref"] = "A 61",
        };
        var b = new PathWithMetadata(Line(new(1000, 14), new(0, 14)), null, null, 2, bTags,
            false, false, StructureType.None, 0, null)
        {
            LaneSegments = [new LaneSegment { StartPointIndex = 0, LaneInfo = OsmLaneInfo.TryParse(bTags)! }],
        };

        var result = LateralCarriagewayMerger.Merge([a, b], MotorwayOnly);

        var merged = Assert.Single(result);

        // Way 1 starts 40 m before the overlap: there the longer carriageway continues alone
        // and keeps its own oneway 3-lane info; the combined 3+1 config covers the run.
        Assert.Equal(2, merged.LaneSegments.Count);
        var prefix = merged.LaneSegments[0].LaneInfo;
        Assert.True(prefix.IsOneWay);
        Assert.Equal(3, prefix.TotalLanes);

        var core = merged.LaneSegments[1].LaneInfo;
        Assert.False(core.IsOneWay);
        Assert.Equal(3, core.LanesForward);
        Assert.Equal(1, core.LanesBackward);
        Assert.Equal(4, core.TotalLanes);

        // Tags describe the dominant (in-run) configuration.
        Assert.Equal("4", merged.Tags["lanes"]);
        Assert.Equal("3", merged.Tags["lanes:forward"]);
        Assert.Equal("1", merged.Tags["lanes:backward"]);
    }

    [Fact]
    public void LaneCountChangeAlongTheChain_SurvivesPerSegment_NoMaxFlattening()
    {
        // The forward chain (strictly longer ⇒ merge base) is 2 lanes for 600 m, then widens to 3
        // (exit section) and overhangs the partner by 40 m. Taking the MAX over the whole chain
        // would declare the corridor 3+2 end to end — the "always three lanes per way" bug. The
        // merged profile must stay 2+2 where the road IS 2+2.
        var forwardPoints = Line(new(0, 0), new(1040, 0));
        var forward = Carriageway(1, forwardPoints);
        var idx600 = forwardPoints.FindIndex(p => p.X >= 600f);
        var lanes3 = OsmLaneInfo.TryParse(new Dictionary<string, string>
        {
            ["oneway"] = "yes", ["lanes"] = "3",
        })!;
        forward.LaneSegments.Add(new LaneSegment { StartPointIndex = idx600, LaneInfo = lanes3 });

        var backward = Carriageway(2, Line(new(1000, 14), new(0, 14)));

        var result = LateralCarriagewayMerger.Merge([forward, backward], MotorwayOnly);

        var merged = Assert.Single(result);
        var profile = string.Join(" | ", merged.LaneSegments.Select(s =>
            $"@{s.StartPointIndex}(x={merged.Points[s.StartPointIndex].X:F1}): " +
            $"{s.LaneInfo.LanesForward}+{s.LaneInfo.LanesBackward} ow={s.LaneInfo.IsOneWay}"));
        Assert.True(3 == merged.LaneSegments.Count, $"profile: {profile}");

        var first = merged.LaneSegments[0];
        Assert.Equal(0, first.StartPointIndex);
        Assert.Equal(2, first.LaneInfo.LanesForward);
        Assert.Equal(2, first.LaneInfo.LanesBackward);

        // The longer path's boundary index stays valid verbatim on the merged path.
        var second = merged.LaneSegments[1];
        Assert.Equal(idx600, second.StartPointIndex);
        Assert.Equal(3, second.LaneInfo.LanesForward);
        Assert.Equal(2, second.LaneInfo.LanesBackward);

        // Past the partner's end the longer carriageway continues alone: oneway again.
        var tail = merged.LaneSegments[2].LaneInfo;
        Assert.True(tail.IsOneWay);
        Assert.Equal(3, tail.TotalLanes);

        // Tags carry the dominant configuration (600 m of 2+2 beats ~420 m of 3+2).
        Assert.Equal("4", merged.Tags["lanes"]);
        Assert.Equal("2", merged.Tags["lanes:forward"]);
        Assert.Equal("2", merged.Tags["lanes:backward"]);
    }

    [Fact]
    public void PartnerLaneChange_SplitsTheMergedProfileAtTheProjectedBoundary()
    {
        // The OPPOSITE (strictly shorter) carriageway widens from 2 to 3 lanes at x=500 — its own
        // second half, x <= 500, since it runs 1000 -> 0. The merged profile must flip 2+3 -> 2+2
        // near x=500, at the PROJECTED boundary.
        var forward = Carriageway(1, Line(new(0, 0), new(1040, 0)));
        var backwardPoints = Line(new(1000, 14), new(0, 14));
        var backward = Carriageway(2, backwardPoints);
        var idx500 = backwardPoints.FindIndex(p => p.X <= 500f);
        var lanes3 = OsmLaneInfo.TryParse(new Dictionary<string, string>
        {
            ["oneway"] = "yes", ["lanes"] = "3",
        })!;
        backward.LaneSegments.Add(new LaneSegment { StartPointIndex = idx500, LaneInfo = lanes3 });

        var result = LateralCarriagewayMerger.Merge([forward, backward], MotorwayOnly);

        var merged = Assert.Single(result);
        var profile = string.Join(" | ", merged.LaneSegments.Select(s =>
            $"@{s.StartPointIndex}(x={merged.Points[s.StartPointIndex].X:F1}): " +
            $"{s.LaneInfo.LanesForward}+{s.LaneInfo.LanesBackward} ow={s.LaneInfo.IsOneWay}"));
        Assert.True(3 == merged.LaneSegments.Count, $"profile: {profile}");

        Assert.Equal(2, merged.LaneSegments[0].LaneInfo.LanesForward);
        Assert.Equal(3, merged.LaneSegments[0].LaneInfo.LanesBackward);
        Assert.Equal(2, merged.LaneSegments[1].LaneInfo.LanesForward);
        Assert.Equal(2, merged.LaneSegments[1].LaneInfo.LanesBackward);

        var boundaryX = merged.Points[merged.LaneSegments[1].StartPointIndex].X;
        Assert.InRange(boundaryX, 460f, 540f);

        // Past the partner's end (x > ~1020) the longer carriageway continues alone: oneway.
        Assert.True(merged.LaneSegments[2].LaneInfo.IsOneWay);
    }

    [Fact]
    public void TwinBridgeSpans_UnionIntoOneSpanOnTheMergedPath()
    {
        // Twin viaduct: each carriageway carries its own bridge span over the same valley
        // (stations mirrored — the A61 spline 365/366 pattern).
        var forwardPoints = Line(new(0, 0), new(1000, 0));
        var backwardPoints = Line(new(1000, 14), new(0, 14));
        var forward = Carriageway(1, forwardPoints);
        var backward = Carriageway(2, backwardPoints);

        forward.StructureSegments =
        [
            SpanBetween(forwardPoints, 400f, 600f, wayId: 11),
        ];
        backward.StructureSegments =
        [
            // Same physical range, but this path runs 1000→0 so its span sits at stations 400-600
            // FROM ITS OWN START, i.e. x = 600..400.
            SpanBetween(backwardPoints, 400f, 600f, wayId: 22),
        ];

        var result = LateralCarriagewayMerger.Merge([forward, backward], MotorwayOnly);

        var merged = Assert.Single(result);
        var span = Assert.Single(merged.StructureSegments);
        Assert.Contains(11L, span.OsmWayIds);
        Assert.Contains(22L, span.OsmWayIds);
        Assert.True(span.IsBridge);

        // The unioned span covers the physical bridge range (x 400..600). Which x sits at the
        // span START depends on which carriageway won the merge base (equal lengths here), so
        // assert the unordered pair.
        var spanEndXs = new[]
        {
            merged.Points[span.StartPointIndex].X,
            merged.Points[span.EndPointIndex].X,
        };
        Assert.InRange(spanEndXs.Min(), 350f, 450f);
        Assert.InRange(spanEndXs.Max(), 550f, 650f);
        // Original endpoint coords survive for the downstream V2 0.3a station reprojection.
        Assert.NotNull(span.OriginalStartPoint);
        Assert.NotNull(span.OriginalEndPoint);
    }

    private static StructureSegment SpanBetween(List<Vector2> points, float fromStation, float toStation, long wayId)
    {
        var cum = 0f;
        var startIdx = -1;
        var endIdx = points.Count - 1;
        for (var i = 1; i < points.Count; i++)
        {
            cum += Vector2.Distance(points[i - 1], points[i]);
            if (startIdx < 0 && cum >= fromStation) startIdx = i;
            if (cum >= toStation) { endIdx = i; break; }
        }

        return new StructureSegment
        {
            StartPointIndex = startIdx,
            EndPointIndex = endIdx,
            Type = StructureType.Bridge,
            Layer = 1,
            OsmWayIds = [wayId],
            OriginalStartPoint = points[startIdx],
            OriginalEndPoint = points[endIdx],
        };
    }

    [Fact]
    public void ShorterPathTurningAway_KeepsItsDivergingLegAsResidualTail()
    {
        // The opposite carriageway runs antiparallel for 1400 m, then turns 90° away for 300 m
        // (e.g. an exit alignment). The turn must survive as its own path, not be averaged in.
        var longer = Carriageway(1, Line(new(0, 0), new(2000, 0)));
        var backward = Line(new(1400, 14), new(0, 14));
        backward.AddRange(Line(new(0, 34), new(0, 314)));
        var shorter = Carriageway(2, backward);

        var result = LateralCarriagewayMerger.Merge([longer, shorter], MotorwayOnly);

        Assert.Equal(2, result.Count);
        var merged = result.First(p => p.AllWayIds.Contains(1));
        var tail = result.First(p => !p.AllWayIds.Contains(1));

        Assert.Contains(2L, merged.AllWayIds);
        Assert.Equal(2L, tail.OsmWayId);
        // The tail is the northward leg (x ≈ 0, y rising).
        Assert.All(tail.Points, p => Assert.InRange(p.X, -1f, 1f));
        Assert.True(tail.Points[^1].Y > 250f);
    }

    // ---------------------------------------------------------------------------------------------
    // ConvertLinesToSplines integration
    // ---------------------------------------------------------------------------------------------

    private static OsmFeature MotorwayWay(long id, List<GeoCoordinate> coords, List<long> nodeIds)
    {
        return new OsmFeature
        {
            Id = id,
            FeatureType = OsmFeatureType.Way,
            GeometryType = OsmGeometryType.LineString,
            Coordinates = coords,
            NodeIds = nodeIds,
            Tags = new Dictionary<string, string>
            {
                ["highway"] = "motorway",
                ["oneway"] = "yes",
                ["lanes"] = "2",
                ["ref"] = "A 61",
            },
        };
    }

    private static (GeoBoundingBox bbox, List<OsmFeature> features) DualCarriagewayScenario()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(3.0, 42.4),
            new GeoCoordinate(3.005, 42.405));

        // ~14 m lateral separation ≈ 0.000126° latitude; antiparallel node order.
        var forward = MotorwayWay(132678377,
            [new(3.001, 42.402), new(3.0025, 42.402), new(3.004, 42.402)], [1, 2, 3]);
        var backward = MotorwayWay(1448505388,
            [new(3.004, 42.402126), new(3.0025, 42.402126), new(3.001, 42.402126)], [4, 5, 6]);

        return (bbox, [forward, backward]);
    }

    [Fact]
    public void ConvertLinesToSplines_WithLateralMerge_ProducesOneBidirectionalSpline()
    {
        var (bbox, features) = DualCarriagewayScenario();
        var processor = new OsmGeometryProcessor();

        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f,
            lateralMergeRoadTypes: new HashSet<string> { "motorway" });

        var spline = Assert.Single(splines);
        Assert.Contains(132678377L, spline.OsmWayIds);
        Assert.Contains(1448505388L, spline.OsmWayIds);
        Assert.False(spline.OsmTags!.ContainsKey("oneway"));

        var lane = Assert.Single(spline.LaneSegments!).LaneInfo;
        Assert.Equal(4, lane.TotalLanes);
        Assert.False(lane.IsOneWay);
    }

    [Fact]
    public void ConvertLinesToSplines_WithoutLateralMerge_KeepsTwoSplines_ByteIdenticalDefault()
    {
        var (bbox, features) = DualCarriagewayScenario();
        var processor = new OsmGeometryProcessor();

        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 512, metersPerPixel: 1f);

        Assert.Equal(2, splines.Count);
    }
}
