using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.RoadGeometry;

/// <summary>
/// Unit tests for <see cref="StructureSegmentOps"/> — the merge/reverse/consolidate bookkeeping that keeps
/// a bridge sub-range correctly positioned as the underlying point array is concatenated/reversed during
/// spline merging (the "merged-corridor bridge" refactor, plan doc 11). Mirrors LaneSegmentMergeTests but
/// for explicit [start, end] ranges that do not tile the whole path.
/// </summary>
public class StructureSegmentOpsTests
{
    private static StructureSegment Bridge(int start, int end, params long[] wayIds) => new()
    {
        StartPointIndex = start,
        EndPointIndex = end,
        Type = StructureType.Bridge,
        Layer = 1,
        OsmWayIds = new HashSet<long>(wayIds),
    };

    private static StructureSegment Tunnel(int start, int end, params long[] wayIds) => new()
    {
        StartPointIndex = start,
        EndPointIndex = end,
        Type = StructureType.Tunnel,
        Layer = -1,
        OsmWayIds = new HashSet<long>(wayIds),
    };

    // --- ReverseSegments ---

    [Fact]
    public void ReverseSegments_SingleSpan_MapsBothIndices()
    {
        // N=50, span [10, 20] -> [50-1-20, 50-1-10] = [29, 39]
        var segs = new List<StructureSegment> { Bridge(10, 20, 1002) };

        var reversed = StructureSegmentOps.ReverseSegments(segs, totalPointCount: 50);

        Assert.Single(reversed);
        Assert.Equal(29, reversed[0].StartPointIndex);
        Assert.Equal(39, reversed[0].EndPointIndex);
        Assert.Equal(StructureType.Bridge, reversed[0].Type);
        Assert.Contains(1002L, reversed[0].OsmWayIds);
    }

    [Fact]
    public void ReverseSegments_Empty_ReturnsEmpty()
    {
        Assert.Empty(StructureSegmentOps.ReverseSegments([], totalPointCount: 50));
    }

    // --- MergeSegments (offset) ---

    [Fact]
    public void MergeSegments_BridgeInSecondPath_OffsetByPointCount()
    {
        // path1 is plain road (no spans), path2 (11 pts) is a bridge [0,10].
        // EndToStart offset = path1.Points.Count - 1 = 10 -> bridge becomes [10, 20].
        var segs1 = new List<StructureSegment>();
        var segs2 = new List<StructureSegment> { Bridge(0, 10, 1002) };

        var merged = StructureSegmentOps.MergeSegments(segs1, segs2, pointOffset: 10);

        Assert.Single(merged);
        Assert.Equal(10, merged[0].StartPointIndex);
        Assert.Equal(20, merged[0].EndPointIndex);
        Assert.Contains(1002L, merged[0].OsmWayIds);
    }

    [Fact]
    public void MergeSegments_BridgeInFirstPath_Unshifted()
    {
        // road + bridge + road: after the first merge the bridge sits in segments1 and must NOT shift again.
        var segs1 = new List<StructureSegment> { Bridge(10, 20, 1002) };
        var segs2 = new List<StructureSegment>(); // trailing road, no spans

        var merged = StructureSegmentOps.MergeSegments(segs1, segs2, pointOffset: 20);

        Assert.Single(merged);
        Assert.Equal(10, merged[0].StartPointIndex);
        Assert.Equal(20, merged[0].EndPointIndex);
    }

    [Fact]
    public void MergeSegments_TwoDistinctBridges_StaySeparate()
    {
        // A bridge in path1 and a (different) bridge in path2 with a road gap between -> two spans.
        var segs1 = new List<StructureSegment> { Bridge(2, 6, 10) };
        var segs2 = new List<StructureSegment> { Bridge(2, 6, 20) }; // offset 10 -> [12,16]

        var merged = StructureSegmentOps.MergeSegments(segs1, segs2, pointOffset: 10);

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged[0].StartPointIndex);
        Assert.Equal(6, merged[0].EndPointIndex);
        Assert.Equal(12, merged[1].StartPointIndex);
        Assert.Equal(16, merged[1].EndPointIndex);
    }

    // --- Consolidate ---

    [Fact]
    public void Consolidate_ContiguousSameType_Joins_AndUnionsWayIds()
    {
        // Two bridge ways that became adjacent (end+1 == next start) -> one continuous bridge span.
        var segs = new List<StructureSegment>
        {
            Bridge(0, 10, 1002),
            Bridge(11, 20, 1003),
        };

        var result = StructureSegmentOps.Consolidate(segs);

        Assert.Single(result);
        Assert.Equal(0, result[0].StartPointIndex);
        Assert.Equal(20, result[0].EndPointIndex);
        Assert.Contains(1002L, result[0].OsmWayIds);
        Assert.Contains(1003L, result[0].OsmWayIds);
    }

    [Fact]
    public void Consolidate_DifferentType_NotJoined()
    {
        // A bridge touching a tunnel must NOT merge (different structure type).
        var segs = new List<StructureSegment>
        {
            Bridge(0, 10, 1002),
            Tunnel(11, 20, 1003),
        };

        var result = StructureSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
        Assert.Equal(StructureType.Bridge, result[0].Type);
        Assert.Equal(StructureType.Tunnel, result[1].Type);
    }

    [Fact]
    public void Consolidate_NonAdjacentSameType_NotJoined()
    {
        var segs = new List<StructureSegment>
        {
            Bridge(0, 5, 1002),
            Bridge(20, 30, 1003), // gap of road between
        };

        var result = StructureSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
    }

    // --- Reverse does not mutate the source ---

    [Fact]
    public void ReverseSegments_DoesNotMutateSource()
    {
        var src = Bridge(10, 20, 1002);
        var segs = new List<StructureSegment> { src };

        StructureSegmentOps.ReverseSegments(segs, totalPointCount: 50);

        Assert.Equal(10, src.StartPointIndex);
        Assert.Equal(20, src.EndPointIndex);
    }

    // --- Doc 13: bridge-to-bridge continuation-end flags ---

    [Fact]
    public void Clone_CopiesBridgeToBridgeContinuationFlags()
    {
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartContinuesOntoDeck = true,
            EndContinuesOntoDeck = true,
        };

        var clone = seg.Clone();

        Assert.True(clone.StartContinuesOntoDeck);
        Assert.True(clone.EndContinuesOntoDeck);
    }
}
