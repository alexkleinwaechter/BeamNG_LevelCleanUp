using BeamNgTerrainPoc.Terrain.Algorithms;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     Tests for the "junction follows the main road" elevation policy used by the no-blend-zones
///     affine path: a T-junction's elevation is the THROUGH road's (low-pass) elevation there, so the
///     terminating road meets it without a ramp and the through road is never dragged off its profile.
/// </summary>
public class ThroughRoadJunctionElevationTests
{
    [Fact]
    public void TJunction_UsesThroughRoadElevation_NotTerminating()
    {
        // 1 through road @ 154.57 (the main road), 1 terminating @ 156.77 (the side road on terrain).
        var contributors = new[] { (154.57f, true), (156.77f, false) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.Equal(154.57f, z, 3); // follows the through road, ignores the terminating road
    }

    [Fact]
    public void MultipleThroughRoads_AveragesThem()
    {
        // X-crossing: two through roads — average them (neither is dragged to terrain).
        var contributors = new[] { (150f, true), (152f, true), (160f, false) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.Equal(151f, z, 3);
    }

    [Fact]
    public void NoThroughRoad_AveragesTerminatingRoads()
    {
        // Y-junction / fork: both roads terminate, no through road to dictate — meet at their mean.
        var contributors = new[] { (150f, false), (152f, false) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.Equal(151f, z, 3);
    }

    [Fact]
    public void SingleDeadEnd_ReturnsNaN_LeavesRoadOnItsNaturalProfile()
    {
        // A lone road end has nothing to meet — no correction (NaN => caller applies no target).
        var contributors = new[] { (150f, false) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.True(float.IsNaN(z));
    }

    [Fact]
    public void NoContributors_ReturnsNaN()
    {
        var z = ThroughRoadJunctionElevation.Compute(System.Array.Empty<(float, bool)>());

        Assert.True(float.IsNaN(z));
    }

    [Fact]
    public void IgnoresNonFiniteElevations()
    {
        // A through road with a finite value dominates; NaN contributors are skipped.
        var contributors = new[] { (154f, true), (float.NaN, false), (float.PositiveInfinity, true) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.Equal(154f, z, 3);
    }

    [Fact]
    public void AllThroughNonFinite_FallsBackToTerminatingMean()
    {
        var contributors = new[] { (float.NaN, true), (148f, false), (152f, false) };

        var z = ThroughRoadJunctionElevation.Compute(contributors);

        Assert.Equal(150f, z, 3);
    }
}
