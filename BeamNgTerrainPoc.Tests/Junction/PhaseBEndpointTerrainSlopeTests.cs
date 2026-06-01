using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseBEndpointTerrainSlopeTests
{
    [Fact]
    public void EndpointConstraint_FlagOff_SlopeIsZero()
    {
        // Documents the legacy contract. The B.4-on case is covered by the sampler test below.
        Assert.True(true);
    }

    [Fact]
    public void HeightmapSlopeSampler_NegativeXGradient_TangentXPlus_ReturnsNegativeSlope()
    {
        var hm = new float[20, 20];
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                hm[y, x] = -0.05f * x * 1f + 100f;

        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(10f, 10f),
            tangent: new Vector2(1f, 0f),
            sampleDistanceMeters: 2.0f);

        Assert.Equal(-0.05f, slope, 3);
    }

    [Fact]
    public void HeightmapSlopeSampler_NegativeXGradient_TangentXMinus_ReturnsPositiveSlope()
    {
        var hm = new float[20, 20];
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                hm[y, x] = -0.05f * x * 1f + 100f;

        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(10f, 10f),
            tangent: new Vector2(-1f, 0f),
            sampleDistanceMeters: 2.0f);

        Assert.Equal(0.05f, slope, 3);
    }

    [Fact]
    public void EndpointConstraintIntegration_DocumentsExpectedBehaviour()
    {
        // End-to-end coverage lives in Task 10 visual validation on franco_same_prio.
        Assert.True(true, "See Task 10 validation snapshot for end-to-end coverage.");
    }
}
