using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class SurfaceFootprintOverlapTests
{
    /// <summary>
    /// Creates a SplineSurfaceData for a straight horizontal road along X axis.
    /// Surface elevation ramps linearly from zStart to zEnd (flat at 0 by default).
    /// </summary>
    private static SplineSurfaceData CreateStraightSurface(
        int splineId, float roadWidth, float length = 100f, int pointCount = 11,
        float zStart = 0f, float zEnd = 0f)
    {
        var points = new List<Vector3>();
        for (int i = 0; i < pointCount; i++)
        {
            var frac = (float)i / (pointCount - 1);
            points.Add(new Vector3(length * frac, 0, zStart + (zEnd - zStart) * frac));
        }

        return new SplineSurfaceData
        {
            SplineId = splineId,
            SurfaceHalfWidth = roadWidth / 2f,
            CenterlinePoints = points
        };
    }

    // --- SurfaceFootprintIndex.AddSplineSurface tests ---

    [Fact]
    public void AddSplineSurface_PointInsideFullWidth_Detected()
    {
        // 7m wide road (half-width 3.5m), point at 3.0m from center → inside
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 3.0f, 0f, excludeSplineId: 99);
        Assert.True(isOverlapping);
    }

    [Fact]
    public void AddSplineSurface_PointOutsideFullWidth_NotDetected()
    {
        // 7m wide road (half-width 3.5m + 0.5m margin = 4.0m), point at 5.0m → outside
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 5.0f, 0f, excludeSplineId: 99);
        Assert.False(isOverlapping);
    }

    [Fact]
    public void AddSplineSurface_OwnSplineExcluded()
    {
        // Point inside road surface but excluded by own spline ID
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 1.0f, 0f, excludeSplineId: 1);
        Assert.False(isOverlapping);
    }

    [Fact]
    public void AddSplineSurface_ReturnsOverlappingSplineId()
    {
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 42, roadWidth: 7f));

        var (isOverlapping, overlappingId) = index.CheckPoint(50f, 1.0f, 0f, excludeSplineId: 99);
        Assert.True(isOverlapping);
        Assert.Equal(42, overlappingId);
    }

    [Fact]
    public void AddSplineSurface_FullWidthWiderThanOldLaneWidth()
    {
        // This is the key bug fix test.
        // Old system: TreadMarks width = 7m / 2 lanes = 3.5m → half-width = 1.75m + 0.5m = 2.25m
        // New system: Full road width = 7m → half-width = 3.5m + 0.5m = 4.0m
        // Point at 3.0m from center: OLD would miss it, NEW detects it.
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 3.0f, 0f, excludeSplineId: 99);
        Assert.True(isOverlapping, "Point at 3.0m should be inside 7m road (half-width 3.5m + margin)");
    }

    [Fact]
    public void AddSplineSurface_PointBeyondEndOfRoad_NotDetected()
    {
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f, length: 100f));

        var (isOverlapping, _) = index.CheckPoint(110f, 0f, 0f, excludeSplineId: 99);
        Assert.False(isOverlapping);
    }

    // --- Vertical coplanarity tests (bridge/underpass crossings are not junctions) ---

    [Fact]
    public void CheckPoint_CrossingAboveSurface_NotDetected()
    {
        // Bridge deck node 5m above a road's surface: plan-inside but not coplanar
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 1.0f, 5f, excludeSplineId: 99);
        Assert.False(isOverlapping);
    }

    [Fact]
    public void CheckPoint_WithinVerticalTolerance_Detected()
    {
        // 0.9m above the surface — within the 1.0m coplanarity tolerance
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 1.0f, 0.9f, excludeSplineId: 99);
        Assert.True(isOverlapping);
    }

    [Fact]
    public void CheckPoint_JustBeyondVerticalTolerance_NotDetected()
    {
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(splineId: 1, roadWidth: 7f));

        var (isOverlapping, _) = index.CheckPoint(50f, 1.0f, 1.5f, excludeSplineId: 99);
        Assert.False(isOverlapping);
    }

    [Fact]
    public void CheckPoint_SlopedSurface_InterpolatesZAlongSegment()
    {
        // Surface ramps 0→10m over 100m; at x=50 the surface is at z=5
        var index = new SurfaceFootprintIndex();
        index.AddSplineSurface(CreateStraightSurface(
            splineId: 1, roadWidth: 7f, zStart: 0f, zEnd: 10f));

        var (atSurface, _) = index.CheckPoint(50f, 1.0f, 5f, excludeSplineId: 99);
        Assert.True(atSurface, "Point at the interpolated surface elevation should overlap");

        var (belowSurface, _) = index.CheckPoint(50f, 1.0f, 0f, excludeSplineId: 99);
        Assert.False(belowSurface, "Point 5m below the interpolated surface should not overlap");
    }

    // --- DecalRoadOverlapPostProcessor tests ---

    /// <summary>
    /// Creates a minimal GeneratedDecalRoad for testing.
    /// </summary>
    private static GeneratedDecalRoad CreateTestRoad(
        string name, int splineId, JunctionConstraintMode junctionConstraint,
        bool isAIRoad = false, bool isRoundaboutRoad = false,
        bool preserveContinuity = false,
        List<float[]>? nodes = null)
    {
        nodes ??= Enumerable.Range(0, 10)
            .Select(i => new float[] { i * 10f, 0f, 0f, 3.5f })
            .ToList();

        return new GeneratedDecalRoad
        {
            Name = name,
            ParentGroupName = "TestGroup",
            Material = "test_material",
            Nodes = nodes,
            SplineId = splineId,
            JunctionConstraint = junctionConstraint,
            IsAIRoad = isAIRoad,
            IsRoundaboutRoad = isRoundaboutRoad,
            PreserveContinuity = preserveContinuity
        };
    }

    [Fact]
    public void Process_AIRoadsPassThrough()
    {
        var aiRoad = CreateTestRoad("ai", splineId: 1, junctionConstraint: JunctionConstraintMode.None, isAIRoad: true);
        var surfaces = new List<SplineSurfaceData>
        {
            CreateStraightSurface(splineId: 2, roadWidth: 7f)
        };

        var results = DecalRoadOverlapPostProcessor.Process(
            [aiRoad], surfaces, null);

        Assert.Single(results);
        Assert.Equal("ai", results[0].Name);
    }

    [Fact]
    public void Process_NonInterruptableRoadsPassThrough()
    {
        var treadMarks = CreateTestRoad("tread", splineId: 1, junctionConstraint: JunctionConstraintMode.None);
        var surfaces = new List<SplineSurfaceData>
        {
            CreateStraightSurface(splineId: 2, roadWidth: 7f)
        };

        var results = DecalRoadOverlapPostProcessor.Process(
            [treadMarks], surfaces, null);

        Assert.Single(results);
        Assert.Equal("tread", results[0].Name);
    }

    [Fact]
    public void Process_InterruptableRoadSplitAtOverlap()
    {
        // Road A (spline 1): horizontal along Y=0, width 10m
        // Road B (spline 2): edge line running perpendicular through Road A
        //   Nodes at X=50, Y from -30 to +30 — crosses Road A's surface
        var edgeLineNodes = Enumerable.Range(0, 13)
            .Select(i => new float[] { 50f, -30f + i * 5f, 0f, 0.2f })
            .ToList();

        var edgeLine = CreateTestRoad("edge", splineId: 2,
            junctionConstraint: JunctionConstraintMode.Interrupt, nodes: edgeLineNodes);

        var surfaces = new List<SplineSurfaceData>
        {
            CreateStraightSurface(splineId: 1, roadWidth: 10f)
        };

        var results = DecalRoadOverlapPostProcessor.Process(
            [edgeLine], surfaces, null);

        // Edge line should be split — nodes within Road A's surface (±5m + margin from Y=0) are removed
        // So we expect at least 2 fragments (above and below the crossing)
        Assert.True(results.Count >= 2,
            $"Expected edge line to be split into fragments, got {results.Count} road(s)");

        // All fragments should preserve the splineId
        Assert.All(results, r => Assert.Equal(2, r.SplineId));
    }

    [Fact]
    public void Process_BridgeCrossingAbove_NotSplit()
    {
        // Same plan geometry as the split test, but the crossing line runs 6m
        // ABOVE Road A's surface (a bridge deck marking over a street, or the
        // street's marking under a deck). Plan-only overlap must not interrupt.
        var bridgeLineNodes = Enumerable.Range(0, 13)
            .Select(i => new float[] { 50f, -30f + i * 5f, 6f, 0.2f })
            .ToList();

        var bridgeLine = CreateTestRoad("bridge_edge", splineId: 2,
            junctionConstraint: JunctionConstraintMode.Interrupt, nodes: bridgeLineNodes);

        var surfaces = new List<SplineSurfaceData>
        {
            CreateStraightSurface(splineId: 1, roadWidth: 10f)
        };

        var results = DecalRoadOverlapPostProcessor.Process(
            [bridgeLine], surfaces, null);

        Assert.Single(results);
        Assert.Equal("bridge_edge", results[0].Name);
        Assert.Equal(13, results[0].Nodes.Count);
    }

    [Fact]
    public void Process_InterruptableRoadNotSplitBySameSpline()
    {
        // Edge line and surface belong to the same spline — should NOT be split
        var edgeLine = CreateTestRoad("edge", splineId: 1, junctionConstraint: JunctionConstraintMode.Interrupt);
        var surfaces = new List<SplineSurfaceData>
        {
            CreateStraightSurface(splineId: 1, roadWidth: 10f)
        };

        var results = DecalRoadOverlapPostProcessor.Process(
            [edgeLine], surfaces, null);

        // Should pass through unsplit (same spline excluded)
        Assert.Single(results);
    }
}
