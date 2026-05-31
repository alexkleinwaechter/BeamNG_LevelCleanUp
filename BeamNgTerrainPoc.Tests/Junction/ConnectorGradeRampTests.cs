using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     No-blend connector grade ramp. After §3 (flush centerline) + §4 (banking match), a steep
///     terminating connector still meets the through road at a grade discontinuity (kink) at the seam.
///     <c>EaseConnectorGradeToThroughSurface</c> fits a local end-weld curve on the connector
///     centerline over a short ramp zone: tangent to the through surface at the seam (grade <c>g_seam</c>),
///     then welded back onto the unchanged connector body at the zone end. Seam Z, far-junction Z, and the
///     body beyond the weld stay fixed.
///
///     Geometry used throughout: a FLAT-graded through road runs E-W (tangent +x, normal +y) BANKED by
///     <c>throughBank</c>; a connector runs N-S (tangent +y) and TERMINATES at the junction (50,50) as its
///     spline start (body extends +y, away from the junction). For this perpendicular T the directional
///     derivative of the through plane along the connector's into-body tangent (+y) is
///     <c>g_seam = sin(throughBank)</c> (the through SLOPE term projects out; only the BANK term remains).
/// </summary>
public class ConnectorGradeRampTests
{
    private static ParameterizedRoadSpline MakeSpline(int id, float rampLen) => new()
    {
        SplineId = id,
        Priority = 5,
        MaterialName = "test_asphalt",
        Spline = new RoadSpline(
            new List<Vector2> { new(0f, 0f), new(1f, 0f) },
            SplineInterpolationType.LinearControlPoints),
        Parameters = new RoadSmoothingParameters
        {
            JunctionHarmonizationParameters = new JunctionHarmonizationParameters
            {
                ConnectorGradeRampLengthMeters = rampLen
            }
        }
    };

    private static UnifiedCrossSection Cs(
        int splineId, int localIndex, int index, Vector2 center, Vector2 tangent, Vector2 normal,
        float elev, float dist, float width, float bank = 0f) => new()
    {
        OwnerSplineId = splineId,
        LocalIndex = localIndex,
        Index = index,
        CenterPoint = center,
        TangentDirection = tangent,
        NormalDirection = normal,
        TargetElevation = elev,
        DistanceAlongSpline = dist,
        EffectiveRoadWidth = width,
        SurfaceWidth = width,
        BankAngleRadians = bank
    };

