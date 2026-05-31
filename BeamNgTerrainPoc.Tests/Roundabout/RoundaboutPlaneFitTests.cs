using System;
using System.Collections.Generic;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

public class RoundaboutPlaneFitTests
{
    private static List<(Vector2 Xy, float Z)> Pts(params (float x, float y, float z)[] p)
    {
        var list = new List<(Vector2, float)>();
        foreach (var (x, y, z) in p) list.Add((new Vector2(x, y), z));
        return list;
    }

    [Fact]
    public void FlatTerrain_ZeroTilt_MeanElevation()
    {
        var pts = Pts((0, 0, 100), (10, 0, 100), (0, 10, 100), (10, 10, 100));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0f, tilt, 4);
        Assert.Equal(100f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(5, 5)), 3);
    }

    [Fact]
    public void TiltedTerrain_PlaneFollowsTilt()
    {
        // z = 0.02*x  → tilt 0.02 along +x, within the 6% cap.
        var pts = Pts((0, 0, 0f), (100, 0, 2f), (0, 100, 0f), (100, 100, 2f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0.02f, tilt, 3);
        Assert.Equal(0.02f, a, 3);
        Assert.Equal(0f, b, 3);
        Assert.Equal(1f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(50, 50)), 2);
    }

    [Fact]
    public void SteepTerrain_TiltClampedTo6Percent_ThroughCentroid()
    {
        // z = 0.20*x  → wants 20% tilt, must clamp to 6%; plane stays through the centroid.
        var pts = Pts((0, 0, 0f), (100, 0, 20f), (0, 100, 0f), (100, 100, 20f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0.06f, MathF.Sqrt(a * a + b * b), 3); // clamped magnitude
        Assert.Equal(0.20f, tilt, 2);                       // pre-clamp tilt reported
        // Centroid (50,50,10) stays on the plane → cut/fill balanced.
        Assert.Equal(10f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(50, 50)), 2);
    }

    [Fact]
    public void DegenerateInput_FallsBackToFlatMean()
    {
        // All points identical (rank-deficient) → flat plane at the mean, no NaN.
        var pts = Pts((5, 5, 7f), (5, 5, 7f), (5, 5, 7f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0f, tilt, 4);
        Assert.Equal(7f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(5, 5)), 3);
    }
}
