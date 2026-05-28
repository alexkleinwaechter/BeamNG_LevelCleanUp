using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     Phase A.8.2 (Task 4) integration tests for the surface-pass priority override
///     plumbed through <see cref="RoadMaskBuilder.BuildCombinedMaskWithElevation" />.
///     Builds a synthetic two-spline T-junction and verifies that
///     <see cref="JunctionHarmonizationParameters.EnableSurfacePriorityOverride" /> flips
///     ownership of contested overlap pixels from the legacy width-first first-writer-wins
///     to the strictly-higher-Priority spline (cascade tier 1).
/// </summary>
public class SurfacePriorityOverrideTests
{
    /// <summary>
    ///     Minimal 2-point RoadSpline (LinearControlPoints to keep the geometry exact).
    ///     Satisfies ParameterizedRoadSpline.Spline; the rasterizer reads cross-sections
    ///     directly, so the Akima curve is not exercised here.
    /// </summary>
    private static RoadSpline MakeMinimalSpline(Vector2 start, Vector2 end) =>
        new(new List<Vector2> { start, end }, SplineInterpolationType.LinearControlPoints);

    /// <summary>
    ///     Builds a two-spline network forming a T-junction at (50, 50):
    ///       Spline 1 (through, Priority=10, RoadWidthMeters=6):
    ///         CSes at (40,50), (60,50) — east-west; surface polygon spans x∈[40,60], y∈[47,53].
    ///       Spline 2 (side, Priority=5, RoadWidthMeters=10):
    ///         CSes at (50,40), (50,50) — south-to-north; surface polygon spans x∈[45,55], y∈[40,50].
    ///     Side road iterates first in Pass 1 (wider RoadWidthMeters: 10 > 6) and stamps its
    ///     surface; through road then tries to claim contested overlap pixels at x∈[45,55], y∈[47,50].
    ///     With the flag off, the side road retains the overlap (legacy first-writer-wins).
    ///     With the flag on, the through road (Priority 10 > 5) takes the overlap via the
    ///     cascade tier-1 override.
    /// </summary>
    private static UnifiedRoadNetwork BuildTJunctionNetwork(bool enableSurfacePriorityOverride)
    {
        var jhParams = new JunctionHarmonizationParameters
        {
            EnableSurfaceWidthProtection = true,
            EnableSurfacePriorityOverride = enableSurfacePriorityOverride
        };

        var throughParams = new RoadSmoothingParameters
        {
            RoadWidthMeters = 6f,
            TerrainAffectedRangeMeters = 6f,
            RoadEdgeProtectionBufferMeters = 1.0f,
            JunctionHarmonizationParameters = jhParams
        };
        var sideParams = new RoadSmoothingParameters
        {
            RoadWidthMeters = 10f,
            TerrainAffectedRangeMeters = 6f,
            RoadEdgeProtectionBufferMeters = 1.0f,
            JunctionHarmonizationParameters = jhParams
        };

        var throughSpline = new ParameterizedRoadSpline
        {
            SplineId = 1,
            Priority = 10,
            Spline = MakeMinimalSpline(new Vector2(40f, 50f), new Vector2(60f, 50f)),
            MaterialName = "test_through",
            Parameters = throughParams
        };
        var sideSpline = new ParameterizedRoadSpline
        {
            SplineId = 2,
            Priority = 5,
            Spline = MakeMinimalSpline(new Vector2(50f, 40f), new Vector2(50f, 50f)),
            MaterialName = "test_side",
            Parameters = sideParams
        };

        var network = new UnifiedRoadNetwork();
        network.AddSpline(throughSpline);
        network.AddSpline(sideSpline);

        // Through road cross-sections (east-west, elev 100, surface 6 m).
        network.AddCrossSection(new UnifiedCrossSection
        {
            Index = 1001,
            LocalIndex = 0,
            OwnerSplineId = 1,
            CenterPoint = new Vector2(40f, 50f),
            TangentDirection = new Vector2(1f, 0f),
            NormalDirection = new Vector2(0f, 1f),
            TargetElevation = 100f,
            BankAngleRadians = 0f,
            EffectiveRoadWidth = 6f,
            SurfaceWidth = 6f,
            LeftEdgeElevation = 100f,
            RightEdgeElevation = 100f,
            Priority = 10
        });
        network.AddCrossSection(new UnifiedCrossSection
        {
            Index = 1002,
            LocalIndex = 1,
            OwnerSplineId = 1,
            CenterPoint = new Vector2(60f, 50f),
            TangentDirection = new Vector2(1f, 0f),
            NormalDirection = new Vector2(0f, 1f),
            TargetElevation = 100f,
            BankAngleRadians = 0f,
            EffectiveRoadWidth = 6f,
            SurfaceWidth = 6f,
            LeftEdgeElevation = 100f,
            RightEdgeElevation = 100f,
            Priority = 10
        });

        // Side road cross-sections (south-to-north, elev 110, surface 10 m).
        network.AddCrossSection(new UnifiedCrossSection
        {
            Index = 2001,
            LocalIndex = 0,
            OwnerSplineId = 2,
            CenterPoint = new Vector2(50f, 40f),
            TangentDirection = new Vector2(0f, 1f),
            NormalDirection = new Vector2(1f, 0f),
            TargetElevation = 110f,
            BankAngleRadians = 0f,
            EffectiveRoadWidth = 10f,
            SurfaceWidth = 10f,
            LeftEdgeElevation = 110f,
            RightEdgeElevation = 110f,
            Priority = 5
        });
        network.AddCrossSection(new UnifiedCrossSection
        {
            Index = 2002,
            LocalIndex = 1,
            OwnerSplineId = 2,
            CenterPoint = new Vector2(50f, 50f),
            TangentDirection = new Vector2(0f, 1f),
            NormalDirection = new Vector2(1f, 0f),
            TargetElevation = 110f,
            BankAngleRadians = 0f,
            EffectiveRoadWidth = 10f,
            SurfaceWidth = 10f,
            LeftEdgeElevation = 110f,
            RightEdgeElevation = 110f,
            Priority = 5
        });

        return network;
    }

