using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Phase 3 of the "merged-corridor bridge" refactor (plan doc 11): the per-section exclusion marking.
///     <see cref="UnifiedRoadSmoother.MarkStructureExclusions" /> must, when
///     <see cref="BeamNgTerrainPoc.Terrain.Models.RoadSmoothingParameters.MergeStructuresIntoCorridor" /> is on,
///     exclude ONLY the cross-sections inside a bridge span's arc-range (tagging them with a stable span id) and
///     leave the surrounding road sections untouched — so the road still stamps terrain while the span doesn't.
///     With the flag off it must keep excluding the whole separated bridge spline (byte-identical legacy path).
/// </summary>
public class StructureExclusionMarkingTests
{
    private static Dictionary<int, List<UnifiedCrossSection>> GroupBySpline(UnifiedRoadNetwork network) =>
        network.CrossSections.GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

    [Fact]
    public void MergedCorridor_ExcludesOnlySpanSections_AndTagsSpanId()
    {
        var network = new UnifiedRoadNetwork();

        // Interior bridge span over [10, 20] m of a 30 m corridor: road – bridge – road.
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 10f,
            EndDistance = 20f,
            OsmWayIds = { 4242L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));

        foreach (var c in cs)
        {
            var inSpan = c.DistanceAlongSpline >= 10f && c.DistanceAlongSpline <= 20f;
            Assert.Equal(inSpan, c.IsExcluded);
            Assert.Equal(inSpan ? seg.SpanId : -1, c.StructureSpanId);
        }

