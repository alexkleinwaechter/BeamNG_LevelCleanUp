using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     §3 no-blend ordering fix (root cause of the 282534707 screenshot). After the affine/iteration
///     loop settles, the through roads are at their FINAL Z, but each T-junction's
///     <see cref="NetworkJunction.HarmonizedElevation" /> and each terminating road's affine target
///     were last computed from the through road's PRE-affine Z (one iteration stale). That stale gap
///     is both the junction-fill BUMP (HarmonizedElevation too high) and the seam STEP (terminating
///     endpoint too high). <c>RetargetTerminatingRoadsToSettledThrough</c> recomputes both from the
///     SETTLED through-road Z and re-levels the terminating roads to meet it. Through roads are never
///     re-moved.
/// </summary>
public class RetargetTerminatingToSettledThroughTests
{
    private static ParameterizedRoadSpline MakeSpline(int id) => new()
    {
        SplineId = id,
        Priority = 5,
        MaterialName = "test_asphalt",
        Spline = new RoadSpline(
            new List<Vector2> { new(0f, 0f), new(1f, 0f) },
            SplineInterpolationType.LinearControlPoints),
        Parameters = new RoadSmoothingParameters()
    };

    private static UnifiedCrossSection Cs(
        int splineId, int localIndex, int index, Vector2 center, float elev, float dist) => new()
    {
        OwnerSplineId = splineId,
        LocalIndex = localIndex,
        Index = index,
        CenterPoint = center,
        TangentDirection = new Vector2(1f, 0f),
        NormalDirection = new Vector2(0f, 1f),
        TargetElevation = elev,
        DistanceAlongSpline = dist
    };