    [Fact]
    public void BuildMask_FlagOff_OverlapPixelsKeptBySideSplineFromWidthOrder()
    {
        // Side road (wider RoadWidthMeters=10) iterates first in Pass 1; its 110m elevation
        // claims overlap pixels x∈[45,55], y∈[47,50] regardless of the through road's
        // higher Priority. This is the legacy first-writer-wins bug.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: false);
        var builder = new RoadMaskBuilder();

        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 100, height: 100, metersPerPixel: 1.0f);

        // Overlap pixel (x=50, y=49): inside both surface polygons (side: y∈[40,50],
        // through: y∈[47,53]). Side claims first → owner=2, elev=110.
        Assert.Equal(255, result.Mask[49, 50]);
        Assert.Equal(2, result.SplineOwnerMap[49, 50]);
        Assert.Equal(110f, result.ElevationMap[49, 50], 1);
    }

    [Fact]
    public void BuildMask_FlagOn_OverlapPixelsTakenByThroughRoadFromPriorityOverride()
    {
        // Same topology, flag on: through road (Priority 10 > 5) takes the overlap pixels
        // via the cascade tier-1 priority override in ContestedPixelResolver.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: true);
        var builder = new RoadMaskBuilder();

        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 100, height: 100, metersPerPixel: 1.0f);

        Assert.Equal(255, result.Mask[49, 50]);
        Assert.Equal(1, result.SplineOwnerMap[49, 50]);
        Assert.Equal(100f, result.ElevationMap[49, 50], 1);
    }

    [Fact]
    public void BuildMask_FlagOn_NonOverlapPixelsUnchanged()
    {
        // Pixels far from the overlap zone must be unaffected by the priority override.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: true);
        var builder = new RoadMaskBuilder();

        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 100, height: 100, metersPerPixel: 1.0f);

        // Pixel (x=45, y=50): inside through road only (x∈[40,60], y∈[47,53]); outside
        // side road surface (|x − 50| = 5 sits at the side surface boundary, but the
        // scanline only fires for x ceil≤ left edge ≤ x ≤ x floor≥ right edge; at scanY=50.5
        // the side polygon's edges are out of range). Through claims, owner=1, elev=100.
        Assert.Equal(1, result.SplineOwnerMap[50, 45]);
        Assert.Equal(100f, result.ElevationMap[50, 45], 1);

        // Pixel (x=50, y=42): inside side road only (x∈[45,55], y∈[40,50]); outside
        // through road surface (|y − 50| = 8 > 3 surface half-width). Side claims,
        // owner=2, elev=110.
        Assert.Equal(2, result.SplineOwnerMap[42, 50]);
        Assert.Equal(110f, result.ElevationMap[42, 50], 1);
    }
}
