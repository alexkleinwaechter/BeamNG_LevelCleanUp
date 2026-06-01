using System.Collections.Generic;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     The roundabout ring tilts only when the per-map opt-in flag
///     <see cref="JunctionHarmonizationParameters.EnableTiltedRoundaboutPlane" /> is set. Default is a flat
///     ring (the tilt spread overlapping connector mouths to different Z and they stepped — the south-cluster
///     kink). The no-blend/affine path is now unconditional, so the gate depends only on the tilt flag.
///     These guard <see cref="UnifiedRoadSmoother.ShouldUseTiltedRoundaboutPlane" />.
/// </summary>
public class TiltedRoundaboutGateTests
{
    private static UnifiedRoadNetwork NetworkWith(bool tiltFlag)
    {
        var jh = new JunctionHarmonizationParameters
        {
            EnableTiltedRoundaboutPlane = tiltFlag
        };
        var spline = new ParameterizedRoadSpline
        {
            SplineId = 1,
            Priority = 5,
            MaterialName = "asphalt",
            Spline = new RoadSpline(
                new List<Vector2> { new(0f, 0f), new(1f, 0f) },
                SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters { JunctionHarmonizationParameters = jh }
        };
        var network = new UnifiedRoadNetwork();
        network.AddSpline(spline);
        return network;
    }

    [Fact]
    public void FlagOff_StaysFlat()
    {
        Assert.False(UnifiedRoadSmoother.ShouldUseTiltedRoundaboutPlane(NetworkWith(tiltFlag: false)));
    }

    [Fact]
    public void FlagOn_Tilts()
    {
        Assert.True(UnifiedRoadSmoother.ShouldUseTiltedRoundaboutPlane(NetworkWith(tiltFlag: true)));
    }
}
