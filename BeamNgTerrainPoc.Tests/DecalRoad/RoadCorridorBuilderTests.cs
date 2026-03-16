using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class RoadCorridorBuilderTests
{
    [Fact]
    public void CalculateCorridorHalfWidth_MirroredEdgeBlend_UsesOuterExtent()
    {
        // EdgeBlend at position 1.25, width 2.0m, mirrored
        // roadWidth = 7.0m, margin = 1.0m
        // |1.25| * 0.5 * 7.0 + 2.0/2 + 1.0 = 4.375 + 1.0 + 1.0 = 6.375
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeBlend", Position = 1.25f, Width = 2.0f,
                     IsMirrored = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 1.0f);
        Assert.Equal(6.375f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_TrackWidthLayer_UsesFullRoadWidth()
    {
        // AIRoad: IsTrackWidth=true, position=0.0
        // nodeWidth = roadWidth = 8.0
        // |0.0| * 0.5 * 8.0 + 8.0/2 = 0 + 4.0 = 4.0 (+ margin 0)
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "AIRoad", Position = 0.0f, IsTrackWidth = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(4.0f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_LaneWidthTreadMarks_ExtendToRoadEdge()
    {
        // TreadMarks: IsLaneWidth=true, 2 lanes
        // Lane centers at -0.5, +0.5 (from CalculateLaneCenterPositions)
        // nodeWidth = 8.0 / 2 = 4.0
        // Outermost: |0.5| * 0.5 * 8.0 + 4.0/2 = 2.0 + 2.0 = 4.0 (= roadWidth/2)
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "TreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     IsLaneWidth = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(4.0f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_PerLaneBoundary_UsesOutermostBoundary()
    {
        // LaneMarking: IsPerLane=true, 4 lanes, width 0.2m
        // Boundaries at -0.5, 0.0, +0.5
        // Outermost: |0.5| * 0.5 * 8.0 + 0.2/2 = 2.0 + 0.1 = 2.1
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "LaneMarking", IsPerLane = true, Width = 0.2f, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 4, marginMeters: 0f);
        Assert.Equal(2.1f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_MultipleLayers_TakesMax()
    {
        // EdgeLine position=1.0, width=0.25 → |1.0|*0.5*7 + 0.25/2 = 3.625
        // EdgeBlend position=1.1, width=1.0 → |1.1|*0.5*7 + 1.0/2 = 4.35
        // Max is 4.35, + margin 1.0 = 5.35
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", Position = 1.0f, Width = 0.25f,
                     IsMirrored = true, IsEnabled = true },
            new() { Name = "EdgeBlend", Position = 1.1f, Width = 1.0f,
                     IsMirrored = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 1.0f);
        Assert.Equal(5.35f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_DisabledLayers_AreSkipped()
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "Big", Position = 2.0f, Width = 5.0f,
                     IsMirrored = true, IsEnabled = false },
            new() { Name = "Small", Position = 1.0f, Width = 0.25f,
                     IsMirrored = true, IsEnabled = true }
        };
        // Only "Small": |1.0|*0.5*7 + 0.25/2 = 3.625
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(3.625f, result, precision: 3);
    }
}
