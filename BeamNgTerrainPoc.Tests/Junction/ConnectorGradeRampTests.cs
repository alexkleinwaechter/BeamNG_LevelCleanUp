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
///     No-blend connector grade weld — seam-aware (2026-07-19 junction-edge-step fix). After §3 (flush
///     centerline at the junction CENTER) and §4 (banking match), a terminating connector's solved profile
///     leaves the junction center at its own body grade; the first <c>primaryHalfWidth</c> meters are hidden
///     under the through road's painted surface, so at the through-road EDGE the connector sits
///     <c>(g_body−g_plane)·halfWidth</c> off the through surface (the franco J#450 0.65 m step / 40 % scarp).
///     <c>EaseConnectorGradeToThroughSurface</c> now repairs this in two zones: an APRON
///     <c>[0, halfWidth]</c> projected onto the through surface plane (seam Z preserved exactly), and a C1
///     Hermite WELD <c>(halfWidth, halfWidth+L]</c> whose length adapts to the grade break
///     (<c>ConnectorWeldGradeChangePerMeter</c>; the knob is the MINIMUM). Far-junction Z and the body
///     beyond the weld stay fixed; the walk stops at pinned (bridge deck/dip) cross-sections.
///
///     Geometry used throughout: a FLAT-graded through road runs E-W (tangent +x, normal +y, width 6 ⇒
///     halfWidth hw=3 unless stated) BANKED by <c>throughBank</c>; a connector runs N-S (tangent +y) and
///     TERMINATES at the junction (50,50) as its spline start (body extends +y). For this perpendicular T
///     the through surface plane along the connector is <c>plane(s) = 100 + sin(throughBank)·s</c> and
///     <c>g_seam = sin(throughBank)</c>.
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
    ///     Through road E-W banked by <paramref name="throughBank" /> (flat longitudinal grade, Z=100),
    ///     width <paramref name="throughWidth" />. Connector N-S terminating at (50,50) as its spline START;
    ///     its body runs +y with cross-sections at <paramref name="connectorSDists" /> (distance from the
    ///     seam, ascending — seam first), natural elevation = 100 + <paramref name="gNatural" />·s.
    /// </summary>
    private static (UnifiedRoadNetwork network, UnifiedCrossSection[] connector)
        Build(float gNatural, float throughBank, float rampLen, float[] connectorSDists,
            float throughWidth = 6f)
    {
        var through = MakeSpline(1, rampLen);
        var connector = MakeSpline(2, rampLen);

        var tW = new Vector2(1f, 0f); // through tangent +x
        var tN = new Vector2(0f, 1f); // through normal +y
        var t0 = Cs(1, 0, 100, new Vector2(40f, 50f), tW, tN, 100f, 0f, throughWidth, throughBank);
        var tMid = Cs(1, 1, 101, new Vector2(50f, 50f), tW, tN, 100f, 10f, throughWidth, throughBank);
        var t2 = Cs(1, 2, 102, new Vector2(60f, 50f), tW, tN, 100f, 20f, throughWidth, throughBank);

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
    ///     Test 1 — APRON CO-PLANARITY + seam invariant. Inside the through road's half-width the connector
    ///     centerline must lie ON the through surface plane (plane(s) = 100 + 0.2·s), and the seam Z must
    ///     stay exactly at §3's value (100). The connector then LEAVES the junction at the plane's grade,
    ///     not at its own flat natural grade.
    /// </summary>
    [Fact]
    public void ApronCoplanar_SeamZExact_FootprintFollowsThroughPlane()
    {
        var gSeam = 0.2f;
        var (network, c) = Build(
            gNatural: 0f, throughBank: MathF.Asin(0.2f), rampLen: 6f,
            connectorSDists: new[] { 0f, 0.5f, 1f, 2f, 3f, 4f, 6f, 8f, 10f, 30f });

        var eased = UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.True(eased > 0, "connector should have been eased");
        Assert.Equal(100f, c[0].TargetElevation, 4); // seam Z fixed (§3 invariant)
        // Apron cross-sections (s ≤ hw=3) sit exactly on the through plane.
        Assert.Equal(100f + gSeam * 0.5f, c[1].TargetElevation, 3);
        Assert.Equal(100f + gSeam * 1f, c[2].TargetElevation, 3);
        Assert.Equal(100f + gSeam * 2f, c[3].TargetElevation, 3);
        Assert.Equal(100f + gSeam * 3f, c[4].TargetElevation, 3);
        // The seam grade is the plane's grade, eased toward natural only past the edge.
        var seamSecant = Secant(c[0], c[1]);
        Assert.True(MathF.Abs(seamSecant - gSeam) < 0.01f,
            $"seam grade {seamSecant} should equal g_seam {gSeam}");
    }

    /// <summary>
    ///     Test 2 — C1 AT BOTH WELD BOUNDARIES. Grade must be continuous where the apron hands off to the
    ///     weld (through-road edge, s=hw) and where the weld hands off to the untouched body (s=hw+L).
    ///     Δg=0.1 with the adaptive rate gives L=40 → capped to 0.25·(30−3)=6.75 ⇒ zone end 9.75.
    /// </summary>
    [Fact]
    public void WeldC1AtEdgeAndZoneEnd_NoKinks()
    {
        var (network, c) = Build(
            gNatural: 0.3f, throughBank: MathF.Asin(0.2f), rampLen: 5f,
            connectorSDists: new[] { 0f, 1f, 2.9f, 3f, 3.1f, 5f, 7f, 9f, 9.6f, 9.75f, 10f, 10.5f, 12f, 30f });

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        // C1 at the through-road edge (s=3): apron grade (0.2) continues into the weld.
        var apronSecant = Secant(c[2], c[3]);   // [2.9, 3.0] — inside apron: exactly g_seam
        var weldStartSecant = Secant(c[3], c[4]); // [3.0, 3.1] — first step into the weld
        Assert.True(MathF.Abs(apronSecant - 0.2f) < 0.01f, $"apron grade {apronSecant} ≠ g_seam");
        Assert.True(MathF.Abs(weldStartSecant - apronSecant) < 0.03f,
            $"grade must be continuous at the edge: apron {apronSecant} vs weld start {weldStartSecant}");

        // C1 at the zone end (s=9.75): grade just inside equals grade just outside.
        var insideZoneEnd = Secant(c[8], c[9]);   // [9.6, 9.75]
        var outsideZoneEnd = Secant(c[9], c[10]); // [9.75, 10.0]
        Assert.True(MathF.Abs(insideZoneEnd - outsideZoneEnd) < 0.02f,
            $"grade must be continuous at the zone end: inside {insideZoneEnd} vs outside {outsideZoneEnd}");
    }

    /// <summary>
    ///     Test 3 — LOCAL REPAIR. Everything beyond the weld zone (hw+L = 9.75 here) is byte-untouched:
    ///     original straight body, original far-junction Z.
    /// </summary>
    [Fact]
    public void BodyBeyondWeldUnchanged_FarJunctionFixed()
    {
        var gNatural = 0.3f;
        var sd = new[] { 0f, 1f, 3f, 4f, 6f, 8f, 10f, 12f, 30f };
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 6f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);                    // seam fixed
        Assert.Equal(100f + gNatural * 10f, c[6].TargetElevation, 4);   // beyond zone end (9.75)
        Assert.Equal(100f + gNatural * 12f, c[7].TargetElevation, 4);
        Assert.Equal(100f + gNatural * 30f, c[8].TargetElevation, 3);   // far junction fixed
        Assert.Equal(Secant(c[6], c[7]), Secant(c[7], c[8]), 4);        // body still straight
    }

    /// <summary>
    ///     Test 4 — HIGHER AND LOWER. Sign-agnostic: a connector steeper UP than the plane grade and one
    ///     steeper DOWN both get the apron + weld with seam, far end, and far body fixed.
    /// </summary>
    [Theory]
    [InlineData(0.5f)]  // connector much steeper UP than g_seam (0.2)
    [InlineData(-0.5f)] // connector steeper DOWN
    public void HigherAndLower_BothWeld_EndsFixed(float gNatural)
    {
        var sd = new[] { 0f, 1f, 3f, 4f, 6f, 8f, 10f, 12f, 30f };
        var zB = 100f + gNatural * sd[^1];
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 6f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);            // seam fixed
        Assert.Equal(zB, c[^1].TargetElevation, 3);             // far junction fixed
        Assert.Equal(100f + 0.2f * 3f, c[2].TargetElevation, 3); // apron end on the plane, both signs
        Assert.Equal(100f + gNatural * 10f, c[6].TargetElevation, 3); // beyond zone end: untouched
        Assert.Equal(100f + gNatural * 12f, c[7].TargetElevation, 3);
    }

    /// <summary>
    ///     Test 5 — SHORT CONNECTOR (propagation guard). D=20 with a large requested minimum: L is capped to
    ///     0.25·(D−hw)=4.25 ⇒ zone end 7.25, so the far junction cannot move and the far body stays on the
    ///     original straight profile.
    /// </summary>
    [Fact]
    public void ShortConnector_WeldCappedBeforeFarJunction()
    {
        var sd = new[] { 0f, 0.5f, 2f, 4f, 6f, 8f, 12f, 16f, 20f };
        var gNatural = 0.1f;
        var zB = 100f + gNatural * sd[^1];
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 20f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(100f, c[0].TargetElevation, 4);  // seam fixed
        Assert.Equal(zB, c[^1].TargetElevation, 3);   // far junction did NOT move
        // Beyond the capped zone end (7.25) the body is exactly the original natural profile.
        Assert.Equal(100f + gNatural * 8f, c[5].TargetElevation, 3);
        Assert.Equal(100f + gNatural * 12f, c[6].TargetElevation, 3);
        Assert.Equal(100f + gNatural * 16f, c[7].TargetElevation, 3);
        Assert.Equal(Secant(c[5], c[6]), Secant(c[6], c[7]), 3);
    }

    /// <summary>
    ///     Test 6 — ADAPTIVE LENGTH. A steep grade break must extend the weld far beyond the knob minimum:
    ///     Δg = |0.16−0.03| = 0.13 ⇒ L = 0.13/0.0025 = 52 → capped to 0.25·(200−3) ≈ 49 ⇒ zone end ≈ 52.
    ///     A cross-section at s=40 (way past the old knob-limited 6 m zone) must still be modified; one at
    ///     s=60 must be untouched.
    /// </summary>
    [Fact]
    public void AdaptiveLength_SteepBreak_ExtendsWeldBeyondKnob()
    {
        var gNatural = 0.16f;
        var sd = new[] { 0f, 1f, 3f, 5f, 10f, 15f, 20f, 25f, 30f, 40f, 60f, 100f, 200f };
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.03f), rampLen: 6f, connectorSDists: sd);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        var s40 = c[9];
        var s60 = c[10];
        Assert.True(MathF.Abs(s40.TargetElevation - (100f + gNatural * 40f)) > 0.01f,
            "s=40 must be inside the adaptive weld (knob alone would have ended at s=9)");
        Assert.Equal(100f + gNatural * 60f, s60.TargetElevation, 3); // beyond the capped zone: untouched
        Assert.Equal(100f + gNatural * 200f, c[^1].TargetElevation, 2); // far junction fixed
    }

    /// <summary>
    ///     Test 7 — THE JUNCTION #450 REGRESSION. Replica of the measured franco artifact: through road
    ///     10 m wide (hw=5), connector body grade 16 %, through plane grade along the connector 3 %.
    ///     Before the fix the connector's own surface began at the through-road edge 0.65 m above the
    ///     through plane (rendered as a 2 m 40 % scarp). After: the apron is ON the plane and the residual
    ///     at the first visible cross-section shrinks by ≥ 80 %; the worst secant-to-secant grade jump at
    ///     real cross-section spacing (4.4 m) drops from ~13 pp to ≤ 6 pp.
    /// </summary>
    [Fact]
    public void EdgeStepRemoved_Junction450Replica()
    {
        var gNatural = 0.16f;
        var gPlane = 0.03f;
        var sd = Enumerable.Range(0, 16).Select(i => i * 4.4f) // 0 … 66 @ 4.4 m (real CS spacing)
            .Concat(new[] { 100f, 150f, 200f }).ToArray();
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(gPlane), rampLen: 6f,
            connectorSDists: sd, throughWidth: 10f);

        // The measured artifact this test encodes: natural profile at the edge (s=hw=5) sits
        // (0.16−0.03)·5 = 0.65 m above the through plane.
        var stepBefore = (100f + gNatural * 5f) - (100f + gPlane * 5f);
        Assert.Equal(0.65f, stepBefore, 2);

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        // Apron cross-section (s=4.4 < hw) sits on the through plane.
        Assert.Equal(100f + gPlane * 4.4f, c[1].TargetElevation, 2);

        // First visible cross-section past the edge (s=8.8): residual vs the plane shrinks ≥ 80 %
        // (was 8.8·0.16 − 8.8·0.03 = 1.14 m un-welded).
        var residualBefore = (100f + gNatural * 8.8f) - (100f + gPlane * 8.8f);
        var residualAfter = MathF.Abs(c[2].TargetElevation - (100f + gPlane * 8.8f));
        Assert.True(residualAfter < 0.2f * residualBefore,
            $"edge residual {residualAfter:F3}m should shrink ≥80% vs un-welded {residualBefore:F3}m");

        // Grade progression at real spacing: worst consecutive-secant jump ≤ 6 pp (before: the full
        // 13 pp break landed inside a single spacing → the rendered kink).
        var maxJump = 0f;
        for (var i = 2; i < 12; i++)
        {
            var jump = MathF.Abs(Secant(c[i], c[i + 1]) - Secant(c[i - 1], c[i]));
            maxJump = MathF.Max(maxJump, jump);
        }
        Assert.True(maxJump < 0.06f, $"max secant jump {maxJump:F3} must stay under 6 pp");

        Assert.Equal(100f, c[0].TargetElevation, 4);                     // seam fixed
        Assert.Equal(100f + gNatural * 200f, c[^1].TargetElevation, 2);  // far junction fixed
    }

    /// <summary>
    ///     Test 8 — DECK PIN GUARD. The outward walk truncates at the first pinned cross-section: the pin
    ///     and everything beyond it keep their values (never fight a bridge deck/dip pin).
    /// </summary>
    [Fact]
    public void PinnedCrossSection_TruncatesWeld_NeverFightsDeckPin()
    {
        var gNatural = 0.3f;
        var sd = new[] { 0f, 1f, 3f, 5f, 7f, 9f, 12f, 30f };
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 6f, connectorSDists: sd);
        c[4].PinnedElevation = c[4].TargetElevation; // deck pin at s=7

        UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.True(MathF.Abs(c[3].TargetElevation - (100f + gNatural * 5f)) > 0.01f,
            "cross-sections before the pin are still repaired");
        Assert.Equal(100f + gNatural * 7f, c[4].TargetElevation, 4); // pinned CS untouched
        Assert.Equal(100f + gNatural * 9f, c[5].TargetElevation, 4); // beyond the pin untouched
    }

    /// <summary>
    ///     Test 9 — KILL SWITCH. Knob = 0 disables the whole pass: apron included, nothing moves.
    /// </summary>
    [Fact]
    public void RampLenZero_DisablesEntirely()
    {
        var gNatural = 0.3f;
        var sd = new[] { 0f, 1f, 3f, 5f, 8f, 30f };
        var (network, c) = Build(
            gNatural: gNatural, throughBank: MathF.Asin(0.2f), rampLen: 0f, connectorSDists: sd);

        var eased = UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network);

        Assert.Equal(0, eased);
        for (var i = 0; i < c.Length; i++)
            Assert.Equal(100f + gNatural * sd[i], c[i].TargetElevation, 4);
    }
}
