using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 09 C1/C2 — JunctionA0Elevation and IsJunctionElevationInflated helpers on UnifiedRoadSmoother,
///     plus integration tests for the C2 wiring (decay fires at any A0-inflated junction when
///     EnableNaturalProfileAnchor is on, even when the junction is NOT in BridgeRaisedJunctions).
/// </summary>
public class NaturalProfileAnchorTests
{
    [Fact]
    public void JunctionA0Elevation_AveragesContributorEstimates()
    {
        // Two contributors, A0 estimates 10 and 20 → mean 15.
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(a0First: 10f, a0Second: 20f);
        var a0 = UnifiedRoadSmoother.JunctionA0Elevation(network, junction);
        Assert.Equal(15f, a0, 3);
    }

    [Theory]
    [InlineData(15f, false)]   // solved Z == A0 mean → not inflated
    [InlineData(16.4f, false)] // +1.4 m < 1.5 m threshold → not inflated
    [InlineData(16.6f, true)]  // +1.6 m ≥ 1.5 m threshold → inflated
    [InlineData(69f, true)]    // the Manhattan dam magnitude → inflated
    public void IsJunctionElevationInflated_FiresAboveThreshold(float solvedZ, bool expected)
    {
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(a0First: 10f, a0Second: 20f); // A0 mean = 15
        Assert.Equal(expected, UnifiedRoadSmoother.IsJunctionElevationInflated(network, junction, solvedZ));
    }

    [Fact]
    public void IsJunctionElevationInflated_FalseWhenNoEstimate()
    {
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(10f, 20f);
        network.EarlyElevationEstimate = null;
        Assert.False(UnifiedRoadSmoother.IsJunctionElevationInflated(network, junction, 99f));
    }

