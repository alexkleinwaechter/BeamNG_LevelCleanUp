using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 05 — pre-smooth junction room widening. A junction is an agreement point; instead of late
///     ramps/wells stopping short of it (the render-#11 11.4 % [STEEP] squeeze / boxed spans / §8
///     squeezed dip wells), the agreement is changed BEFORE smoothing: junctions inside a raised span's
///     approach-ramp run are re-pinned UP to the ramp line
///     (<see cref="UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps"/>), junctions inside a dip well
///     are re-pinned DOWN to the well line (sparse <see cref="UnifiedRoadSmoother.ApplyLowerRoadDipPins"/>),
///     and the pin-respecting blender solves ALL contributor roads to the new height. A raise always wins
///     over a lower (§4.4).
/// </summary>
public class BridgeJunctionRoomWideningTests
{
    // Corridor (50,150)→(450,150) = 400 m, span [100,200], flat solved Z; "primary" ⇒ 5 % ramps.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, StructureSegment span)
        BuildCorridor(float roadZ = 10f)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableGradedDeck = true, EnableSparseDeckConstraints = true
        };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = roadZ;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
            }
        }

        return (network, corridor, span);
    }

    /// <summary>A raised-span plan whose pins hold a uniform deck at <paramref name="deckZ"/>.</summary>
    private static BridgeElevationPlan RaisedPlan(
        UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, StructureSegment span, float deckZ)
    {
        var pins = network.GetCrossSectionsForSpline(corridor.SplineId)
            .Where(c => c.StructureSpanId == span.SpanId)
            .OrderBy(c => c.DistanceAlongSpline)
            .Select(c => new DeckPin(c, c.Index, c.DistanceAlongSpline, deckZ, deckZ - c.TargetElevation))
            .ToList();
        return new BridgeElevationPlan
        {
            Spans =
            [
                new SpanDeckPlan
                {
                    OwnerSplineId = corridor.SplineId,
                    SpanId = span.SpanId,
                    StartDistance = span.StartDistance,
                    EndDistance = span.EndDistance,
                    Layer = 1,
                    RequiredDeckZ = deckZ,
                    IsRaised = true,
                    ApproachZLeft = 10f,
                    ApproachZRight = 10f,
                    ClearanceUsed = 5f,
                    Pins = pins,
                },
            ],
        };
    }

    private static NetworkJunction AddJunction(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station,
        int id, JunctionType type = JunctionType.TJunction)
    {
        var cs = network.GetCrossSectionsForSpline(spline.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();
        var junction = new NetworkJunction { JunctionId = id, Type = type, Position = cs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = cs, Spline = spline });
        network.Junctions.Add(junction);
        return junction;
    }

    private static float[,] FlatMap => RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

    // ── §4.2 junction RAISE along the approach ramp line ───────────────────────────────────────────────

    [Fact]
    public void JunctionInsideRampRun_RePinnedUpToRampLine()
    {
        // Deck end 14.7, natural 10 → delta 4.7, ramp LengthFor(4.7, 5 %) ≈ 125 m. Junction 20 m past the
        // abutment sits on the crest curve: ramp line there = 10 + 4.7·w(20/125.3) ≈ 14.38 — the junction
        // agreement moves UP, IsPinned makes it stick.
        var (network, corridor, span) = BuildCorridor();
        var inside = AddJunction(network, corridor, station: 220f, id: 1);
        var beyond = AddJunction(network, corridor, station: 340f, id: 2); // 140 m out — past the 125 m run

        var (count, raised) = UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f);

        Assert.Equal(1, count);
        Assert.True(inside.IsPinned);
        Assert.Contains(inside, raised);
        Assert.Equal(14.38f, inside.HarmonizedElevation, 0.1f);
        Assert.False(beyond.IsPinned);
        Assert.True(float.IsNaN(beyond.HarmonizedElevation));

        // 2026-07-14: the agreement Z is stamped on the corridor section so the smoother's approach
        // feather anchors to the SAME line the blender enforces (no post-blend notch).
        var insideCs = inside.Contributors.Single().CrossSection;
        Assert.True(insideCs.JunctionPinnedElevation.HasValue);
        Assert.Equal(14.38f, insideCs.JunctionPinnedElevation!.Value, 0.1f);
        Assert.Null(beyond.Contributors.Single().CrossSection.JunctionPinnedElevation);
    }

    [Fact]
    public void JunctionAtTheAbutment_RisesToDeckHeight()
    {
        // The 199 bridgehead case: a junction right at the span end rises to ≈ deck-end Z (w≈1), so the
        // side road is solved up to the bridgehead instead of boxing the raise forever.
        var (network, corridor, span) = BuildCorridor();
        var bridgehead = AddJunction(network, corridor, station: 201f, id: 1);

        UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f);

        Assert.True(bridgehead.IsPinned);
        Assert.Equal(14.7f, bridgehead.HarmonizedElevation, 0.1f);
    }

    [Fact]
    public void RoundaboutJunction_NeverRePinned()
    {
        var (network, corridor, span) = BuildCorridor();
        var ring = AddJunction(network, corridor, station: 220f, id: 1, JunctionType.Roundabout);

        UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f);

        Assert.False(ring.IsPinned);
        Assert.True(float.IsNaN(ring.HarmonizedElevation));
    }

    [Fact]
    public void ExistingHigherPin_NeverLowered()
    {
        // A Phase-1.9-pinned junction already ABOVE the ramp line keeps its Z (max semantics, only raises).
        var (network, corridor, span) = BuildCorridor();
        var high = AddJunction(network, corridor, station: 220f, id: 1);
        high.HarmonizedElevation = 16f;
        high.IsPinned = true;

        var (count, _) = UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f);

        Assert.Equal(0, count);
        Assert.Equal(16f, high.HarmonizedElevation, 0.001f);
    }

    // ── §4.3 junction LOWER along the dip well (the §8 mirror) ─────────────────────────────────────────

    // Lower road 2: (200,100)→(200,200), 100 m, solved at 9; crossing under the corridor at its station 50.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline lower, BridgeElevationPlan plan)
        BuildDipScenario()
    {
        var (network, corridor, _) = BuildCorridor(roadZ: 13f);
        var lower = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(200, 100), new(200, 200));
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, lower);
        foreach (var cs in network.GetCrossSectionsForSpline(lower.SplineId))
            cs.TargetElevation = 9f;

        var crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = corridor.SplineId,
            LowerSplineId = lower.SplineId,
            CrossingXY = new Vector2(200, 150),
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 10000,
            LowerPriority = 50,
            UpperIsBridge = true,
            LowerIsBridge = false,
        };
        var plan = new BridgeElevationPlan
        {
            Crossings =
            [
                new CrossingPlan
                {
                    Crossing = crossing,
                    Action = BridgeElevationAction.DipLowerRoad,
                    RequiredSeparationMeters = 5f,
                    DipDepthMeters = 1.5f,
                    LowerRoadTargetZ = 7.5f,
                },
            ],
        };
        return (network, lower, plan);
    }

    [Fact]
    public void DipWell_JunctionInsideDesiredWell_LoweredAndWellExtends()
    {
        // Junction 20 m from the crossing clamps the legacy well to 18 m; sparse widening instead keeps
        // the full (way-end-limited) 50 m well, re-pins the junction DOWN to the well line
        // (9 − 1.5·w(20/50) ≈ 8.03) and pins sections beyond the old 18 m clamp.
        var (network, lower, plan) = BuildDipScenario();
        var junction = AddJunction(network, lower, station: 70f, id: 7);

        var pinned = UnifiedRoadSmoother.ApplyLowerRoadDipPins(
            network, plan, null, FlatMap, 1f, widenRoomViaJunctions: true);

        Assert.True(pinned > 0);
        Assert.True(junction.IsPinned);
        Assert.Equal(8.03f, junction.HarmonizedElevation, 0.1f);

        var at75 = network.GetCrossSectionsForSpline(lower.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 75f)).First();
        Assert.True(at75.PinnedElevation.HasValue,
            "the well must extend past the old junction clamp (18 m) once the junction is re-pinned");
    }

    [Fact]
    public void DipWell_LegacyFlag_KeepsJunctionClamp()
    {
        // Dip pins without sparse widening keep the old junction-safe clamp — no silent behaviour change.
        var (network, lower, plan) = BuildDipScenario();
        var junction = AddJunction(network, lower, station: 70f, id: 7);

        UnifiedRoadSmoother.ApplyLowerRoadDipPins(network, plan, null, FlatMap, 1f);

        Assert.False(junction.IsPinned);
        var at75 = network.GetCrossSectionsForSpline(lower.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 75f)).First();
        Assert.False(at75.PinnedElevation.HasValue, "legacy well stays clamped to the junction room");
    }

    [Fact]
    public void DipWell_RaisedJunction_NeverLowered()
    {
        // §4.4: a junction the bridge raised for an approach ramp keeps its raised Z — the dip's residual
        // is reconciled post-solve instead.
        var (network, lower, plan) = BuildDipScenario();
        var junction = AddJunction(network, lower, station: 70f, id: 7);
        junction.HarmonizedElevation = 14f;
        junction.IsPinned = true;

        UnifiedRoadSmoother.ApplyLowerRoadDipPins(
            network, plan, null, FlatMap, 1f,
            widenRoomViaJunctions: true, raisedJunctions: [junction]);

        Assert.Equal(14f, junction.HarmonizedElevation, 0.001f);
    }
}
