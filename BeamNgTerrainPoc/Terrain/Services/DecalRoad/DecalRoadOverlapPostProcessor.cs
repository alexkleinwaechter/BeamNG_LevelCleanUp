using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Post-processes generated DecalRoads to detect and resolve overlaps.
/// Works on actual generated geometry instead of predicting overlaps from corridors.
/// AI roads are never touched — overlapping AI roads is intentional.
/// </summary>
public static class DecalRoadOverlapPostProcessor
{
    /// <summary>
    /// Processes all generated DecalRoads: builds a surface footprint from per-spline
    /// corridor data (full road width), then splits interruptable roads where they
    /// overlap another spline's surface.
    /// </summary>
    public static List<GeneratedDecalRoad> Process(
        List<GeneratedDecalRoad> allRoads,
        IReadOnlyList<SplineSurfaceData> splineSurfaces,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
    {
        // 1. Build footprint index from per-spline full road surface corridors
        var index = new SurfaceFootprintIndex();
        foreach (var surface in splineSurfaces)
            index.AddSplineSurface(surface);

        // 2. Classify roads — only AI vs interruptable matters now
        var results = new List<GeneratedDecalRoad>(allRoads.Count);
        var interruptableNonRoundabout = new List<GeneratedDecalRoad>();
        var interruptableRoundabout = new List<GeneratedDecalRoad>();

        foreach (var road in allRoads)
        {
            if (road.IsAIRoad || !road.InterruptAtJunctions)
            {
                // AI roads and non-interruptable roads pass through unchanged
                results.Add(road);
            }
            else if (road.IsRoundaboutRoad)
            {
                interruptableRoundabout.Add(road);
            }
            else
            {
                interruptableNonRoundabout.Add(road);
            }
        }

        // 3. Process non-roundabout interruptable roads
        foreach (var road in interruptableNonRoundabout)
            results.AddRange(SplitOpenRoad(road, index, continuityLookup));

        // 4. Process roundabout interruptable roads last
        foreach (var road in interruptableRoundabout)
            results.AddRange(SplitClosedLoopRoad(road, index, continuityLookup));

        return results;
    }

    /// <summary>
    /// Splits an open-ended interruptable road at overlap boundaries.
    /// Contiguous runs of non-overlapping nodes with >= 3 nodes become fragments.
    /// </summary>
    private static List<GeneratedDecalRoad> SplitOpenRoad(
        GeneratedDecalRoad road,
        SurfaceFootprintIndex index,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
    {
        var nodes = road.Nodes;
        var isOverlapping = ComputeOverlapMask(road, index, continuityLookup);

        // Collect contiguous runs of non-overlapping nodes
        var fragments = new List<GeneratedDecalRoad>();
        int segIndex = 0;
        int i = 0;

        while (i < nodes.Count)
        {
            // Skip overlapping nodes
            if (isOverlapping[i])
            {
                i++;
                continue;
            }

            // Start of a non-overlapping run
            int start = i;
            while (i < nodes.Count && !isOverlapping[i])
                i++;

            int runLength = i - start;
            if (runLength < 3) continue;

            var fragmentNodes = nodes.GetRange(start, runLength);
            var isFirst = start == 0;
            var isLast = i == nodes.Count;

            fragments.Add(new GeneratedDecalRoad
            {
                Name = $"{road.Name}_seg{segIndex}",
                ParentGroupName = road.ParentGroupName,
                Material = road.Material,
                TextureLength = road.TextureLength,
                RenderPriority = road.RenderPriority,
                StartEndFade = [
                    isFirst ? road.StartEndFade[0] : 0f,
                    isLast ? road.StartEndFade[1] : 0f
                ],
                DistanceFade = road.DistanceFade,
                Drivability = road.Drivability,
                Nodes = fragmentNodes,
                SplineId = road.SplineId,
                InterruptAtJunctions = road.InterruptAtJunctions,
                IsRoundaboutRoad = road.IsRoundaboutRoad,
                PreserveContinuity = road.PreserveContinuity,
                OverObjects = road.OverObjects,
                ImprovedSpline = road.ImprovedSpline,
                Smoothness = road.Smoothness,
                Detail = road.Detail,
            });
            segIndex++;
        }

        // If no splits needed, return original
        return fragments.Count == 0
            ? []
            : fragments.Count == 1 && fragments[0].Nodes.Count == nodes.Count
                ? [road]
                : fragments;
    }

    /// <summary>
    /// Splits a closed-loop (roundabout) interruptable road at overlap boundaries.
    /// Handles wrap-around at the seam by rotating the logical start point.
    /// </summary>
    private static List<GeneratedDecalRoad> SplitClosedLoopRoad(
        GeneratedDecalRoad road,
        SurfaceFootprintIndex index,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
    {
        var nodes = road.Nodes;
        var isOverlapping = ComputeOverlapMask(road, index, continuityLookup);

        // All overlapping → discard
        if (isOverlapping.All(x => x))
            return [];

        // None overlapping → keep as-is
        if (isOverlapping.All(x => !x))
            return [road];

        // Find first non-overlapping index as rotation start
        int startIdx = Array.IndexOf(isOverlapping, false);

        // Walk rotated sequence, collect contiguous non-overlapping runs
        var fragments = new List<GeneratedDecalRoad>();
        int segIndex = 0;
        int count = nodes.Count;
        int pos = 0;

        while (pos < count)
        {
            int actualIdx = (startIdx + pos) % count;

            if (isOverlapping[actualIdx])
            {
                pos++;
                continue;
            }

            // Start of a non-overlapping run
            var runNodes = new List<float[]>();
            while (pos < count)
            {
                actualIdx = (startIdx + pos) % count;
                if (isOverlapping[actualIdx]) break;
                runNodes.Add(nodes[actualIdx]);
                pos++;
            }

            if (runNodes.Count < 3) continue;

            fragments.Add(new GeneratedDecalRoad
            {
                Name = $"{road.Name}_seg{segIndex}",
                ParentGroupName = road.ParentGroupName,
                Material = road.Material,
                TextureLength = road.TextureLength,
                RenderPriority = road.RenderPriority,
                StartEndFade = [0f, 0f],
                DistanceFade = road.DistanceFade,
                Drivability = road.Drivability,
                Nodes = runNodes,
                SplineId = road.SplineId,
                InterruptAtJunctions = road.InterruptAtJunctions,
                IsRoundaboutRoad = road.IsRoundaboutRoad,
                PreserveContinuity = road.PreserveContinuity,
                OverObjects = road.OverObjects,
                ImprovedSpline = road.ImprovedSpline,
                Smoothness = road.Smoothness,
                Detail = road.Detail,
            });
            segIndex++;
        }

        return fragments;
    }

    /// <summary>
    /// Computes per-node overlap mask. A node is overlapping if it falls inside
    /// another road's surface footprint, unless continuity exempts it.
    /// </summary>
    private static bool[] ComputeOverlapMask(
        GeneratedDecalRoad road,
        SurfaceFootprintIndex index,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
    {
        var nodes = road.Nodes;
        var mask = new bool[nodes.Count];
        var ownSplineId = road.SplineId;

        // Continuity exemption only applies to layers that should preserve continuity
        // (e.g., DirectionDivider center lines). Edge blends and side lines are always suppressed.
        HashSet<int>? continuousSplines = null;
        if (road.PreserveContinuity)
            continuityLookup?.TryGetValue(ownSplineId, out continuousSplines);

        for (int i = 0; i < nodes.Count; i++)
        {
            var (isOverlapping, overlappingSplineId) =
                index.CheckPoint(nodes[i][0], nodes[i][1], ownSplineId);

            if (isOverlapping)
            {
                // Continuity exemption: if this road is continuous with the overlapping one,
                // don't suppress (the other road terminates, this one continues)
                if (continuousSplines != null && continuousSplines.Contains(overlappingSplineId))
                    continue;

                mask[i] = true;
            }
        }

        return mask;
    }
}
