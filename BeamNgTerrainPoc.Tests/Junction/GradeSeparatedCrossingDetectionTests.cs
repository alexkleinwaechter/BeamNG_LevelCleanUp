using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
/// E-A stage (a): NetworkJunctionDetector classifies a vertically-separated mid-spline crossing as a
/// <see cref="GradeSeparatedCrossing"/> on the network instead of emitting a (false) at-grade
/// <see cref="JunctionType.MidSplineCrossing"/> junction. See doc 07 §6.
/// </summary>
public class GradeSeparatedCrossingDetectionTests
{
    // A horizontal spline and a vertical spline that cross at (150,150) — mid-spline for both, so the
    // crossing detector (which only samples non-endpoint sections) sees it.
    private static (ParameterizedRoadSpline horizontal, ParameterizedRoadSpline vertical) CrossingPair(
        bool horizIsBridge = false, bool vertIsBridge = false,
        int horizLayer = 0, int vertLayer = 0,
        int horizPriority = 50, int vertPriority = 50)
    {
        var horizontal = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(250, 150), priority: horizPriority, isBridge: horizIsBridge);
        horizontal.Layer = horizLayer;

        var vertical = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 50), new(150, 250), priority: vertPriority, isBridge: vertIsBridge);
        vertical.Layer = vertLayer;

        return (horizontal, vertical);
    }

    [Fact]
    public void BridgeOverRoad_EmitsGradeSeparatedCrossing_NotAtGradeJunction()
    {
        var (road, bridge) = CrossingPair(vertIsBridge: true);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road, bridge);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(bridge.SplineId, crossing.UpperSplineId);
        Assert.Equal(road.SplineId, crossing.LowerSplineId);
        Assert.True(crossing.UpperIsBridge);
        Assert.False(crossing.LowerIsBridge);

        // The latent-bug fix: this pair must NOT also be recorded as an at-grade mid-spline crossing.
        Assert.DoesNotContain(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    [Fact]
    public void TwoSameLayerGroundRoads_StillAtGradeCrossing_NoGradeSeparation()
    {
        var (a, b) = CrossingPair(); // both layer 0, neither a bridge

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(a, b);

        Assert.Empty(network.GradeSeparatedCrossings);
        Assert.Contains(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    [Fact]
    public void DifferentLayers_NoBridge_HigherLayerIsUpper()
    {
        // Vertical road on layer 1 rides over the horizontal road on layer 0 (no bridge flag involved).
        var (lower, upper) = CrossingPair(vertLayer: 1);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(lower, upper);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(upper.SplineId, crossing.UpperSplineId);
        Assert.Equal(1, crossing.UpperLayer);
        Assert.Equal(0, crossing.LowerLayer);
        Assert.DoesNotContain(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    [Fact]
    public void Priority_DoesNotDecideGradeSeparation_LayerFirst()
    {
        // Lower-priority road sits on the higher layer; layer must win the under/over decision, not priority.
        var (horiz, vert) = CrossingPair(vertLayer: 1, horizPriority: 100, vertPriority: 25);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(horiz, vert);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(vert.SplineId, crossing.UpperSplineId); // layer 1 wins despite priority 25 < 100
        Assert.Equal(25, crossing.UpperPriority);
        Assert.Equal(100, crossing.LowerPriority);
    }

    [Fact]
    public void TwoBridgesSameLayer_NotGradeSeparated()
    {
        // Both bridges on the same layer → ambiguous; left as an at-grade crossing (documented edge case).
        var (a, b) = CrossingPair(horizIsBridge: true, vertIsBridge: true);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(a, b);

        Assert.Empty(network.GradeSeparatedCrossings);
    }

    // ── Phase A1 (plan doc 14 §5/§1a): merged-corridor effective layer ──
    // On a merged corridor the whole-spline Layer/IsBridge are the merge-base way's (layer 0, not a bridge);
    // the bridge is an interior StructureSegment carrying layer=1. Grade-sep must classify from the span's
    // EFFECTIVE layer at the crossing, not the whole-spline layer (which is why merged corridors recorded 0).

    // A long ground-level corridor (layer 0, not a bridge) carrying a bridge StructureSegment over the arc
    // range [spanStart, spanEnd]m, crossed by a vertical equal-priority ground road at the given X.
    private static (ParameterizedRoadSpline corridor, ParameterizedRoadSpline underRoad) MergedCorridorPair(
        float crossingX, float spanStart, float spanEnd, int corridorLayer = 0)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart,
            EndDistance = spanEnd,
            Type = StructureType.Bridge,
            Layer = 1,
            OsmWayIds = { 99001 },
        };

        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 8002, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = corridorLayer; // merge-base layer (the approach), NOT the span's layer=1

        var underRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(crossingX, 50), new(crossingX, 250), priority: 8002, isBridge: false);
        underRoad.Layer = 0;

        return (corridor, underRoad);
    }

    [Fact]
    public void MergedCorridorBridgeSpan_OverEqualPriorityRoad_EmitsGradeSeparated_NotAtGrade()
    {
        // Under-road crosses at x=200 → ~150 m along the corridor, INSIDE the bridge span [100,200].
        var (corridor, underRoad) = MergedCorridorPair(crossingX: 200, spanStart: 100, spanEnd: 200);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, underRoad);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(corridor.SplineId, crossing.UpperSplineId); // span's effective layer 1 > under-road layer 0
        Assert.Equal(underRoad.SplineId, crossing.LowerSplineId);
        Assert.Equal(1, crossing.UpperLayer);
        Assert.Equal(0, crossing.LowerLayer);
        Assert.True(crossing.UpperIsBridge);

        // The under-deck road must NOT be harmonized as an at-grade crossing with the deck.
        Assert.DoesNotContain(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    [Fact]
    public void MergedCorridorRoadBesideSpan_StillAtGradeJunction()
    {
        // Under-road crosses at x=350 → ~300 m along the corridor, OUTSIDE the bridge span [100,200], so the
        // corridor's effective layer there is the whole-spline layer 0 → genuine at-grade crossing.
        var (corridor, underRoad) = MergedCorridorPair(crossingX: 350, spanStart: 100, spanEnd: 200);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, underRoad);

        Assert.Empty(network.GradeSeparatedCrossings);
        Assert.Contains(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    // ── Phase A3 (plan doc 14 §5): the footprint pass catches under-deck roads the proximity sampler misses.
    [Fact]
    public void FootprintPass_CatchesUnderDeckRoad_TheSamplerMissed()
    {
        // Wide deck (width 40 → half-width 20) on a merged corridor; a short under-road sits squarely beneath
        // the deck. With the proximity sampler effectively disabled (radius 0.1 m) the only way this crossing
        // can be recorded is the footprint sweep.
        var span = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 200f, Layer = 1,
            OsmWayIds = { 4711L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), roadWidth: 40f, priority: 8002, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);

        // Under-road crosses the deck at x=200 (≈150 m along the corridor, inside the span) but its centerline
        // is ≥ 15 m from the corridor centerline at every point → outside a 0.1 m proximity radius.
        var underRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 135), new(200, 165), priority: 50, isBridge: false);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, underRoad);

        var detector = new NetworkJunctionDetector();
        foreach (var j in detector.DetectJunctions(network, detectionRadiusOverride: 0.1f))
            network.Junctions.Add(j);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(corridor.SplineId, crossing.UpperSplineId);
        Assert.Equal(underRoad.SplineId, crossing.LowerSplineId);
        Assert.Equal(1, crossing.UpperLayer);
        Assert.DoesNotContain(network.Junctions, j => j.Type == JunctionType.MidSplineCrossing);
    }

    // ── EnableHiddenCrossingDetection, junction-connected half (run 160210 bridge_8655179): a corridor
    // that tees into another road can STILL bridge over it elsewhere. The pair-connected skip hid the
    // crossing from BOTH detector passes — the footprint pass now re-examines such pairs, guarded by
    // distance from the shared junction.

    // Corridor (tertiary-like, prio 6001) with a bridge span [100,200] (x∈[150,250], layer 1); an
    // under-road (primary-like, prio 8001) that crosses beneath the deck at x=200 AND tees into the
    // corridor at (350,150) — 150 m from the crossing, like the K 102 joining the B 53.
    private static (ParameterizedRoadSpline corridor, ParameterizedRoadSpline underRoad)
        ConnectedCrossingPair(bool hiddenCrossings)
    {
        var span = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 200f, Layer = 1,
            OsmWayIds = { 4712L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 6001, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableHiddenCrossingDetection = hiddenCrossings,
        };

        var underRoad = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(
                [new(200, 50), new(200, 250), new(350, 250), new(350, 150)],
                SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 8f,
                TerrainAffectedRangeMeters = 6f,
                CrossSectionIntervalMeters = 0.5f,
            },
            MaterialName = "asphalt",
            SplineId = 2,
            OsmRoadType = "primary",
            Priority = 8001,
        };

        return (corridor, underRoad);
    }

    [Fact]
    public void JunctionConnectedPair_CrossingFarFromSharedJunction_IsRecorded()
    {
        var (corridor, underRoad) = ConnectedCrossingPair(hiddenCrossings: true);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, underRoad);

        // The tee at (350,150) must exist — it is exactly what used to hide the crossing.
        Assert.Contains(network.Junctions, j =>
            j.Contributors.Any(c => c.Spline.SplineId == corridor.SplineId) &&
            j.Contributors.Any(c => c.Spline.SplineId == underRoad.SplineId));

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(corridor.SplineId, crossing.UpperSplineId);
        Assert.Equal(underRoad.SplineId, crossing.LowerSplineId);
        Assert.Equal(1, crossing.UpperLayer);
        Assert.Equal(0, crossing.LowerLayer);
        Assert.True(crossing.UpperIsBridge);
        Assert.True(crossing.LowerPriority > crossing.UpperPriority); // primary under tertiary ⇒ veto raise
    }

    [Fact]
    public void JunctionConnectedPair_FlagOff_StaysHidden()
    {
        var (corridor, underRoad) = ConnectedCrossingPair(hiddenCrossings: false);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, underRoad);

        Assert.Empty(network.GradeSeparatedCrossings); // legacy baseline: connected pairs never checked
    }

    [Fact]
    public void JunctionConnectedPair_HitNearSharedJunction_IsNotACrossing()
    {
        // The side road tees into the corridor at the span EDGE (abutment area — station 105 of span
        // [100,200], inside DeckEndEpsilonMeters): a genuine connection, so the pair is
        // junction-connected and its footprint sections right at the shared junction are the
        // T-junction overlap area, not a crossing to clear. (2026-07-18: a tee into the deck
        // INTERIOR is no longer a junction at all — the structure-state admission guard rejects it
        // and the crossing IS recorded; see StructureStateTJunctionGuardTests.)
        var span = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 200f, Layer = 1,
            OsmWayIds = { 4713L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 6001, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableHiddenCrossingDetection = true,
        };

        var sideRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(155, 50), new(155, 150), priority: 8001, isBridge: false);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, sideRoad);

        // The tee must survive as a junction — the edge admission is what makes the pair
        // junction-connected and routes it through the shared-junction exclusion.
        Assert.Contains(network.Junctions, j =>
            j.Contributors.Any(c => c.Spline.SplineId == corridor.SplineId) &&
            j.Contributors.Any(c => c.Spline.SplineId == sideRoad.SplineId));

        Assert.Empty(network.GradeSeparatedCrossings);
    }
}
