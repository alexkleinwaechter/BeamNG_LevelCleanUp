using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using System.Numerics;

namespace BeamNgTerrainPoc.Tests.Junction;

public class BlendSplineProfileParabolicTests
{
    private static NetworkJunction StubJunction() =>
        new() { Position = Vector2.Zero, JunctionId = 0, Type = JunctionType.TJunction };

    /// <summary>
    ///     Build a synthetic descending spline: n cross-sections, 1 m spacing.
    /// </summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> originalElev,
                    Dictionary<int, float> originalBank)
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
    public void BlendParabolic_DescendingSpline_NoUpwardOvershoot()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f,
            Slope = 0f,
            BankAngleRadians = 0f,
            IsSplineStart = true,
            Junction = StubJunction(),
            FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        for (var i = 0; i <= 30; i++)
        {
            Assert.InRange(sections[i].TargetElevation, 96.79f, 100.01f);
        }
    }

    [Fact]
    public void BlendParabolic_BeyondBlendZone_LeavesNaturalElevationUntouched()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(),
            FlatZoneDistance = 0f, BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        for (var i = 31; i < 100; i++)
        {
            var naturalAtI = 100f - 0.04f * i;
            Assert.Equal(naturalAtI, sections[i].TargetElevation, 3);
        }
    }

    [Fact]
    public void BlendParabolic_AtJunctionEndpoint_MatchesConstraintElevation()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, Junction = StubJunction(),
            FlatZoneDistance = 0f, BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        Assert.Equal(100f, sections[0].TargetElevation, 3);
    }

    [Fact]
    public void BlendParabolic_NoConstraints_LeavesEverythingUntouched()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var modified = UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint: null, endConstraint: null, elev, bank);

        Assert.Equal(0, modified);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(elev[i], sections[i].TargetElevation, 3);
        }
    }

    [Fact]
    public void BlendParabolic_Junction126Reproduction_NoSignFlipAt15m()
    {
        // Mimics franco_same_prio junction 126 / spline 64:
        //   - Spline length 60 m (test-scale of the real ~312 m case)
        //   - End constraint anchors elevation to 158.95 m (continuous road surface)
        //     with slope ≈ 0 (continuous road near-flat at this point)
        //   - Natural spline descends from 159.0 m at far end down toward the junction end
        //     at −4 % grade
        //   - Legacy code reports delta_5/15/30/60 = [+0.13, +2.46, +2.05, −1.18]
        //     (sign flip between 5 m and 15 m, and again between 30 m and 60 m)
        //
        // Parabolic path: end-anchor at d=length, blend back over L=30 m. The road
        // inside the blend zone must monotonically descend from 158.95 to
        // natural-at-(length-30).
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 60, spacing: 1f, startZ: 159.0f, slope: -0.04f);

        var endConstraint = new JunctionEndpointConstraint
        {
            Elevation = 158.95f,
            Slope = 0f,
            BankAngleRadians = 0f,
            IsSplineStart = false,
            Junction = StubJunction(),
            FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint: null, endConstraint: endConstraint, elev, bank);

        // Cross-section indices to inspect (measured from the junction = end of spline,
        // i.e. distFromEnd = 59 - i):
        //   d_from_junction = 5  → CS index 54
        //   d_from_junction = 15 → CS index 44
        //   d_from_junction = 30 → CS index 29 (blend boundary; NOT in zone since "<30")
        //   d_from_junction = 59 → CS index 0 (well outside blend — equals natural)
        var elevAt5 = sections[54].TargetElevation;
        var elevAt15 = sections[44].TargetElevation;
        var elevAt30 = sections[29].TargetElevation;
        var elevAt60 = sections[0].TargetElevation;
        var elevAtJunction = sections[59].TargetElevation;

        // Junction anchor preserved
        Assert.Equal(158.95f, elevAtJunction, 2);

        // Monotone descent INSIDE blend zone: as we move away from junction
        // (decreasing CS index), elevation must monotonically decrease.
        Assert.True(elevAtJunction >= elevAt5,
            $"d=5 elev ({elevAt5}) must be <= junction ({elevAtJunction})");
        Assert.True(elevAt5 >= elevAt15,
            $"d=15 elev ({elevAt15}) must be <= d=5 elev ({elevAt5})");
        Assert.True(elevAt15 >= elevAt30,
            $"d=30 elev ({elevAt30}) must be <= d=15 elev ({elevAt15})");

        // Outside blend zone, road follows natural profile (no junction effect).
        // CS 0 = far end where the synthetic spline was initialized.
        var naturalAtFarEnd = 159.0f + (-0.04f) * 0f; // = 159.0f
        Assert.Equal(naturalAtFarEnd, elevAt60, 2);
    }
}
