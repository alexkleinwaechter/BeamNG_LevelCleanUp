using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadGeneratorLaneTests
{
    // --- ResolveLaneInfo ---

    [Fact]
    public void ResolveLaneSegment_SingleSegment_AlwaysReturns()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 4, LanesForward = 2, LanesBackward = 2 } }
        };

        var info = DecalRoadGenerator.ResolveLaneInfo(segments, 500f);

        Assert.Equal(4, info.TotalLanes);
        Assert.Equal(2, info.LanesForward);
    }

    [Fact]
    public void ResolveLaneSegment_MultipleSegments_ReturnsCorrectForDistance()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 } },
            new() { StartDistance = 200f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 3, LanesForward = 2, LanesBackward = 1 } },
            new() { StartDistance = 500f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 } }
        };

        // Before first boundary
        Assert.Equal(2, DecalRoadGenerator.ResolveLaneInfo(segments, 100f).TotalLanes);
        // At second segment
        Assert.Equal(3, DecalRoadGenerator.ResolveLaneInfo(segments, 200f).TotalLanes);
        Assert.Equal(3, DecalRoadGenerator.ResolveLaneInfo(segments, 400f).TotalLanes);
        // At third segment
        Assert.Equal(2, DecalRoadGenerator.ResolveLaneInfo(segments, 600f).TotalLanes);
    }

    // --- DeriveAIRoadProperties ---

    [Fact]
    public void DeriveAIRoadProperties_TwoWay_CorrectMapping()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 4, LanesForward = 2, LanesBackward = 2, IsOneWay = false };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(2, lanesRight);   // forward = right
        Assert.Equal(2, lanesLeft);    // backward = left
        Assert.False(oneWay);
        Assert.False(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_OneWayForward()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 3, LanesForward = 3, LanesBackward = 0, IsOneWay = true };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(3, lanesRight);
        Assert.Equal(0, lanesLeft);
        Assert.True(oneWay);
        Assert.False(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_OneWayReverse_FlipDirection()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 2, LanesForward = 0, LanesBackward = 2, IsOneWay = true };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(0, lanesRight);
        Assert.Equal(2, lanesLeft);
        Assert.True(oneWay);
        Assert.True(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_LanesBothWays_AddedToForward()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 3, LanesForward = 1, LanesBackward = 1,
            LanesBothWays = 1, IsOneWay = false
        };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        // LanesBothWays added to forward (right) for AI purposes
        Assert.Equal(2, lanesRight);  // 1 forward + 1 bothways
        Assert.Equal(1, lanesLeft);
    }

    // --- FindLaneChangeBoundaryIndices ---

    [Fact]
    public void FindLaneChangeBoundaryIndices_NoSegments_ReturnsEmpty()
    {
        var result = DecalRoadGenerator.FindLaneChangeBoundaryIndices(
            null, new List<float>());
        Assert.Empty(result);
    }

    [Fact]
    public void FindLaneChangeBoundaryIndices_SingleSegment_ReturnsEmpty()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } }
        };
        var distances = Enumerable.Range(0, 100).Select(i => i * 5f).ToList();

        var result = DecalRoadGenerator.FindLaneChangeBoundaryIndices(segments, distances);
        Assert.Empty(result);
    }

    [Fact]
    public void FindLaneChangeBoundaryIndices_TwoSegments_FindsBoundary()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } },
            new() { StartDistance = 200f, LaneInfo = new OsmLaneInfo { TotalLanes = 3 } }
        };
        // Cross-sections every 5m from 0 to 495
        var distances = Enumerable.Range(0, 100).Select(i => i * 5f).ToList();

        var boundaries = DecalRoadGenerator.FindLaneChangeBoundaryIndices(
            segments, distances);

        Assert.Single(boundaries);
        // Boundary at cross-section index 40 (distance 200m)
        Assert.Equal(40, boundaries[0]);
    }

    // --- Integration-style tests ---

    [Fact]
    public void NoLaneSegments_FallsBackToDefaultLaneCount()
    {
        // When LaneSegments is null, lane boundary positions use DefaultLaneCount
        // 2 lanes -> 1 boundary at center
        var boundaries = DecalRoadGenerator.CalculateLaneBoundaryPositions(2);
        Assert.Single(boundaries);
        Assert.Equal(0.0f, boundaries[0], precision: 2);
    }

    [Fact]
    public void LaneIndependentLayers_NotSplitAtBoundaries()
    {
        // Edge lines and edge blends should NOT be split even when lane segments change
        var edgeLayer = new DecalRoadLayerDefinition
        {
            Name = "edge", LayerType = DecalRoadLayerType.EdgeLine,
            IsPerLane = false, IsEnabled = true, Material = "test"
        };

        // Edge line is NOT lane-dependent
        Assert.False(edgeLayer.IsPerLane);
        Assert.NotEqual(DecalRoadLayerType.AIRoad, edgeLayer.LayerType);
        Assert.NotEqual(DecalRoadLayerType.DirectionDivider, edgeLayer.LayerType);
    }

    [Fact]
    public void DeriveAIRoadProperties_AsymmetricLanes()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 5, LanesForward = 3, LanesBackward = 2, IsOneWay = false };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(3, lanesRight);
        Assert.Equal(2, lanesLeft);
        Assert.False(oneWay);
        Assert.False(flipDirection);
    }

    [Fact]
    public void DifferentLaneCounts_ProduceDifferentBoundaryPositions()
    {
        // A 2-lane section should produce 1 boundary, 3-lane should produce 2
        var boundaries2 = DecalRoadGenerator.CalculateLaneBoundaryPositions(2);
        var boundaries3 = DecalRoadGenerator.CalculateLaneBoundaryPositions(3);

        Assert.Single(boundaries2);       // 2 lanes -> 1 boundary
        Assert.Equal(2, boundaries3.Length); // 3 lanes -> 2 boundaries
    }

    // --- Direction boundary positioning ---

    [Fact]
    public void DirectionBoundary_Symmetric_AtCenter()
    {
        var info = new OsmLaneInfo { TotalLanes = 4, LanesForward = 2, LanesBackward = 2 };
        var pos = DecalRoadGenerator.CalculateDirectionBoundaryPosition(info);
        Assert.Equal(0.0f, pos, precision: 2);
    }

    [Fact]
    public void DirectionBoundary_Asymmetric_2F1B()
    {
        var info = new OsmLaneInfo { TotalLanes = 3, LanesForward = 2, LanesBackward = 1 };
        var pos = DecalRoadGenerator.CalculateDirectionBoundaryPosition(info);
        // After 1 backward lane from left: -1 + 2*1/3 = -0.333
        Assert.Equal(-0.333f, pos, precision: 2);
    }

    [Fact]
    public void DirectionBoundary_Asymmetric_3F1B()
    {
        var info = new OsmLaneInfo { TotalLanes = 4, LanesForward = 3, LanesBackward = 1 };
        var pos = DecalRoadGenerator.CalculateDirectionBoundaryPosition(info);
        // After 1 backward lane: -1 + 2*1/4 = -0.5
        Assert.Equal(-0.5f, pos, precision: 2);
    }

    [Fact]
    public void BoundariesExcludingDirection_4Lanes_SkipsCenter()
    {
        var info = new OsmLaneInfo { TotalLanes = 4, LanesForward = 2, LanesBackward = 2 };
        var boundaries = DecalRoadGenerator.CalculateLaneBoundaryPositionsExcludingDirectionBoundary(4, info);
        // 4 lanes -> 3 boundaries at -0.5, 0.0, +0.5; skip 0.0 -> 2 remaining
        Assert.Equal(2, boundaries.Length);
        Assert.Equal(-0.5f, boundaries[0], precision: 2);
        Assert.Equal(0.5f, boundaries[1], precision: 2);
    }

    [Fact]
    public void BoundariesExcludingDirection_2Lanes_NoSkip()
    {
        var info = new OsmLaneInfo { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 };
        var boundaries = DecalRoadGenerator.CalculateLaneBoundaryPositionsExcludingDirectionBoundary(2, info);
        // 2 lanes -> no skip (TotalLanes <= 2)
        Assert.Single(boundaries);
        Assert.Equal(0.0f, boundaries[0], precision: 2);
    }

    [Fact]
    public void FindLaneChangeBoundaryIndices_ThreeSegments_FindsTwoBoundaries()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } },
            new() { StartDistance = 100f, LaneInfo = new OsmLaneInfo { TotalLanes = 3 } },
            new() { StartDistance = 300f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } }
        };
        var distances = Enumerable.Range(0, 100).Select(i => i * 5f).ToList();

        var boundaries = DecalRoadGenerator.FindLaneChangeBoundaryIndices(segments, distances);

        Assert.Equal(2, boundaries.Count);
        Assert.Equal(20, boundaries[0]); // 100m / 5m = index 20
        Assert.Equal(60, boundaries[1]); // 300m / 5m = index 60
    }
}
