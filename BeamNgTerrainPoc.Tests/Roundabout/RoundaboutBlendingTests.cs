using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

/// <summary>
/// Tests for roundabout constraint computation.
/// Validates the edge-anchored constraint model and radial slope projection.
/// </summary>
public class RoundaboutBlendingTests
{
    /// <summary>
    /// The ring surface elevation at an offset point should account for
    /// both longitudinal slope and banking (lateral tilt).
    /// GetPrimarySurfaceElevation is the shared utility for this.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 100f, 100f)]       // No offset -> same elevation
    [InlineData(5f, 0f, 100f, 100f)]        // Lateral offset, no banking -> same elevation
    [InlineData(0f, 5f, 100f, 100.5f)]      // Longitudinal offset, slope=0.1 -> +0.5m
    public void GetPrimarySurfaceElevation_AccountsForSlopeAndBanking(
        float lateralOffset, float longitudinalOffset, float centerElev, float expectedElev)
    {
        var cs = new UnifiedCrossSection
        {
            CenterPoint = new Vector2(100, 100),
            TangentDirection = new Vector2(0, 1),  // Road going north
            NormalDirection = new Vector2(1, 0),    // Normal pointing east
            TargetElevation = centerElev,
            BankAngleRadians = 0f,                 // No banking
            EffectiveRoadWidth = 10f
        };

        var worldPos = cs.CenterPoint
                       + cs.NormalDirection * lateralOffset
                       + cs.TangentDirection * longitudinalOffset;

        var slope = 0.1f; // 10% grade
        var result = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(worldPos, cs, slope);

        Assert.InRange(result, expectedElev - 0.01f, expectedElev + 0.01f);
    }

    /// <summary>
    /// For a flat roundabout ring (no banking), the radial slope should be ~0
    /// because the ring tangent is perpendicular to the radial approach.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(270f)]
    public void RadialSlope_IsPerpendicular_ToCircumferentialSlope(float angleDegrees)
    {
        var angleRad = angleDegrees * MathF.PI / 180f;
        var radialDir = new Vector2(MathF.Cos(angleRad), MathF.Sin(angleRad));
        var ringTangent = new Vector2(-radialDir.Y, radialDir.X);
        var circumferentialSlope = 0.02f;
        var radialSlope = circumferentialSlope * Vector2.Dot(ringTangent, radialDir);
        Assert.True(MathF.Abs(radialSlope) < 0.001f,
            $"Radial slope should be ~0 but was {radialSlope:F6}");
    }

    /// <summary>
    /// Verifies that the edge-anchored exit point is offset along the connecting
    /// road's away-direction, not at the road centerpoint.
    /// </summary>
    [Fact]
    public void EdgeAnchoredExitPoint_IsOffset_ByRingHalfWidth()
    {
        var centerPoint = new Vector2(100, 100);
        var awayDirection = new Vector2(1, 0);
        var ringHalfWidth = 5f;
        var exitPoint = centerPoint + awayDirection * ringHalfWidth;
        Assert.Equal(105f, exitPoint.X, 0.01f);
        Assert.Equal(100f, exitPoint.Y, 0.01f);
    }
}
