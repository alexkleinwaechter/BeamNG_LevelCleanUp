using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class LaneSegmentMergeTests
{
    private static OsmLaneInfo TwoLane() => new()
        { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 };
    private static OsmLaneInfo ThreeLane() => new()
        { TotalLanes = 3, LanesForward = 2, LanesBackward = 1 };

    // --- ReverseSegments ---

    [Fact]
    public void ReverseSegments_SingleSegment_ReversesLaneInfo()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        var reversed = LaneSegmentOps.ReverseSegments(segs, totalPointCount: 50);

        Assert.Single(reversed);
        Assert.Equal(0, reversed[0].StartPointIndex);
        // Forward and backward should be swapped
        Assert.Equal(1, reversed[0].LaneInfo.LanesForward);
        Assert.Equal(2, reversed[0].LaneInfo.LanesBackward);
    }

    [Fact]
    public void ReverseSegments_MultipleSegments_CorrectIndicesAndOrder()
    {
        // N=100, segments at [0, 48, 93]
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 48, LaneInfo = ThreeLane() },
            new() { StartPointIndex = 93, LaneInfo = TwoLane() }
        };

        var reversed = LaneSegmentOps.ReverseSegments(segs, totalPointCount: 100);

        // Expected: [0(was Seg2), 7(was Seg1), 52(was Seg0)]
        Assert.Equal(3, reversed.Count);
        Assert.Equal(0, reversed[0].StartPointIndex);   // was Seg2: 100-1-99=0
        Assert.Equal(7, reversed[1].StartPointIndex);   // was Seg1: 100-1-92=7
        Assert.Equal(52, reversed[2].StartPointIndex);  // was Seg0: 100-1-47=52

        // Seg2 (TwoLane) reversed
        Assert.Equal(1, reversed[0].LaneInfo.LanesForward);
        Assert.Equal(1, reversed[0].LaneInfo.LanesBackward);
        // Seg1 (ThreeLane) reversed
        Assert.Equal(1, reversed[1].LaneInfo.LanesForward);
        Assert.Equal(2, reversed[1].LaneInfo.LanesBackward);
    }

    // --- MergeSegments ---

    [Fact]
    public void MergeSegments_EndToStart_CombinesWithOffset()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        // path1 has 50 points, overlap by 1 -> offset = 49
        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Equal(2, merged.Count);
        Assert.Equal(0, merged[0].StartPointIndex);
        Assert.Equal(49, merged[1].StartPointIndex);
        Assert.Equal(2, merged[0].LaneInfo.TotalLanes);
        Assert.Equal(3, merged[1].LaneInfo.TotalLanes);
    }

    [Fact]
    public void MergeSegments_IdenticalAdjacentSegments_Consolidated()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        // Both are TwoLane -> consolidated to single segment
        Assert.Single(merged);
        Assert.Equal(0, merged[0].StartPointIndex);
    }

    [Fact]
    public void MergeSegments_EmptyFirst_ReturnsSecondWithOffset()
    {
        var segs1 = new List<LaneSegment>();
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Single(merged);
        Assert.Equal(49, merged[0].StartPointIndex);
    }

    [Fact]
    public void MergeSegments_EmptySecond_ReturnsFirst()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>();

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Single(merged);
    }

    [Fact]
    public void MergeSegments_MultipleMerges_PreserveBoundaries()
    {
        // Simulate: path1(2-lane) + path2(3-lane) + path3(2-lane)
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };
        var segs3 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };

        var merged12 = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 29);
        var merged123 = LaneSegmentOps.MergeSegments(merged12, segs3, pointOffset: 58);

        Assert.Equal(3, merged123.Count);
        Assert.Equal(0, merged123[0].StartPointIndex);
        Assert.Equal(29, merged123[1].StartPointIndex);
        Assert.Equal(58, merged123[2].StartPointIndex);
    }

    // --- Consolidate ---

    [Fact]
    public void Consolidate_RemovesAdjacentIdentical()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 30, LaneInfo = TwoLane() },
            new() { StartPointIndex = 60, LaneInfo = ThreeLane() }
        };

        var result = LaneSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartPointIndex);
        Assert.Equal(60, result[1].StartPointIndex);
    }

    [Fact]
    public void Consolidate_NoIdentical_Unchanged()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 30, LaneInfo = ThreeLane() }
        };

        var result = LaneSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
    }

    // --- EndToEnd merge with reversal ---

    [Fact]
    public void EndToEnd_ReversesThenMerges()
    {
        // Simulates TryEndToEnd: path1 forward + reversed(path2)
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        // path2 has 3 lanes forward before reversal
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        // Reverse path2 (40 points), then merge
        var reversed2 = LaneSegmentOps.ReverseSegments(segs2, totalPointCount: 40);
        var merged = LaneSegmentOps.MergeSegments(segs1, reversed2, pointOffset: 49);

        Assert.Equal(2, merged.Count);
        // After reversal, ThreeLane becomes 1 forward, 2 backward
        Assert.Equal(1, merged[1].LaneInfo.LanesForward);
        Assert.Equal(2, merged[1].LaneInfo.LanesBackward);
    }
}