        // It really is an interior span: there are both excluded (bridge) and stamped (road) sections.
        Assert.Contains(cs, c => c.IsExcluded);
        Assert.Contains(cs, c => !c.IsExcluded);
        Assert.True(seg.SpanId >= 0);
    }

    [Fact]
    public void LegacyWholeSpline_FlagOff_ExcludesEntireBridgeSpline()
    {
        var network = new UnifiedRoadNetwork();
        // MergeStructuresIntoCorridor stays false (default) → legacy whole-spline path. Even though Phase 1
        // seeds a full-span StructureSegment on a separated bridge, legacy mode ignores it and excludes all.
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: true, excludeBridges: true, excludeTunnels: true,
            structureSegments:
            [
                new StructureSegment { Type = StructureType.Bridge, StartDistance = 0f, EndDistance = 30f, OsmWayIds = { 7L } }
            ]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));

        Assert.All(cs, c => Assert.True(c.IsExcluded));
        // Legacy path does not assign per-span ids.
        Assert.All(cs, c => Assert.Equal(-1, c.StructureSpanId));
    }

    // ── Phase A2 (plan doc 14 §4a): TagStructureSpans — the hoisted "tagging half" that runs before
    // junction detection. It must set StructureSpanId on span sections WITHOUT excluding them, agree with
    // MarkStructureExclusions' tag, and be a no-op with the flag off / on a non-structure corridor.

    [Fact]
    public void TagStructureSpans_TagsSpanSections_WithoutExcluding()
    {
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 10f,
            EndDistance = 20f,
            OsmWayIds = { 4242L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.TagStructureSpans(network.Splines, GroupBySpline(network));

        foreach (var c in cs)
        {
            var inSpan = c.DistanceAlongSpline >= 10f && c.DistanceAlongSpline <= 20f;
            Assert.Equal(inSpan ? seg.SpanId : -1, c.StructureSpanId);
            Assert.False(c.IsExcluded); // tagging must NOT exclude — that stays in Phase 2.0
        }

        Assert.Contains(cs, c => c.StructureSpanId == seg.SpanId);
    }

    [Fact]
    public void TagStructureSpans_AgreesWithMarkStructureExclusions()
    {
        // The early tag and the Phase-2.0 exclusion tag must be identical (idempotent on a second pass).
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 8f,
            EndDistance = 22f,
            OsmWayIds = { 7L, 9L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: false, mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.TagStructureSpans(network.Splines, GroupBySpline(network));
        var earlyTags = cs.Select(c => c.StructureSpanId).ToList();

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));
        var afterExclusionTags = cs.Select(c => c.StructureSpanId).ToList();

        Assert.Equal(earlyTags, afterExclusionTags);
    }

    [Fact]
    public void TagStructureSpans_FlagOff_IsNoOp()
    {
        var network = new UnifiedRoadNetwork();
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: true, // legacy separated bridge; flag stays off
            structureSegments:
            [
                new StructureSegment { Type = StructureType.Bridge, StartDistance = 0f, EndDistance = 30f, OsmWayIds = { 7L } }
            ]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.TagStructureSpans(network.Splines, GroupBySpline(network));

        Assert.All(cs, c => Assert.Equal(-1, c.StructureSpanId));
    }

    // ── Doc 13: bridge-to-bridge abutment suppression ──────────────────────────────────────────

    private static BridgeRuleSystemOptions SuppressionRules() => new()
    {
        EnableSparseDeckConstraints = true, // the overlap shrink is sparse-mode only
        EnableBridgeToBridgeAbutmentSuppression = true,
    };

    /// <summary>Trunk (0,0)→(60,0) span [10,50]; ramp (30,30)→(30,1) whose span [5,29] ends ON the
    /// trunk deck (distance 1 m &lt; halfWidth 4 + 1). The ramp's landing end must stay fully excluded
    /// (no 3 m overlap zone); its ground start and both trunk ends keep today's shrink.</summary>
    private static (ParameterizedRoadSpline trunk, StructureSegment trunkSeg,
        ParameterizedRoadSpline ramp, StructureSegment rampSeg,
        Dictionary<int, List<UnifiedCrossSection>> bySpline)
        BuildRampLandingOnTrunk(bool suppressionOn = true)
    {
        var trunkSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 11L }
        };
        var trunk = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [trunkSeg]);

        var rampSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 5f, EndDistance = 29f, OsmWayIds = { 22L }
        };
        var ramp = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(30, 30), new Vector2(30, 1),
            mergeStructuresIntoCorridor: true, structureSegments: [rampSeg]);

        trunk.Parameters.BridgeRules = SuppressionRules();
        ramp.Parameters.BridgeRules = SuppressionRules();
        if (!suppressionOn)
        {
            trunk.Parameters.BridgeRules.EnableBridgeToBridgeAbutmentSuppression = false;
            ramp.Parameters.BridgeRules.EnableBridgeToBridgeAbutmentSuppression = false;
        }

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, trunk);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, ramp);
        return (trunk, trunkSeg, ramp, rampSeg, GroupBySpline(network));
    }

    private static UnifiedCrossSection At(Dictionary<int, List<UnifiedCrossSection>> bySpline,
        int splineId, float station) =>
        bySpline[splineId].OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    [Fact]
    public void RampLandingOnTrunkDeck_EndSuppressed_StaysFullyExcluded()
    {
        var (trunk, trunkSeg, ramp, rampSeg, bySpline) = BuildRampLandingOnTrunk();

        UnifiedRoadSmoother.MarkStructureExclusions([trunk, ramp], bySpline);

        Assert.True(rampSeg.EndContinuesOntoDeck);    // lands on the trunk deck
        Assert.False(rampSeg.StartContinuesOntoDeck); // ground abutment
        Assert.False(trunkSeg.StartContinuesOntoDeck);
        Assert.False(trunkSeg.EndContinuesOntoDeck);

        // Suppressed ramp end: the last 3 m stay EXCLUDED (no overlap zone → no embankment pillar).
        Assert.True(At(bySpline, 2, 28f).IsExcluded);
        // Ramp ground start keeps today's shrink: first 3 m of the span stay stampable road.
        Assert.False(At(bySpline, 2, 6f).IsExcluded);
        Assert.True(At(bySpline, 2, 10f).IsExcluded);
        // Trunk ends keep today's shrink.
        Assert.False(At(bySpline, 1, 11f).IsExcluded);
        Assert.False(At(bySpline, 1, 49f).IsExcluded);
        Assert.True(At(bySpline, 1, 15f).IsExcluded);
    }

    [Fact]
    public void ParallelTwinDecks_BesideNotOn_NoSuppression()
    {
        // Two parallel decks 10 m apart, width 8 (halfWidth 4 + 1 margin = 5 < 10): true shore
        // abutments must keep their overlap shrink — "beside a deck" is not "on a deck".
        var segA = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 31L }
        };
        var a = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [segA]);
        var segB = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 32L }
        };
        var b = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(0, 10), new Vector2(60, 10),
            mergeStructuresIntoCorridor: true, structureSegments: [segB]);
        a.Parameters.BridgeRules = SuppressionRules();
        b.Parameters.BridgeRules = SuppressionRules();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, a);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, b);
        var bySpline = GroupBySpline(network);

        UnifiedRoadSmoother.MarkStructureExclusions([a, b], bySpline);

        Assert.False(segA.StartContinuesOntoDeck);
        Assert.False(segA.EndContinuesOntoDeck);
        Assert.False(segB.StartContinuesOntoDeck);
        Assert.False(segB.EndContinuesOntoDeck);
        Assert.False(At(bySpline, 1, 11f).IsExcluded); // shrink still applies
        Assert.False(At(bySpline, 2, 49f).IsExcluded);
    }

    [Fact]
    public void SameSplineNeighbourSegments_FacingEndsSuppressed()
    {
        // Two bridge segments on ONE spline with a 4 m gap (≤ 2 × AbutmentOverlapMeters = 6):
        // the facing ends are a continuation (un-consolidated same-spline joint), the outer ends
        // are ground abutments.
        var seg1 = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 20f, OsmWayIds = { 41L }
        };
        var seg2 = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 24f, EndDistance = 40f, OsmWayIds = { 42L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg1, seg2]);
        spline.Parameters.BridgeRules = SuppressionRules();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        var bySpline = GroupBySpline(network);

        UnifiedRoadSmoother.MarkStructureExclusions([spline], bySpline);

        Assert.False(seg1.StartContinuesOntoDeck);
        Assert.True(seg1.EndContinuesOntoDeck);
        Assert.True(seg2.StartContinuesOntoDeck);
        Assert.False(seg2.EndContinuesOntoDeck);

        Assert.True(At(bySpline, 1, 19f).IsExcluded);  // facing end of seg1: no shrink
        Assert.True(At(bySpline, 1, 25f).IsExcluded);  // facing end of seg2: no shrink
        Assert.False(At(bySpline, 1, 11f).IsExcluded); // outer ends: today's shrink
        Assert.False(At(bySpline, 1, 39f).IsExcluded);
    }

    [Fact]
    public void SuppressionFlagOff_ByteIdenticalShrink()
    {
        var (trunk, trunkSeg, ramp, rampSeg, bySpline) = BuildRampLandingOnTrunk(suppressionOn: false);

        UnifiedRoadSmoother.MarkStructureExclusions([trunk, ramp], bySpline);

        Assert.False(rampSeg.EndContinuesOntoDeck);
        Assert.False(At(bySpline, 2, 28f).IsExcluded); // legacy: shrunk at the landing end too
    }

    [Fact]
    public void NonStructureCorridor_FlagOn_ExcludesNothing()
    {
        var network = new UnifiedRoadNetwork();
        // No StructureSegments → a plain road corridor.
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));

        Assert.All(cs, c => Assert.False(c.IsExcluded));
        Assert.All(cs, c => Assert.Equal(-1, c.StructureSpanId));
    }
}
