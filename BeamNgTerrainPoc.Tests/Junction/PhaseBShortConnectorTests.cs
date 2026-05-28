using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseBShortConnectorTests
{
    private static NetworkJunction StubJunction() => new()
    {
        Position = Vector2.Zero,
        JunctionId = 0,
        Type = JunctionType.TJunction
    };

    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildShortConnector(int n, float spacing, float startZ, float slope)
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
    public void ShortConnector_AnchorsExactlyMatchedAtBothEnds()
    {
        var (sections, elev, bank) = BuildShortConnector(20, 1f, 100f, -0.02f);
        var startC = new JunctionEndpointConstraint
        {
            Elevation = 105f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endC = new JunctionEndpointConstraint
        {
            Elevation = 95f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startC, endC, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: true);

        Assert.Equal(105f, sections[0].TargetElevation, 2);
        Assert.Equal(95f, sections[^1].TargetElevation, 2);
    }

    [Fact]
    public void ShortConnector_MidpointBetweenAnchors()
    {
        var (sections, elev, bank) = BuildShortConnector(20, 1f, 100f, 0f);
        var startC = new JunctionEndpointConstraint
        {
            Elevation = 105f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endC = new JunctionEndpointConstraint
        {
            Elevation = 95f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startC, endC, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: true);

        Assert.InRange(sections[9].TargetElevation, 99.0f, 101.0f);
        Assert.InRange(sections[10].TargetElevation, 99.0f, 101.0f);
    }

    [Fact]
    public void ShortConnector_MonotoneBetweenAnchors_NoOvershoot()
    {
        var (sections, elev, bank) = BuildShortConnector(20, 1f, 100f, 0f);
        var startC = new JunctionEndpointConstraint
        {
            Elevation = 105f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endC = new JunctionEndpointConstraint
        {
            Elevation = 95f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startC, endC, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: true);

        for (var i = 0; i < sections.Count; i++)
            Assert.InRange(sections[i].TargetElevation, 94.99f, 105.01f);
    }

    [Fact]
    public void ShortConnector_FlagOff_FallsBackToLegacy_AnchorsMatchUnderBothBranches()
    {
        var (sections1, elev1, bank1) = BuildShortConnector(20, 1f, 100f, 0f);
        var (sections2, elev2, bank2) = BuildShortConnector(20, 1f, 100f, 0f);

        var startC = new JunctionEndpointConstraint
        {
            Elevation = 105f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endC = new JunctionEndpointConstraint
        {
            Elevation = 95f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections1, startC, endC, elev1, bank1,
            enableC1: false, claimedZone: null, enableShortConnectorBlend: false);
        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections2, startC, endC, elev2, bank2,
            enableC1: false, claimedZone: null, enableShortConnectorBlend: true);

        Assert.Equal(sections1[^1].TargetElevation, sections2[^1].TargetElevation, 1);
    }

    [Fact]
    public void ShortConnector_NotShort_LegacyPathRunsAndIsUnaffected()
    {
        // 100m spline with 30m+30m = 60m total blend < 100m → NOT short.
        var (sections, elev, bank) = BuildShortConnector(100, 1f, 100f, -0.04f);
        var startC = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endC = new JunctionEndpointConstraint
        {
            Elevation = 96f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startC, endC, elev, bank,
            enableC1: false, claimedZone: null, enableShortConnectorBlend: true);

        // Anchors exact; midpoint (d=50) outside both blend zones, natural elevation = 100 + (-0.04 × 50) = 98.
        Assert.Equal(100f, sections[0].TargetElevation, 2);
        Assert.Equal(96f, sections[^1].TargetElevation, 2);
        Assert.Equal(98f, sections[50].TargetElevation, 2);
    }
}
