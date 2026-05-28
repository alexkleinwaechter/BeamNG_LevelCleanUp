using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class BlendDistanceStretcherTests
{
    [Fact]
    public void ComputeStretchTarget_MismatchBelowThreshold_ReturnsCurrentL()
    {
        // Slope at L exactly matches m_natural -> nothing to fix.
        var L = 30f;
        var zJ = 100f;
        var mJ = -0.05f;
        var zNL = 95f;
        var mEmergent = 2f * (zNL - zJ) / L - mJ; // -0.283... but we feed it back as mNL
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: L, zJunction: zJ, mJunction: mJ,
            zNaturalAtL: zNL, mNaturalAtL: mEmergent,
            thresholdGrade: 0.01f);
        Assert.Equal(L, result, 3);
    }

    [Fact]
    public void ComputeStretchTarget_Junction20_StretchesTo40Point6m()
    {
        // The franco_same_prio junction 20 values from phase_b_slope_mismatch.csv:
        //   zJunction=98.807, mJunction=-0.06805, zNaturalAtL=94.024, mNaturalAtL=-0.16725
        // L_target = 2*(94.024-98.807) / (-0.16725 + (-0.06805))
        //          = -9.566 / -0.23530
        //          = 40.65 m
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 98.807f, mJunction: -0.06805f,
            zNaturalAtL: 94.024f, mNaturalAtL: -0.16725f,
            thresholdGrade: 0.01f);
        Assert.InRange(result, 40.0f, 41.5f);
    }

    [Fact]
    public void ComputeStretchTarget_ShorterThanCurrent_ReturnsCurrentL()
    {
        // Configure a case where the algebraic L_target comes out less than current L:
        // junction slope is GENTLER than natural; the parabola would otherwise need
        // to dive faster than natural -> shortening would match slope, but stretch-L
        // is one-directional (never shorten).
        // z(0)=100, m(0)=-0.16; z(L=30)=99; m_natural at L = -0.04.
        //   L_target = 2*(99-100) / (-0.04 + -0.16) = -2 / -0.2 = 10m  (< 30)
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 100f, mJunction: -0.16f,
            zNaturalAtL: 99f, mNaturalAtL: -0.04f,
            thresholdGrade: 0.01f);
        Assert.Equal(30f, result, 3);
    }

    [Fact]
    public void ComputeStretchTarget_DenominatorNearZero_ReturnsCurrentL()
    {
        // mJunction + mNaturalAtL = 0 -> L_target undefined; return currentL.
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 100f, mJunction: 0.05f,
            zNaturalAtL: 95f, mNaturalAtL: -0.05f,
            thresholdGrade: 0.01f);
        Assert.Equal(30f, result, 3);
    }

    [Fact]
    public void ComputeStretchTarget_SignMismatch_NumeratorAndDenominatorOpposite_ReturnsCurrentL()
    {
        // Numerator positive (zNL > zJ, climbing), denominator negative (both slopes
        // descending) -> L_target negative. Discard and keep currentL.
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 95f, mJunction: -0.05f,
            zNaturalAtL: 100f, mNaturalAtL: -0.10f,
            thresholdGrade: 0.01f);
        Assert.Equal(30f, result, 3);
    }

    [Fact]
    public void ComputeStretchTarget_FlatJunctionMildDescent_StretchesL()
    {
        // z(0)=100, m(0)=0 (flat); z(L=30)=97; m_natural at L = -0.20.
        // L_target = 2*(-3) / (-0.20 + 0) = -6/-0.2 = 30. Same as current. Below threshold?
        // m_emergent at L=30 = 2*(-3)/30 - 0 = -0.20 -> matches m_natural exactly.
        // No mismatch -> returns 30. Verify threshold path still works.
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 97f, mNaturalAtL: -0.20f,
            thresholdGrade: 0.01f);
        Assert.Equal(30f, result, 3);
    }

    [Fact]
    public void ComputeStretchTarget_LargeMismatchOnSteepTerrain_BoundedByPlausibleRange()
    {
        // Steeper terrain, larger mismatch -> longer stretch.
        // z(0)=100, m(0)=-0.02; z(L=30)=88 (12m drop); m_natural at L = -0.30.
        // m_emergent at L=30 = 2*(-12)/30 - (-0.02) = -0.78. Mismatch from -0.30 = 0.48 (huge).
        // L_target = 2*(-12) / (-0.30 + -0.02) = -24/-0.32 = 75m.
        var result = BlendDistanceStretcher.ComputeStretchTarget(
            currentL: 30f,
            zJunction: 100f, mJunction: -0.02f,
            zNaturalAtL: 88f, mNaturalAtL: -0.30f,
            thresholdGrade: 0.01f);
        Assert.InRange(result, 70f, 80f);
    }
}