    /// <summary>
    ///     Builds a T-junction where the through road (spline 1) has SETTLED to 154.83, but the
    ///     terminating road's junction endpoint (spline 2) and the junction's HarmonizedElevation
    ///     are still at the stale pre-affine value 155.57.
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection termEnd, UnifiedCrossSection termFar)
        BuildStaleTJunction(JunctionType type = JunctionType.TJunction)
    {
        var through = MakeSpline(1);
        var terminating = MakeSpline(2);

        // Through road: flat at the settled Z, 3 CSes; the middle one is the junction crossing.
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), 154.83f, 0f);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), 154.83f, 10f);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), 154.83f, 20f);

        // Terminating road: rises from 150 (far end) to the STALE 155.57 at its junction end.
        var termFar = Cs(2, 0, 200, new Vector2(50f, 30f), 150.00f, 0f);
        var termEnd = Cs(2, 1, 201, new Vector2(50f, 49f), 155.57f, 20f);

        var network = new UnifiedRoadNetwork();
        network.AddSpline(through);
        network.AddSpline(terminating);
        foreach (var cs in new[] { t0, tMid, t2, termFar, termEnd })
            network.AddCrossSection(cs);

        var junction = new NetworkJunction
        {
            JunctionId = 1,
            Type = type,
            Position = new Vector2(50f, 50f),
            HarmonizedElevation = 155.57f // stale pre-affine snapshot
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = tMid, Spline = through, IsSplineStart = false, IsSplineEnd = false // continuous
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = termEnd, Spline = terminating, IsSplineStart = false, IsSplineEnd = true // terminating
        });
        network.Junctions.Add(junction);

        return (network, termEnd, termFar);
    }

    [Fact]
    public void HarmonizedElevation_RecomputedFromSettledThroughZ_ClosesBump()
    {
        var (network, _, _) = BuildStaleTJunction();

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        // The junction-fill disk must sit on the through road's SETTLED Z, not the stale 155.57.
        Assert.Equal(154.83f, network.Junctions[0].HarmonizedElevation, 2);
    }

    [Fact]
    public void TerminatingEndpoint_ReleveledToSettledThroughZ_ClosesStep()
    {
        var (network, termEnd, termFar) = BuildStaleTJunction();

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        // Junction endpoint meets the through road flush; the free far end is untouched (affine decay).
        Assert.Equal(154.83f, termEnd.TargetElevation, 2);
        Assert.Equal(150.00f, termFar.TargetElevation, 2);
    }

    [Fact]
    public void ThroughRoad_NeverMoved()
    {
        var (network, _, _) = BuildStaleTJunction();

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        foreach (var cs in network.GetCrossSectionsForSpline(1))
            Assert.Equal(154.83f, cs.TargetElevation, 2);
    }

    /// <summary>
    ///     The hard case from the 152336→154838 logs: spline A is the THROUGH road at J1 but is itself a
    ///     TERMINATING road at J0 (its own start), so re-leveling it to J0 tilts its body and drops the J1
    ///     crossing — invalidating a single-pass harmonized/target computed from A's pre-tilt Z. The pass must
    ///     ITERATE until A settles, so J1's seam stays flush. (Without iteration: residual ~2.4 m here, the
    ///     real-log +0.29 m.)
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection aMid, UnifiedCrossSection bEnd, NetworkJunction j1)
        BuildChainedThroughTerminating()
    {
        var a = MakeSpline(1); // through at J1, terminating at J0 (start)
        var b = MakeSpline(2); // terminating at J1 (end)
        var c = MakeSpline(3); // through at J0, dictates J0 Z = 150

        // A: flat at 154.83 over 200 m; mid (dist 100) is the J1 crossing, start (dist 0) ends at J0.
        var aStart = Cs(1, 0, 100, new Vector2(0f, 50f), 154.83f, 0f);
        var aMid = Cs(1, 1, 101, new Vector2(100f, 50f), 154.83f, 100f);
        var aEnd = Cs(1, 2, 102, new Vector2(200f, 50f), 154.83f, 200f);

        var bFar = Cs(2, 0, 200, new Vector2(100f, 30f), 150.00f, 0f);
        var bEnd = Cs(2, 1, 201, new Vector2(100f, 49f), 155.57f, 20f);

        var cMid = Cs(3, 1, 301, new Vector2(0f, 50f), 150.00f, 100f); // continuous through J0 at 150

        var network = new UnifiedRoadNetwork();
        network.AddSpline(a);
        network.AddSpline(b);
        network.AddSpline(c);
        foreach (var cs in new[] { aStart, aMid, aEnd, bFar, bEnd, cMid })
            network.AddCrossSection(cs);

        var j0 = new NetworkJunction { JunctionId = 0, Type = JunctionType.TJunction, Position = new Vector2(0f, 50f) };
        j0.Contributors.Add(new JunctionContributor { CrossSection = aStart, Spline = a, IsSplineStart = true, IsSplineEnd = false });
        j0.Contributors.Add(new JunctionContributor { CrossSection = cMid, Spline = c, IsSplineStart = false, IsSplineEnd = false });

        var j1 = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.TJunction, Position = new Vector2(100f, 50f), HarmonizedElevation = 155.57f
        };
        j1.Contributors.Add(new JunctionContributor { CrossSection = aMid, Spline = a, IsSplineStart = false, IsSplineEnd = false });
        j1.Contributors.Add(new JunctionContributor { CrossSection = bEnd, Spline = b, IsSplineStart = false, IsSplineEnd = true });

        network.Junctions.Add(j0);
        network.Junctions.Add(j1);

        return (network, aMid, bEnd, j1);
    }

    [Fact]
    public void ThroughRoadAlsoTerminatingElsewhere_IteratesUntilSeamFlush()
    {
        var (network, aMid, bEnd, j1) = BuildChainedThroughTerminating();

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        // After A settles to its own J0 target, J1's harmonized Z and B's endpoint must both equal A's
        // FINAL crossing Z — the seam is flush no matter how far A sank.
        Assert.Equal(aMid.TargetElevation, bEnd.TargetElevation, 2);
        Assert.Equal(aMid.TargetElevation, j1.HarmonizedElevation, 2);
    }

    [Fact]
    public void Roundabout_NowProcessed_RetargetsConnectorToRingZ()
    {
        // Roundabout junction = ring (through) + connector (terminating), modelled exactly like a T.
        // Approach A: it must now be retargeted like any T-junction (was previously skipped).
        var (network, termEnd, termFar) = BuildStaleTJunction(JunctionType.Roundabout);

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(154.83f, network.Junctions[0].HarmonizedElevation, 2); // pinned to ring (through) Z
        Assert.Equal(154.83f, termEnd.TargetElevation, 2);                   // connector ring-end meets ring
        Assert.Equal(150.00f, termFar.TargetElevation, 2);                   // far end untouched (affine decay)
    }

    [Fact]
    public void Roundabout_ProcessedEvenWhenExcluded()
    {
        // Real roundabout parent junctions carry IsExcluded=true (set by RoundaboutElevationHarmonizer).
        // The §3 gate must let Roundabout through despite IsExcluded.
        var (network, termEnd, _) = BuildStaleTJunction(JunctionType.Roundabout);
        network.Junctions[0].IsExcluded = true;

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(154.83f, termEnd.TargetElevation, 2); // still retargeted to ring Z
    }
}
