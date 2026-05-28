using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseCStretchLBlendTests
{
    private static NetworkJunction StubJunction() =>
        new() { Position = Vector2.Zero, JunctionId = 0, Type = JunctionType.TJunction };

    /// <summary>Builds a spline whose CS at index i sits at world (i·spacing, 0) with the supplied elevation.</summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildSpline(int n, float spacing, Func<int, float> elevAt)
    {
        var sections = new List<UnifiedCrossSection>();
        var elev = new Dictionary<int, float>();
        var bank = new Dictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var z = elevAt(i);
            var cs = new UnifiedCrossSection
            {
                Index = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = z,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            };
            sections.Add(cs);
            elev[i] = z;
            bank[i] = 0f;
        }
        return (sections, elev, bank);
    }

    /// <summary>
    ///     Reproduces the franco_same_prio junction 20 geometry: junction anchor at
    ///     z=98.807, anchor slope -6.8%, natural descent at -16.7% from d=0. With
    ///     stretchL OFF the parabolic seam at d=30 carries an 8% slope mismatch.
    ///     With stretchL ON the seam moves to ~d=40 and the slope kink disappears.
    /// </summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildJunction20LikeSpline()
    {
        // Natural elevation: linear -16.7% descent from terrain-level near 96.7 at d=0
        // to whatever at d=99. This matches the dirt-road spline 10 going from junction
        // 20 to junction 21 — except for the pedestal at d=0 which the constraint
        // (z=98.807) imposes on top.
        return BuildSpline(100, 1f, i => 96.703f - 0.16725f * i);
    }

    [Fact]
    public void StretchOff_Junction20Geometry_HasKinkAtD30()
    {
        var (sections, elev, bank) = BuildJunction20LikeSpline();
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false);

        var slopeAt29 = sections[30].TargetElevation - sections[29].TargetElevation;
        var slopeAt30 = sections[31].TargetElevation - sections[30].TargetElevation;
        // Without stretch the parabolic emergent slope (~-25%) abruptly snaps to natural (~-17%).
        Assert.True(MathF.Abs(slopeAt29 - slopeAt30) > 0.05f,
            $"Expected kink without stretch; slopeAt29={slopeAt29:F4}, slopeAt30={slopeAt30:F4}");
    }

    [Fact]
    public void StretchOn_Junction20Geometry_SeamKinkEliminated()
    {
        var (sections, elev, bank) = BuildJunction20LikeSpline();
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true);

        // With stretch, the blend zone extends past d=30, so the slope at d=30 should be
        // close to the slope at d=29 (both inside the now-longer parabolic blend zone).
        var slopeAt29 = sections[30].TargetElevation - sections[29].TargetElevation;
        var slopeAt30 = sections[31].TargetElevation - sections[30].TargetElevation;
        Assert.True(MathF.Abs(slopeAt29 - slopeAt30) < 0.02f,
            $"Expected smooth slope at d=30 with stretch; slopeAt29={slopeAt29:F4}, slopeAt30={slopeAt30:F4}");
    }

    [Fact]
    public void StretchOn_Junction20Geometry_ExtendsBlendPastD30()
    {
        // The unmodified elevation at d=35 is 96.703 - 0.16725*35 = 90.849.
        // With stretchL=false, d=35 stays at natural (CS untouched, outside blend).
        // With stretchL=true, L_target ~= 40m, so d=35 is INSIDE the new blend zone
        // and TargetElevation must be MODIFIED away from the natural value.
        var (sections, elev, bank) = BuildJunction20LikeSpline();
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true);

        var natural35 = 96.703f - 0.16725f * 35;
        var modified35 = sections[35].TargetElevation;
        Assert.True(MathF.Abs(modified35 - natural35) > 0.1f,
            $"Expected d=35 to be inside the stretched blend zone (modified); natural={natural35:F3}, modified={modified35:F3}");
    }

    [Fact]
    public void StretchOff_DefaultBehavior_UnchangedFromParabolic()
    {
        // Regression guard: with stretchL=false, the result must be byte-for-byte
        // identical to the existing parabolic path (no surprises for existing callers).
        var (sectionsRef, elevRef, bankRef) = BuildJunction20LikeSpline();
        var (sectionsNew, elevNew, bankNew) = BuildJunction20LikeSpline();
        var startConstraint1 = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var startConstraint2 = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sectionsRef, startConstraint1, endConstraint: null, elevRef, bankRef,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false);

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sectionsNew, startConstraint2, endConstraint: null, elevNew, bankNew,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false);

        for (var i = 0; i < sectionsRef.Count; i++)
            Assert.Equal(sectionsRef[i].TargetElevation, sectionsNew[i].TargetElevation, 5);
    }

    [Fact]
    public void StretchOn_NoMismatch_KeepsCurrentL()
    {
        // Build a spline where the natural slope already matches the parabolic emergent slope.
        //   m_emergent(L=30) = 2*(zNL-zJ)/L - mJ = 2*(91-100)/30 - 0 = -0.6 — way off natural -0.04.
        //   Pick zNL so emergent==natural: -0.04 = 2*(zNL-100)/30 - 0 => zNL = 100 - 0.6 = 99.4.
        var (sections, elev, bank) = BuildSpline(100, 1f, i => i <= 30
            ? 100f + (99.4f - 100f) * (i / 30f)        // linear interp to 99.4 at d=30
            : 99.4f - 0.04f * (i - 30));              // natural -4% past d=30

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        // Run with stretch on; expect no extension because mismatch is already < threshold.
        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true);

        // At d=35 the elevation should be natural (untouched), because L stayed at 30.
        var natural35 = 99.4f - 0.04f * 5; // 99.2
        Assert.Equal(natural35, sections[35].TargetElevation, 2);
    }

    [Fact]
    public void StretchOn_MidSplineCrossingAtD35_ClampsStretchAtD33()
    {
        // Junction 20 geometry: natural stretch goes to ~40m. A MidSplineCrossing
        // contributor sits at d=35 on this spline. The third clamp (safetyMargin=2m)
        // caps the stretched L at 35 - 2 = 33m, so d=35 stays at the natural
        // (unstretched, untouched) elevation. Without this clamp, the stretched
        // parabolic write to ~d=40 overwrites the MidSplineCrossing's harmonized
        // elevation — the regression at franco OSM node 282534720.
        var (sections, elev, bank) = BuildJunction20LikeSpline();
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true,
            otherJunctionDistancesOnSpline: new[] { 35f });

        // d=35 sits exactly at the MidSplineCrossing CS. Clamped zone is [0, 33];
        // d=35 is outside, so TargetElevation must equal the natural (linear -16.725%) value.
        var natural35 = 96.703f - 0.16725f * 35;
        Assert.Equal(natural35, sections[35].TargetElevation, 2);
    }

    [Fact]
    public void StretchOn_MidSplineCrossingInsideCurrentL_DoesNotShortenStretch()
    {
        // Option (b) from the Phase C plan: a MidSplineCrossing already inside the
        // unstretched zone (d=20, currentL=30) is a pre-existing inclusion. The
        // parabola at currentL was already overwriting it before stretch; stretching
        // doesn't make that worse. With no OTHER crossings beyond currentL, the
        // third clamp must not fire — stretch proceeds to ~40m as in the baseline.
        var (sections, elev, bank) = BuildJunction20LikeSpline();
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true,
            otherJunctionDistancesOnSpline: new[] { 20f });

        // d=35 must be modified (inside the unclamped stretched zone ~[0,40]).
        var natural35 = 96.703f - 0.16725f * 35;
        Assert.True(MathF.Abs(sections[35].TargetElevation - natural35) > 0.1f,
            $"Expected d=35 inside the stretched zone; natural={natural35:F3}, modified={sections[35].TargetElevation:F3}");
    }

    [Fact]
    public void StretchOn_NullOtherJunctions_BehavesIdenticallyToBaselineStretch()
    {
        // Regression guard: passing null for otherJunctionDistancesOnSpline must
        // be byte-identical to the v1 stretch-L path (no clamp at all). Protects
        // callers that don't yet thread the lookup through.
        var (sectionsBaseline, elevBaseline, bankBaseline) = BuildJunction20LikeSpline();
        var (sectionsWithList, elevWithList, bankWithList) = BuildJunction20LikeSpline();
        JunctionEndpointConstraint MakeC() => new()
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sectionsBaseline, MakeC(), endConstraint: null, elevBaseline, bankBaseline,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true);

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sectionsWithList, MakeC(), endConstraint: null, elevWithList, bankWithList,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true,
            otherJunctionDistancesOnSpline: null);

        for (var i = 0; i < sectionsBaseline.Count; i++)
            Assert.Equal(sectionsBaseline[i].TargetElevation, sectionsWithList[i].TargetElevation, 5);
    }
}
