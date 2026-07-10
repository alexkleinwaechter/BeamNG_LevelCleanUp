using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     §3 retarget divergence (Manhattan spline 157, +113 m dam — bridge_2101591116 walls, 2026-07-07).
///     A street that terminates at a junction on a deck-raised road, while its own endpoint sections are
///     dip-pinned (it passes under other bridges), is UNLEVELABLE: the affine pin-weight exemption (D6)
///     zeroes the correction at the endpoint, so the endpoint never reaches the junction target and the
///     per-pass error never shrinks. Each of the up-to-8 retarget passes then re-adds the FULL correction
///     onto the unpinned mid-body (Manhattan: 8 × +14.9 m = the +113.75 m hump at s=80), the junction where
///     that street is itself the THROUGH road inherits the runaway Z (junction 249 → 68.16), and the affine
///     targets transplant it onto every terminating side road (splines 156/300: +49.8 m dams).
///     <para>
///         Contract under test: (a) an endpoint whose affine pin weight is ~0 is SKIPPED — the pinned
///         profile is authoritative and the correction would only bulge the body; (b) re-application across
///         retarget passes is ABSOLUTE (from the pass-0 baseline), never cumulative, so a partially
///         pin-locked endpoint yields at most ONE application instead of one per pass.
///     </para>
/// </summary>
public class RetargetPinLockedEndpointTests
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
    ///     Mini-Manhattan: deck road A (flat 160) is the through road at J0. Street B (flat 150, 200 m)
    ///     terminates at J0 and carries a pin near its junction end (dip under a crossing bridge).
    ///     Street C (flat 150) terminates at J1, where B is the through road — J1's Z follows B's body,
    ///     which keeps the retarget loop's convergence measure moving and sustains all 8 passes.
    /// </summary>
    private static (UnifiedRoadNetwork network,
        List<UnifiedCrossSection> b, List<UnifiedCrossSection> c, NetworkJunction j1)
        BuildDeckJunctionWithPinnedStreet(float pinDistanceAlongB)
    {
        var deck = MakeSpline(1);
        var street = MakeSpline(2);
        var side = MakeSpline(3);

        // A: flat deck road at 160, J0 crossing at its middle section.
        var a0 = Cs(1, 0, 100, new Vector2(40f, 50f), 160f, 0f);
        var aMid = Cs(1, 1, 101, new Vector2(50f, 50f), 160f, 10f);
        var a2 = Cs(1, 2, 102, new Vector2(60f, 50f), 160f, 20f);

        // B: flat street at 150 running toward J0; section at dist==pinDistanceAlongB is dip-pinned.
        var b = new List<UnifiedCrossSection>();
        var bDists = new[] { 0f, 50f, 100f, 150f, 180f, 200f };
        for (var i = 0; i < bDists.Length; i++)
        {
            var cs = Cs(2, i, 200 + i, new Vector2(50f, 250f - bDists[i]), 150f, bDists[i]);
            if (System.MathF.Abs(bDists[i] - pinDistanceAlongB) < 0.01f)
                cs.PinnedElevation = 150f;
            b.Add(cs);
        }

        // C: flat side street at 150 terminating at J1 (B's dist-100 section).
        var cFar = Cs(3, 0, 300, new Vector2(30f, 150f), 150f, 0f);
        var cEnd = Cs(3, 1, 301, new Vector2(49f, 150f), 150f, 20f);
        var c = new List<UnifiedCrossSection> { cFar, cEnd };

        var network = new UnifiedRoadNetwork();
        network.AddSpline(deck);
        network.AddSpline(street);
        network.AddSpline(side);
        foreach (var cs in new[] { a0, aMid, a2 }.Concat(b).Concat(c))
            network.AddCrossSection(cs);

        var j0 = new NetworkJunction
        {
            JunctionId = 0,
            Type = JunctionType.TJunction,
            Position = new Vector2(50f, 50f),
            HarmonizedElevation = 150f // stale — through deck road has settled to 160
        };
        j0.Contributors.Add(new JunctionContributor
        {
            CrossSection = aMid, Spline = deck, IsSplineStart = false, IsSplineEnd = false // continuous
        });
        j0.Contributors.Add(new JunctionContributor
        {
            CrossSection = b[^1], Spline = street, IsSplineStart = false, IsSplineEnd = true // terminating
        });

        var j1 = new NetworkJunction
        {
            JunctionId = 1,
            Type = JunctionType.TJunction,
            Position = new Vector2(50f, 150f),
            HarmonizedElevation = 150f
        };
        j1.Contributors.Add(new JunctionContributor
        {
            CrossSection = b[2], Spline = street, IsSplineStart = false, IsSplineEnd = false // continuous
        });
        j1.Contributors.Add(new JunctionContributor
        {
            CrossSection = cEnd, Spline = side, IsSplineStart = false, IsSplineEnd = true // terminating
        });

        network.Junctions.Add(j0);
        network.Junctions.Add(j1);

        return (network, b, c, j1);
    }

    [Fact]
    public void PinLockedEndpoint_CorrectionSkipped_BodyNeverAccumulates()
    {
        // Pin sits ON the junction endpoint (weight 0 there): the target is unreachable, so the whole
        // endpoint correction must be skipped — B keeps its pinned street profile end to end.
        var (_, b, _, _) = RunRetarget(pinDistanceAlongB: 200f, out var network);

        foreach (var cs in b)
            Assert.Equal(150f, cs.TargetElevation, 2);
    }

    [Fact]
    public void PinLockedEndpoint_ThroughJunctionKeepsStreetZ_SideRoadNotDammed()
    {
        // J1 (street B is the through road) must stay at B's real Z, and side street C must not
        // inherit any runaway elevation (Manhattan junction 249 → 68.16 → splines 156/300 +49.8 m).
        var (_, _, c, j1) = RunRetarget(pinDistanceAlongB: 200f, out _);

        Assert.Equal(150f, j1.HarmonizedElevation, 2);
        foreach (var cs in c)
            Assert.Equal(150f, cs.TargetElevation, 2);
    }

    [Fact]
    public void PartiallyPinLockedEndpoint_AppliesAtMostOnce_NotOncePerPass()
    {
        // Pin at dist 180, endpoint at 200 → endpoint weight ≈ 0.5 (not skipped). Re-application across
        // passes must be absolute (baseline + correction), so the body carries at most ONE application:
        // B(dist=100) = 150 + affine ramp (100/200 × e=10) = 155 — not 155 + another 5 m per pass.
        var (_, b, _, _) = RunRetarget(pinDistanceAlongB: 180f, out _);

        var bAt100 = b.First(cs => cs.DistanceAlongSpline == 100f);
        Assert.InRange(bAt100.TargetElevation, 150f, 155.01f);
    }

    private static (UnifiedRoadNetwork network,
        List<UnifiedCrossSection> b, List<UnifiedCrossSection> c, NetworkJunction j1)
        RunRetarget(float pinDistanceAlongB, out UnifiedRoadNetwork networkOut)
    {
        var (network, b, c, j1) = BuildDeckJunctionWithPinnedStreet(pinDistanceAlongB);
        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);
        networkOut = network;
        return (network, b, c, j1);
    }
}
