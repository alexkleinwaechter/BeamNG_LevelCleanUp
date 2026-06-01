using System;
using System.Collections.Generic;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

/// <summary>
///     Surface-protection margin (Phase A.8 follow-up). Pass 1 of the two-pass rasterizer stamps the
///     protected painted-surface zone at <c>SurfaceWidth/2 + SurfaceProtectionMarginMeters</c> instead of
///     a hard <c>SurfaceWidth/2</c>. The margin closes the convex chord-slivers at curved/junction
///     segments (which otherwise fall to Pass 2's smoothing corridor and read as a "bite" out of an
///     elevated road edge) and, in junction overlap zones, gives the higher-priority road's surface a
///     slightly wider authoritative claim. These tests pin the geometric widening on the exact path that
///     changed: <see cref="RoadMaskBuilder.RasterizeSplinePolygons" /> with <c>useSurfaceWidthOnly: true</c>.
/// </summary>
public class SurfaceProtectionMarginTests
{
    // A straight horizontal segment at y = 10 m, surface width 6 m (half-width 3 m), normal +y.
    private static List<UnifiedCrossSection> StraightSurfaceSegment() => new()
    {
        new UnifiedCrossSection
        {
            OwnerSplineId = 1, LocalIndex = 0,
            CenterPoint = new Vector2(4f, 10f),
            TangentDirection = new Vector2(1f, 0f),
            NormalDirection = new Vector2(0f, 1f),
            TargetElevation = 100f, BankAngleRadians = 0f,
            EffectiveRoadWidth = 6f, SurfaceWidth = 6f
        },
        new UnifiedCrossSection
        {
            OwnerSplineId = 1, LocalIndex = 1,
            CenterPoint = new Vector2(16f, 10f),
            TangentDirection = new Vector2(1f, 0f),
            NormalDirection = new Vector2(0f, 1f),
            TargetElevation = 100f, BankAngleRadians = 0f,
            EffectiveRoadWidth = 6f, SurfaceWidth = 6f
        }
    };

    private static byte StampSurfaceAndSampleBandPixel(float margin)
    {
        const float mpp = 0.25f; // 4 px/m → resolves the 0.5 m band
        const int dim = 80;      // covers world 0..20 m
        var mask = new byte[dim, dim];
        var elevation = new float[dim, dim];
        var owner = new int[dim, dim];
        for (var y = 0; y < dim; y++)
        for (var x = 0; x < dim; x++) owner[y, x] = -1;

        var sections = StraightSurfaceSegment();
        var meta = new SplineOverlapMetadata(SplineId: 1, Priority: 5, TotalLengthMeters: 12f);
        var metaById = new Dictionary<int, SplineOverlapMetadata> { [1] = meta };
        Span<float> intersections = stackalloc float[4];

        RoadMaskBuilder.RasterizeSplinePolygons(
            sections, splineId: 1,
            splineMetadata: meta, metadataByOwnerId: metaById,
            enableSurfacePriorityOverride: true,
            margin,
            useSurfaceWidthOnly: true,
            mask, elevation, owner,
            width: dim, height: dim, metersPerPixel: mpp,
            intersections);

        // World (10, 6.75) → 3.25 m from the centerline (y = 10): inside 3.5 (margin 0.5), outside 3.0.
        // Pixel = world / mpp = (40, 27).
        return mask[27, 40];
    }

    [Fact]
    public void Margin_ExtendsProtectedSurface_BeyondHardSurfaceWidth()
    {
        // 3.25 m from centerline: outside SurfaceWidth/2 (3.0) but inside SurfaceWidth/2 + 0.5.
        Assert.Equal(255, StampSurfaceAndSampleBandPixel(margin: 0.5f));
    }

    [Fact]
    public void ZeroMargin_LeavesBandPixelToTheCorridor()
    {
        // Legacy hard boundary: the same 3.25 m pixel is NOT part of the protected surface.
        Assert.Equal(0, StampSurfaceAndSampleBandPixel(margin: 0f));
    }
}
