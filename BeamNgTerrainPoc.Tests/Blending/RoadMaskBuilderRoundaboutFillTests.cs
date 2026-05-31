using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

/// <summary>
///     Roundabout connector-mouth gap fill. Roundabout parent junctions are <c>IsExcluded=true</c> (set by
///     RoundaboutElevationHarmonizer), so the junction-gap-fill in
///     <see cref="RoadMaskBuilder.BuildCombinedMaskWithElevation"/> skipped them — leaving the triangular
///     gaps at every ring↔connector seam unmasked, which the blender renders as a hard cliff (the step the
///     car parks on). On the no-blend path §3 pins a valid <see cref="NetworkJunction.HarmonizedElevation"/>
///     (the local ring Z) to these junctions, so the fill can now bridge the mouth at the correct Z.
///     The legacy path never sets HarmonizedElevation (stays NaN) → roundabout fill stays skipped, unchanged.
/// </summary>
public class RoadMaskBuilderRoundaboutFillTests
{
    // A horizontal road far from the junction, plus a roundabout junction whose small fill disk covers an
    // otherwise-unmasked pixel. Isolates the fill gate: the junction's contributors only supply widths.
    private static (UnifiedRoadNetwork network, NetworkJunction junction) BuildRoadWithRoundaboutJunction(
        float harmonizedElevation)
    {
        var jhParams = new JunctionHarmonizationParameters();
        var roadParams = new RoadSmoothingParameters
        {
            RoadWidthMeters = 10f,
            TerrainAffectedRangeMeters = 4f,
            CrossSectionIntervalMeters = 0.5f,
            RoadEdgeProtectionBufferMeters = 2f,
            JunctionHarmonizationParameters = jhParams
        };

        var controlPoints = new List<Vector2>();
        for (var i = 0; i < 5; i++) controlPoints.Add(new Vector2(40f + i * 12f, 64f));
        var spline = new RoadSpline(controlPoints, SplineInterpolationType.LinearControlPoints);

        var road = new ParameterizedRoadSpline
        {
            Spline = spline, Parameters = roadParams, MaterialName = "asphalt", SplineId = 1, Priority = 10
        };

        var network = new UnifiedRoadNetwork();
        network.AddSpline(road);

        var css = new List<UnifiedCrossSection>();
        for (var i = 0; i < 5; i++)
        {
            var cs = new UnifiedCrossSection
            {
                Index = 1_000 + i, LocalIndex = i, OwnerSplineId = 1,
                CenterPoint = new Vector2(40f + i * 12f, 64f),
                TangentDirection = new Vector2(1f, 0f), NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f, EffectiveRoadWidth = 10f, SurfaceWidth = 10f,
                LeftEdgeElevation = 100f, RightEdgeElevation = 100f, Priority = 10
            };
            css.Add(cs);
            network.AddCrossSection(cs);
        }

        // Roundabout junction 34 m away from the road (so the fill pixel at its center is unmasked).
        var junction = new NetworkJunction
        {
            JunctionId = 1, Type = JunctionType.Roundabout, Position = new Vector2(64f, 30f),
            IsExcluded = true, HarmonizedElevation = harmonizedElevation
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = css[0], Spline = road, IsSplineStart = false, IsSplineEnd = false
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = css[1], Spline = road, IsSplineStart = false, IsSplineEnd = true
        });
        network.Junctions.Add(junction);

        return (network, junction);
    }

    [Fact]
    public void RoundaboutJunction_WithHarmonizedElevation_FillsMouthGap()
    {
        var (network, _) = BuildRoadWithRoundaboutJunction(harmonizedElevation: 105f);
        var builder = new RoadMaskBuilder();

        var result = builder.BuildCombinedMaskWithElevation(network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Junction-center pixel (64,30) is far from the road (y=64) → unmasked unless the roundabout fill runs.
        Assert.Equal(255, result.Mask[30, 64]);
        Assert.Equal(105f, result.ElevationMap[30, 64], 1);
    }

    [Fact]
    public void RoundaboutJunction_NaNHarmonizedElevation_StaysSkipped_LegacyUnchanged()
    {
        var (network, _) = BuildRoadWithRoundaboutJunction(harmonizedElevation: float.NaN);
        var builder = new RoadMaskBuilder();

        var result = builder.BuildCombinedMaskWithElevation(network, width: 128, height: 128, metersPerPixel: 1.0f);

        // No valid ring Z (legacy path) → roundabout junction stays excluded from the fill → pixel unmasked.
        Assert.Equal(0, result.Mask[30, 64]);
    }
}
