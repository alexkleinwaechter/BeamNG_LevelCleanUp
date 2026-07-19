using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.RoadGeometry;

/// <summary>
/// Regression tests for degenerate control-point geometry (alexanderplatz NaN-node crash,
/// 2026-06-12). Duplicate consecutive control points produce duplicate arc-length knots;
/// MathNet's *Sorted interpolators then yield NaN coefficients, so every sampled position
/// becomes (NaN, NaN) — which poisoned grade-separation detection and crashed the DecalRoad
/// JSON scene writer. The RoadSpline constructor must enforce strictly increasing knots.
/// </summary>
public class RoadSplineDegenerateGeometryTests
{
    private static void AssertAllSamplesFinite(RoadSpline spline)
    {
        var samples = spline.SampleByDistance(0.5f);
        Assert.NotEmpty(samples);
        foreach (var s in samples)
        {
            Assert.True(float.IsFinite(s.Position.X), $"Position.X not finite at d={s.Distance}");
            Assert.True(float.IsFinite(s.Position.Y), $"Position.Y not finite at d={s.Distance}");
            Assert.True(float.IsFinite(s.Tangent.X), $"Tangent.X not finite at d={s.Distance}");
            Assert.True(float.IsFinite(s.Tangent.Y), $"Tangent.Y not finite at d={s.Distance}");
        }
    }

    [Fact]
    public void DuplicateInteriorPoint_AkimaPath_ProducesFiniteSamples()
    {
        // ≥5 points → Akima branch (the one that NaNs on duplicate knots)
        var points = new List<Vector2>
        {
            new(0, 0),
            new(2, 0.5f),
            new(4, 1.0f),
            new(4, 1.0f), // exact duplicate — previously created a duplicate knot
            new(6, 1.5f),
            new(8, 2.0f)
        };

        var spline = new RoadSpline(points);

        Assert.Equal(5, spline.ControlPoints.Count); // duplicate dropped
        AssertAllSamplesFinite(spline);
    }

    [Fact]
    public void ManyDuplicates_ShortSpline_ProducesFiniteSamples()
    {
        // Mirrors the alexanderplatz failure shape: a ~3.5m spline with many coincident points.
        var points = new List<Vector2>();
        for (var i = 0; i < 7; i++)
        {
            points.Add(new Vector2(i * 0.5f, 0));
            points.Add(new Vector2(i * 0.5f, 0)); // every point duplicated
        }

        var spline = new RoadSpline(points);

        Assert.Equal(7, spline.ControlPoints.Count);
        AssertAllSamplesFinite(spline);
    }

    [Fact]
    public void NonFiniteControlPoint_IsDropped()
    {
        var points = new List<Vector2>
        {
            new(0, 0),
            new(2, 0),
            new(float.NaN, float.NaN), // distance to/from this point is NaN → dropped
            new(4, 0),
            new(6, 0),
            new(8, 0)
        };

        var spline = new RoadSpline(points);

        Assert.Equal(5, spline.ControlPoints.Count);
        AssertAllSamplesFinite(spline);
    }

    [Fact]
    public void AllPointsIdentical_StillThrowsZeroLength()
    {
        var p = new Vector2(3, 7);
        var points = new List<Vector2> { p, p, p };

        Assert.Throws<ArgumentException>(() => new RoadSpline(points));
    }

    [Fact]
    public void CleanSpline_KeepsCallerListReference()
    {
        var points = new List<Vector2>
        {
            new(0, 0), new(2, 1), new(4, 0), new(6, 1), new(8, 0)
        };

        var spline = new RoadSpline(points);

        Assert.Same(points, spline.ControlPoints);
        AssertAllSamplesFinite(spline);
    }

    [Fact]
    public void LinearInterpolation_DuplicatePoints_ProducesFiniteSamples()
    {
        var points = new List<Vector2>
        {
            new(0, 0),
            new(5, 0),
            new(5, 0), // duplicate on the linear path too
            new(10, 0)
        };

        var spline = RoadSpline.CreateLinear(points);

        Assert.Equal(3, spline.ControlPoints.Count);
        AssertAllSamplesFinite(spline);
    }
}
