using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class RoadCorridorOverlapCheckerTests
{
    /// <summary>
    /// Creates a straight horizontal corridor along X axis from (0,0) to (length,0),
    /// with normal pointing up (+Y), and given half-width.
    /// </summary>
    private static RoadCorridor CreateStraightCorridor(
        int splineId, float halfWidth, float length = 100f, int sectionCount = 11)
    {
        var sections = new List<CorridorSection>();
        for (int i = 0; i < sectionCount; i++)
        {
            float x = length * i / (sectionCount - 1);
            sections.Add(new CorridorSection(
                new Vector2(x, 0), new Vector2(0, 1), x));
        }
        return new RoadCorridor
        {
            SplineId = splineId,
            RoadWidth = halfWidth * 2,
            CorridorHalfWidth = halfWidth,
            Sections = sections
        };
    }

    [Fact]
    public void PointInsideCorridor_ReturnsOverlapping()
    {
        // Corridor along X from (0,0) to (100,0), half-width = 5m
        // Point at (50, 3) is 3m from centerline, inside 5m corridor
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 3), corridor);
        Assert.True(result.IsOverlapping);
        Assert.Equal(1, result.OverlappingSplineId);
    }

    [Fact]
    public void PointOutsideCorridor_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, 7) is 7m from centerline, outside 5m corridor
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 7), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointOnOppositeSide_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, -7) is 7m from centerline on opposite side
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, -7), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointPastCorridorEnd_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
        // Point at (110, 0) is past the end of the corridor
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(110, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointBeforeCorridorStart_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
        // Point at (-10, 0) is before the start
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(-10, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointOnCorridorEdge_ReturnsOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, 4.9) is just inside
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 4.9f), corridor);
        Assert.True(result.IsOverlapping);
    }

    [Fact]
    public void PerpendicularCorridor_OverlapsAtCrossing()
    {
        // Road A: horizontal along X axis, half-width 5m
        var corridorA = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);

        // Road B: vertical corridor along Y axis from (50,-50) to (50,50)
        var sectionsB = new List<CorridorSection>();
        for (int i = 0; i < 11; i++)
        {
            float y = -50f + 100f * i / 10;
            sectionsB.Add(new CorridorSection(
                new Vector2(50, y), new Vector2(1, 0), i * 10f));
        }
        var corridorB = new RoadCorridor
        {
            SplineId = 2, RoadWidth = 8f, CorridorHalfWidth = 4f, Sections = sectionsB
        };

        // Point on road B's left edge at (46, 0) — should be inside road A's corridor
        // (it's at Y=0 which is road A's centerline, and X=46 is within road A's length)
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(46, 0), corridorA);
        Assert.True(result.IsOverlapping);

        // Point on road B's left edge at (46, 20) — outside road A's corridor
        // (Y=20 is way outside road A's 5m half-width)
        var result2 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(46, 20), corridorA);
        Assert.False(result2.IsOverlapping);
    }

    [Fact]
    public void TwoSectionCorridor_WorksCorrectly()
    {
        // Minimal corridor with just 2 sections
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f,
            length: 50f, sectionCount: 2);
        // Point inside
        var r1 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(25, 2), corridor);
        Assert.True(r1.IsOverlapping);
        // Point outside
        var r2 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(25, 7), corridor);
        Assert.False(r2.IsOverlapping);
    }

    [Fact]
    public void CheckAgainstAllCorridors_SkipsOwnSpline()
    {
        var corridors = new Dictionary<int, RoadCorridor>
        {
            [1] = CreateStraightCorridor(1, 5f),
            [2] = CreateStraightCorridor(2, 5f)
        };
        // Point at (50, 0) is inside both corridors, but checking for splineId=1
        // should skip corridor 1 and only check corridor 2
        var result = RoadCorridorOverlapChecker.CheckAgainstAllCorridors(
            new Vector2(50, 0), ownSplineId: 1, corridors);
        Assert.True(result.IsOverlapping);
        Assert.Equal(2, result.OverlappingSplineId);
    }

    [Fact]
    public void SideSpecificSuppression_LeftEdgeUnaffectedByRightSideRoad()
    {
        // Road A: horizontal along X axis, half-width 5m, normal pointing +Y
        var corridorA = CreateStraightCorridor(splineId: 1, halfWidth: 5f);

        // Road B connects from the +Y side (right/above) at X=50
        var sectionsB = new List<CorridorSection>();
        for (int i = 0; i < 6; i++)
        {
            float y = 10f + 20f * i;  // from (50,10) to (50,110)
            sectionsB.Add(new CorridorSection(
                new Vector2(50, y), new Vector2(1, 0), i * 20f));
        }
        var corridorB = new RoadCorridor
        {
            SplineId = 2, RoadWidth = 6f, CorridorHalfWidth = 4f, Sections = sectionsB
        };

        // Road A's LEFT edge node at (50, -4.5) — opposite side from road B
        // Should NOT be inside road B's corridor (road B is at Y=10..110)
        var leftResult = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, -4.5f), corridorB);
        Assert.False(leftResult.IsOverlapping);

        // Road A's RIGHT edge node at (50, 12) — same side as road B
        // Should be inside road B's corridor (road B starts at Y=10, halfWidth=4, extends to Y=6)
        var rightResult = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 12f), corridorB);
        Assert.True(rightResult.IsOverlapping);
    }

    /// <summary>
    /// Creates a circular corridor (closed loop) centered at origin with given radius.
    /// Sections are sampled every ~spacing degrees around the circle.
    /// Normal points outward (radially away from center).
    /// </summary>
    private static RoadCorridor CreateCircularCorridor(
        int splineId, float halfWidth, float radius = 30f, int sectionCount = 24)
    {
        var sections = new List<CorridorSection>();
        for (int i = 0; i < sectionCount; i++)
        {
            float angle = 2f * MathF.PI * i / sectionCount;
            var center = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            var normal = Vector2.Normalize(center); // Points outward
            sections.Add(new CorridorSection(center, normal, i * (2f * MathF.PI * radius / sectionCount)));
        }
        return new RoadCorridor
        {
            SplineId = splineId,
            RoadWidth = halfWidth * 2,
            CorridorHalfWidth = halfWidth,
            Sections = sections,
            IsClosedLoop = true
        };
    }

    [Fact]
    public void ClosedLoopCorridor_PointNearWrapSeam_ReturnsOverlapping()
    {
        // Circular corridor with radius=30m, halfWidth=5m, 24 sections
        // Section 0 is at angle=0 (30,0), section 23 is at angle=345°
        // A point between section 23 and section 0 should still be detected
        var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f, sectionCount: 24);

        // Point on the ring between section 23 and section 0 at angle ~352.5°
        float testAngle = (23.5f / 24f) * 2f * MathF.PI;
        var testPoint = new Vector2(MathF.Cos(testAngle) * 30f, MathF.Sin(testAngle) * 30f);

        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(testPoint, corridor);
        Assert.True(result.IsOverlapping);
    }

    [Fact]
    public void ClosedLoopCorridor_PointInsideRing_ReturnsOverlapping()
    {
        var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f);

        // Point at angle=90° (top), slightly inside the ring (radius=27m, inside 30±5)
        var testPoint = new Vector2(0, 27f);
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(testPoint, corridor);
        Assert.True(result.IsOverlapping);
    }

    [Fact]
    public void ClosedLoopCorridor_PointFarOutside_ReturnsNotOverlapping()
    {
        var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f);

        // Point at center of ring (0,0) — way outside the corridor (30 - 5 = 25m from closest section)
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(new Vector2(0, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void NonClosedLoop_PointNearEnd_ReturnsNotOverlapping()
    {
        // Verify that non-closed-loop corridors still reject points past the end
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(new Vector2(110, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

}
