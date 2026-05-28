using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseDBankBlendTests
{
    private static NetworkJunction StubJunction() =>
        new() { Position = Vector2.Zero, JunctionId = 0, Type = JunctionType.TJunction };

    /// <summary>Builds a spline whose CS at index i sits at (i·spacing, 0) with supplied elevation and natural bank.</summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildSpline(int n, float spacing, Func<int, float> elevAt, Func<int, float>? bankAt = null)
    {
        bankAt ??= _ => 0f;
        var sections = new List<UnifiedCrossSection>();
        var elev = new Dictionary<int, float>();
        var bank = new Dictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var z = elevAt(i);
            var b = bankAt(i);
            var cs = new UnifiedCrossSection
            {
                Index = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = z,
                BankAngleRadians = b,
                EffectiveRoadWidth = 6f
            };
            sections.Add(cs);
            elev[i] = z;
            bank[i] = b;
        }
        return (sections, elev, bank);
    }

    [Fact]
    public void BankBlendOn_StartConstraint_BankAtAnchorEqualsConstraint_DecaysToNaturalAtL()
    {
        // 100-CS straight spline at z=100, natural bank = 0 everywhere.
        // Start constraint imposes bank = 4.5° (0.0785 rad) at the junction anchor.
        // Blend distance L = 30m. With EnableParabolicBankBlend = true:
        //   bank at d=0  → constraint bank (0.0785 rad)
        //   bank at d=L  → natural bank (0)
        //   monotone decay in between (h00 is monotone on [0,1]).
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var constraintBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = constraintBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Anchor bank exact match.
        Assert.Equal(constraintBank, sections[0].BankAngleRadians, 4);

        // At d=L (index 30) bank should be back to natural (0).
        Assert.Equal(0f, sections[30].BankAngleRadians, 3);

        // At d=15 (midpoint) bank should be between 0 and constraint, strictly.
        Assert.InRange(sections[15].BankAngleRadians, 0.001f, constraintBank - 0.001f);

        // Past L bank stays untouched.
        Assert.Equal(0f, sections[50].BankAngleRadians, 4);
    }

    [Fact]
    public void BankBlendOn_BothEnds_MatchEachConstraintAtItsAnchor()
    {
        // 100-CS straight spline, natural bank = 0. Constraints at both ends with
        // different banks. The two zones do not overlap (30 + 30 = 60 < 99).
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var startBank = 4.5f * MathF.PI / 180f;   // 4.5°
        var endBank   = -2.0f * MathF.PI / 180f;  // -2.0° (opposite tilt)
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = startBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = endBank,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(-1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Each anchor matches its own constraint exactly.
        Assert.Equal(startBank, sections[0].BankAngleRadians, 4);
        Assert.Equal(endBank, sections[^1].BankAngleRadians, 4);

        // Middle (d=50, outside both blend zones) untouched.
        Assert.Equal(0f, sections[50].BankAngleRadians, 4);
    }

    [Fact]
    public void BankBlendOn_ShortConnectorCompositional_BothAnchorsMatch()
    {
        // 20-CS short spline (19m long), natural bank = 0. Both blend zones
        // are 15m each, so startBlendDist + endBlendDist = 30 > 19 — dispatch
        // hits the compositional path when enableShortConnectorBlend=true.
        var (sections, elev, bank) = BuildSpline(20, 1f, _ => 100f, _ => 0f);
        var startBank = 4.5f * MathF.PI / 180f;
        var endBank   = -2.0f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = startBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 15f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = endBank,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 15f,
            PrimaryTangentDirection = new Vector2(-1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: true,   // dispatch to compositional
            enableStretchL: false,
            enableBankBlend: true);

        // Each anchor matches its own constraint within tolerance.
        // Tolerance is looser than the long-spline case because OverlapTaper
        // composition is not perfectly localized at the endpoints.
        Assert.Equal(startBank, sections[0].BankAngleRadians, 3);
        Assert.Equal(endBank, sections[^1].BankAngleRadians, 3);
    }

    [Fact]
    public void BankBlendOn_StretchedL_BankZoneExtendsWithElevationZone()
    {
        // Reuse the franco junction 20 geometry from PhaseCStretchLBlendTests:
        // natural -16.7% descent, anchor at z=98.807 with slope -6.8%. With
        // stretchL on, L extends from 30 to ~40m. Bank constraint is 4.5°.
        // After Phase D, the bank zone follows the stretched L: at d=35 the
        // bank should still be inside the (now-longer) blend zone, NOT back
        // at natural 0.
        var (sections, elev, bank) = BuildSpline(100, 1f,
            i => 96.703f - 0.16725f * i,
            _ => 0f);
        var constraintBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = constraintBank,
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
            enableBankBlend: true);

        // d=35 is inside the stretched zone → bank still nonzero.
        Assert.True(MathF.Abs(sections[35].BankAngleRadians) > 0.001f,
            $"Expected bank at d=35 still inside stretched blend zone; got {sections[35].BankAngleRadians:F4}");

        // d=73 is clearly past the stretched zone (which extends to ~60m for this geometry) → bank back to natural (0).
        Assert.Equal(0f, sections[73].BankAngleRadians, 3);
    }

    [Fact]
    public void BankBlendOff_ParabolicPathLeavesBankUntouched()
    {
        // Escape hatch: with enableBankBlend = false, bank values must remain
        // exactly the natural pre-blend value (parabolic path's current behavior).
        // Protects callers that pin the flag off to avoid the new behavior.
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f,
            BankAngleRadians = 4.5f * MathF.PI / 180f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: false);   // OFF

        for (var i = 0; i < sections.Count; i++)
            Assert.Equal(0f, sections[i].BankAngleRadians, 6);
    }

    [Fact]
    public void BankBlendOn_FrancoJunction20Like_AnchorEdgesMatchPrimarySurface()
    {
        // Synthetic stand-in for the franco junction 20 cross-slope artefact:
        // - Terminating road's natural bank = 0.8° (its own curvature-driven value).
        // - Primary road's bank at the junction = 4.5° (the constraint target).
        // After Phase D the terminating road's per-CS bank at d=0 must equal the
        // constraint (4.5°), so that Step 4's edge derivation (TargetElevation ±
        // halfWidth × sin(bank)) lines its edges up with the primary's surface.
        var (sections, elev, bank) = BuildSpline(100, 1f,
            _ => 100f,
            _ => 0.8f * MathF.PI / 180f);   // natural bank = 0.8° everywhere
        var primaryBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = primaryBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Anchor CS bank == primary's bank exactly (this is the contract).
        Assert.Equal(primaryBank, sections[0].BankAngleRadians, 4);

        // Derived anchor edge elevations must match what Step 4 would produce
        // for the primary's surface at the terminating road's edge positions.
        var halfWidth = sections[0].EffectiveRoadWidth / 2f;
        var anchorLeftEdge  = sections[0].TargetElevation - halfWidth * MathF.Sin(sections[0].BankAngleRadians);
        var anchorRightEdge = sections[0].TargetElevation + halfWidth * MathF.Sin(sections[0].BankAngleRadians);
        var primarySin = MathF.Sin(primaryBank);
        var expectedLeftEdge  = 100f - halfWidth * primarySin;
        var expectedRightEdge = 100f + halfWidth * primarySin;
        Assert.Equal(expectedLeftEdge,  anchorLeftEdge,  3);
        Assert.Equal(expectedRightEdge, anchorRightEdge, 3);

        // At d=L (index 30) bank decays back to natural 0.8°.
        var naturalBank = 0.8f * MathF.PI / 180f;
        Assert.Equal(naturalBank, sections[30].BankAngleRadians, 3);
    }
}