    // ── C2 wiring integration tests ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds the same T-junction topology as BridgeSideRoadContainmentTests.BuildRaisedTee(), but:
    ///     - the junction is NOT added to BridgeRaisedJunctions
    ///     - EarlyElevationEstimate places the junction at A0≈0, so solved Z≈30 is inflated (+30 ≫ 1.5)
    ///     - EnableNaturalProfileAnchor=true is set on the side spline when anchorOn=true
    /// </summary>
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline side, NetworkJunction junction)
        BuildInflatedTee(bool anchorOn)
    {
        // Corridor (through): solved at 30, extends 400 m.
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(50, 150), new Vector2(450, 150), priority: 10000);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 30f;

        // Side road (terminating at start): solved at 0, extends 400 m away.
        var side = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(200, 150), new Vector2(200, 550));
        if (anchorOn)
            side.Parameters.BridgeRules = new BridgeRuleSystemOptions
            {
                EnableNaturalProfileAnchor = true
            };
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, side);
        foreach (var cs in network.GetCrossSectionsForSpline(side.SplineId))
            cs.TargetElevation = 0f;

        // T-junction: corridor is through (non-endpoint), side terminates at start.
        var corridorCs = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 150f)).First();
        var sideCs = network.GetCrossSectionsForSpline(side.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).First();

        var junction = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.TJunction, Position = corridorCs.CenterPoint,
        };
        junction.Contributors.Add(new JunctionContributor { CrossSection = corridorCs, Spline = corridor });
        junction.Contributors.Add(
            new JunctionContributor { CrossSection = sideCs, Spline = side, IsSplineStart = true });
        network.Junctions.Add(junction);

        // EarlyElevationEstimate: A0 at the junction ≈ 0 (natural ground), so junction solved Z=30 is
        // inflated by 30 m >> 1.5 m threshold.
        network.EarlyElevationEstimate = new Dictionary<int, float>
        {
            [corridorCs.Index] = 0f,
            [sideCs.Index] = 0f,
        };

        // Critically: junction is NOT added to network.BridgeRaisedJunctions.
        return (network, side, junction);
    }

    private static UnifiedCrossSection SectionAt(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station) =>
        network.GetCrossSectionsForSpline(spline.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    [Fact]
    public void InflatedJunction_FlagOn_AffineCorrectionDecays_WithinClassSlopeRun()
    {
        // Junction solved at 30, A0≈0 (not in BridgeRaisedJunctions). With EnableNaturalProfileAnchor=true
        // the decay path fires and the side road climbs to the junction over the class-slope run
        // (primary 5 % ⇒ 600 m for Δ=30), then flattens back to 0 — it does NOT tilt the whole 400 m.
        var (network, side, _) = BuildInflatedTee(anchorOn: true);

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        // Junction endpoint: must land on the corridor Z (30).
        Assert.Equal(30f, SectionAt(network, side, 0f).TargetElevation, 0.15f);
        // Partway into the decay run: elevation should be above the natural 0 but well below 30
        // (decayed, not flat). At ~50 m the road is still climbing toward the junction — it should
        // be materially above 0 (proving the affine fired) but the far body returns to 0.
        Assert.True(SectionAt(network, side, 50f).TargetElevation > 1f,
            "elevation at 50 m should be above natural 0 (affine correction applied)");
        // Far end (400 m): past any 5 % class-slope run for Δ=30 (run = 600 m > spline length of
        // 400 m), so the road body returns toward 0 — with decay the correction blends out. In
        // practice "past the run" means near the natural profile end: assert < 15 (halfway mark),
        // proving it doesn't stay at the full +30 tilt.
        Assert.True(SectionAt(network, side, 390f).TargetElevation < 15f,
            "far end should not remain at full-length tilt of +30 when decay fires");
    }

    [Fact]
    public void InflatedJunction_FlagOff_LegacyFullLengthTilt()
    {
        // Same topology with EnableNaturalProfileAnchor=false (default). Junction NOT in BridgeRaisedJunctions.
        // Without the wiring the full-length legacy tilt applies: the far end stays near +30·(1-t)
        // at mid-length, proving the flag truly gates the new behaviour.
        var (network, side, _) = BuildInflatedTee(anchorOn: false);

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(30f, SectionAt(network, side, 0f).TargetElevation, 0.15f);
        // Legacy linear correction at mid-length (t≈0.5): still carries ~+15.
        Assert.True(SectionAt(network, side, 200f).TargetElevation > 12f,
            "flag-off legacy tilt should keep mid-road well above natural Z");
        // Far end: correction = e0·(1−t) → near 0 at t=1 (last cross-section).
        Assert.Equal(0f, SectionAt(network, side, 399f).TargetElevation, 2f);
    }

    // ── Doc 09 C4/C5 — AssertCrossingClearances: read-only, counts only short crossings ─────────────────

    // Re-use the same scenario builder + helpers that BridgeApproachRaiseRampTests uses so we do NOT
    // need extra scaffolding — construct a plan with one crossing and check count + no writes.

    private static (UnifiedRoadNetwork network, StructureSegment span)
        BuildClearanceScenario(float deckZ, float obstacleZ)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(50, 150), new Vector2(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableGradedDeck = true, EnableSparseDeckConstraints = true,
        };

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = deckZ;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
            }
        }

        // Synthetic water crossing at station 190 (near-abutment end — same as BridgeApproachRaiseRampTests).
        var crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = 1,
            LowerSplineId = -1, // synthetic — no road spline below
            CrossingXY = new Vector2(50f + 190f, 150f),
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 10000,
            LowerPriority = 0,
            UpperIsBridge = true,
            LowerIsBridge = false,
            LowerKind = BridgeObstacleKind.Water,
            LowerNavigable = true,
        };
        var crossingPlan = new CrossingPlan
        {
            Crossing = crossing,
            Action = BridgeElevationAction.RaiseBridgeVeto,
            RequiredSeparationMeters = 6.7f,
            ObstacleZEstimate = obstacleZ,
            LowerRoadTargetZ = float.NaN,
        };
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Spans =
            [
                new SpanDeckPlan
                {
                    OwnerSplineId = 1,
                    SpanId = span.SpanId,
                    StartDistance = span.StartDistance,
                    EndDistance = span.EndDistance,
                    Layer = 1,
                    RequiredDeckZ = deckZ,
                    IsRaised = false,
                    ApproachZLeft = deckZ,
                    ApproachZRight = deckZ,
                    ClearanceUsed = 5f,
                },
            ],
            Crossings = [crossingPlan],
        };

        return (network, span);
    }

    [Fact]
    public void AssertCrossingClearances_NullPlan_ReturnsZero()
    {
        // A network with no plan at all must return 0 (never throws).
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(100, 0));
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        network.BridgeElevationPlan = null;

        var count = GradeSeparationResolver.AssertCrossingClearances(network);

        Assert.Equal(0, count);
    }

    [Fact]
    public void AssertCrossingClearances_SufficientClearance_ReturnsZero_WritesNothing()
    {
        // Deck=16, obstacle=8 → clearance 8 ≥ 6.7 required → no short crossing, count=0.
        var (network, _) = BuildClearanceScenario(deckZ: 16f, obstacleZ: 8f);
        var before = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();

        var count = GradeSeparationResolver.AssertCrossingClearances(network);

        Assert.Equal(0, count);
        // NOTHING must have been written.
        var after = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void AssertCrossingClearances_InsufficientClearance_ReturnsOne_WritesNothing()
    {
        // Deck=10, obstacle=8 → clearance 2 < 6.7 required − 0.05 tolerance → short count=1.
        // Crossing is at station 190 within span 100–200 → t=0.9, shape≈0.13 < 0.25 (approach zone).
        var (network, _) = BuildClearanceScenario(deckZ: 10f, obstacleZ: 8f);
        var before = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();

        var count = GradeSeparationResolver.AssertCrossingClearances(network);

        Assert.Equal(1, count);
        // Critical: read-only — TargetElevation must NOT have changed.
        var after = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void AssertCrossingClearances_InteriorBandCrossing_ReturnsZero()
    {
        // Crossing at mid-span (station 150, t=0.5): shape = 16·0.5²·0.5² = 1.0 ≥ FloorMinArchShape=0.25.
        // The interior-floor band belongs to the RefineSpans arch — AssertCrossingClearances must skip it
        // and return 0 even when clearance is short (deck=10, obstacle=8 → clearance 2 < 6.7 required).
        // Regression guard for I1: without the floor-band guard the old code would return 1.
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(50, 150), new Vector2(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableGradedDeck = true, EnableSparseDeckConstraints = true,
        };

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        const float deckZ = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = deckZ;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
            }
        }

        // Crossing at station 150 (mid-span): t = (150-100)/100 = 0.5, shape = 16·0.25·0.25 = 1.0 ≥ 0.25.
        // CrossingXY = (50 + 150, 150) = (200, 150).
        var crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = 1,
            LowerSplineId = -1,
            CrossingXY = new Vector2(50f + 150f, 150f),
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 10000,
            LowerPriority = 0,
            UpperIsBridge = true,
            LowerIsBridge = false,
            LowerKind = BridgeObstacleKind.Water,
            LowerNavigable = true,
        };
        var crossingPlan = new CrossingPlan
        {
            Crossing = crossing,
            Action = BridgeElevationAction.RaiseBridgeVeto,
            RequiredSeparationMeters = 6.7f,
            ObstacleZEstimate = 8f,
            LowerRoadTargetZ = float.NaN,
        };
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Spans =
            [
                new SpanDeckPlan
                {
                    OwnerSplineId = 1,
                    SpanId = span.SpanId,
                    StartDistance = span.StartDistance,
                    EndDistance = span.EndDistance,
                    Layer = 1,
                    RequiredDeckZ = deckZ,
                    IsRaised = false,
                    ApproachZLeft = deckZ,
                    ApproachZRight = deckZ,
                    ClearanceUsed = 5f,
                },
            ],
            Crossings = [crossingPlan],
        };

        // Interior band → arch owns this crossing → assert must return 0 (no warning).
        var count = GradeSeparationResolver.AssertCrossingClearances(network);

        Assert.Equal(0, count);
    }
}
