using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
///     Combines the two parallel oneway chains of an OSM dual carriageway (motorway/trunk direction
///     lanes) into ONE wider bidirectional path (ai_docs/2026-07-10_lateral_spline_merge).
///
///     Runs AFTER longitudinal chaining (<see cref="NodeBasedPathConnector"/>): the connector's oneway
///     guards keep the two directions apart, so at that point each carriageway is typically one long
///     antiparallel chain — the ideal pairing input. Two independent parallel splines drift apart in
///     elevation (each is terrain-sampled, smoothed and bridge-solved on its own centerline; no pass
///     couples parallel non-intersecting splines), which shows as multi-meter steps between twin bridge
///     decks. One merged centerline means one elevation solve — the drift disappears by construction.
///
///     Pairing (all must hold):
///     - both paths' highway type is lateral-merge enabled (per-roadtype layer set flag),
///     - both paths are oneway over their whole length (per lane segments — merged chains keep only the
///       base way's tags, so the tag alone is unreliable),
///     - tangents are antiparallel where they overlap (dot &lt;= <see cref="MaxAntiparallelDot"/>),
///     - median lateral separation within [<see cref="MinPairMedianSeparationMeters"/>,
///       <see cref="MaxPairSeparationMeters"/>],
///     - the shorter path overlaps the longer one over at least <see cref="MinOverlapFraction"/> of its
///       length,
///     - when both carry a ref tag, the refs match (keeps frontage roads / adjacent parallels apart).
///
///     The merged centerline keeps the LONGER path's point count/index space: every point inside the
///     overlap run is pulled halfway toward its projection on the shorter path (weight tapered over
///     <see cref="BoundaryTaperMeters"/> at interior run boundaries so the corridor bends smoothly where
///     one side continues alone). The longer path's lane/structure segment indices therefore stay valid
///     verbatim; the shorter path's structure segments are re-anchored by projecting their original way
///     endpoints onto the merged line (final station accuracy is restored downstream by the V2 plan 0.3a
///     reprojection, which projects OriginalStart/EndPoint onto the final Chaikin-densified spline).
///
///     Lane data is merged PER STATION, never flattened: the longer path's lane-segment boundaries
///     stay valid verbatim, the shorter path's boundaries arrive via the same projections, and every
///     merged segment carries LanesForward/LanesBackward = the two sides' LOCAL lane counts. Outside
///     the overlap run the longer carriageway continues alone and keeps its own oneway lane info.
/// </summary>
internal static class LateralCarriagewayMerger
{
    /// <summary>Pair detection: median centerline separation must be at least this (else it's the same road).</summary>
    internal const float MinPairMedianSeparationMeters = 4f;

    /// <summary>Pair detection and construction: points farther apart than this never pair.</summary>
    internal const float MaxPairSeparationMeters = 30f;

    /// <summary>Pair detection: the shorter path must overlap the longer over at least this fraction of its length.</summary>
    internal const float MinOverlapFraction = 0.7f;

    /// <summary>Pair detection: tangent dot must be at or below this (≈149° or more apart) to count as antiparallel.</summary>
    internal const float MaxAntiparallelDot = -0.85f;

    /// <summary>Centerline construction: looser antiparallel bound so curves don't punch holes into the run.</summary>
    internal const float MaxConstructionDot = -0.5f;

    /// <summary>Averaging weight ramps 0 → 0.5 over this arc length at run boundaries interior to the longer path.</summary>
    internal const float BoundaryTaperMeters = 30f;

    /// <summary>Leftover (non-overlapping) pieces of the shorter path below this length are dropped, longer ones kept.</summary>
    internal const float MinResidualTailMeters = 20f;

    /// <summary>Paths shorter than this are never pairing candidates (junction stubs, crossovers).</summary>
    internal const float MinCandidateLengthMeters = 50f;

    /// <summary>Lanes assumed per carriageway when neither side carries a lanes tag (motorways virtually always do).</summary>
    internal const int FallbackLanesPerCarriageway = 2;

    /// <summary>Invalid-projection gaps of up to this many consecutive points inside a run are bridged.</summary>
    private const int MaxRunGapPoints = 3;

