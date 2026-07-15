using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     The engineered approach-ramp vertical profile (2026-07-13 shape rework): parabolic crest curve
///     at the deck end (u &lt; a), constant-grade tangent, parabolic sag curve at the ramp end
///     (u &gt; 1−a). Weight 1 / flat at u=0, weight 0 / flat at u=1, monotone in between, and the
///     tangent grade is exactly the class grade when the ramp is sized by <c>LengthFor</c>.
/// </summary>
public class ApproachRampProfileTests
{
    private const float A = ApproachRampProfile.VerticalCurveFraction;
    private const float G = ApproachRampProfile.TangentGradeFactor;

    [Fact]
    public void Endpoints_AreExact()
    {
        Assert.Equal(1f, ApproachRampProfile.Weight(0f));
        Assert.Equal(0f, ApproachRampProfile.Weight(1f));
        Assert.Equal(1f, ApproachRampProfile.Weight(-0.5f)); // clamped outside the ramp
        Assert.Equal(0f, ApproachRampProfile.Weight(1.5f));
    }

    [Fact]
    public void Ends_AreFlat_SeamsStayKinkFree()
    {
        const float h = 1e-3f;
        Assert.Equal(0f, (1f - ApproachRampProfile.Weight(h)) / h, 0.01f); // slope ≈ 0 at the abutment
        Assert.Equal(0f, ApproachRampProfile.Weight(1f - h) / h, 0.01f); // slope ≈ 0 at the ramp end
    }

    [Fact]
    public void PiecewiseJoints_AreContinuous()
    {
        const float eps = 1e-4f;
        Assert.Equal(ApproachRampProfile.Weight(A - eps), ApproachRampProfile.Weight(A + eps), 0.001f);
        Assert.Equal(ApproachRampProfile.Weight(1f - A - eps), ApproachRampProfile.Weight(1f - A + eps), 0.001f);
    }

    [Fact]
    public void Tangent_HoldsOneConstantGrade()
    {
        // Numeric derivative across the middle section must be −G everywhere (no curvature).
        const float h = 1e-3f;
        for (var u = A + 0.02f; u <= 1f - A - 0.02f; u += 0.05f)
        {
            var slope = (ApproachRampProfile.Weight(u + h) - ApproachRampProfile.Weight(u - h)) / (2f * h);
            Assert.Equal(-G, slope, 0.01f);
        }
    }

    [Fact]
    public void Weight_IsMonotoneDecreasing()
    {
        var prev = ApproachRampProfile.Weight(0f);
        for (var u = 0.01f; u <= 1f; u += 0.01f)
        {
            var w = ApproachRampProfile.Weight(u);
            Assert.True(w <= prev + 1e-6f, $"weight must not increase (u={u:F2})");
            prev = w;
        }
    }

    [Fact]
    public void LengthFor_SizesTangentAtClassGrade()
    {
        // 4.5 m climb at 5 %: naive length 90 m; honest length 90·G = 120 m. The steepest point of the
        // profile (the tangent) then runs at exactly 5 %.
        var len = ApproachRampProfile.LengthFor(4.5f, 0.05f);
        Assert.Equal(120f, len, 0.01f);

        const float h = 1e-3f;
        var midSlope = (ApproachRampProfile.Weight(0.5f - h) - ApproachRampProfile.Weight(0.5f + h)) / (2f * h);
        Assert.Equal(0.05f, 4.5f / len * midSlope, 0.001f);
    }
}
