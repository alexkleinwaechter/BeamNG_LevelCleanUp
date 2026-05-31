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
}
