namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public static class LaneSegmentOps
{
    /// <summary>
    /// Reverses a segment list when the underlying point array is reversed.
    /// Each segment's LaneInfo is also reversed (forward <-> backward).
    /// Index recalculation: segment that ended at endIdx gets new start = N-1-endIdx.
    /// </summary>
    public static List<LaneSegment> ReverseSegments(
        List<LaneSegment> segments, int totalPointCount)
    {
        if (segments.Count == 0) return [];

        var sorted = segments.OrderBy(s => s.StartPointIndex).ToList();
        var reversed = new List<LaneSegment>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            // Each segment spans from StartPointIndex to endIdx
            int endIdx = (i + 1 < sorted.Count)
                ? sorted[i + 1].StartPointIndex - 1
                : totalPointCount - 1;

            reversed.Add(new LaneSegment
            {
                StartPointIndex = totalPointCount - 1 - endIdx,
                LaneInfo = sorted[i].LaneInfo.Reversed()
            });
        }

        // Sort ascending by new StartPointIndex
        reversed.Sort((a, b) => a.StartPointIndex.CompareTo(b.StartPointIndex));
        return reversed;
    }

    /// <summary>
    /// Combines two segment lists during path merge.
    /// Offsets segments2's indices by pointOffset, then consolidates.
    /// </summary>
    public static List<LaneSegment> MergeSegments(
        List<LaneSegment> segments1,
        List<LaneSegment> segments2,
        int pointOffset)
    {
        var combined = new List<LaneSegment>(segments1.Count + segments2.Count);

        foreach (var seg in segments1)
        {
            combined.Add(new LaneSegment
            {
                StartPointIndex = seg.StartPointIndex,
                LaneInfo = seg.LaneInfo
            });
        }

        foreach (var seg in segments2)
        {
            combined.Add(new LaneSegment
            {
                StartPointIndex = seg.StartPointIndex + pointOffset,
                LaneInfo = seg.LaneInfo
            });
        }

        combined.Sort((a, b) => a.StartPointIndex.CompareTo(b.StartPointIndex));
        return Consolidate(combined);
    }

    /// <summary>
    /// Removes adjacent segments with identical lane configuration.
    /// </summary>
    public static List<LaneSegment> Consolidate(List<LaneSegment> segments)
    {
        if (segments.Count <= 1) return segments.ToList();

        var result = new List<LaneSegment> { segments[0] };
        for (int i = 1; i < segments.Count; i++)
        {
            if (!AreLaneConfigsEqual(result[^1].LaneInfo, segments[i].LaneInfo))
                result.Add(segments[i]);
        }
        return result;
    }

    private static bool AreLaneConfigsEqual(OsmLaneInfo a, OsmLaneInfo b)
    {
        return a.TotalLanes == b.TotalLanes
            && a.LanesForward == b.LanesForward
            && a.LanesBackward == b.LanesBackward
            && a.IsOneWay == b.IsOneWay
            && a.WidthMeters == b.WidthMeters
            && a.EstWidthMeters == b.EstWidthMeters;
    }
}