    /// <summary>
    ///     Merges eligible antiparallel oneway pairs in <paramref name="paths"/> and returns the new path
    ///     list (merged paths + residual tails replace their pair; everything else passes through).
    /// </summary>
    /// <param name="paths">Longitudinally connected paths (output of <see cref="NodeBasedPathConnector.Connect"/>).</param>
    /// <param name="eligibleRoadTypes">OSM highway values whose layer set enables lateral merging.</param>
    public static List<PathWithMetadata> Merge(
        List<PathWithMetadata> paths,
        IReadOnlySet<string> eligibleRoadTypes)
    {
        var candidates = new List<(PathWithMetadata Path, PolylineIndex Index)>();
        foreach (var path in paths)
        {
            if (!IsCandidate(path, eligibleRoadTypes))
                continue;
            var index = PolylineIndex.Build(path.Points);
            if (index.TotalLength >= MinCandidateLengthMeters)
                candidates.Add((path, index));
        }

        if (candidates.Count < 2)
            return paths;

        // Score all pairs (AABB-prefiltered), then greedily take the best-overlapped pair first,
        // one merge per path (v1 — a chain broken into several opposite-direction pieces keeps
        // only its best partner; logged below).
        var matches = new List<PairMatch>();
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (!AabbsWithinSeparation(candidates[i].Index, candidates[j].Index))
                    continue;
                var match = ScorePair(candidates[i], candidates[j]);
                if (match != null)
                    matches.Add(match);
            }
        }

        if (matches.Count == 0)
            return paths;

        var used = new HashSet<PathWithMetadata>();
        var replacements = new Dictionary<PathWithMetadata, List<PathWithMetadata>>();
        foreach (var match in matches.OrderByDescending(m => m.OverlappedMeters))
        {
            if (used.Contains(match.Longer.Path) || used.Contains(match.Shorter.Path))
            {
                TerrainCreationLogger.InfoFileOnlyOrQueue(
                    $"[LATERAL-MERGE] skipped pair ways {match.Longer.Path.OsmWayId}+{match.Shorter.Path.OsmWayId}: " +
                    "a path is already part of a better-overlapped pair");
                continue;
            }

            var outcome = ExecuteLateralMerge(match);
            if (outcome == null)
                continue;

            used.Add(match.Longer.Path);
            used.Add(match.Shorter.Path);
            replacements[match.Longer.Path] = [outcome.Merged, .. outcome.ResidualTails];
            replacements[match.Shorter.Path] = [];
        }

        if (used.Count == 0)
            return paths;

        var result = new List<PathWithMetadata>(paths.Count);
        foreach (var path in paths)
        {
            if (replacements.TryGetValue(path, out var replacement))
                result.AddRange(replacement);
            else
                result.Add(path);
        }

        Console.WriteLine($"Lateral carriageway merge: {used.Count / 2} pair(s) combined, " +
                          $"{paths.Count} paths -> {result.Count}");
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Pair detection
    // ---------------------------------------------------------------------------------------------

    private static bool IsCandidate(PathWithMetadata path, IReadOnlySet<string> eligibleRoadTypes)
    {
        if (path.Points.Count < 2)
            return false;

        var highway = path.Tags.GetValueOrDefault("highway");
        if (highway == null || !eligibleRoadTypes.Contains(highway))
            return false;

        return IsFullyOneway(path);
    }

    /// <summary>
    ///     A path is a carriageway candidate only when EVERY lane segment reports oneway — merged
    ///     chains keep only the base way's tags, so the covering segments are the source of truth
    ///     (mirrors <see cref="PathWithMetadata.IsOnewayAtEndpoint"/>). Sentinel segments (no lane
    ///     or oneway tags on the source way) report false and correctly disqualify the path.
    /// </summary>
    internal static bool IsFullyOneway(PathWithMetadata path)
    {
        if (path.LaneSegments.Count == 0)
            return PathWithMetadata.HasOnewayTag(path.Tags);

        foreach (var segment in path.LaneSegments)
        {
            if (!segment.LaneInfo.IsOneWay)
                return false;
        }

        return true;
    }

    private static bool AabbsWithinSeparation(PolylineIndex a, PolylineIndex b)
    {
        return a.Min.X - MaxPairSeparationMeters <= b.Max.X &&
               b.Min.X - MaxPairSeparationMeters <= a.Max.X &&
               a.Min.Y - MaxPairSeparationMeters <= b.Max.Y &&
               b.Min.Y - MaxPairSeparationMeters <= a.Max.Y;
    }

    private sealed record PairMatch(
        (PathWithMetadata Path, PolylineIndex Index) Longer,
        (PathWithMetadata Path, PolylineIndex Index) Shorter,
        float OverlappedMeters,
        float MedianSeparation);

    private static PairMatch? ScorePair(
        (PathWithMetadata Path, PolylineIndex Index) first,
        (PathWithMetadata Path, PolylineIndex Index) second)
    {
        var (longer, shorter) = first.Index.TotalLength >= second.Index.TotalLength
            ? (first, second)
            : (second, first);

        // Frontage-road guard: two parallel oneways that belong to DIFFERENT roads must not pair.
        var refLonger = longer.Path.Tags.GetValueOrDefault("ref");
        var refShorter = shorter.Path.Tags.GetValueOrDefault("ref");
        if (refLonger != null && refShorter != null && refLonger != refShorter)
            return null;

        var shorterPoints = shorter.Path.Points;
        var overlappedMeters = 0f;
        var separations = new List<float>();
        var previousValid = false;

        for (var i = 0; i < shorterPoints.Count; i++)
        {
            var tangent = shorter.Index.TangentAt(i);
            var valid = longer.Index.TryProject(shorterPoints[i], MaxPairSeparationMeters,
                            out _, out var distance, out _, out var longerTangent) &&
                        Vector2.Dot(tangent, longerTangent) <= MaxAntiparallelDot;

            if (valid)
            {
                separations.Add(distance);
                if (previousValid)
                    overlappedMeters += shorter.Index.CumLengths[i] - shorter.Index.CumLengths[i - 1];
            }

            previousValid = valid;
        }

        if (separations.Count < 2)
            return null;

        var overlapFraction = overlappedMeters / shorter.Index.TotalLength;
        if (overlapFraction < MinOverlapFraction)
            return null;

        separations.Sort();
        var median = separations[separations.Count / 2];
        if (median < MinPairMedianSeparationMeters || median > MaxPairSeparationMeters)
            return null;

        return new PairMatch(longer, shorter, overlappedMeters, median);
    }

    // ---------------------------------------------------------------------------------------------
    // Merge construction
    // ---------------------------------------------------------------------------------------------

    private sealed record MergeOutcome(PathWithMetadata Merged, List<PathWithMetadata> ResidualTails);

    private static MergeOutcome? ExecuteLateralMerge(PairMatch match)
    {
        var longer = match.Longer.Path;
        var shorter = match.Shorter.Path;
        var longerIndex = match.Longer.Index;
        var shorterIndex = PolylineIndex.Build(shorter.Points);

        var count = longer.Points.Count;
        var projections = new (bool Valid, Vector2 Point, float Station)[count];
        for (var i = 0; i < count; i++)
        {
            var tangent = longerIndex.TangentAt(i);
            if (shorterIndex.TryProject(longer.Points[i], MaxPairSeparationMeters,
                    out var station, out _, out var projected, out var shorterTangent) &&
                Vector2.Dot(tangent, shorterTangent) <= MaxConstructionDot)
            {
                projections[i] = (true, projected, station);
            }
        }

        var (runStart, runEnd) = FindLongestRun(projections);
        if (runEnd <= runStart)
            return null; // no usable overlap despite the pair score — keep both paths untouched

        // Points inside the run whose individual projection failed the constraints (short gaps,
        // e.g. an isolated kink) still get their nearest projection so the centerline stays smooth.
        for (var i = runStart; i <= runEnd; i++)
        {
            if (!projections[i].Valid &&
                shorterIndex.TryProject(longer.Points[i], MaxPairSeparationMeters * 2f,
                    out var station, out _, out var projected, out _))
            {
                projections[i] = (true, projected, station);
            }
        }

        // Averaged centerline in the longer path's index space. Weight 0.5 in the core; tapered to 0
        // at run boundaries INTERIOR to the path (the corridor bends toward the surviving single
        // carriageway instead of jumping half a separation sideways).
        var mergedPoints = new List<Vector2>(longer.Points);
        var minStation = float.MaxValue;
        var maxStation = float.MinValue;
        for (var i = runStart; i <= runEnd; i++)
        {
            if (!projections[i].Valid)
                continue;

            var weight = 0.5f;
            if (runStart > 0)
            {
                var fromStart = longerIndex.CumLengths[i] - longerIndex.CumLengths[runStart];
                weight = MathF.Min(weight, 0.5f * MathF.Min(1f, fromStart / BoundaryTaperMeters));
            }

            if (runEnd < count - 1)
            {
                var fromEnd = longerIndex.CumLengths[runEnd] - longerIndex.CumLengths[i];
                weight = MathF.Min(weight, 0.5f * MathF.Min(1f, fromEnd / BoundaryTaperMeters));
            }

            mergedPoints[i] = Vector2.Lerp(longer.Points[i], projections[i].Point, weight);
            minStation = MathF.Min(minStation, projections[i].Station);
            maxStation = MathF.Max(maxStation, projections[i].Station);
        }

        var merged = BuildMergedPath(longer, shorter, mergedPoints, runStart, runEnd, count,
            projections, shorterIndex);
        var tails = BuildShorterResidualTails(shorter, shorterIndex, minStation, maxStation);

        TerrainCreationLogger.InfoFileOnlyOrQueue(
            $"[LATERAL-MERGE] ways {longer.OsmWayId}(+{longer.AllWayIds.Count - 1}) + " +
            $"{shorter.OsmWayId}(+{shorter.AllWayIds.Count - 1}) " +
            $"({longer.Tags.GetValueOrDefault("highway", "?")}" +
            $"{(longer.Tags.TryGetValue("ref", out var reference) ? $" {reference}" : "")}): " +
            $"overlap={match.OverlappedMeters:F0}m sep~{match.MedianSeparation:F1}m " +
            $"lanes={merged.Tags.GetValueOrDefault("lanes:forward", "?")}+" +
            $"{merged.Tags.GetValueOrDefault("lanes:backward", "?")} " +
            $"laneSegs={merged.LaneSegments.Count} " +
            $"structureSegs={merged.StructureSegments.Count} residualTails={tails.Count}");

        return new MergeOutcome(merged, tails);
    }

    private static (int Start, int End) FindLongestRun((bool Valid, Vector2 Point, float Station)[] projections)
    {
        var bestStart = 0;
        var bestEnd = -1;
        var runStart = -1;
        var lastValid = -1;

        for (var i = 0; i < projections.Length; i++)
        {
            if (!projections[i].Valid)
                continue;

            if (runStart < 0 || i - lastValid > MaxRunGapPoints + 1)
                runStart = i;

            lastValid = i;
            if (lastValid - runStart > bestEnd - bestStart)
            {
                bestStart = runStart;
                bestEnd = lastValid;
            }
        }

        return (bestStart, bestEnd);
    }

    private static PathWithMetadata BuildMergedPath(
        PathWithMetadata longer,
        PathWithMetadata shorter,
        List<Vector2> mergedPoints,
        int runStart,
        int runEnd,
        int count,
        (bool Valid, Vector2 Point, float Station)[] projections,
        PolylineIndex shorterIndex)
    {
        var laneSegments = BuildMergedLaneSegments(longer, shorter, shorterIndex,
            projections, runStart, runEnd);
        var coreInfo = DominantCoreLaneInfo(laneSegments, mergedPoints, runStart, runEnd);

        // A stale oneway=yes on the merged corridor would leak into RoadSpline.OsmTags and every
        // downstream raw-tag reader; the lane tags are rewritten to describe the combined road's
        // dominant (longest in-run) configuration. LaneSegments hold the per-station truth.
        var tags = new Dictionary<string, string>(longer.Tags);
        tags.Remove("oneway");
        tags["lanes"] = coreInfo.TotalLanes.ToString();
        tags["lanes:forward"] = coreInfo.LanesForward.ToString();
        tags["lanes:backward"] = coreInfo.LanesBackward.ToString();

        var merged = new PathWithMetadata(
            mergedPoints,
            runStart > 0 ? longer.StartNodeId : null,
            runEnd < count - 1 ? longer.EndNodeId : null,
            longer.OsmWayId,
            tags,
            longer.IsBridge,
            longer.IsTunnel,
            longer.StructureType,
            longer.Layer,
            longer.BridgeStructureType)
        {
            AllWayIds = [.. longer.AllWayIds, .. shorter.AllWayIds],
            LaneSegments = laneSegments,
            StructureSegments = MergeStructureSegments(longer, shorter, mergedPoints),
            IsLaterallyMerged = true,
        };

        return merged;
    }

    /// <summary>
    ///     Per-station lane data for the merged corridor. Flattening (one segment with the max lane
    ///     count over each whole chain) declared a mostly-2-lane carriageway 3 lanes end to end as
    ///     soon as ONE exit section was tagged lanes=3. Here the longer path's lane-segment
    ///     boundaries stay valid verbatim (shared index space), the shorter path's boundaries land
    ///     where its points project, and every emitted segment carries the two sides' LOCAL lane
    ///     counts. Outside the overlap run the longer carriageway continues alone and keeps its own
    ///     (oneway) lane info verbatim.
    /// </summary>
    private static List<LaneSegment> BuildMergedLaneSegments(
        PathWithMetadata longer,
        PathWithMetadata shorter,
        PolylineIndex shorterIndex,
        (bool Valid, Vector2 Point, float Station)[] projections,
        int runStart,
        int runEnd)
    {
        var segments = new List<LaneSegment>();
        OsmLaneInfo? current = null;
        OsmLaneInfo? shorterCarry = null; // last resolved partner info (bridges in-run projection gaps)
        OsmLaneInfo? cachedLonger = null;
        OsmLaneInfo? cachedShorter = null;
        OsmLaneInfo? cachedCombined = null;

        for (var i = 0; i < projections.Length; i++)
        {
            var longerInfo = LaneInfoCovering(longer, i);

            OsmLaneInfo info;
            if (i < runStart || i > runEnd)
            {
                info = longerInfo;
            }
            else
            {
                if (projections[i].Valid)
                {
                    var shorterPointIndex = shorterIndex.PointIndexAtStation(projections[i].Station);
                    shorterCarry = LaneInfoCovering(shorter, shorterPointIndex);
                }

                // Combined infos change only at covering-segment boundaries — rebuild on
                // reference change instead of allocating one per point.
                if (cachedCombined == null ||
                    !ReferenceEquals(cachedLonger, longerInfo) ||
                    !ReferenceEquals(cachedShorter, shorterCarry))
                {
                    cachedCombined = CombineLaneInfos(longerInfo, shorterCarry);
                    cachedLonger = longerInfo;
                    cachedShorter = shorterCarry;
                }

                info = cachedCombined;
            }

            if (current == null || !SameLaneConfig(current, info))
            {
                segments.Add(new LaneSegment { StartPointIndex = i, LaneInfo = info });
                current = info;
            }
        }

        return segments;
    }

    /// <summary>
    ///     One overlap-run lane config: the merged path keeps the longer path's travel direction,
    ///     so the longer side's lanes run forward and the antiparallel shorter side's run backward.
    ///     A side without a lane count (width-only tags) mirrors its partner; both unknown falls
    ///     back to <see cref="FallbackLanesPerCarriageway"/> per side. Widths sum only when BOTH
    ///     sides carry the tag (a half-known sum undersizes).
    /// </summary>
    private static OsmLaneInfo CombineLaneInfos(OsmLaneInfo longerInfo, OsmLaneInfo? shorterInfo)
    {
        var rawForward = longerInfo.TotalLanes;
        var rawBackward = shorterInfo?.TotalLanes ?? 0;
        var forward = rawForward > 0 ? rawForward
            : rawBackward > 0 ? rawBackward
            : FallbackLanesPerCarriageway;
        var backward = rawBackward > 0 ? rawBackward : forward;

        return new OsmLaneInfo
        {
            TotalLanes = forward + backward,
            LanesForward = forward,
            LanesBackward = backward,
            IsOneWay = false,
            MaxSpeed = longerInfo.MaxSpeed ?? shorterInfo?.MaxSpeed,
            Surface = longerInfo.Surface ?? shorterInfo?.Surface,
            WidthMeters = SumIfBothPresent(longerInfo.WidthMeters, shorterInfo?.WidthMeters),
            EstWidthMeters = SumIfBothPresent(longerInfo.EstWidthMeters, shorterInfo?.EstWidthMeters),
        };
    }

    private static float? SumIfBothPresent(float? a, float? b) =>
        a.HasValue && b.HasValue ? a.Value + b.Value : null;

    private static bool SameLaneConfig(OsmLaneInfo a, OsmLaneInfo b)
    {
        if (ReferenceEquals(a, b))
            return true;

        return a.TotalLanes == b.TotalLanes &&
               a.LanesForward == b.LanesForward &&
               a.LanesBackward == b.LanesBackward &&
               a.LanesBothWays == b.LanesBothWays &&
               a.IsOneWay == b.IsOneWay &&
               a.MaxSpeed == b.MaxSpeed &&
               a.Surface == b.Surface &&
               a.WidthMeters == b.WidthMeters &&
               a.EstWidthMeters == b.EstWidthMeters;
    }

    /// <summary>
    ///     The in-run lane config covering the most arc length — written into the merged tags for
    ///     raw-tag readers. Out-of-run (single carriageway) segments never win: their clipped
    ///     in-run coverage is empty.
    /// </summary>
    private static OsmLaneInfo DominantCoreLaneInfo(
        List<LaneSegment> segments, List<Vector2> mergedPoints, int runStart, int runEnd)
    {
        var cum = new float[mergedPoints.Count];
        for (var i = 1; i < mergedPoints.Count; i++)
            cum[i] = cum[i - 1] + Vector2.Distance(mergedPoints[i - 1], mergedPoints[i]);

        OsmLaneInfo? best = null;
        var bestLength = float.MinValue;
        for (var k = 0; k < segments.Count; k++)
        {
            var start = Math.Max(segments[k].StartPointIndex, runStart);
            var endInclusive = Math.Min(
                k + 1 < segments.Count ? segments[k + 1].StartPointIndex - 1 : mergedPoints.Count - 1,
                runEnd);
            if (endInclusive < start)
                continue;

            var length = cum[endInclusive] - cum[start];
            if (length > bestLength)
            {
                bestLength = length;
                best = segments[k].LaneInfo;
            }
        }

        return best ?? segments[0].LaneInfo;
    }

    /// <summary>
    ///     The longer path's structure segments keep their indices verbatim (the merged path shares its
    ///     index space). The shorter path's segments are re-anchored by projecting their ORIGINAL way
    ///     endpoint coords onto the merged line (antiparallel ⇒ start/end swap, exactly like
    ///     <see cref="StructureSegmentOps.ReverseSegments"/>). Overlapping same-type ranges — the twin
    ///     decks of one physical bridge — are unioned so the span solver sees ONE deck.
    /// </summary>
    private static List<StructureSegment> MergeStructureSegments(
        PathWithMetadata longer,
        PathWithMetadata shorter,
        List<Vector2> mergedPoints)
    {
        var mergedIndex = PolylineIndex.Build(mergedPoints);
        var segments = new List<StructureSegment>();
        foreach (var segment in longer.StructureSegments)
            segments.Add(segment.Clone());

        foreach (var segment in shorter.StructureSegments)
        {
            var startPoint = segment.OriginalStartPoint ??
                             shorter.Points[Math.Clamp(segment.StartPointIndex, 0, shorter.Points.Count - 1)];
            var endPoint = segment.OriginalEndPoint ??
                           shorter.Points[Math.Clamp(segment.EndPointIndex, 0, shorter.Points.Count - 1)];

            var startIdx = mergedIndex.NearestPointIndex(startPoint);
            var endIdx = mergedIndex.NearestPointIndex(endPoint);

            var clone = segment.Clone();
            if (startIdx <= endIdx)
            {
                clone.StartPointIndex = startIdx;
                clone.EndPointIndex = endIdx;
            }
            else
            {
                // Antiparallel carriageway: the span runs against the merged direction.
                clone.StartPointIndex = endIdx;
                clone.EndPointIndex = startIdx;
                clone.OriginalStartPoint = segment.OriginalEndPoint ?? endPoint;
                clone.OriginalEndPoint = segment.OriginalStartPoint ?? startPoint;
            }

            segments.Add(clone);
        }

        return UnionOverlappingSegments(segments);
    }

    private static List<StructureSegment> UnionOverlappingSegments(List<StructureSegment> segments)
    {
        if (segments.Count < 2)
            return segments;

        var result = new List<StructureSegment>();
        foreach (var segment in segments.OrderBy(s => s.StartPointIndex))
        {
            var previous = result.Count > 0 ? result[^1] : null;
            if (previous != null && previous.Type == segment.Type &&
                segment.StartPointIndex <= previous.EndPointIndex + 1)
            {
                if (segment.EndPointIndex > previous.EndPointIndex)
                {
                    previous.EndPointIndex = segment.EndPointIndex;
                    previous.OriginalEndPoint = segment.OriginalEndPoint;
                }

                previous.OsmWayIds.UnionWith(segment.OsmWayIds);
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    /// <summary>
    ///     Pieces of the shorter path that project outside the merged run (it extended beyond the longer
    ///     path) survive as their own paths when long enough — they are real road, e.g. a carriageway
    ///     continuing past the partner's terrain crop. Short leftovers are dropped (logged): they sit a
    ///     half-separation away from the merged centerline and would only produce sliver splines.
    /// </summary>
    private static List<PathWithMetadata> BuildShorterResidualTails(
        PathWithMetadata shorter,
        PolylineIndex shorterIndex,
        float minStation,
        float maxStation)
    {
        var tails = new List<PathWithMetadata>();
        if (minStation > maxStation)
            return tails;

        // Leading tail: [0, minStation]; trailing tail: [maxStation, total].
        AddTail(0f, minStation, keepStartNode: true);
        AddTail(maxStation, shorterIndex.TotalLength, keepStartNode: false);
        return tails;

        void AddTail(float fromStation, float toStation, bool keepStartNode)
        {
            var length = toStation - fromStation;
            if (length < MinResidualTailMeters)
            {
                if (length > 1f)
                    TerrainCreationLogger.InfoFileOnlyOrQueue(
                        $"[LATERAL-MERGE] dropped {length:F0}m residual tail of way {shorter.OsmWayId}");
                return;
            }

            var points = new List<Vector2>();
            var firstIdx = -1;
            var lastIdx = -1;
            for (var i = 0; i < shorter.Points.Count; i++)
            {
                if (shorterIndex.CumLengths[i] >= fromStation && shorterIndex.CumLengths[i] <= toStation)
                {
                    if (firstIdx < 0) firstIdx = i;
                    lastIdx = i;
                    points.Add(shorter.Points[i]);
                }
            }

            if (points.Count < 2)
                return;

            var tail = new PathWithMetadata(
                points,
                keepStartNode && firstIdx == 0 ? shorter.StartNodeId : null,
                !keepStartNode && lastIdx == shorter.Points.Count - 1 ? shorter.EndNodeId : null,
                shorter.OsmWayId,
                new Dictionary<string, string>(shorter.Tags),
                shorter.IsBridge,
                shorter.IsTunnel,
                shorter.StructureType,
                shorter.Layer,
                shorter.BridgeStructureType)
            {
                AllWayIds = [.. shorter.AllWayIds],
                LaneSegments = SliceLaneSegments(shorter, firstIdx, lastIdx),
                StructureSegments = shorter.StructureSegments
                    .Where(s => s.StartPointIndex >= firstIdx && s.EndPointIndex <= lastIdx)
                    .Select(s =>
                    {
                        var clone = s.Clone();
                        clone.StartPointIndex -= firstIdx;
                        clone.EndPointIndex -= firstIdx;
                        return clone;
                    })
                    .ToList(),
            };
            tails.Add(tail);
        }
    }

    /// <summary>
    ///     Lane segments of <paramref name="path"/> clipped to the point range [firstIdx, lastIdx]
    ///     and re-indexed to a tail starting at 0 — a tail long enough to keep can span a lane
    ///     count change of its own.
    /// </summary>
    private static List<LaneSegment> SliceLaneSegments(PathWithMetadata path, int firstIdx, int lastIdx)
    {
        var result = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = LaneInfoCovering(path, firstIdx) },
        };

        foreach (var segment in path.LaneSegments.OrderBy(s => s.StartPointIndex))
        {
            if (segment.StartPointIndex > firstIdx && segment.StartPointIndex <= lastIdx)
                result.Add(new LaneSegment
                {
                    StartPointIndex = segment.StartPointIndex - firstIdx,
                    LaneInfo = segment.LaneInfo,
                });
        }

        return result;
    }

    private static OsmLaneInfo LaneInfoCovering(PathWithMetadata path, int pointIndex)
    {
        LaneSegment? covering = null;
        foreach (var segment in path.LaneSegments)
        {
            if (segment.StartPointIndex <= pointIndex &&
                (covering == null || segment.StartPointIndex > covering.StartPointIndex))
                covering = segment;
        }

        return covering?.LaneInfo ?? new OsmLaneInfo();
    }

    // ---------------------------------------------------------------------------------------------
    // Polyline projection support
    // ---------------------------------------------------------------------------------------------

    /// <summary>A polyline with cumulative arc lengths, AABB and point projection.</summary>
    private sealed class PolylineIndex
    {
        public List<Vector2> Points = null!;
        public float[] CumLengths = null!;
        public float TotalLength;
        public Vector2 Min;
        public Vector2 Max;

        public static PolylineIndex Build(List<Vector2> points)
        {
            var cum = new float[points.Count];
            var min = points[0];
            var max = points[0];
            for (var i = 1; i < points.Count; i++)
            {
                cum[i] = cum[i - 1] + Vector2.Distance(points[i - 1], points[i]);
                min = Vector2.Min(min, points[i]);
                max = Vector2.Max(max, points[i]);
            }

            return new PolylineIndex
            {
                Points = points,
                CumLengths = cum,
                TotalLength = cum[^1],
                Min = min,
                Max = max,
            };
        }

        /// <summary>Unit direction at a point (average of the adjacent segment directions).</summary>
        public Vector2 TangentAt(int index)
        {
            var before = index > 0 ? Points[index] - Points[index - 1] : Vector2.Zero;
            var after = index < Points.Count - 1 ? Points[index + 1] - Points[index] : Vector2.Zero;
            var tangent = before + after;
            var length = tangent.Length();
            return length > 1e-6f ? tangent / length : Vector2.UnitX;
        }

        /// <summary>Closest point on the polyline within <paramref name="maxDistance"/>; false when farther.</summary>
        public bool TryProject(Vector2 point, float maxDistance,
            out float station, out float distance, out Vector2 projected, out Vector2 tangent)
        {
            station = 0f;
            distance = float.MaxValue;
            projected = Points[0];
            tangent = Vector2.UnitX;

            for (var i = 0; i < Points.Count - 1; i++)
            {
                var a = Points[i];
                var b = Points[i + 1];
                var ab = b - a;
                var lengthSq = ab.LengthSquared();
                if (lengthSq < 1e-12f)
                    continue;

                var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
                var candidate = a + ab * t;
                var d = Vector2.Distance(point, candidate);
                if (d < distance)
                {
                    distance = d;
                    projected = candidate;
                    station = CumLengths[i] + MathF.Sqrt(lengthSq) * t;
                    tangent = ab / MathF.Sqrt(lengthSq);
                }
            }

            return distance <= maxDistance;
        }

        /// <summary>Index of the last polyline point at or before arc-length <paramref name="station"/>.</summary>
        public int PointIndexAtStation(float station)
        {
            var idx = Array.BinarySearch(CumLengths, station);
            if (idx < 0)
                idx = ~idx - 1;
            return Math.Clamp(idx, 0, CumLengths.Length - 1);
        }

        /// <summary>Index of the polyline point nearest to <paramref name="point"/>.</summary>
        public int NearestPointIndex(Vector2 point)
        {
            var best = 0;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < Points.Count; i++)
            {
                var d = Vector2.DistanceSquared(point, Points[i]);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            return best;
        }
    }
}
