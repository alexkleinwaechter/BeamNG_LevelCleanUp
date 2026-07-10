using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 08 §7 C3 step A — on-deck junction pinning. Junctions whose corridor station lies INSIDE a
///     raised span are invisible to the M1 approach-ramp pass (d &lt; 0 is skipped) and silently inherit
///     deck Z through the blender; <see cref="UnifiedRoadSmoother.PinOnDeckJunctions"/> makes that explicit
///     (logged, §4.4-protected against dip lowering). The side-road containment itself is NOT tested here:
///     two attempts failed on render (doc 08 §7b — pre-smooth estimate-anchored pins oscillate; post-solve
///     lowering + carve shears terrain between adjacent roads) and the preventive in-solver design is
///     pending.
/// </summary>
public class BridgeSideRoadContainmentTests
{
    // Corridor (50,150)→(450,150) = 400 m, span [100,200], flat solved Z 10; "primary" ⇒ 5 % ramps.
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
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station, int id)
    {
        var cs = network.GetCrossSectionsForSpline(spline.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();
        var junction = new NetworkJunction { JunctionId = id, Type = JunctionType.TJunction, Position = cs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = cs, Spline = spline });
        network.Junctions.Add(junction);
        return junction;
    }

    private static float[,] FlatMap => RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

    [Fact]
    public void OnDeckJunction_PinnedToDeckLine_AndJoinsRaisedSet()
    {
        // The winningen #533/#537 case: a junction whose corridor station lies INSIDE the raised span. M1
        // skips it (d < 0); this pass pins it to the deck Z explicitly so the inheritance is deterministic.
        var (network, corridor, span) = BuildCorridor();
        var onDeckJunction = AddJunction(network, corridor, station: 150f, id: 1);
        var raised = new HashSet<NetworkJunction>();

        var onDeck = UnifiedRoadSmoother.PinOnDeckJunctions(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f, raised);

        Assert.Equal(1, onDeck);
        Assert.True(onDeckJunction.IsPinned);
        Assert.Equal(14.7f, onDeckJunction.HarmonizedElevation, 0.05f);
        Assert.Contains(onDeckJunction, raised);
    }

    [Fact]
    public void JunctionOutsideSpan_NotTouchedByOnDeckPass()
    {
        var (network, corridor, span) = BuildCorridor();
        var approach = AddJunction(network, corridor, station: 250f, id: 1); // 50 m past the span end

        var onDeck = UnifiedRoadSmoother.PinOnDeckJunctions(
            network, RaisedPlan(network, corridor, span, 14.7f), null, FlatMap, 1f, []);

        Assert.Equal(0, onDeck);
        Assert.False(approach.IsPinned);
    }

    // ── C3 step B: decayed affine correction at bridge-raised junctions (in-solver containment) ───────

    /// <summary>Corridor (through) solved at 24, side road (terminating, "primary") solved at 10.</summary>
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline side, NetworkJunction junction)
        BuildRaisedTee()
    {
        var (network, corridor, _) = BuildCorridor(roadZ: 24f);

        var side = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(200, 150), new(200, 550));
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, side);
        foreach (var cs in network.GetCrossSectionsForSpline(side.SplineId))
            cs.TargetElevation = 10f;

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
        return (network, side, junction);
    }

    private static UnifiedCrossSection SectionAt(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station) =>
        network.GetCrossSectionsForSpline(spline.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    [Fact]
    public void BridgeRaisedJunction_AffineCorrectionDecays_WithinClassSlopeRun()
    {
        // Through corridor at 24, terminating side road at 10 (+14 error). With the junction in
        // BridgeRaisedJunctions the affine retarget climbs the side road to the junction over the
        // class-slope run (primary 5 % ⇒ 280 m) and leaves the far body on its own profile — instead
        // of tilting all 400 m into an embankment.
        var (network, side, junction) = BuildRaisedTee();
        network.BridgeRaisedJunctions.Add(junction);

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(24f, SectionAt(network, side, 0f).TargetElevation, 0.15f);
        Assert.Equal(17f, SectionAt(network, side, 140f).TargetElevation, 0.5f); // 10 + 14·w(0.5)
        Assert.Equal(10f, SectionAt(network, side, 300f).TargetElevation, 0.05f); // past the 280 m run
        Assert.Equal(10f, SectionAt(network, side, 390f).TargetElevation, 0.05f);
    }

    [Fact]
    public void OrdinaryJunction_KeepsLegacyFullLengthAffine()
    {
        // Same topology WITHOUT the bridge-raised flag: the legacy full-length tilt applies
        // (linear decay to the free end), proving the decay is gated on the raised set only.
        var (network, side, _) = BuildRaisedTee();

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(24f, SectionAt(network, side, 0f).TargetElevation, 0.15f);
        // Legacy: correction = e0·(1−t) → at mid-length (t=0.5) the road still carries +7.
        Assert.Equal(17f, SectionAt(network, side, 200f).TargetElevation, 0.5f);
        Assert.Equal(10f, SectionAt(network, side, 399f).TargetElevation, 0.3f);
    }
}
