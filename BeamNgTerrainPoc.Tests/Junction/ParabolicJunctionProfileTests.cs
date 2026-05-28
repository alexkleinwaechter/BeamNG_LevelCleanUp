using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class ParabolicJunctionProfileTests
{
    [Fact]
    public void Sample_AtJunctionDistance0_ReturnsJunctionElevation()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 0f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_AtBlendEndDistanceL_ReturnsNaturalElevationAtL()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 30f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(95f, z, 4);
    }

    [Fact]
    public void Sample_MonotoneDescent_DoesNotOvershootAboveJunctionZ()
    {
        for (var d = 0f; d <= 30f; d += 1f)
        {
            var z = ParabolicJunctionProfile.Sample(
                d, blendLength: 30f,
                zJunction: 100f, mJunction: 0f, zNaturalAtL: 90f);

            Assert.InRange(z, 89.999f, 100.001f);
        }
    }

    [Fact]
    public void Sample_MonotoneAscent_DoesNotOvershootBelowJunctionZ()
    {
        for (var d = 0f; d <= 30f; d += 1f)
        {
            var z = ParabolicJunctionProfile.Sample(
                d, blendLength: 30f,
                zJunction: 100f, mJunction: 0f, zNaturalAtL: 110f);

            Assert.InRange(z, 99.999f, 110.001f);
        }
    }

    [Fact]
    public void Sample_AtJunction_SlopeMatchesMJunction()
    {
        var eps = 0.001f;
        var z0 = ParabolicJunctionProfile.Sample(
            0f, 30f, zJunction: 100f, mJunction: -0.04f, zNaturalAtL: 95f);
        var zEps = ParabolicJunctionProfile.Sample(
            eps, 30f, zJunction: 100f, mJunction: -0.04f, zNaturalAtL: 95f);

        var observedSlope = (zEps - z0) / eps;
        Assert.Equal(-0.04f, observedSlope, 2);
    }

    [Fact]
    public void Sample_ZeroBlendLength_ReturnsJunctionElevation()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 0f, blendLength: 0f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_BeyondBlendLength_ReturnsNaturalElevationAtL()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 100f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(95f, z, 4);
    }
}
