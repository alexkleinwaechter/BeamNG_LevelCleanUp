using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Amendment 03 — sparse floor constraints (render #5 "crumpled roads"): with
///     <c>EnableSparseDeckConstraints</c> the planner authors constraint VALUES, never geometry. No deck
///     pins, no approach-ramp pins, no dip wells — the smoother treats the corridor as pure road; the span
///     is re-curved from the SOLVED approaches by <see cref="BridgeProfileSolver.RefineSpans"/> with the
///     plan's typed budgets as interior FLOORS (arch lift only when the natural curve is short — overshoot
///     allowed); dips return to the post-solve resolver well against the final deck Z.
/// </summary>
public class BridgeSparseFloorConstraintTests
{
    // Corridor (50,150)→(450,150), span [100,200]; optional under-road crossing at corridor station 150.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, StructureSegment span)
        BuildScenario(float zLeft, float zRight, BridgeRuleSystemOptions rules,
            float underZ = float.NaN, int underPriority = 50)
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

        ParameterizedRoadSpline? under = null;
        if (!float.IsNaN(underZ))
        {
            under = RoadNetworkTestHelpers.CreateParameterizedSpline(
                2, new(200, 100), new(200, 200), priority: underPriority); // crosses at span station 150
            under.Layer = 0;
        }

        var network = under != null
            ? RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under)
            : RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);

        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = zLeft + (zRight - zLeft) * (cs.DistanceAlongSpline / 400f);
        if (under != null)
            foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
                cs.TargetElevation = underZ;

        return (network, corridor, span);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    private static void TagSpanSections(UnifiedRoadNetwork network, int splineId, StructureSegment seg)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            if (cs.DistanceAlongSpline < seg.StartDistance || cs.DistanceAlongSpline > seg.EndDistance)
                continue;
            cs.StructureSpanId = seg.SpanId;
            cs.IsExcluded = true;
        }
    }

    // ── Planner: hump-shaped soft pins under sparse mode (v3 — "give the bridge cross-sections") ───────

    [Fact]
    public void SparseMode_RaisedSpan_EmitsUniformLiftPins()
    {
        // The veto scenario (under-road at 8 outranks the corridor → clearance target 13 at the crossing
        // station ≈150, chord there ≈8.5 → lift ≈4.5). Doc 05 §4.1 (render #10 user law "equally raised,
        // equally distributed cross-sections"): pins = chord + ONE uniform lift on EVERY section — the
        // span-wide lift survives the box filter where the old 30–150 m humps were diluted to 20–30 %.
        var (network, _, _) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableGradedDeck = true, EnableSparseDeckConstraints = true },
            underZ: 8f, underPriority: 20000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.NotEmpty(span.Pins);

        var pins = span.Pins.OrderBy(p => p.DistanceAlongSpline).ToList();
        var lift = pins[0].SoftRiseMeters;
        Assert.Equal(4.5f, lift, 0.2f);
        Assert.All(pins, p => Assert.Equal(lift, p.SoftRiseMeters, 0.001f)); // uniform — no hump shape

        // The crossing station meets its target; the ends carry chord + the SAME lift.
        var atCrossing = pins.OrderBy(p => MathF.Abs(p.DistanceAlongSpline - 150f)).First();
        Assert.Equal(13f, atCrossing.DeckZ, 0.2f);
        Assert.Equal(13.5f, pins.First().DeckZ, 0.25f);
        Assert.Equal(12.5f, pins.Last().DeckZ, 0.25f);
    }

    [Fact]
    public void ApplySoftShapingToRaw_AnchorsChordToApproaches_RiverRawReplaced()
    {
        // The span's raw filter input is the RIVER (50 m below the road) — soft shaping must replace it
        // with the boundary-anchored chord (+ rise), making the span raw continuous with the road.
        var (network, corridor, span) = BuildScenario(zLeft: 100f, zRight: 100f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        TagSpanSections(network, corridor.SplineId, span);
        var cs = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();

        var raw = new float[cs.Count];
        for (var i = 0; i < cs.Count; i++)
        {
            var inSpan = cs[i].StructureSpanId >= 0;
            raw[i] = inSpan ? 50f : 100f; // river under the span
            if (inSpan)
                cs[i].SoftDeckRiseMeters = 6f * Hump(cs[i].DistanceAlongSpline, 150f, 50f);
        }

        OptimizedElevationSmoother.ApplySoftShapingToRaw(cs, raw);

        var first = cs.FindIndex(c => c.StructureSpanId >= 0);
        var last = cs.FindLastIndex(c => c.StructureSpanId >= 0);
        Assert.Equal(100f, raw[first], 0.1f); // boundary-anchored chord, river gone
        Assert.Equal(100f, raw[last], 0.1f);
        var center = cs.FindIndex(c => MathF.Abs(c.DistanceAlongSpline - 150f) < 0.5f);
        Assert.Equal(106f, raw[center], 0.2f); // chord + full rise at the hump peak
    }

    private static float Hump(float d, float station, float runOut)
    {
        var u = MathF.Abs(d - station) / runOut;
        if (u >= 1f) return 0f;
        return (1f - u) * (1f - u) * (1f + 2f * u);
    }

    [Fact]
    public void ChainSmoothing_SoftEndRise_ConvergesToRampedApproach_NoSeamStep()
    {
        // A near-abutment crossing (394's t=0.86 case): the rise is SUSTAINED toward the span end. The
        // filter + re-anchoring iterations must turn it into a real ramp spilling into the approach —
        // and the output must be continuous everywhere (soft shaping is never hard-held, so no step).
        var (network, corridor, span) = BuildScenario(zLeft: 100f, zRight: 100f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        TagSpanSections(network, corridor.SplineId, span);
        var cs = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
            c.SoftDeckRiseMeters = 3f * Hump(c.DistanceAlongSpline, 200f, 60f); // peak at the END abutment
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 100f);

        var smoother = new OptimizedElevationSmoother();
        smoother.CalculateChainElevations(cs, corridor.Parameters, hm, 1f);
        smoother.ReSmoothChainFromExistingElevations(cs, corridor.Parameters);
        smoother.ReSmoothChainFromExistingElevations(cs, corridor.Parameters);

        // The deck end region is lifted and the approach beyond it climbs with it (the ramp).
        var endDeck = cs.First(c => MathF.Abs(c.DistanceAlongSpline - 199f) < 0.5f);
        var beyond = cs.First(c => MathF.Abs(c.DistanceAlongSpline - 210f) < 0.5f);
        Assert.True(endDeck.TargetElevation > 101f,
            $"deck end must be lifted, got {endDeck.TargetElevation:F2}");
        Assert.True(beyond.TargetElevation > 100.5f,
            $"approach must ramp with the deck, got {beyond.TargetElevation:F2}");

        // Continuity EVERYWHERE — the property hard holds could never give us.
        var ordered = cs.OrderBy(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var step = MathF.Abs(ordered[i].TargetElevation - ordered[i - 1].TargetElevation);
            Assert.True(step < 0.25f,
                $"discontinuity {step:F2}m at station {ordered[i].DistanceAlongSpline:F1}");
        }
    }

    // ── Soft approach ramps: the feather builds the climb in RAW input, never hard-held ────────────────

    [Fact]
    public void FeatherRawApproachRamps_BuildsEasedClimb_StopsAtJunctionPins()
    {
        // 300-section flat chain at 100, pinned run [150,200] at 105, a junction pin at index 60 at 100.
        var (network, corridor, _) = BuildScenario(zLeft: 100f, zRight: 100f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        var cs = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        var raw = new float[cs.Count];
        for (var i = 0; i < raw.Length; i++) raw[i] = 100f;
        for (var i = 150; i <= 200 && i < cs.Count; i++)
        {
            cs[i].PinnedElevation = 105f;
            raw[i] = 105f;
        }

        cs[60].PinnedElevation = 100f; // junction pin on the approach
        OptimizedElevationSmoother.FeatherRawApproachRamps(cs, raw, 1f);

        // Eased climb on the approach: just outside the abutment ≈ deck Z, decaying away from it.
        Assert.Equal(105f, raw[149], 0.2f);
        Assert.True(raw[130] is > 101f and < 105f, $"mid-ramp should be partially lifted, got {raw[130]:F2}");
        for (var i = 100; i < 149; i++)
            Assert.True(raw[i + 1] >= raw[i] - 0.01f, $"climb must be monotone at {i}");

        // The far end of the chain (beyond the ramp) is untouched; the junction pin's raw stays its own,
        // and the ramp from the run start at 150 (length = 5/0.05 = 100 samples → down to 50) STOPS there.
        Assert.Equal(100f, raw[60], 0.001f); // junction pin never overwritten
        Assert.Equal(100f, raw[55], 0.001f); // beyond the junction pin the ramp is cut off
        Assert.Equal(100f, raw[320], 0.001f); // beyond the far ramp end (200 + 100 samples) — untouched
    }

    [Fact]
    public void ChainSmoothing_SparseMode_ApproachClimbsToTheDeck()
    {
        // End-to-end through the real filter: flat terrain 100, span [100,200] pinned at 110. Without the
        // feather the approach reaches ≈ (110+100)/2 = 105 at the abutment (the doc-16 §3b half-way stop);
        // with it the climb carries the road to the deck. Iterations re-base on the solved profile and
        // converge the residual step away.
        var (network, corridor, span) = BuildScenario(zLeft: 100f, zRight: 100f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        TagSpanSections(network, corridor.SplineId, span);
        var cs = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
            c.PinnedElevation = 110f;
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 100f);

        var smoother = new OptimizedElevationSmoother();
        smoother.CalculateChainElevations(cs, corridor.Parameters, hm, 1f);
        smoother.ReSmoothChainFromExistingElevations(cs, corridor.Parameters);
        smoother.ReSmoothChainFromExistingElevations(cs, corridor.Parameters);

        // Deck held.
        Assert.All(cs.Where(c => c.StructureSpanId >= 0), c => Assert.Equal(110f, c.TargetElevation, 0.01f));

        // The approach ARRIVES at the deck: residual abutment step well under the §3b half-delta (5 m).
        var lastApproach = cs.Last(c => c.StructureSpanId < 0 && c.DistanceAlongSpline < 100f);
        Assert.True(110f - lastApproach.TargetElevation < 1.0f,
            $"approach must climb to the deck, residual step {110f - lastApproach.TargetElevation:F2}m");

        // And it is a monotone climb, not a sawtooth (the render-#5 crumple guarantee).
        var approach = cs.Where(c => c.DistanceAlongSpline is > 20f and < 100f)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < approach.Count; i++)
            Assert.True(approach[i].TargetElevation >= approach[i - 1].TargetElevation - 0.02f,
                $"sawtooth at station {approach[i].DistanceAlongSpline:F1}");
    }

    // ── PlanFloorConstraints: plan → interior floors ───────────────────────────────────────────────────

    [Fact]
    public void PlanFloorConstraints_RoadCrossing_FloorFromFinalLowerZ()
    {
        var (network, _, _) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true },
            underZ: 8f, underPriority: 20000);

        network.BridgeElevationPlan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        // The lower road's FINAL solved Z moved after planning (estimate 8 → final 7.5): the floor must
        // read the final surface, not the planner-time estimate.
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.TargetElevation = 7.5f;

        var floors = GradeSeparationResolver.PlanFloorConstraints(network, log: false);

        var floor = Assert.Single(floors);
        Assert.Equal(1, floor.BridgeSplineId);
        Assert.Equal(150f, floor.DistanceAlongSpline, 5f); // the crossing's upper station (detector XY ≈ midpoint)
        // Legacy budget C=5 (typing off, no deck profile → thickness offset 0): floor = 7.5 + 5.
        Assert.Equal(12.5f, floor.MinZ, 0.1f);
    }

    [Fact]
    public void PlanFloorConstraints_SplitSubtractsDipShare()
    {
        var (network, _, _) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true },
            underZ: 8f);

        var crossing = network.GradeSeparatedCrossings.Single();
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = crossing,
                    Action = BridgeElevationAction.Split,
                    RequiredSeparationMeters = 6f,
                    DipDepthMeters = 1.5f,
                },
            ],
        };

        var floors = GradeSeparationResolver.PlanFloorConstraints(network, log: false);

        // The resolver dips the 1.5 m share against the final deck afterwards — the deck only owes the rest.
        var floor = Assert.Single(floors);
        Assert.Equal(8f + 6f - 1.5f, floor.MinZ, 0.1f);
    }

    [Fact]
    public void PlanFloorConstraints_PureDip_NoFloor()
    {
        var (network, _, _) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true },
            underZ: 8f);

        var crossing = network.GradeSeparatedCrossings.Single();
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = crossing,
                    Action = BridgeElevationAction.DipLowerRoad,
                    RequiredSeparationMeters = 6f,
                    DipDepthMeters = 3f,
                },
            ],
        };

        Assert.Empty(GradeSeparationResolver.PlanFloorConstraints(network, log: false));
    }

    [Fact]
    public void PlanFloorConstraints_SyntheticRail_UsesObstacleEstimate()
    {
        var (network, _, _) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });

        var rail = new GradeSeparatedCrossing
        {
            UpperSplineId = 1,
            LowerSplineId = -1, // synthetic — no road spline to read
            CrossingXY = new Vector2(200, 150),
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 10000,
            LowerPriority = 0,
            UpperIsBridge = true,
            LowerIsBridge = false,
            LowerKind = BridgeObstacleKind.Rail,
        };
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = rail,
                    Action = BridgeElevationAction.RaiseBridgeVeto,
                    RequiredSeparationMeters = 6f,
                    ObstacleZEstimate = 9f,
                },
            ],
        };

        var floor = Assert.Single(GradeSeparationResolver.PlanFloorConstraints(network, log: false));
        Assert.Equal(15f, floor.MinZ, 0.1f);
    }

    [Fact]
    public void PlanFloorConstraints_NearAbutment_Skipped()
    {
        // Crossing at corridor station 105 → span fraction t = 0.05 → arch shape ≈ 0.03 < 0.25: enforcing
        // it would hump the whole deck — skipped with a warning (end deficits are approach territory).
        var (network, _, span) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true },
            underZ: 8f);

        var crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = 1,
            LowerSplineId = 2,
            CrossingXY = new Vector2(155, 150), // station 105, just inside the abutment
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 10000,
            LowerPriority = 20000,
            UpperIsBridge = true,
            LowerIsBridge = false,
        };
        network.BridgeElevationPlan = new BridgeElevationPlan
        {
            Spans =
            [
                new SpanDeckPlan
                {
                    OwnerSplineId = 1,
                    SpanId = span.SpanId,
                    StartDistance = 100f,
                    EndDistance = 200f,
                    Layer = 1,
                    RequiredDeckZ = 13f,
                    IsRaised = true,
                    ApproachZLeft = 9f,
                    ApproachZRight = 8f,
                    ClearanceUsed = 5f,
                },
            ],
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = crossing,
                    Action = BridgeElevationAction.RaiseBridgeVeto,
                    RequiredSeparationMeters = 6f,
                },
            ],
        };

        Assert.Empty(GradeSeparationResolver.PlanFloorConstraints(network, log: false));
    }

    // ── RefineSpans: floors enforced with overshoot semantics ──────────────────────────────────────────

    [Fact]
    public void RefineSpans_FloorAboveCurve_ArchLiftsTheDeck()
    {
        // Solved corridor grade 10 → 6 (curve through the span ≈ 9.0 → 8.0). A floor at mid-span demands
        // 13: the deck arches up to it; the approaches are NEVER touched.
        var (network, corridor, span) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        TagSpanSections(network, corridor.SplineId, span);

        var floors = new[]
        {
            new BridgeProfileSolver.BridgeInteriorConstraint(corridor.SplineId, 150f, 13f),
        };
        var result = BridgeProfileSolver.RefineSpans(network, interiorConstraints: floors, log: false);

        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        Assert.True(app.InteriorLiftMeters > 3f,
            $"expected an arch lift over the floor, got {app.InteriorLiftMeters:F2}");

        var mid = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 150f)).First();
        Assert.True(mid.TargetElevation >= 13f - 0.1f,
            $"deck must clear the floor at the crossing, got {mid.TargetElevation:F2}");

        // The approaches keep their solved profile (no ramp pins, no overrides outside the span).
        var approach = network.GetCrossSectionsForSpline(corridor.SplineId)
            .First(c => MathF.Abs(c.DistanceAlongSpline - 50f) < 0.5f);
        Assert.Equal(10f + (6f - 10f) * (50f / 400f), approach.TargetElevation, 0.05f);
    }

    [Fact]
    public void RefineSpans_FloorBelowCurve_OvershootUntouched()
    {
        // The natural curve already clears the floor with room to spare — nothing moves ("and if we
        // overshoot constraints, why not").
        var (network, corridor, span) = BuildScenario(zLeft: 10f, zRight: 6f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true });
        TagSpanSections(network, corridor.SplineId, span);

        var floors = new[]
        {
            new BridgeProfileSolver.BridgeInteriorConstraint(corridor.SplineId, 150f, 5f),
        };
        var result = BridgeProfileSolver.RefineSpans(network, interiorConstraints: floors, log: false);

        var app = Assert.Single(result.Applications);
        Assert.Equal(0f, app.InteriorLiftMeters, 0.001f);

        var mid = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 150f)).First();
        Assert.Equal(8.5f, mid.TargetElevation, 0.15f); // the road's own curve, not the floor
    }

    // ── Resolver: under SPARSE mode the dip is delivered by the PRE-smooth pin (doc 04 §8.3 B, 2026-06-11),
    //    exactly like dip-as-pin — so the resolver is VERIFY-ONLY and never re-carves the dip post-solve
    //    (the harsh dip-edge step). Supersedes the old "sparse overrides dip-as-pin, resolver dips actively":
    //    sparse no longer routes dips through the late carve; it pins them pre-smooth so the smoother solves
    //    the well continuously. ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SparseMode_DipsViaPreSmoothPin_ResolverIsVerifyOnly()
    {
        // Sparse on (dip-as-pin flag OFF): B routes the dip through ApplyLowerRoadDipPins with no second
        // flag. Deck at 13 over a road at 9 with C=5 → dip 1 m, pinned pre-smooth as an eased well (bottom
        // 9 − 1 = 8); the post-solve resolver must NOT drop the road profile a second time.
        var (network, _, _) = BuildScenario(zLeft: 13f, zRight: 13f,
            new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true },
            underZ: 9f);

        var crossing = network.GradeSeparatedCrossings.Single();
        var plan = new BridgeElevationPlan
        {
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = crossing,
                    Action = BridgeElevationAction.DipLowerRoad,
                    RequiredSeparationMeters = 5f,
                    DipDepthMeters = 1f,
                },
            ],
        };
        network.BridgeElevationPlan = plan;

        // 1) Sparse mode now EMITS the pre-smooth dip pin (the mechanism B adds), well bottom at target 8.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 9f);
        var pinned = UnifiedRoadSmoother.ApplyLowerRoadDipPins(network, plan, null, hm, 1f);
        Assert.True(pinned > 0, "sparse mode must emit pre-smooth dip pins (doc 04 §8.3 B)");

        var lowerMid = network.GetCrossSectionsForSpline(2)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 50f)).First();
        Assert.True(lowerMid.PinnedElevation.HasValue, "the crossing section must be pinned");
        Assert.Equal(8f, lowerMid.PinnedElevation!.Value, 0.001f);

        // 2) The post-solve resolver is verify-only in sparse mode now — it records the dip but must NEVER
        //    actively re-drop TargetElevation (that late re-carve is what stepped the dip edges).
        GradeSeparationResolver.ApplyLowerRoadDips(network, log: false);
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);
        Assert.True(lowerMid.TargetElevation > 9f - 0.001f,
            $"sparse is now verify-only — the resolver must NOT re-dip the profile, got {lowerMid.TargetElevation:F2}");
    }
}
