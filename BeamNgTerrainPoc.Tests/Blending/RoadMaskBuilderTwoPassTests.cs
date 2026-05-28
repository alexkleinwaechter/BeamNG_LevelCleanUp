using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

/// <summary>
///     Phase A.8 (Task 4) integration tests for the two-pass rasterizer in
///     <see cref="RoadMaskBuilder.BuildCombinedMaskWithElevation"/>.
///     Verifies the painted-road-width protection flag (<see cref="JunctionHarmonizationParameters.EnableSurfaceWidthProtection"/>):
///     when on, a terminating narrow road's surface pixels keep their own elevation
///     even where a wider road's smoothing corridor geometrically overlaps them.
///     The legacy single-pass behavior (flag off) is also covered to document the
///     bug this feature fixes.
/// </summary>
public class RoadMaskBuilderTwoPassTests
{
    /// <summary>
    ///     Builds a minimal two-spline network:
    ///     - Primary: horizontal road, 16 m surface width, target elevation 100 m, y = 64.
    ///     - Terminating: vertical road, 6 m surface width, target elevation 95 m, x = 64,
    ///       last cross-section sits exactly on the primary's centerline (y = 64).
    ///     Junctions list is left empty on purpose — the junction-gap-fill block in
    ///     <see cref="RoadMaskBuilder.BuildCombinedMaskWithElevation"/> short-circuits,
    ///     so the test isolates the two-pass rasterizer.
    ///     Both splines share a single <see cref="JunctionHarmonizationParameters"/> instance
    ///     so tests can flip <see cref="JunctionHarmonizationParameters.EnableSurfaceWidthProtection"/>
    ///     for the whole network at once.
    /// </summary>
    private static (UnifiedRoadNetwork network, int primaryId, int terminatingId)
        BuildTwoSplineCrossing()
    {
        var jhParams = new JunctionHarmonizationParameters();

        var primaryParams = new RoadSmoothingParameters
        {
            RoadWidthMeters = 16f,
            TerrainAffectedRangeMeters = 6f,
            CrossSectionIntervalMeters = 0.5f,
            RoadEdgeProtectionBufferMeters = 2f,
            JunctionHarmonizationParameters = jhParams
        };
        var terminatingParams = new RoadSmoothingParameters
        {
            RoadWidthMeters = 6f,
            TerrainAffectedRangeMeters = 6f,
            CrossSectionIntervalMeters = 0.5f,
            RoadEdgeProtectionBufferMeters = 2f,
            JunctionHarmonizationParameters = jhParams
        };

        // Primary: horizontal, y = 64, x in [10, 110] (7 control points spaced 16.6m apart).
        var primaryControlPoints = new List<Vector2>();
        for (var i = 0; i < 7; i++)
            primaryControlPoints.Add(new Vector2(10f + i * 16.6f, 64f));
        var primarySpline = new RoadSpline(primaryControlPoints, SplineInterpolationType.LinearControlPoints);

        // Terminating: vertical, x = 64, y in [10, 64] (4 control points — last sits AT primary y=64).
        var terminatingControlPoints = new List<Vector2>();
        for (var i = 0; i < 4; i++)
            terminatingControlPoints.Add(new Vector2(64f, 10f + i * 18f));
        var terminatingSpline = new RoadSpline(terminatingControlPoints, SplineInterpolationType.LinearControlPoints);

        var primary = new ParameterizedRoadSpline
        {
            Spline = primarySpline,
            Parameters = primaryParams,
            MaterialName = "asphalt",
            SplineId = 1,
            Priority = 10
        };
        var terminating = new ParameterizedRoadSpline
        {
            Spline = terminatingSpline,
            Parameters = terminatingParams,
            MaterialName = "asphalt",
            SplineId = 2,
            Priority = 5
        };

        var network = new UnifiedRoadNetwork();
        network.AddSpline(primary);
        network.AddSpline(terminating);

        // Primary cross-sections.
        for (var i = 0; i < 7; i++)
        {
            var x = 10f + i * 16.6f;
            network.AddCrossSection(new UnifiedCrossSection
            {
                Index = 1_000 + i,
                LocalIndex = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(x, 64f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 16f,
                SurfaceWidth = 16f,
                LeftEdgeElevation = 100f,
                RightEdgeElevation = 100f,
                Priority = 10
            });
        }

        // Terminating cross-sections.
        for (var i = 0; i < 4; i++)
        {
            var y = 10f + i * 18f;
            network.AddCrossSection(new UnifiedCrossSection
            {
                Index = 2_000 + i,
                LocalIndex = i,
                OwnerSplineId = 2,
                CenterPoint = new Vector2(64f, y),
                TangentDirection = new Vector2(0f, 1f),
                NormalDirection = new Vector2(1f, 0f),
                TargetElevation = 95f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f,
                SurfaceWidth = 6f,
                LeftEdgeElevation = 95f,
                RightEdgeElevation = 95f,
                Priority = 5
            });
        }

        return (network, 1, 2);
    }

    [Fact]
    public void LegacyPath_TerminatingCenterlinePixel_OverwrittenByPrimaryCorridor()
    {
        // Default: EnableSurfaceWidthProtection = false → legacy single-pass.
        var (network, _, _) = BuildTwoSplineCrossing();
        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Pixel at (x=64, y=60): on the terminating road's centerline, 4 m before the
        // primary's centerline (y=64). The terminating's own surface half-width is 3 m,
        // so this pixel is 1 m inside the terminating road's painted surface AND
        // also 4 m inside the primary's corridor half-width (8 m + 2 m buffer = 10 m).
        // Widest-first ordering: primary processes first, claims this pixel with elevation 100.
        Assert.Equal(255, result.Mask[60, 64]);
        Assert.Equal(100f, result.ElevationMap[60, 64], 1);
    }

    [Fact]
    public void TwoPass_TerminatingCenterlinePixel_HoldsTerminatingElevation()
    {
        // Flag-on path: terminating road's surface pixels are protected.
        var (network, _, _) = BuildTwoSplineCrossing();
        foreach (var s in network.Splines)
            s.Parameters.JunctionHarmonizationParameters!.EnableSurfaceWidthProtection = true;

        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Pixel (x=64, y=54): on terminating centerline (|x − 64| = 0 ≤ 3 surface half-width)
        // but OUTSIDE the primary's 8 m surface half-width (|y − 64| = 10 > 8). Still inside
        // the primary's corridor + buffer (8 + 2 = 10 m half-width).
        // Legacy: primary corridor wins → 100 m. Two-pass: Pass 1 stamps terminating surface
        // (primary surface doesn't reach this y), so the pixel keeps terminating's 95 m.
        Assert.Equal(255, result.Mask[54, 64]);
        Assert.Equal(95f, result.ElevationMap[54, 64], 1);
    }

    [Fact]
    public void TwoPass_PrimaryCorridorPixel_StillPaintedInPass2()
    {
        // Pixel that is OUTSIDE every spline's painted surface but INSIDE some spline's
        // corridor + edge buffer. Confirms Pass 2 still fills corridor pixels that Pass 1
        // skipped, so the protection mask isn't lost outside surfaces.
        var (network, _, _) = BuildTwoSplineCrossing();
        foreach (var s in network.Splines)
            s.Parameters.JunctionHarmonizationParameters!.EnableSurfaceWidthProtection = true;

        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Pixel (x=70, y=54): outside primary surface (|y − 64| = 10 > 8), outside
        // terminating surface (|x − 64| = 6 > 3), but inside primary corridor+buffer
        // (|y − 64| = 10 ≤ 10). Pass 1 leaves this pixel unclaimed; Pass 2 stamps it,
        // primary first (widest), at ~100 m (with a banking-aware bilinear lerp toward
        // 95 in the terminating's overlap zone — hence the InRange bracket).
        Assert.Equal(255, result.Mask[54, 70]);
        Assert.InRange(result.ElevationMap[54, 70], 94f, 101f);
    }
}
