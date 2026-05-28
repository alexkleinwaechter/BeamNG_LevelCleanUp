using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseBBlendZoneEndC1Tests
{
    private static NetworkJunction StubJunction() =>
        new() { Position = Vector2.Zero, JunctionId = 0, Type = JunctionType.TJunction };

    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildDescendingSpline(int n, float spacing, float startZ, float slope)
    {
        var sections = new List<UnifiedCrossSection>();
        var elev = new Dictionary<int, float>();
        var bank = new Dictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var cs = new UnifiedCrossSection
            {
                Index = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = startZ + slope * (i * spacing),
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            };
            sections.Add(cs);
            elev[i] = cs.TargetElevation;
            bank[i] = 0f;
        }
        return (sections, elev, bank);
    }

    [Fact]
    public void Parabolic_StartZone_LeavesSlopeKinkAtD30()
    {
        var (sections, elev, bank) = BuildDescendingSpline(100, 1f, 100f, -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null);

        var slopeAt29 = sections[30].TargetElevation - sections[29].TargetElevation;
        var slopeAt30 = sections[31].TargetElevation - sections[30].TargetElevation;
        Assert.True(MathF.Abs(slopeAt29 - slopeAt30) > 0.01f,
            $"Expected visible kink at d=30; slopeAt29={slopeAt29:F4}, slopeAt30={slopeAt30:F4}");
    }

    [Fact]
    public void Cubic_StartZone_SmoothesSlopeAcrossD30()
    {
        var (sections, elev, bank) = BuildDescendingSpline(100, 1f, 100f, -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: true, claimedZone: null);

        var slopeAt29 = sections[30].TargetElevation - sections[29].TargetElevation;
        var slopeAt30 = sections[31].TargetElevation - sections[30].TargetElevation;
        Assert.True(MathF.Abs(slopeAt29 - slopeAt30) < 0.005f,
            $"Expected near-continuous slope across d=30 with cubic; slopeAt29={slopeAt29:F4}, slopeAt30={slopeAt30:F4}");
    }

    [Fact]
    public void Cubic_StartZone_NestedClaimAtSamplePoint_FallsBackToParabola()
    {
        var (sections, elev, bank) = BuildDescendingSpline(100, 1f, 100f, -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 80f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        var distFromStart = new Dictionary<int, float>();
        for (var i = 0; i < 100; i++) distFromStart[i] = i;
        var claimedZone = new SplineClaimedZone
        {
            SplineId = 1, RoadLength = 99f,
            StartClaim = new SplineEndClaim { JunctionId = 7, BlendDistanceMeters = 80f },
            EndClaim = new SplineEndClaim { JunctionId = 8, BlendDistanceMeters = 30f },
            DistFromStartByCsIndex = distFromStart
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: true, claimedZone: claimedZone);

        var (refSections, refElev, refBank) = BuildDescendingSpline(100, 1f, 100f, -0.04f);
        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            refSections, startConstraint, endConstraint: null, refElev, refBank,
            enableC1: false, claimedZone: null);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(refSections[i].TargetElevation, sections[i].TargetElevation, 3);
        }
    }
}
