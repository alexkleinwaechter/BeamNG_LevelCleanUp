using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tests for <see cref="BridgeProfileSolver" /> — Step 1 (seam diagnostics) and Step 2
///     (connected approach contributor lookup with grade estimation) of the bridge elevation /
///     continuity plan (doc 05).
/// </summary>
public class BridgeProfileSolverTests
{
    // road1 (10..100) → bridge (100..200) → road2 (200..290), all along y=50.
    private static UnifiedRoadNetwork BuildRoadBridgeRoad(float[,] heightMap)
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary");

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.IsExcluded = true;

        RoadNetworkTestHelpers.RunChainSmoothing(network, heightMap);
        return network;
    }

    [Fact]
    public void FindConnectedRoadContributor_BothEnds_ReturnsApproachWithMatchingZ()
    {
        // Slope rising in +x so approaches carry a real (positive) longitudinal grade.
        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f);
        var network = BuildRoadBridgeRoad(hm);

        var start = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true);
        var end = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: false);

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal(1, start!.RoadSplineId); // road1 feeds the bridge start
        Assert.Equal(3, end!.RoadSplineId);   // road2 feeds the bridge end

        var bridgeSections = network.GetCrossSectionsForSpline(2).OrderBy(c => c.LocalIndex).ToList();
        // Connected approach elevation should be close to the bridge endpoint's current elevation
        // (they share a junction; both followed the same smoothed slope here).
        Assert.True(Math.Abs(start.Elevation - bridgeSections[0].TargetElevation) < 2f);
        Assert.True(Math.Abs(end.Elevation - bridgeSections[^1].TargetElevation) < 2f);
    }

    [Fact]
    public void FindConnectedRoadContributor_GradeOrientedAlongBridgePlusS_IsPositiveOnRisingSlope()
    {
        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f); // rises in +x = bridge +s
        var network = BuildRoadBridgeRoad(hm);

        var start = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true);
        var end = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: false);

        // Both endpoint grades expressed in the bridge +s direction must be positive (uphill in +x).
        Assert.True(start!.GradeAlongBridge > 0.05f, $"start grade {start.GradeAlongBridge:F3} should be clearly positive");
        Assert.True(end!.GradeAlongBridge > 0.05f, $"end grade {end.GradeAlongBridge:F3} should be clearly positive");
    }

    [Fact]
    public void FindConnectedRoadContributor_IsolatedBridge_ReturnsNull()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        Assert.Null(BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true));
        Assert.Null(BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: false));
    }

    [Fact]
    public void DiagnoseSeams_CollinearStraightChain_HasNearZeroHeadingAndNormalDelta()
    {
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(300);
        var network = BuildRoadBridgeRoad(hm);

        var diags = BridgeProfileSolver.DiagnoseSeams(network, log: false);

        Assert.Equal(2, diags.Count); // one per bridge endpoint
        Assert.All(diags, d => Assert.True(d.Connected));
        Assert.All(diags, d => Assert.True(d.HeadingDeltaDegrees < 1f, $"headingΔ={d.HeadingDeltaDegrees:F2}"));
        Assert.All(diags, d => Assert.True(d.NormalDeltaDegrees < 1f, $"normalΔ={d.NormalDeltaDegrees:F2}"));
    }

    [Fact]
    public void DiagnoseSeams_IsolatedBridge_ReportsUnconnectedSeams()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        var diags = BridgeProfileSolver.DiagnoseSeams(network, log: false);

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.False(d.Connected));
        Assert.All(diags, d => Assert.Null(d.RoadSplineId));
    }

    [Fact]
    public void DiagnoseSeams_NoBridges_ReturnsEmpty()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road);
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        Assert.Empty(BridgeProfileSolver.DiagnoseSeams(network, log: false));
    }

    // ---- Step 3: RefineSpans (vertical override) ----

    private static List<UnifiedCrossSection> BridgeSections(UnifiedRoadNetwork network, int splineId) =>
        network.GetCrossSectionsForSpline(splineId).OrderBy(c => c.LocalIndex).ToList();

    [Fact]
    public void RefineSpans_BothEndsConnected_OverridesSectionsToCubicProfile()
    {
        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f);
        var network = BuildRoadBridgeRoad(hm);

        var startC = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true)!;
        var endC = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: false)!;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        // Gentle, consistent slope ⇒ no bulge ⇒ the exact cubic is used.
        Assert.Equal(BridgeProfileSolver.BridgeProfileCurve.Cubic, app.Curve);

        var bridge = BridgeSections(network, 2);
        var s0 = bridge[0].DistanceAlongSpline;
        var length = bridge[^1].DistanceAlongSpline - s0;

        // G0 at both endpoints: deck end elevation == connected approach elevation.
        Assert.Equal(startC.Elevation, bridge[0].TargetElevation, precision: 3);
        Assert.Equal(endC.Elevation, bridge[^1].TargetElevation, precision: 3);

        // Every interior section lies on the analytic cubic P(s) (no terrain following).
        foreach (var cs in bridge)
        {
            var expected = CubicHermite(cs.DistanceAlongSpline - s0, length,
                startC.Elevation, endC.Elevation, startC.GradeAlongBridge, endC.GradeAlongBridge);
            Assert.Equal(expected, cs.TargetElevation, precision: 2);
        }
    }

    [Fact]
    public void RefineSpans_EndpointGrades_MatchApproachGrades()
    {
        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f);
        var network = BuildRoadBridgeRoad(hm);

        var startC = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true)!;
        var endC = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: false)!;

        BridgeProfileSolver.RefineSpans(network, log: false);

        var bridge = BridgeSections(network, 2);
        var startSlope = (bridge[1].TargetElevation - bridge[0].TargetElevation) /
                         (bridge[1].DistanceAlongSpline - bridge[0].DistanceAlongSpline);
        var endSlope = (bridge[^1].TargetElevation - bridge[^2].TargetElevation) /
                       (bridge[^1].DistanceAlongSpline - bridge[^2].DistanceAlongSpline);

        Assert.True(Math.Abs(startSlope - startC.GradeAlongBridge) < 0.02f,
            $"start slope {startSlope:F3} vs approach grade {startC.GradeAlongBridge:F3}");
        Assert.True(Math.Abs(endSlope - endC.GradeAlongBridge) < 0.02f,
            $"end slope {endSlope:F3} vs approach grade {endC.GradeAlongBridge:F3}");
    }

    [Fact]
    public void RefineSpans_StrongArch_TriggersOvershootGuardFallback()
    {
        // Flat chain, then craft a pure ARCH above the chord (z0≈z1, g0≈+0.5 rising into the span,
        // g1≈-0.5 falling out in +s). No sag below the chord, so the sag cap leaves it alone and the
        // arch bulge blows past the guard threshold → parabola/chord fallback.
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));

        var road1 = BridgeSections(network, 1);
        var dEnd1 = road1[^1].DistanceAlongSpline;
        foreach (var cs in road1)
        {
            var fromEnd = dEnd1 - cs.DistanceAlongSpline;
            if (fromEnd <= 15f) cs.TargetElevation = 100f - 0.5f * fromEnd; // rising toward junction (g0=+0.5)
        }

        var road2 = BridgeSections(network, 3);
        var dStart2 = road2[0].DistanceAlongSpline;
        foreach (var cs in road2)
        {
            var fromStart = cs.DistanceAlongSpline - dStart2;
            if (fromStart <= 15f) cs.TargetElevation = 100f - 0.5f * fromStart; // descending in +x (g1=-0.5)
        }

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);

        Assert.True(app.Applied);
        Assert.NotEqual(BridgeProfileSolver.BridgeProfileCurve.Cubic, app.Curve); // guard fired
        Assert.Equal(1f, app.SagCapFactor, precision: 3); // arch, not sag → cap untouched

        var bridge = BridgeSections(network, 2);
        var length = bridge[^1].DistanceAlongSpline - bridge[0].DistanceAlongSpline;
        var threshold = MathF.Min(0.25f * length, BridgeProfileSolver.DefaultMaxProfileBulgeCapMeters);
        Assert.True(app.MaxBulgeMeters <= threshold + 1e-3f,
            $"bulge {app.MaxBulgeMeters:F2} should be bounded by {threshold:F2}");
    }

    [Fact]
    public void RefineSpans_SaggingApproaches_CapsDeckToChord()
    {
        // Reproduces bridge_82: steep approaches that descend into the span at the start (g0≈-0.5) and
        // rise out at the end (g1≈+0.5) with near-level endpoints → the raw cubic sags deep below the
        // chord. The sag cap must blend it back so the deck never bows below the chord beyond tolerance.
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));

        var road1 = BridgeSections(network, 1);
        var dEnd1 = road1[^1].DistanceAlongSpline;
        foreach (var cs in road1)
        {
            var fromEnd = dEnd1 - cs.DistanceAlongSpline;
            if (fromEnd <= 15f) cs.TargetElevation = 100f + 0.5f * fromEnd; // descending toward junction (g0=-0.5)
        }

        var road2 = BridgeSections(network, 3);
        var dStart2 = road2[0].DistanceAlongSpline;
        foreach (var cs in road2)
        {
            var fromStart = cs.DistanceAlongSpline - dStart2;
            if (fromStart <= 15f) cs.TargetElevation = 100f + 0.5f * fromStart; // rising in +x (g1=+0.5)
        }

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);

        Assert.True(app.Applied);
        Assert.Equal(BridgeProfileSolver.BridgeProfileCurve.Cubic, app.Curve); // capped cubic, not a fallback
        Assert.True(app.SagCapFactor < 1f, $"expected sag cap to engage, factor={app.SagCapFactor:F3}");

        // No deck section may sit below the endpoint chord by more than the tolerance.
        var bridge = BridgeSections(network, 2);
        var s0 = bridge[0].DistanceAlongSpline;
        var length = bridge[^1].DistanceAlongSpline - s0;
        var z0 = bridge[0].TargetElevation;
        var z1 = bridge[^1].TargetElevation;
        foreach (var cs in bridge)
        {
            var chord = z0 + (z1 - z0) * ((cs.DistanceAlongSpline - s0) / length);
            Assert.True(cs.TargetElevation >= chord - BridgeProfileSolver.DefaultMaxSagBelowChordMeters - 1e-2f,
                $"section dipped {chord - cs.TargetElevation:F2}m below chord (tol {BridgeProfileSolver.DefaultMaxSagBelowChordMeters})");
        }
    }

    [Fact]
    public void RefineSpans_OneEndIsolated_AppliesWithFallback()
    {
        // road1 → bridge, with the bridge's far end unconnected.
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2)) cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f));

        var startC = BridgeProfileSolver.FindConnectedRoadContributor(network, 2, isStart: true)!;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);

        Assert.True(app.Applied);
        Assert.True(app.StartConnected);
        Assert.False(app.EndConnected);

        var bridge2 = BridgeSections(network, 2);
        Assert.Equal(startC.Elevation, bridge2[0].TargetElevation, precision: 3); // connected end exact
        Assert.All(bridge2, cs => Assert.False(float.IsNaN(cs.TargetElevation)));
    }

    [Fact]
    public void RefineSpans_BothEndsIsolated_LeavesUntouched()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2)) cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        var before = BridgeSections(network, 2).Select(c => c.TargetElevation).ToList();

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);

        Assert.False(app.Applied);
        var after = BridgeSections(network, 2).Select(c => c.TargetElevation).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void RefineSpans_UnchainedBridgeOneConnectedEnd_RescuesAllSections()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2)) cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f));

        // Simulate an unchained bridge: wipe the solved elevation.
        foreach (var cs in network.GetCrossSectionsForSpline(2)) cs.TargetElevation = float.NaN;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        var app = Assert.Single(result.Applications);

        Assert.True(app.Applied);
        Assert.True(app.RescuedUnchained);
        Assert.All(BridgeSections(network, 2), cs => Assert.False(float.IsNaN(cs.TargetElevation)));
    }

    [Fact]
    public void RefineSpans_RecomputesBankedEdgeElevations()
    {
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f));

        const float bank = 0.1f;
        foreach (var cs in BridgeSections(network, 2)) cs.BankAngleRadians = bank;

        BridgeProfileSolver.RefineSpans(network, log: false);

        foreach (var cs in BridgeSections(network, 2))
        {
            var edgeDelta = cs.EffectiveRoadWidth / 2f * MathF.Sin(bank);
            Assert.Equal(cs.TargetElevation - edgeDelta, cs.LeftEdgeElevation, precision: 3);
            Assert.Equal(cs.TargetElevation + edgeDelta, cs.RightEdgeElevation, precision: 3);
        }
    }

    [Fact]
    public void RefineSpans_DoesNotTouchNonBridgeSplines()
    {
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f));

        var road1Before = BridgeSections(network, 1).Select(c => c.TargetElevation).ToList();
        var road2Before = BridgeSections(network, 3).Select(c => c.TargetElevation).ToList();

        BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Equal(road1Before, BridgeSections(network, 1).Select(c => c.TargetElevation).ToList());
        Assert.Equal(road2Before, BridgeSections(network, 3).Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void RefineSpans_NoBridges_IsNoop()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road);
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        var before = BridgeSections(network, 1).Select(c => c.TargetElevation).ToList();
        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Empty(result.Applications);
        Assert.Equal(before, BridgeSections(network, 1).Select(c => c.TargetElevation).ToList());
    }

    // ---- E-A stage (d): interior min-Z clearance constraints (D-4) ----

    [Fact]
    public void RefineSpans_InteriorConstraint_LiftsDeckAboveMinZ_PreservesEndpoints()
    {
        // Flat terrain → the natural deck is ~level at 100. A mid-span min-Z of 105 must lift the deck via
        // a smooth interior arch (no grade clamp), while the abutments stay at ~100 with ~level grade.
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));
        var bridge = BridgeSections(network, 2);
        var s0 = bridge[0].DistanceAlongSpline;
        var length = bridge[^1].DistanceAlongSpline - s0;
        const float minZ = 105f;

        var constraints = new[] { new BridgeProfileSolver.BridgeInteriorConstraint(2, s0 + length / 2f, minZ) };

        var result = BridgeProfileSolver.RefineSpans(network, interiorConstraints: constraints, log: false);
        var app = Assert.Single(result.Applications);

        Assert.True(app.Applied);
        Assert.True(app.InteriorLiftMeters > 0f, $"expected an interior lift, got {app.InteriorLiftMeters:F2}");

        // G0 preserved: the arch is zero-valued at both abutments.
        Assert.Equal(100f, bridge[0].TargetElevation, precision: 1);
        Assert.Equal(100f, bridge[^1].TargetElevation, precision: 1);

        // G1 preserved: the arch is zero-SLOPE at both abutments, so endpoint grade stays ~level.
        var startSlope = (bridge[1].TargetElevation - bridge[0].TargetElevation) /
                         (bridge[1].DistanceAlongSpline - bridge[0].DistanceAlongSpline);
        Assert.True(MathF.Abs(startSlope) < 0.05f, $"endpoint grade not preserved: {startSlope:F3}");

        // The deck nearest the constraint station clears MinZ.
        var atMid = bridge.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - s0 - length / 2f)).First();
        Assert.True(atMid.TargetElevation >= minZ - 0.1f, $"deck at mid {atMid.TargetElevation:F2} < {minZ}");
    }

    [Fact]
    public void RefineSpans_InteriorConstraintAlreadyCleared_NoLift()
    {
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));
        var bridge = BridgeSections(network, 2);
        var s0 = bridge[0].DistanceAlongSpline;
        var length = bridge[^1].DistanceAlongSpline - s0;

        // Natural deck ~100; a min-Z of 95 is already satisfied → no arch added.
        var constraints = new[] { new BridgeProfileSolver.BridgeInteriorConstraint(2, s0 + length / 2f, 95f) };

        var result = BridgeProfileSolver.RefineSpans(network, interiorConstraints: constraints, log: false);
        Assert.Equal(0f, Assert.Single(result.Applications).InteriorLiftMeters);
    }

    [Fact]
    public void RefineSpans_NullConstraints_NoInteriorLift_RegressionGuard()
    {
        var network = BuildRoadBridgeRoad(RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 50f, 110f));
        var result = BridgeProfileSolver.RefineSpans(network, log: false);
        Assert.Equal(0f, Assert.Single(result.Applications).InteriorLiftMeters);
    }

    private static float CubicHermite(float s, float length, float z0, float z1, float g0, float g1)
    {
        var t = s / length;
        var t2 = t * t;
        var t3 = t2 * t;
        var h00 = 2f * t3 - 3f * t2 + 1f;
        var h10 = t3 - 2f * t2 + t;
        var h01 = -2f * t3 + 3f * t2;
        var h11 = t3 - t2;
        return h00 * z0 + h10 * (length * g0) + h01 * z1 + h11 * (length * g1);
    }

    [Fact]
    public void EstimateForwardGrade_RisingSections_ReturnsPositiveSlope()
    {
        var sections = new List<UnifiedCrossSection>();
        for (var i = 0; i < 20; i++)
            sections.Add(new UnifiedCrossSection
            {
                LocalIndex = i,
                DistanceAlongSpline = i,      // 1 m spacing
                TargetElevation = 100f + i    // 100% would be 1 m per 1 m; use +1m/m → grade 1.0
            });

        var atStart = BridgeProfileSolver.EstimateForwardGrade(sections, atStart: true, 10f);
        var atEnd = BridgeProfileSolver.EstimateForwardGrade(sections, atStart: false, 10f);

        Assert.True(Math.Abs(atStart - 1f) < 1e-3f, $"start grade {atStart}");
        Assert.True(Math.Abs(atEnd - 1f) < 1e-3f, $"end grade {atEnd}");
    }
}
