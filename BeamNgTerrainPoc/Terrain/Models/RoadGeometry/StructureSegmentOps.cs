namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Merge/reverse/consolidate operations for <see cref="StructureSegment"/>, the structural analogue of
/// <c>LaneSegmentOps</c>. Keeps a bridge/tunnel sub-range correctly positioned as the underlying point
/// array is concatenated or reversed during spline merging.
///
/// Difference from <c>LaneSegmentOps</c>: structure segments carry an explicit [start, end] range (they do
/// not tile the whole path), so both indices are shifted/reversed, and consolidation joins adjacent or
/// overlapping spans of the SAME type+layer (two contiguous bridge ways → one continuous bridge span).
/// </summary>
public static class StructureSegmentOps
{
    /// <summary>
    /// Reverses a segment list when the underlying point array (of <paramref name="totalPointCount"/> points)
    /// is reversed. A span [s, e] maps to [N-1-e, N-1-s].
    /// </summary>
    public static List<StructureSegment> ReverseSegments(
        List<StructureSegment> segments, int totalPointCount)
    {
        if (segments.Count == 0) return [];

        var reversed = new List<StructureSegment>(segments.Count);
        foreach (var seg in segments)
        {
            var clone = seg.Clone();
            clone.StartPointIndex = totalPointCount - 1 - seg.EndPointIndex;
            clone.EndPointIndex = totalPointCount - 1 - seg.StartPointIndex;
            // Original endpoint coords always track the Start/End index sides (V2 plan 0.3a).
            clone.OriginalStartPoint = seg.OriginalEndPoint;
            clone.OriginalEndPoint = seg.OriginalStartPoint;
            reversed.Add(clone);
        }

        reversed.Sort((a, b) => a.StartPointIndex.CompareTo(b.StartPointIndex));
        return reversed;
    }

    /// <summary>
    /// Combines two segment lists during a path merge. <paramref name="segments2"/>'s indices are offset by
    /// <paramref name="pointOffset"/> (= <c>path1.Points.Count - 1</c>, the shared boundary point), then the
    /// result is sorted and consolidated.
    /// </summary>
    public static List<StructureSegment> MergeSegments(
        List<StructureSegment> segments1,
        List<StructureSegment> segments2,
        int pointOffset)
    {
        var combined = new List<StructureSegment>(segments1.Count + segments2.Count);

        foreach (var seg in segments1)
            combined.Add(seg.Clone());

        foreach (var seg in segments2)
        {
            var clone = seg.Clone();
            clone.StartPointIndex = seg.StartPointIndex + pointOffset;
            clone.EndPointIndex = seg.EndPointIndex + pointOffset;
            combined.Add(clone);
        }

        combined.Sort((a, b) => a.StartPointIndex.CompareTo(b.StartPointIndex));
        return Consolidate(combined);
    }

    /// <summary>
    /// Joins adjacent or overlapping spans of identical type+layer into one (so two contiguous bridge ways
    /// become a single continuous bridge span). Way IDs are unioned; tags/structure-type are taken from the
    /// first span of the run.
    /// </summary>
    public static List<StructureSegment> Consolidate(List<StructureSegment> segments)
    {
        if (segments.Count <= 1) return segments.ToList();

        var sorted = segments.OrderBy(s => s.StartPointIndex).ToList();
        var result = new List<StructureSegment> { sorted[0].Clone() };

        for (var i = 1; i < sorted.Count; i++)
        {
            var prev = result[^1];
            var cur = sorted[i];

            var contiguous = cur.StartPointIndex <= prev.EndPointIndex + 1;
            if (contiguous && cur.Type == prev.Type && cur.Layer == prev.Layer)
            {
                if (cur.EndPointIndex > prev.EndPointIndex)
                {
                    prev.EndPointIndex = cur.EndPointIndex;
                    // The joined span's outermost original endpoint comes from the segment that extends it.
                    prev.OriginalEndPoint = cur.OriginalEndPoint;
                }
                prev.OsmWayIds.UnionWith(cur.OsmWayIds);
                prev.BridgeStructureType ??= cur.BridgeStructureType;
            }
            else
            {
                result.Add(cur.Clone());
            }
        }

        return result;
    }

    /// <summary>
    /// Doc 10: final whole-spline pass that joins adjacent spans of identical TYPE across layer differences,
    /// by arc-length station. OSM's <c>layer</c> tag encodes only the LOCAL crossing order, so one physical
    /// deck arrives as many contiguous ways whose layers alternate (Brooklyn Bridge: 3/0/3/…/1) — the
    /// point-index <see cref="Consolidate"/> (same type+layer) never joins them and every internal boundary
    /// grows a fake abutment pair. Runs AFTER stations are final (post reprojection); each contributor's
    /// layer survives as a <see cref="StructureSegment.LayerRanges"/> sub-range for grade-separation
    /// classification, and the joined span's <see cref="StructureSegment.Layer"/> becomes the max
    /// (governing) layer. Way IDs are unioned (one stable <see cref="StructureSegment.SpanId"/> per deck);
    /// tags/structure-type come from the first span of the run, as in <see cref="Consolidate"/>.
    /// <paramref name="toleranceMeters"/> absorbs sub-metre reprojection seams — a real ground gap between
    /// two decks is never that small.
    /// </summary>
    public static List<StructureSegment> ConsolidateByStation(
        List<StructureSegment> segments, float toleranceMeters = 1.5f)
    {
        if (segments.Count <= 1) return segments.Select(s => s.Clone()).ToList();

        var sorted = segments.OrderBy(s => s.StartDistance).ToList();
        var result = new List<StructureSegment> { sorted[0].Clone() };

        for (var i = 1; i < sorted.Count; i++)
        {
            var prev = result[^1];
            var cur = sorted[i];

            var contiguous = cur.StartDistance <= prev.EndDistance + toleranceMeters;
            if (contiguous && cur.Type == prev.Type)
            {
                prev.LayerRanges ??= [new StructureLayerRange(prev.StartDistance, prev.EndDistance, prev.Layer)];
                prev.LayerRanges.Add(new StructureLayerRange(cur.StartDistance, cur.EndDistance, cur.Layer));

                if (cur.EndDistance > prev.EndDistance)
                {
                    prev.EndDistance = cur.EndDistance;
                    prev.OriginalEndPoint = cur.OriginalEndPoint;
                }

                if (cur.EndPointIndex > prev.EndPointIndex)
                    prev.EndPointIndex = cur.EndPointIndex;

                prev.OsmWayIds.UnionWith(cur.OsmWayIds);
                prev.BridgeStructureType ??= cur.BridgeStructureType;
                prev.Layer = Math.Max(prev.Layer, cur.Layer);
            }
            else
            {
                result.Add(cur.Clone());
            }
        }

        return result;
    }
}
