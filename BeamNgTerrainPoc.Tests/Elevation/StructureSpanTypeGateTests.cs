using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Tunnel plan Phase 0 (ai_docs/2026-07-18_tunnel_generation): the span machinery is shared between
///     bridges and tunnels (<see cref="UnifiedCrossSection.StructureSpanId" />), but every downstream span
///     consumer historically assumed "span ⇒ bridge deck". With "Exclude Tunnels" on, tunnel spans were
///     tagged too and flowed into the whole deck pipeline (deck solve, snapshot capture, DAE export,
///     excavation, abutment tongues). <see cref="UnifiedCrossSection.StructureSpanType" /> gates them out:
///     tunnel spans keep their tagging (the tunnel pipeline builds on it) while bridge consumers act on
///     Bridge spans only.
/// </summary>
public class StructureSpanTypeGateTests
{
    private static Dictionary<int, List<UnifiedCrossSection>> GroupBySpline(UnifiedRoadNetwork network) =>
        network.CrossSections.GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

    /// <summary>
    ///     Builds a 40 m corridor: road [0,15) – tunnel span [15,25] – road (25,40] with a chain solve
    ///     that "climbs over the mountain" inside the span (parabolic bump), roads on a constant 4% grade.
    ///     The span is tagged the production way (TagStructureSpans + MarkStructureExclusions).
    /// </summary>
    private static (UnifiedRoadNetwork network, StructureSegment seg, List<UnifiedCrossSection> cs)
        BuildTunnelCorridor()
    {
        const float grade = 0.04f;
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 777L },
            OsmTags = new Dictionary<string, string> { ["tunnel"] = "yes" }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);
        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, bySpline);

        foreach (var c in cs)
        {
            var d = c.DistanceAlongSpline;
            var roadZ = 100f + grade * d;
            if (d >= 15f && d <= 25f)
            {
                // Chain solve today follows smoothed terrain OVER the mountain inside the span.
                var t = (d - 15f) / 10f;
                c.TargetElevation = roadZ + 30f * t * (1f - t);
            }
            else
            {
                c.TargetElevation = roadZ;
            }
        }

        return (network, seg, cs);
    }

    [Fact]
    public void TagStructureSpans_SetsSpanTypeFromSegment()
    {
        var (_, seg, cs) = BuildTunnelCorridor();

        foreach (var c in cs)
        {
            var inSpan = c.DistanceAlongSpline >= 15f && c.DistanceAlongSpline <= 25f;
            Assert.Equal(inSpan ? seg.SpanId : -1, c.StructureSpanId);
            Assert.Equal(inSpan ? StructureType.Tunnel : StructureType.None, c.StructureSpanType);
        }
    }

    [Fact]
    public void MarkStructureExclusions_AloneSetsSpanTypeToo()
    {
        // The Phase-2.0 exclusion pass must tag the type on its own (it re-affirms the early tag,
        // but legacy call orders may reach it first).
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 10f, EndDistance = 20f, OsmWayIds = { 5L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(30, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));

        Assert.Contains(cs, c => c.StructureSpanType == StructureType.Tunnel);
        Assert.All(cs.Where(c => c.StructureSpanId >= 0),
            c => Assert.Equal(StructureType.Tunnel, c.StructureSpanType));
    }

    [Fact]
    public void TunnelSpan_RefineSpans_NoDeckSolve_NoSnapshot_ElevationsUntouched()
    {
        var (network, _, cs) = BuildTunnelCorridor();
        var before = cs.Select(c => c.TargetElevation).ToList();

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Empty(result.Applications);
        Assert.Empty(network.BridgeSpans);
        // The over-the-mountain profile is NOT flattened into a bridge chord.
        Assert.Equal(before, cs.Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void TunnelSpan_NoDeckGroups_NoExcavation()
    {
        var (network, _, _) = BuildTunnelCorridor();

        Assert.Empty(BridgeDeckExcavator.CollectDeckGroups(network));

        var heightMap = RoadNetworkTestHelpers.CreateFlatHeightmap(64, 130f);
        var result = BridgeDeckExcavator.Excavate(network, heightMap, metersPerPixel: 1f, log: false);
        Assert.Equal(0, result.CellsLowered);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            Assert.Equal(130f, heightMap[y, x]);
    }

    [Fact]
    public void TunnelSpan_NoAbutmentOverlapTongue()
    {
        var (network, _, _) = BuildTunnelCorridor();
        network.Splines[0].Parameters.BridgeRules = new BeamNgTerrainPoc.Terrain.Models.BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = true
        };

        var heightMap = RoadNetworkTestHelpers.CreateFlatHeightmap(64, 100f);
        var stamped = BridgeAbutmentOverlapStamper.Stamp(network, heightMap, metersPerPixel: 1f, log: false);

        Assert.Equal(0, stamped);
    }

    [Fact]
    public void MixedCorridor_OnlyBridgeSpanFlowsIntoDeckPipeline()
    {
        // road – TUNNEL [10,18] – road – BRIDGE [26,34] – road on one merged corridor.
        const float grade = 0.02f;
        var network = new UnifiedRoadNetwork();
        var tunnelSeg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 10f, EndDistance = 18f, OsmWayIds = { 1L }
        };
        var bridgeSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 26f, EndDistance = 34f, OsmWayIds = { 2L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(44, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [tunnelSeg, bridgeSeg]);
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);
        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, bySpline);
        foreach (var c in cs)
            c.TargetElevation = 100f + grade * c.DistanceAlongSpline;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        var snap = Assert.Single(network.BridgeSpans);
        Assert.Equal(bridgeSeg.SpanId, snap.SpanId);

        var deckGroups = BridgeDeckExcavator.CollectDeckGroups(network);
        var deck = Assert.Single(deckGroups);
        Assert.All(deck, c => Assert.Equal(bridgeSeg.SpanId, c.StructureSpanId));
    }

    [Fact]
    public void BridgeSpan_StillFullyCaptured_TypeGateDoesNotRegressBridges()
    {
        // Same corridor as the tunnel fixture but typed Bridge: the deck pipeline must engage.
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 15f, EndDistance = 25f, OsmWayIds = { 777L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);
        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, bySpline);
        foreach (var c in cs)
            c.TargetElevation = 100f + 0.04f * c.DistanceAlongSpline;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Single(result.Applications);
        Assert.Single(network.BridgeSpans);
        Assert.NotEmpty(BridgeDeckExcavator.CollectDeckGroups(network));
    }
}