    /// <summary>
    ///     Through road E-W banked by <paramref name="throughBank" /> (flat longitudinal grade, Z=100).
    ///     Connector N-S terminating at (50,50) as its spline START; its body runs +y with cross-sections at
    ///     the given <paramref name="connectorSDists" /> (distance from the seam, ascending — seam first),
    ///     natural elevation = 100 + <paramref name="gNatural" />·s. Returns the connector CSes ordered by s.
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection[] connector)
        Build(float gNatural, float throughBank, float rampLen, float[] connectorSDists)
    {
        var through = MakeSpline(1, rampLen);
        var connector = MakeSpline(2, rampLen);

        var tW = new Vector2(1f, 0f); // through tangent +x
        var tN = new Vector2(0f, 1f); // through normal +y
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), tW, tN, 100f, 0f, 6f, throughBank);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), tW, tN, 100f, 10f, 6f, throughBank);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), tW, tN, 100f, 20f, 6f, throughBank);

        var cW = new Vector2(0f, 1f); // connector tangent +y (into-body = +y at the seam)
        var cN = new Vector2(1f, 0f); // connector normal +x
        var connectorCs = new UnifiedCrossSection[connectorSDists.Length];
        for (var i = 0; i < connectorSDists.Length; i++)
        {
            var s = connectorSDists[i];
            connectorCs[i] = Cs(
                2, i, 200 + i, new Vector2(50f, 50f + s), cW, cN, 100f + gNatural * s, s, 4f);
        }

        var network = new UnifiedRoadNetwork();
        network.AddSpline(through);
        network.AddSpline(connector);
        foreach (var cs in new[] { t0, tMid, t2 }) network.AddCrossSection(cs);
        foreach (var cs in connectorCs) network.AddCrossSection(cs);

        var junction = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.TJunction, Position = new Vector2(50f, 50f)
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = tMid, Spline = through, IsSplineStart = false, IsSplineEnd = false // continuous
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = connectorCs[0], Spline = connector, IsSplineStart = true, IsSplineEnd = false // terminating
        });
        network.Junctions.Add(junction);

        return (network, connectorCs);
    }

    /// <summary>Secant grade dz/ds between two connector cross-sections.</summary>
    private static float Secant(UnifiedCrossSection a, UnifiedCrossSection b) =>
        (b.TargetElevation - a.TargetElevation) / (b.DistanceAlongSpline - a.DistanceAlongSpline);

    /// <summary>
    ///     Asserts the connector was actually eased toward the through surface: the near-seam grade moved
    ///     from the connector's natural grade toward <c>g_seam</c>. Fails on an un-eased (straight) connector.
    /// </summary>
    private static void AssertEasedTowardSeamGrade(UnifiedCrossSection seam, UnifiedCrossSection next,
        float gSeam, float gNatural)
    {
        var seamSecant = Secant(seam, next);
        Assert.True(MathF.Abs(seamSecant - gSeam) < MathF.Abs(seamSecant - gNatural),
            $"near-seam grade {seamSecant} should be eased toward g_seam {gSeam}, away from natural {gNatural}");
    }

    /// <summary>
    ///     Test 1 — TANGENT AT SEAM (the "plain"/co-planar connection) + seam elevation unchanged (§3
    ///     invariant). A flat connector (g_natural=0) meeting a banked through (g_seam=sin(bank)=0.2) must,
    ///     after easing, leave the seam Z exactly at 100 and enter the connector at a grade ≈ g_seam (eased
    ///     toward the through surface, NOT at its old flat grade).
    /// </summary>
    [Fact]
    public void TangentAtSeam_GradeMatchesThroughSurface_SeamElevationUnchanged()
    {
        var gSeam = MathF.Sin(MathF.Asin(0.2f)); // 0.2 by construction
        // Dense near-seam sampling so the first secant is close to the seam tangent.
        var (network, c) = Build(
            gNatural: 0f, throughBank: MathF.Asin(0.2f), rampLen: 6f,
            connectorSDists: new[] { 0f, 0.1f, 0.5f, 1f, 2f, 3f, 4f, 6f, 8f, 10f, 30f });

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);          // seam Z fixed (§3 invariant)
        var seamSecant = Secant(c[0], c[1]);                  // grade over [0, 0.1]
        Assert.True(MathF.Abs(seamSecant - gSeam) < 0.03f,    // tangent to the through surface
            $"seam grade {seamSecant} should be ≈ g_seam {gSeam}");
        Assert.True(MathF.Abs(seamSecant - gSeam) < MathF.Abs(seamSecant - 0f),
            "seam grade should be eased toward the through surface, not the connector's flat natural grade");
    }

    /// <summary>
    ///     Test 2 — TANGENT AT THE ZONE END (no kink rejoining the body). The grade just inside the zone end
    ///     must equal the grade just outside it (G1 continuity where the local weld hands off to the straight
    ///     body).
    /// </summary>
    [Fact]
    public void TangentAtZoneEnd_NoKinkRejoiningTheBody()
    {
        // Use D=30 so the connector is not short; samples straddle requested L=5 closely.
        var (network, c) = Build(
            gNatural: 0.3f, throughBank: MathF.Asin(0.2f), rampLen: 5f,
            connectorSDists: new[] { 0f, 1f, 4f, 4.5f, 5f, 5.5f, 6f, 12f, 30f });

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        AssertEasedTowardSeamGrade(c[0], c[1], gSeam: 0.2f, gNatural: 0.3f); // easing happened
        var insideZoneEnd = Secant(c[3], c[4]);  // [4.5, 5]
        var outsideZoneEnd = Secant(c[4], c[5]); // [5, 5.5]
        Assert.True(MathF.Abs(insideZoneEnd - outsideZoneEnd) < 0.02f,
            $"grade must be continuous across the zone end: inside {insideZoneEnd} vs outside {outsideZoneEnd}");
    }

    /// <summary>
    ///     Test 3 — LOCAL BODY PRESERVATION. The correction is local and smooth: the body beyond the zone is
    ///     left on the original straight profile.
    /// </summary>
    [Fact]
    public void LocalWeld_BodyBeyondZoneUnchanged()
    {
        var (network, c) = Build(
            gNatural: 0f, throughBank: MathF.Asin(0.2f), rampLen: 6f,
            connectorSDists: new[] { 0f, 1f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 30f });

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        AssertEasedTowardSeamGrade(c[0], c[1], gSeam: 0.2f, gNatural: 0f); // easing happened

        // Body beyond the zone is the original straight line, not a re-tilted body.
        Assert.Equal(100f, c[6].TargetElevation, 4);
        Assert.Equal(100f, c[7].TargetElevation, 4);
        Assert.Equal(100f, c[8].TargetElevation, 4);
        Assert.Equal(Secant(c[6], c[7]), Secant(c[7], c[8]), 4);
    }

    /// <summary>
    ///     Test 4 — HIGHER AND LOWER. The construction is sign-agnostic: a connector steeper UP than the
    ///     seam grade and one steeper DOWN both produce a local weld with the seam, far end, and body fixed.
    /// </summary>
    [Theory]
    [InlineData(0.5f)]  // connector much steeper UP than g_seam (0.2)
    [InlineData(-0.5f)] // connector steeper DOWN than g_seam
    public void HigherAndLower_BothProduceSmoothCurve_EndsFixed(float gNatural)
    {
        var sd = new[] { 0f, 1f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 30f };
        var zB = 100f + gNatural * sd[^1];
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 6f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);   // seam fixed
        Assert.Equal(zB, c[^1].TargetElevation, 3);    // far junction fixed
        AssertEasedTowardSeamGrade(c[0], c[1], gSeam: 0.2f, gNatural: gNatural); // easing happened

        // Body beyond the weld zone is unchanged from the original natural profile.
        Assert.Equal(100f + gNatural * 8f, c[7].TargetElevation, 3);
        Assert.Equal(100f + gNatural * 10f, c[8].TargetElevation, 3);
    }

    /// <summary>
    ///     Test 5 — SHORT CONNECTOR (propagation guard). With a 20 m connector and a large requested ramp
    ///     length, L is clamped to 25% of the connector length so the ramp cannot reach the far junction: the
    ///     far endpoint Z stays exactly put, and cross-sections past the clamped zone end still lie on the
    ///     original straight body.
    /// </summary>
    [Fact]
    public void ShortConnector_RampClampedToFraction_FarJunctionUnmoved()
    {
        // D=20, request rampLen=20 -> clamp to a fraction (<=0.25*20=5). A CS at s=8 is beyond the clamp.
        var sd = new[] { 0f, 0.5f, 2f, 4f, 6f, 8f, 12f, 16f, 20f };
        var gNatural = 0.1f;
        var zB = 100f + gNatural * sd[^1];
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 20f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);  // seam fixed
        Assert.Equal(zB, c[^1].TargetElevation, 3);   // far junction did NOT move (propagation guard held)
        AssertEasedTowardSeamGrade(c[0], c[1], gSeam: 0.2f, gNatural: gNatural); // easing happened

        // The far body is exactly the original natural profile, proving the ramp was clamped short and never
        // marched down to the far junction.
        Assert.Equal(100f + gNatural * 8f, c[5].TargetElevation, 3);
        Assert.Equal(100f + gNatural * 12f, c[6].TargetElevation, 3);
        Assert.Equal(100f + gNatural * 16f, c[7].TargetElevation, 3);
        Assert.Equal(Secant(c[5], c[6]), Secant(c[6], c[7]), 3);
    }
}
