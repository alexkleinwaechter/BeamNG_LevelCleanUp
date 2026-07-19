using BeamNgTerrainPoc.Terrain.Algorithms;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

public class AffineJunctionLevelerTests
{
    private static float[] Distances(int n, float spacing = 1f)
    {
        var d = new float[n];
        for (var i = 0; i < n; i++) d[i] = i * spacing;
        return d;
    }

    [Fact]
    public void BothTargets_HitsBothEndpointsExactly()
    {
        var elev = new[] { 0f, 0f, 0f, 0f, 0f };
        var dist = Distances(5); // L = 4

        var modified = AffineJunctionLeveler.Apply(elev, dist, targetStart: 2f, targetEnd: 6f);

        Assert.Equal(2f, elev[0], 3);
        Assert.Equal(6f, elev[4], 3);
        // Affine ramp 2 + d → [2,3,4,5,6]
        Assert.Equal(new[] { 2f, 3f, 4f, 5f, 6f }, elev);
        Assert.Equal(5, modified);
    }

    [Fact]
    public void PreservesCurvature_SecondDifferencesUnchanged()
    {
        var original = new[] { 0f, 1f, 0f, 1f, 0f };
        var elev = (float[])original.Clone();
        var dist = Distances(5);

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 10f, targetEnd: -5f);

        // Affine correction cannot change second differences (curvature).
        for (var i = 1; i < original.Length - 1; i++)
        {
            var d2Orig = original[i + 1] - 2 * original[i] + original[i - 1];
            var d2New = elev[i + 1] - 2 * elev[i] + elev[i - 1];
            Assert.Equal(d2Orig, d2New, 3);
        }
        // Endpoints still land on targets.
        Assert.Equal(10f, elev[0], 3);
        Assert.Equal(-5f, elev[4], 3);
    }

    [Fact]
    public void StartOnly_FarEndUnchanged()
    {
        var elev = new[] { 0f, 0f, 0f, 0f, 0f };
        var dist = Distances(5); // L = 4

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 4f, targetEnd: null);

        Assert.Equal(4f, elev[0], 3);   // start hits target
        Assert.Equal(0f, elev[4], 3);   // free end untouched
        Assert.Equal(new[] { 4f, 3f, 2f, 1f, 0f }, elev);
    }

    [Fact]
    public void EndOnly_StartUnchanged()
    {
        var elev = new[] { 0f, 0f, 0f, 0f, 0f };
        var dist = Distances(5);

        AffineJunctionLeveler.Apply(elev, dist, targetStart: null, targetEnd: 8f);

        Assert.Equal(0f, elev[0], 3);   // free start untouched
        Assert.Equal(8f, elev[4], 3);   // end hits target
        Assert.Equal(new[] { 0f, 2f, 4f, 6f, 8f }, elev);
    }

    [Fact]
    public void NoTargets_NoChange()
    {
        var elev = new[] { 1f, 2f, 3f };
        var dist = Distances(3);

        var modified = AffineJunctionLeveler.Apply(elev, dist, null, null);

        Assert.Equal(0, modified);
        Assert.Equal(new[] { 1f, 2f, 3f }, elev);
    }

    [Fact]
    public void ZeroLengthSpline_NoChange()
    {
        var elev = new[] { 5f, 5f };
        var dist = new[] { 0f, 0f };

        var modified = AffineJunctionLeveler.Apply(elev, dist, targetStart: 9f, targetEnd: 9f);

        Assert.Equal(0, modified);
    }

    // ── Doc 08 §7 C3: bounded decay for bridge-raised junction corrections ────────────────────────────

    [Fact]
    public void DecayedStart_HitsTarget_ZeroBeyondDecayLength()
    {
        // 400 m road, +14 m error at the start, 100 m decay: the correction is full at the junction,
        // eased mid-run (w(0.5) = 0.5), and exactly zero past 100 m — no full-length embankment tilt.
        var n = 401;
        var elev = new float[n];
        var dist = Distances(n); // L = 400

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 14f, targetEnd: null, decayLengthStart: 100f);

        Assert.Equal(14f, elev[0], 3);        // junction end holds the target exactly
        Assert.Equal(7f, elev[50], 2);        // eased midpoint: 14·w(0.5) = 7
        Assert.Equal(0f, elev[100], 3);       // decay length reached
        Assert.Equal(0f, elev[250], 3);       // far body untouched
        Assert.Equal(0f, elev[400], 3);
    }

    [Fact]
    public void DecayedEnd_MirrorsDecayedStart()
    {
        var n = 401;
        var elev = new float[n];
        var dist = Distances(n);

        AffineJunctionLeveler.Apply(elev, dist, targetStart: null, targetEnd: 10f, decayLengthEnd: 100f);

        Assert.Equal(10f, elev[400], 3);
        Assert.Equal(5f, elev[350], 2);
        Assert.Equal(0f, elev[300], 3);
        Assert.Equal(0f, elev[0], 3);
    }

    [Fact]
    public void DecayedBothEnds_MiddleUntouched()
    {
        // The winningen 281 service-loop shape: both ends anchored high at the same bridge junction —
        // legacy affine held the WHOLE loop at +14; decayed, the middle returns to the solved profile.
        var n = 401;
        var elev = new float[n];
        var dist = Distances(n);

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 14f, targetEnd: 14f,
            decayLengthStart: 100f, decayLengthEnd: 100f);

        Assert.Equal(14f, elev[0], 3);
        Assert.Equal(14f, elev[400], 3);
        Assert.Equal(0f, elev[200], 3); // loop middle back on its own profile
    }

    [Fact]
    public void MixedEnds_DecayedStartLegacyEnd()
    {
        // Decayed start composes with a legacy (full-length linear) end correction.
        var n = 401;
        var elev = new float[n];
        var dist = Distances(n);

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 14f, targetEnd: 4f, decayLengthStart: 100f);

        Assert.Equal(14f, elev[0], 2);   // eased start weight is 1, legacy end weight is 0 at t=0
        Assert.Equal(4f, elev[400], 2);  // end lands on its target (linear weight 1)
        Assert.Equal(2f, elev[200], 2);  // start decay is 0 here; end linear = 4·0.5
    }

    [Fact]
    public void DecayLongerThanSpline_ClampsToLegacyShape()
    {
        // A 4 m spline with a 100 m decay: the run clamps to the spline, and the eased weight still
        // reaches 0 exactly at the far end — no residual step.
        var elev = new[] { 0f, 0f, 0f, 0f, 0f };
        var dist = Distances(5); // L = 4

        AffineJunctionLeveler.Apply(elev, dist, targetStart: 4f, targetEnd: null, decayLengthStart: 100f);

        Assert.Equal(4f, elev[0], 3);
        Assert.Equal(0f, elev[4], 3);
    }

    [Fact]
    public void NullDecay_ByteIdenticalToLegacy()
    {
        var elevLegacy = new[] { 1f, 0f, 2f, 0f, 1f };
        var elevDecayNull = (float[])elevLegacy.Clone();
        var dist = Distances(5);

        AffineJunctionLeveler.Apply(elevLegacy, dist, targetStart: 3f, targetEnd: 7f);
        AffineJunctionLeveler.Apply(elevDecayNull, dist, targetStart: 3f, targetEnd: 7f,
            decayLengthStart: null, decayLengthEnd: null);

        Assert.Equal(elevLegacy, elevDecayNull);
    }
}
