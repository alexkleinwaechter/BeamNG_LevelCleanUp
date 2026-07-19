using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Solution A (doc 04 §4.A, render #10 amendment) — post-solve approach-raise ramps, the upward
///     mirror of the lower-road dip well. Where a sparse-mode span is still short of its typed budget at
///     a crossing OUTSIDE the floor band (the skipped near-abutment floors: rail/water can never dip, the
///     filter dilutes the soft humps), <see cref="GradeSeparationResolver.ApplyApproachRaiseRamps"/>
///     raises the WHOLE span by ONE uniform delta — every deck cross-section equally, never a local hump
///     (the render-#10 lesson) — and eases the lifted abutments back to the solved road on BOTH connected
///     approaches (§3.3-class-slope run, junction-clamped; a clamped run gets STEEP, never short — no
///     grade clamp per standing feedback).
/// </summary>
public class BridgeApproachRaiseRampTests
{
    // Corridor (50,150)→(450,150) = 400 m, span [100,200]; "primary" ⇒ normal 5 % / absolute 6 % ramps.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, StructureSegment span)
        BuildScenario(BridgeRuleSystemOptions rules, float roadZ = 10f)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = rules.WithTestClearance();

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = roadZ;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.StructureSpanType = span.Type;
                cs.IsExcluded = true;
            }
        }

        return (network, corridor, span);
    }

    private static BridgeRuleSystemOptions Sparse() => new()
    {
        EnableGradedDeck = true, EnableSparseDeckConstraints = true
    };

    /// <summary>A synthetic water crossing under the span at corridor station <paramref name="station"/>
    /// (world x = 50 + station) — water can never be dipped, the 394 binding case.</summary>
    private static GradeSeparatedCrossing WaterCrossing(float station) => new()
    {
        UpperSplineId = 1,
        LowerSplineId = -1, // synthetic — no road spline below
        CrossingXY = new Vector2(50f + station, 150f),
        UpperLayer = 1,
        LowerLayer = 0,
        UpperPriority = 10000,
        LowerPriority = 0,
        UpperIsBridge = true,
        LowerIsBridge = false,
        LowerKind = BridgeObstacleKind.Water,
        LowerNavigable = true,
    };

    private static CrossingPlan CrossingPlanFor(
        GradeSeparatedCrossing crossing,
        BridgeElevationAction action = BridgeElevationAction.RaiseBridgeVeto,
        float required = 6.7f, float obstacleZ = 8f, float lowerRoadTargetZ = float.NaN) => new()
    {
        Crossing = crossing,
        Action = action,
        RequiredSeparationMeters = required,
        ObstacleZEstimate = obstacleZ,
        LowerRoadTargetZ = lowerRoadTargetZ,
    };

    private static BridgeElevationPlan PlanWith(StructureSegment span, params CrossingPlan[] crossings) => new()
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
                RequiredDeckZ = 10f,
                IsRaised = false,
                ApproachZLeft = 10f,
                ApproachZRight = 10f,
                ClearanceUsed = 5f,
            },
        ],
        Crossings = crossings,
    };

    private static UnifiedCrossSection At(UnifiedRoadNetwork network, int splineId, float station) =>
        network.GetCrossSectionsForSpline(splineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    // ── The binding case: end-of-span water crossing, nothing else can lift the deck end ───────────────

    [Fact]
    public void EndCrossingShort_RaisesWholeSpanUniformly()
    {
        // Water at station 190 (t=0.9, arch shape 0.13 < 0.25 → its floor was SKIPPED), surface 8,
        // navigable budget 6.7, deck at 10 → clearance 2.0, deficit 4.7. The WHOLE deck rises 4.7 —
        // equally distributed cross-sections, no hump — and BOTH approaches ramp ("primary" 5 % → 94 m).
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.Equal(14.7f, At(network, 1, 190f).TargetElevation, 0.1f); // crossing: budget met
        Assert.Equal(14.7f, At(network, 1, 150f).TargetElevation, 0.1f); // mid-span: SAME raise (no hump)
        Assert.Equal(14.7f, At(network, 1, 100f).TargetElevation, 0.1f); // far abutment: SAME raise
        Assert.Equal(14.7f, At(network, 1, 200f).TargetElevation, 0.1f); // near abutment: SAME raise

        var endApproach = At(network, 1, 210f).TargetElevation;          // end approach eases down
        Assert.True(endApproach is > 13f and < 14.7f, $"end approach must ramp, got {endApproach:F2}");
        var startApproach = At(network, 1, 90f).TargetElevation;         // start approach eases down too
        Assert.True(startApproach is > 13f and < 14.7f, $"start approach must ramp, got {startApproach:F2}");

        Assert.Equal(10f, At(network, 1, 300f).TargetElevation, 0.05f);  // beyond the 94 m end run
        Assert.Equal(10f, At(network, 1, 5f).TargetElevation, 0.05f);    // beyond the 94 m start run
    }

    [Fact]
    public void RaiseIsC1_NoStepAnywhere()
    {
        // The render-arc law: anything late-stamped can step — unless it is C1 by construction. Adjacent
        // 1 m sections may never jump (max ramp grade ≈ 1.5·4.7/94 ≈ 0.075 m/m).
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        var ordered = network.GetCrossSectionsForSpline(1).OrderBy(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var step = MathF.Abs(ordered[i].TargetElevation - ordered[i - 1].TargetElevation);
            Assert.True(step < 0.2f,
                $"step {step:F2}m at station {ordered[i].DistanceAlongSpline:F1} — the ramp must be C1");
        }
    }

    [Fact]
    public void WorstDeficitGovernsTheUniformRaise()
    {
        // Start crossing short 1.0, end crossing short 4.7 → ONE uniform raise of 4.7 (the worst);
        // the smaller crossing simply overshoots ("if we overshoot constraints, why not").
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span,
            CrossingPlanFor(WaterCrossing(110f), obstacleZ: 4.3f),  // clearance 5.7 → deficit 1.0
            CrossingPlanFor(WaterCrossing(190f)));                  // clearance 2.0 → deficit 4.7

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.Equal(14.7f, At(network, 1, 110f).TargetElevation, 0.1f);
        Assert.Equal(14.7f, At(network, 1, 190f).TargetElevation, 0.1f);
        Assert.Equal(14.7f, At(network, 1, 150f).TargetElevation, 0.1f);
    }

    [Fact]
    public void BankedEdges_FollowTheRaisedCenterline()
    {
        var (network, _, span) = BuildScenario(Sparse());
        var banked = At(network, 1, 195f);
        banked.BankAngleRadians = 0.1f;
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        var delta = banked.EffectiveRoadWidth / 2f * MathF.Sin(0.1f);
        Assert.Equal(banked.TargetElevation - delta, banked.LeftEdgeElevation, 0.001f);
        Assert.Equal(banked.TargetElevation + delta, banked.RightEdgeElevation, 0.001f);
    }

    // ── Gates: who is NOT ramped ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MidSpanCrossing_InsideFloorBand_NotRamped()
    {
        // t=0.5 → arch shape 1.0 ≥ 0.25: the RefineSpans interior floor owns it — no raise.
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(150f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.All(network.GetCrossSectionsForSpline(1), c => Assert.Equal(10f, c.TargetElevation, 0.001f));
    }

    [Fact]
    public void PureDipCrossing_NeverRamped()
    {
        // A planned dip moves the lower road (pre-smooth pin + A7 carve) — the deck stays put.
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f),
            action: BridgeElevationAction.DipLowerRoad, lowerRoadTargetZ: 8f));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.All(network.GetCrossSectionsForSpline(1), c => Assert.Equal(10f, c.TargetElevation, 0.001f));
    }

    [Fact]
    public void CrossingAlreadyClearing_NotRamped()
    {
        var (network, _, span) = BuildScenario(Sparse(), roadZ: 16f); // clearance 8 ≥ 6.7
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.All(network.GetCrossSectionsForSpline(1), c => Assert.Equal(16f, c.TargetElevation, 0.001f));
    }

    [Fact]
    public void SparseModeOff_NoOp()
    {
        // Flag-off byte-identical: without EnableSparseDeckConstraints the pass must not touch anything.
        var (network, _, span) = BuildScenario(new BridgeRuleSystemOptions());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.All(network.GetCrossSectionsForSpline(1), c => Assert.Equal(10f, c.TargetElevation, 0.001f));
    }

    // ── Junction handling (the junction-in-sag analogue + no-grade-clamp feedback) ─────────────────────

    [Fact]
    public void JunctionInsideRampLength_RampGetsSteep_RaiseStaysFull()
    {
        // Junction at station 220 → end-approach room 220−200−2 = 18 m. No grade clamp: the deck must
        // clear, so the FULL 4.7 m raise stays and the 18 m ramp simply gets steep (warned in the log).
        var (network, corridor, span) = BuildScenario(Sparse());
        var junctionCs = At(network, 1, 220f);
        var junction = new NetworkJunction { Position = junctionCs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = junctionCs, Spline = corridor });
        network.Junctions.Add(junction);
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.Equal(14.7f, At(network, 1, 200f).TargetElevation, 0.1f); // full raise despite the clamp
        Assert.Equal(10f, junctionCs.TargetElevation, 0.05f);            // the junction itself untouched
        Assert.Equal(10f, At(network, 1, 219f).TargetElevation, 0.1f);   // ramp ends inside the margin
    }

    [Fact]
    public void JunctionAtTheAbutment_NoRoom_SpanSkipped()
    {
        // A junction within the 2 m margin of a connected abutment: ANY raise would step that seam —
        // the whole span raise is skipped (logged), nothing moves.
        var (network, corridor, span) = BuildScenario(Sparse());
        var junctionCs = At(network, 1, 201f);
        var junction = new NetworkJunction { Position = junctionCs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = junctionCs, Spline = corridor });
        network.Junctions.Add(junction);
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        Assert.All(network.GetCrossSectionsForSpline(1), c => Assert.Equal(10f, c.TargetElevation, 0.001f));
    }

    // ── Heightmap fill (the embankment) + span snapshot refresh ────────────────────────────────────────

    [Fact]
    public void HeightmapFill_RaisesApproachSurface_NeverUnderTheDeck()
    {
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, hm, 1f, log: false);

        // Approach centerline cell (station 210 → world 260,150): filled to follow the raised road.
        var raisedRoad = At(network, 1, 210f).TargetElevation;
        Assert.Equal(raisedRoad, hm[150, 260], 0.15f);
        // Under the span (station 150 → world 200,150) the terrain is NOT filled — the deck is a bridge.
        Assert.Equal(10f, hm[150, 200], 0.001f);
    }

    [Fact]
    public void HeightmapFill_SkipsCellsOwnedByAnotherSpline()
    {
        // The embankment fill raises terrain under the raised approach — but it must not bury a NEIGHBOURING
        // road's protected surface caught in that footprint. Cells owned by a different spline stay at terrain
        // level; the bridge's own approach + bare terrain still fill up.
        var (network, _, span) = BuildScenario(Sparse());
        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var owner = new int[512, 512];
        for (var y = 0; y < 512; y++)
        for (var x = 0; x < 512; x++)
            owner[y, x] = RoadSurfaceOwnerRaster.NoOwner;
        owner[150, 260] = 99; // foreign road surface in the approach embankment footprint (station 210)

        GradeSeparationResolver.ApplyApproachRaiseRamps(network, hm, 1f, log: false, roadSurfaceOwner: owner);

        Assert.Equal(10f, hm[150, 260], 0.05f); // foreign cell preserved, not filled up
        Assert.True(hm[150, 261] > 12f, $"self/terrain cell must still fill, got {hm[150, 261]:F2}");
    }

    [Fact]
    public void SpanSnapshot_RecapturedAfterRamp()
    {
        // The deck mesh / excavator read the snapshot: after the uniform raise, the snapshot must carry
        // the raised elevations at BOTH ends (doc 04 §4.A watch-out).
        var (network, _, span) = BuildScenario(Sparse());
        BridgeProfileSolver.RefineSpans(network, log: false); // captures the pre-ramp snapshot
        var before = Assert.Single(network.BridgeSpans);
        Assert.Equal(10f, before.Stations.Last().CenterZ, 0.1f);

        network.BridgeElevationPlan = PlanWith(span, CrossingPlanFor(WaterCrossing(190f)));
        GradeSeparationResolver.ApplyApproachRaiseRamps(network, log: false);

        var after = Assert.Single(network.BridgeSpans);
        Assert.Equal(span.SpanId, after.SpanId);
        Assert.Equal(14.7f, after.Stations.First().CenterZ, 0.15f);
        Assert.Equal(14.7f, after.Stations.Last().CenterZ, 0.15f);
    }
}
