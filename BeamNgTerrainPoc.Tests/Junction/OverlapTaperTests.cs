using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class OverlapTaperTests
{
    [Fact]
    public void Compute_AtAnchor_ReturnsZero()
    {
        // d=0 → x=0 → smoothstep(0) = 0.
        var taper = OverlapTaper.Compute(distFromAnchor: 0f, blendLength: 30f);
        Assert.Equal(0f, taper, 4);
    }

    [Fact]
    public void Compute_AtBoundary_ReturnsOne()
    {
        // d=L → x=1 → smoothstep(1) = 1.
        var taper = OverlapTaper.Compute(distFromAnchor: 30f, blendLength: 30f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_AtMidPoint_ReturnsHalf()
    {
        // d=L/2 → x=0.5 → smoothstep(0.5) = 0.25 * (3 - 1) = 0.5.
        var taper = OverlapTaper.Compute(distFromAnchor: 15f, blendLength: 30f);
        Assert.Equal(0.5f, taper, 4);
    }

    [Fact]
    public void Compute_BeyondBoundary_ReturnsOne()
    {
        // d > L → clamp(d/L) = 1 → smoothstep(1) = 1.
        var taper = OverlapTaper.Compute(distFromAnchor: 100f, blendLength: 30f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_NegativeDistance_ReturnsZero()
    {
        // d < 0 → clamp(d/L) = 0 → smoothstep(0) = 0. Defensive against caller bugs.
        var taper = OverlapTaper.Compute(distFromAnchor: -5f, blendLength: 30f);
        Assert.Equal(0f, taper, 4);
    }

    [Fact]
    public void Compute_ZeroBlendLength_ReturnsOne()
    {
        // L=0 → no zone exists → no taper. Avoid divide-by-zero.
        var taper = OverlapTaper.Compute(distFromAnchor: 0f, blendLength: 0f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_NegativeBlendLength_ReturnsOne()
    {
        // L<0 is malformed input → defensive: behave as no zone.
        var taper = OverlapTaper.Compute(distFromAnchor: 5f, blendLength: -10f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_Monotone_NonDecreasing()
    {
        // As d increases from 0 to L, taper must monotonically increase from 0 to 1.
        var prev = -1f;
        for (var d = 0f; d <= 30f; d += 0.5f)
        {
            var t = OverlapTaper.Compute(d, 30f);
            Assert.True(t >= prev, $"d={d}: t={t} < prev={prev}");
            prev = t;
        }
    }

    [Fact]
    public void Compute_C1AtEndpoints_NumericalDerivativeIsZero()
    {
        // Smoothstep has zero derivative at both endpoints (C¹). Numerical check.
        var eps = 0.001f;

        var t0 = OverlapTaper.Compute(0f, 30f);
        var tEps = OverlapTaper.Compute(eps, 30f);
        Assert.True(MathF.Abs(tEps - t0) < 0.001f,
            $"Derivative at d=0 should be ~0; observed (t(eps)-t(0))/eps = {(tEps - t0) / eps}");

        var tL = OverlapTaper.Compute(30f, 30f);
        var tLMinusEps = OverlapTaper.Compute(30f - eps, 30f);
        Assert.True(MathF.Abs(tL - tLMinusEps) < 0.001f,
            $"Derivative at d=L should be ~0; observed (t(L)-t(L-eps))/eps = {(tL - tLMinusEps) / eps}");
    }
}
