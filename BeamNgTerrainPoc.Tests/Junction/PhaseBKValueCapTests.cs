using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseBKValueCapTests
{
    [Fact]
    public void Cap_FlagOff_BehavesLikeLegacyAdaptiveCalculation()
    {
        // L_legacy = max(50, min(elevDiff/tan(6°), 125)) for elevDiff=10m → ≈95m.
        var legacy = UnifiedJunctionProfileBlender.CalculateAdaptiveBlendDistanceForTesting(
            configuredBlendDistance: 50f,
            harmonizedElevation: 110f,
            contributorElevation: 100f,
            roadMaxSlopeDegrees: 6f,
            enableMaxSlopeConstraint: true,
            effectiveDesignSpeedKmh: 120,
            jhParams: new JunctionHarmonizationParameters { EnableAashtoBlendDistanceCap = false });
        Assert.InRange(legacy, 90f, 100f);
    }

    [Fact]
    public void Cap_FlagOn_AtResidentialSpeed_LimitsToKTimesGradePercent()
    {
        // residential: K_sag=4. zDiff=10m over L≈95m → chordGrade≈+10.5% → A=10.5%, sag.
        // L_cap = 4 × 10.5 ≈ 42m. Adaptive 95m → cap to ~42m, but never below configured floor (50).
        var capped = UnifiedJunctionProfileBlender.CalculateAdaptiveBlendDistanceForTesting(
            configuredBlendDistance: 50f,
            harmonizedElevation: 110f,
            contributorElevation: 100f,
            roadMaxSlopeDegrees: 6f,
            enableMaxSlopeConstraint: true,
            effectiveDesignSpeedKmh: 30,
            jhParams: new JunctionHarmonizationParameters { EnableAashtoBlendDistanceCap = true });
        Assert.InRange(capped, 40f, 55f);
    }

    [Fact]
    public void Cap_FlagOn_AtMotorwaySpeed_NeverExtendsBeyondAdaptive()
    {
        // motorway K_sag=57, A=10.5% → cap ≈ 600m. Adaptive ≈ 95m → returned = 95m.
        var result = UnifiedJunctionProfileBlender.CalculateAdaptiveBlendDistanceForTesting(
            configuredBlendDistance: 50f,
            harmonizedElevation: 110f,
            contributorElevation: 100f,
            roadMaxSlopeDegrees: 6f,
            enableMaxSlopeConstraint: true,
            effectiveDesignSpeedKmh: 120,
            jhParams: new JunctionHarmonizationParameters { EnableAashtoBlendDistanceCap = true });
        Assert.InRange(result, 90f, 100f);
    }

    [Fact]
    public void Cap_FlagOn_FallbackSpeed30_UsesResidentialCap()
    {
        var capped = UnifiedJunctionProfileBlender.CalculateAdaptiveBlendDistanceForTesting(
            configuredBlendDistance: 50f,
            harmonizedElevation: 110f,
            contributorElevation: 100f,
            roadMaxSlopeDegrees: 6f,
            enableMaxSlopeConstraint: true,
            effectiveDesignSpeedKmh: 30,
            jhParams: new JunctionHarmonizationParameters { EnableAashtoBlendDistanceCap = true });
        Assert.InRange(capped, 40f, 55f);
    }
}
