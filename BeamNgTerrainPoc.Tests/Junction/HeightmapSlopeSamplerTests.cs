using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class HeightmapSlopeSamplerTests
{
    private static float[,] BuildXGradientHeightmap(int size, float gradientPerMeter, float metersPerPixel)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                hm[y, x] = x * metersPerPixel * gradientPerMeter + 100f;
        return hm;
    }

    [Fact]
    public void SampleAlongTangent_AlongXGradient_TangentXPlus_ReturnsPositiveSlope()
    {
        var hm = BuildXGradientHeightmap(size: 10, gradientPerMeter: 0.05f, metersPerPixel: 1f);
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(5f, 5f),
            tangent: new Vector2(1f, 0f),
            sampleDistanceMeters: 2.0f);
        Assert.Equal(0.05f, slope, 3);
    }

    [Fact]
    public void SampleAlongTangent_TangentXMinus_ReturnsNegativeSlope()
    {
        var hm = BuildXGradientHeightmap(size: 10, gradientPerMeter: 0.05f, metersPerPixel: 1f);
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(5f, 5f),
            tangent: new Vector2(-1f, 0f),
            sampleDistanceMeters: 2.0f);
        Assert.Equal(-0.05f, slope, 3);
    }

    [Fact]
    public void SampleAlongTangent_TangentYAlongXGradient_ReturnsZero()
    {
        var hm = BuildXGradientHeightmap(size: 10, gradientPerMeter: 0.05f, metersPerPixel: 1f);
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(5f, 5f),
            tangent: new Vector2(0f, 1f),
            sampleDistanceMeters: 2.0f);
        Assert.Equal(0f, slope, 3);
    }

    [Fact]
    public void SampleAlongTangent_FlatHeightmap_ReturnsZero()
    {
        var hm = new float[10, 10];
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                hm[y, x] = 100f;
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(5f, 5f),
            tangent: new Vector2(1f, 0f),
            sampleDistanceMeters: 2.0f);
        Assert.Equal(0f, slope, 3);
    }

    [Fact]
    public void SampleAlongTangent_NearEdge_ClampsAndStillReturnsFiniteValue()
    {
        var hm = BuildXGradientHeightmap(size: 10, gradientPerMeter: 0.05f, metersPerPixel: 1f);
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(0.5f, 0.5f),
            tangent: new Vector2(-1f, 0f),
            sampleDistanceMeters: 2.0f);
        Assert.False(float.IsNaN(slope));
        Assert.False(float.IsInfinity(slope));
    }

    [Fact]
    public void SampleAlongTangent_DiagonalTangent_ProjectsCorrectly()
    {
        var hm = BuildXGradientHeightmap(size: 10, gradientPerMeter: 0.05f, metersPerPixel: 1f);
        var tangent = Vector2.Normalize(new Vector2(1f, 1f));
        var slope = HeightmapSlopeSampler.SampleAlongTangent(
            hm, metersPerPixel: 1f,
            position: new Vector2(5f, 5f),
            tangent: tangent,
            sampleDistanceMeters: 2.0f);
        Assert.Equal(0.0354f, slope, 3);
    }
}
