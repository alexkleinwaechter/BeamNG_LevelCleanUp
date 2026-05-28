using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class CubicJunctionProfileTests
{
    [Fact]
    public void Sample_AtJunctionD0_ReturnsJunctionElevation()
    {
        var z = CubicJunctionProfile.Sample(
            d: 0f, blendLength: 30f,
            zJunction: 100f, mJunction: -0.04f,
            zNaturalAtL: 95f, mNaturalAtL: -0.04f);
        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_AtBlendEndDL_ReturnsNaturalElevation()
    {
        var z = CubicJunctionProfile.Sample(
            d: 30f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 95f, mNaturalAtL: -0.04f);
        Assert.Equal(95f, z, 4);
    }

    [Fact]
    public void Sample_NumericalSlopeAtD0_MatchesMJunction()
    {
        var eps = 0.1f;
        var z0 = CubicJunctionProfile.Sample(
            0f, 30f, 100f, -0.05f, 95f, -0.04f);
        var zEps = CubicJunctionProfile.Sample(
            eps, 30f, 100f, -0.05f, 95f, -0.04f);
        var observedSlope = (zEps - z0) / eps;
        Assert.Equal(-0.05f, observedSlope, 2);
    }

    [Fact]
    public void Sample_NumericalSlopeAtDL_MatchesMNaturalAtL()
    {
        var eps = 0.1f;
        var L = 30f;
        var zL = CubicJunctionProfile.Sample(
            L, L, 100f, 0f, 95f, -0.04f);
        var zLMinusEps = CubicJunctionProfile.Sample(
            L - eps, L, 100f, 0f, 95f, -0.04f);
        var observedSlope = (zL - zLMinusEps) / eps;
        Assert.Equal(-0.04f, observedSlope, 2);
    }

    [Fact]
    public void Sample_MonotoneDescent_StaysInBoundingBox()
    {
        // Both anchor slopes match the descent direction → no overshoot expected.
        // z(0)=100, m(0)=-0.05; z(L=30)=98.5 (50m below), m(L)=-0.05.
        for (var d = 0f; d <= 30f; d += 1f)
        {
            var z = CubicJunctionProfile.Sample(
                d, 30f, 100f, -0.05f, 98.5f, -0.05f);
            Assert.InRange(z, 98.4f, 100.1f);
        }
    }

    [Fact]
    public void Sample_ZeroBlendLength_ReturnsJunctionElevation()
    {
        var z = CubicJunctionProfile.Sample(
            d: 0f, blendLength: 0f,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 95f, mNaturalAtL: 0f);
        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_BeyondBlendEnd_ReturnsClampedAtL()
    {
        var z = CubicJunctionProfile.Sample(
            d: 100f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 95f, mNaturalAtL: -0.04f);
        Assert.Equal(95f, z, 4);
    }

    [Fact]
    public void Sample_MatchesParabolic_WhenMNaturalAtLEqualsEmergentSlope()
    {
        // When mNaturalAtL = 2·(zNaturalAtL − zJunction)/L − mJunction (the parabola's
        // emergent slope at L), the cubic degenerates to the parabola.
        var L = 30f;
        var zJ = 100f;
        var mJ = -0.02f;
        var zL = 92f;
        var emergentSlope = 2f * (zL - zJ) / L - mJ;
        for (var d = 0f; d <= L; d += 2f)
        {
            var zCubic = CubicJunctionProfile.Sample(d, L, zJ, mJ, zL, emergentSlope);
            var zParab = ParabolicJunctionProfile.Sample(d, L, zJ, mJ, zL);
            Assert.Equal(zParab, zCubic, 3);
        }
    }
}
