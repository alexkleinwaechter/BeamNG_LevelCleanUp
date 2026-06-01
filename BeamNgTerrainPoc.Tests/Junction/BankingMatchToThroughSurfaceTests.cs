using System.Collections.Generic;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     §4 no-blend banking match. On the affine ThroughRoad path nothing warps a terminating road's
///     banking onto a banked/sloped through road (the old blender did this; it's gated off) — leaving a
///     visible cross-slope twist where the flat side road meets the tilted main road (node 282534733).
///     <c>MatchTerminatingBankingToThroughSurface</c> projects each terminating cross-section near the
///     junction onto the through road's tilted surface plane over a runoff zone
///     (= SurfaceWidth × BankingRunoffSurfaceWidthMultiplier), weight smoothstep-decaying from 1 at the
///     junction to 0 at the zone end.
/// </summary>
public class BankingMatchToThroughSurfaceTests
{
    private static ParameterizedRoadSpline MakeSpline(int id, float runoffMultiplier = 3f) => new()
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
                BankingRunoffSurfaceWidthMultiplier = runoffMultiplier
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
    ///     Through road runs E-W (tangent +x, normal +y), FLAT bank but sloped +0.1 longitudinally
    ///     (rises 0.1/m toward +x). A flat terminating road runs N-S (tangent +y, normal +x = right) and
    ///     ends at the junction (50,50). Because the terminating road's edges spread along the through
    ///     tangent (x), projecting them onto the through plane gives right edge HIGHER than left by
    ///     width×slope → terminating endpoint bank should become asin(slope).
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection termEnd)
        BuildSlopedThroughFlatTerminating(float runoffMultiplier = 3f)
    {
        var through = MakeSpline(1, runoffMultiplier);
        var terminating = MakeSpline(2, runoffMultiplier);

        // Through: E-W, width 6, flat bank, Z = 100 + 0.1*(x-50) → slope +0.1 along +x.
        var tW = new Vector2(1f, 0f);
        var tN = new Vector2(0f, 1f);
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), tW, tN, 99f, 0f, 6f);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), tW, tN, 100f, 10f, 6f);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), tW, tN, 101f, 20f, 6f);

        // Terminating: N-S, width 4, flat (bank 0). Endpoint at the junction (50,50).
        var sW = new Vector2(0f, 1f);
        var sN = new Vector2(1f, 0f); // right = +x
        var termFar = Cs(2, 0, 200, new Vector2(50f, 40f), sW, sN, 100f, 0f, 4f);
        var termEnd = Cs(2, 1, 201, new Vector2(50f, 50f), sW, sN, 100f, 10f, 4f);

        var network = new UnifiedRoadNetwork();
        network.AddSpline(through);
        network.AddSpline(terminating);
        foreach (var cs in new[] { t0, tMid, t2, termFar, termEnd })
            network.AddCrossSection(cs);

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
            CrossSection = termEnd, Spline = terminating, IsSplineStart = false, IsSplineEnd = true // terminating
        });
        network.Junctions.Add(junction);

        return (network, termEnd);
    }

    private static (UnifiedRoadNetwork network, UnifiedCrossSection termEnd)
        BuildSlopedThroughFlatTerminatingAs(JunctionType type, bool excluded)
    {
        var (network, termEnd) = BuildSlopedThroughFlatTerminating();
        network.Junctions[0].Type = type;
        network.Junctions[0].IsExcluded = excluded;
        return (network, termEnd);
    }

    [Fact]
    public void Roundabout_NowBankMatched_EvenWhenExcluded()
    {
        // The ring (through) is sloped; the connector (terminating) must pick up the bank at the seam,
        // exactly like a T-junction. Was skipped before approach A.
        var (network, termEnd) = BuildSlopedThroughFlatTerminatingAs(JunctionType.Roundabout, excluded: true);

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.Equal(MathF.Asin(0.1f), termEnd.BankAngleRadians, 3); // bank matched
        Assert.Equal(100f, termEnd.TargetElevation, 3);              // centerline untouched (§4 invariant)
    }

    [Fact]
    public void TerminatingEndpoint_AdoptsBankFromThroughLongitudinalSlope()
    {
        var (network, termEnd) = BuildSlopedThroughFlatTerminating();

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        // Right edge of the terminating road sits at through +x (higher), left at -x (lower); the bank
        // that places its edges on the through plane is asin(slope) = asin(0.1) ≈ 0.10017 rad.
        Assert.Equal(MathF.Asin(0.1f), termEnd.BankAngleRadians, 3);
    }

    /// <summary>
    ///     Same sloped through road; terminating road has cross-sections at the given distances FROM the
    ///     junction endpoint (descending = far→junction). Returns the terminating CSes in that order so
    ///     tests can assert per-distance behaviour.
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection[] termByDist)
        BuildWithTerminatingDistances(float[] junctionDistsDescending, float runoffMultiplier)
    {
        var through = MakeSpline(1, runoffMultiplier);
        var terminating = MakeSpline(2, runoffMultiplier);

        var tW = new Vector2(1f, 0f);
        var tN = new Vector2(0f, 1f);
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), tW, tN, 99f, 0f, 6f);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), tW, tN, 100f, 10f, 6f);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), tW, tN, 101f, 20f, 6f);

        var sW = new Vector2(0f, 1f);
        var sN = new Vector2(1f, 0f);
        var maxJd = junctionDistsDescending[0];
        var term = new UnifiedCrossSection[junctionDistsDescending.Length];
        for (var i = 0; i < junctionDistsDescending.Length; i++)
        {
            var jd = junctionDistsDescending[i];
            // DistanceAlongSpline increases toward the junction so the endpoint (jd=0) is the LAST CS.
            term[i] = Cs(2, i, 200 + i, new Vector2(50f, 50f - jd), sW, sN, 100f, maxJd - jd, 4f);
        }

        var network = new UnifiedRoadNetwork();
        network.AddSpline(through);
        network.AddSpline(terminating);
        foreach (var cs in new[] { t0, tMid, t2 }) network.AddCrossSection(cs);
        foreach (var cs in term) network.AddCrossSection(cs);

        var junction = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.TJunction, Position = new Vector2(50f, 50f)
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = tMid, Spline = through, IsSplineStart = false, IsSplineEnd = false
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = term[^1], Spline = terminating, IsSplineStart = false, IsSplineEnd = true
        });
        network.Junctions.Add(junction);

        return (network, term);
    }

    [Fact]
    public void Banking_DecaysOverRunoffZone_ZoneEndUnchanged()
    {
        // runoff = SurfaceWidth(4) × mult(3) = 12 m. CSes at junction-distances 14, 12, 6, 0.
        var (network, term) = BuildWithTerminatingDistances(new[] { 14f, 12f, 6f, 0f }, runoffMultiplier: 3f);
        var atEndpoint = term[3]; // jdist 0
        var atHalf = term[2];     // jdist 6  → weight 0.5
        var atZoneEnd = term[1];  // jdist 12 → weight 0 (boundary)
        var beyond = term[0];     // jdist 14 → outside runoff

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.Equal(MathF.Asin(0.1f), atEndpoint.BankAngleRadians, 3);   // full match
        Assert.Equal(MathF.Asin(0.05f), atHalf.BankAngleRadians, 3);      // smoothstep 0.5 → half the spread
        Assert.Equal(0f, atZoneEnd.BankAngleRadians, 3);                  // weight 0 → natural, untouched
        Assert.Equal(0f, beyond.BankAngleRadians, 3);                     // outside runoff → untouched
    }

    [Fact]
    public void Multiplier_ControlsRunoffLength()
    {
        // mult 2 × SurfaceWidth 4 = 8 m runoff. A CS at jdist 10 is now OUTSIDE the zone (would be inside at mult 3).
        var (network, term) = BuildWithTerminatingDistances(new[] { 10f, 4f, 0f }, runoffMultiplier: 2f);
        var inside = term[1];  // jdist 4  → within 8 m
        var outside = term[0]; // jdist 10 → beyond 8 m

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.NotEqual(0f, inside.BankAngleRadians); // warped
        Assert.Equal(0f, outside.BankAngleRadians, 3); // untouched — proves mult=2 bounded the zone
    }

    [Fact]
    public void NoThroughRoad_JunctionSkipped()
    {
        // Y-junction: both roads terminate, no continuous through surface to match → no banking change.
        var a = MakeSpline(1);
        var b = MakeSpline(2);
        var sW = new Vector2(0f, 1f);
        var sN = new Vector2(1f, 0f);
        var aEnd = Cs(1, 0, 100, new Vector2(50f, 50f), sW, sN, 100f, 0f, 4f);
        var bEnd = Cs(2, 0, 200, new Vector2(50f, 50f), sW, sN, 100f, 0f, 4f);

        var network = new UnifiedRoadNetwork();
        network.AddSpline(a);
        network.AddSpline(b);
        network.AddCrossSection(aEnd);
        network.AddCrossSection(bEnd);

        var junction = new NetworkJunction { JunctionId = 1, Type = JunctionType.YJunction, Position = new Vector2(50f, 50f) };
        junction.Contributors.Add(new JunctionContributor { CrossSection = aEnd, Spline = a, IsSplineStart = true, IsSplineEnd = false });
        junction.Contributors.Add(new JunctionContributor { CrossSection = bEnd, Spline = b, IsSplineStart = true, IsSplineEnd = false });
        network.Junctions.Add(junction);

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.Equal(0f, aEnd.BankAngleRadians, 3);
        Assert.Equal(0f, bEnd.BankAngleRadians, 3);
    }

    /// <summary>
    ///     Builds a BANKED (not sloped) through road: E-W (tangent +x, normal +y), flat longitudinally
    ///     (all Z = 100) but tilted laterally by <paramref name="throughBank" /> rad. A flat terminating
    ///     road runs N-S (tangent +y, normal +x) and ends at the junction (50,50). Terminating
    ///     cross-sections sit at the given junction-distances (descending = far→junction), all at Z = 100.
    ///     Because the terminating road runs along the through NORMAL, the through road's banking projects
    ///     onto the terminating road's CENTERLINE — the axis the old §4 dragged into a blend-zone curve.
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection[] termByDist)
        BuildBankedThroughFlatTerminating(float[] junctionDistsDescending, float throughBank)
    {
        var through = MakeSpline(1);
        var terminating = MakeSpline(2);

        var tW = new Vector2(1f, 0f);
        var tN = new Vector2(0f, 1f);
        // Flat longitudinally (Z=100 everywhere) but banked laterally by `throughBank`.
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), tW, tN, 100f, 0f, 6f, throughBank);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), tW, tN, 100f, 10f, 6f, throughBank);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), tW, tN, 100f, 20f, 6f, throughBank);

        var sW = new Vector2(0f, 1f);
        var sN = new Vector2(1f, 0f);
        var maxJd = junctionDistsDescending[0];
        var term = new UnifiedCrossSection[junctionDistsDescending.Length];
        for (var i = 0; i < junctionDistsDescending.Length; i++)
        {
            var jd = junctionDistsDescending[i];
            term[i] = Cs(2, i, 200 + i, new Vector2(50f, 50f - jd), sW, sN, 100f, maxJd - jd, 4f);
        }

        var network = new UnifiedRoadNetwork();
        network.AddSpline(through);
        network.AddSpline(terminating);
        foreach (var cs in new[] { t0, tMid, t2 }) network.AddCrossSection(cs);
        foreach (var cs in term) network.AddCrossSection(cs);

        var junction = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.TJunction, Position = new Vector2(50f, 50f)
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = tMid, Spline = through, IsSplineStart = false, IsSplineEnd = false
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = term[^1], Spline = terminating, IsSplineStart = false, IsSplineEnd = true
        });
        network.Junctions.Add(junction);

        return (network, term);
    }

    /// <summary>
    ///     §4 must add banking ONLY — never drag the terminating centerline. The through road's banking,
    ///     projected onto a terminating road that runs along the through normal, lands on the terminating
    ///     <em>centerline</em>; the old code wrote it into <c>TargetElevation</c> over the runoff zone,
    ///     re-creating exactly the hermite/parabolic blend-zone elevation curve the no-blend branch removes.
    ///     The centerline must stay at its §3-settled value (here 100 m) at every cross-section in the zone.
    ///     (RED pre-fix: the mid-zone centerline read 99.85 m.)
    /// </summary>
    [Fact]
    public void BankedThroughRoad_DoesNotMoveTerminatingCenterline()
    {
        // runoff = SurfaceWidth(4) × mult(3) = 12 m. Interior CSes at jdist 12, 6, 0 are all in/at the zone.
        var (network, term) = BuildBankedThroughFlatTerminating(
            new[] { 12f, 6f, 0f }, throughBank: MathF.Asin(0.1f));
        var atEndpoint = term[2]; // jdist 0 → weight 1
        var atHalf = term[1];     // jdist 6 → weight 0.5 (where the old curve was worst)

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        // Centerline left flush at §3's value — §4 must NOT re-introduce a longitudinal blend curve.
        Assert.Equal(100f, atEndpoint.TargetElevation, 3);
        Assert.Equal(100f, atHalf.TargetElevation, 3);
    }

    /// <summary>
    ///     Companion to <see cref="BankedThroughRoad_DoesNotMoveTerminatingCenterline" />: dropping the
    ///     centerline write must NOT cost the twist. A SLOPED through road still imparts banking to the
    ///     terminating endpoint (bank = asin(slope)) while its centerline stays flush.
    /// </summary>
    [Fact]
    public void SlopedThroughRoad_KeepsTwist_WithFlushCenterline()
    {
        var (network, termEnd) = BuildSlopedThroughFlatTerminating();

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.Equal(MathF.Asin(0.1f), termEnd.BankAngleRadians, 3); // twist preserved
        Assert.Equal(100f, termEnd.TargetElevation, 3);              // centerline untouched
    }
}
