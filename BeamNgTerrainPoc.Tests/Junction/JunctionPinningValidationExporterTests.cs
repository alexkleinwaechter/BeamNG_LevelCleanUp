using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Junction;

public class JunctionPinningValidationExporterTests
{
    [Theory]
    [InlineData(0.0f, JunctionPinningValidationExporter.DeltaBand.Green)]
    [InlineData(0.19f, JunctionPinningValidationExporter.DeltaBand.Green)]
    [InlineData(0.20f, JunctionPinningValidationExporter.DeltaBand.Yellow)]
    [InlineData(0.49f, JunctionPinningValidationExporter.DeltaBand.Yellow)]
    [InlineData(0.50f, JunctionPinningValidationExporter.DeltaBand.Red)]
    [InlineData(1.50f, JunctionPinningValidationExporter.DeltaBand.Red)]
    [InlineData(-0.30f, JunctionPinningValidationExporter.DeltaBand.Yellow)] // negatives use |Δ|
    [InlineData(-0.60f, JunctionPinningValidationExporter.DeltaBand.Red)]
    public void ClassifyBand_UsesAbsoluteThresholds_0p2_0p5(float delta, JunctionPinningValidationExporter.DeltaBand expected)
    {
        Assert.Equal(expected, JunctionPinningValidationExporter.ClassifyBand(delta));
    }

    [Fact]
    public void ComputeResidualStats_OnFixedDeltaList_ReportsMeanSigmaMaxAbs()
    {
        // Clean dataset: mean = 0, σ² = (4+1+0+1+4)/5 = 2 → σ = √2 ≈ 1.4142, maxAbs = 2.
        var residuals = new List<float> { -2f, -1f, 0f, 1f, 2f };
        var stats = JunctionPinningValidationExporter.ComputeResidualStats(residuals);

        Assert.Equal(5, stats.Count);
        Assert.Equal(0f, stats.Mean, 3);
        Assert.Equal(MathF.Sqrt(2f), stats.Sigma, 3);
        Assert.Equal(2f, stats.MaxAbs);
    }

    [Fact]
    public void ComputeResidualStats_EmptyInput_ReturnsZeros()
    {
        var stats = JunctionPinningValidationExporter.ComputeResidualStats(new List<float>());
        Assert.Equal(0, stats.Count);
        Assert.Equal(0f, stats.Mean);
        Assert.Equal(0f, stats.Sigma);
        Assert.Equal(0f, stats.MaxAbs);
    }

    [Theory]
    [InlineData(0.0f, 1.0f, 0.0f)]    // no delta → w = 0
    [InlineData(3.0f, 1.0f, 3.0f)]    // 3°/1° → w = 3 (boundary)
    [InlineData(-3.0f, 1.0f, 3.0f)]   // sign ignored
    [InlineData(4.0f, 2.0f, 2.0f)]    // motorway σ
    [InlineData(1.5f, 1.0f, 1.5f)]
    public void ComputeWStatistic_AbsoluteDeltaOverSigma(float deltaDeg, float sigma, float expected)
    {
        var w = JunctionPinningValidationExporter.ComputeWStatistic(deltaDeg, sigma);
        Assert.Equal(expected, w, 3);
    }

    [Theory]
    [InlineData("motorway", 2.0f)]
    [InlineData("trunk", 2.0f)]
    [InlineData("motorway_link", 2.0f)]
    [InlineData("trunk_link", 2.0f)]
    [InlineData("primary", 1.0f)]
    [InlineData("residential", 1.0f)]
    [InlineData(null, 1.0f)]
    [InlineData("", 1.0f)]
    public void GetSigmaPredictedDeg_ByRoadClass(string? osmRoadType, float expected)
    {
        Assert.Equal(expected, JunctionPinningValidationExporter.GetSigmaPredictedDeg(osmRoadType));
    }
}
