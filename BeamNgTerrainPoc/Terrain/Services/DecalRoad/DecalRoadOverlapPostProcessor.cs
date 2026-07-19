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
            if (road.IsAIRoad || road.JunctionConstraint == JunctionConstraintMode.None)
            {
                // AI roads and non-constrained roads pass through unchanged
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
    /// Splits an open-ended constrained road at overlap boundaries.
    /// Interrupt mode: keeps only non-overlapping runs.
    /// Replace mode: also emits overlapping runs with replacement material.
    /// </summary>
    private static List<GeneratedDecalRoad> SplitOpenRoad(
        GeneratedDecalRoad road,
        SurfaceFootprintIndex index,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
    {
        var nodes = road.Nodes;
        var isOverlapping = ComputeOverlapMask(road, index, continuityLookup);

        // If nothing overlaps, return original unchanged
        if (!isOverlapping.Any(x => x))
            return [road];

        // Interrupt mode: only keep non-overlapping runs (existing behavior)
        if (road.JunctionConstraint == JunctionConstraintMode.Interrupt)
            return BuildFragments(road, nodes, isOverlapping, keepOverlapping: false);

        // Replace mode: emit fragments for BOTH non-overlapping (original)
        // and overlapping (replacement material) runs.
        // If replacement material is empty, fall back to Interrupt behavior.
        if (string.IsNullOrEmpty(road.JunctionReplacementMaterial))
            return BuildFragments(road, nodes, isOverlapping, keepOverlapping: false);

        var results = new List<GeneratedDecalRoad>();
        results.AddRange(BuildFragments(road, nodes, isOverlapping, keepOverlapping: false));
        results.AddRange(BuildReplacementFragments(road, nodes, isOverlapping));
        return results;
    }

    /// <summary>
    /// Builds road fragments from contiguous runs of nodes matching the desired overlap state.
    /// When keepOverlapping=false, collects non-overlapping runs (original material).
    /// When keepOverlapping=true, collects overlapping runs.
    /// </summary>
    private static List<GeneratedDecalRoad> BuildFragments(
        GeneratedDecalRoad road, List<float[]> nodes, bool[] isOverlapping,
        bool keepOverlapping)
    {
        var fragments = new List<GeneratedDecalRoad>();
        int segIndex = 0;
        int i = 0;

        while (i < nodes.Count)
        {
            if (isOverlapping[i] != keepOverlapping)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < nodes.Count && isOverlapping[i] == keepOverlapping)
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
                JunctionConstraint = road.JunctionConstraint,
                JunctionReplacementMaterial = road.JunctionReplacementMaterial,
                JunctionReplacementWidth = road.JunctionReplacementWidth,
                JunctionReplacementTextureLength = road.JunctionReplacementTextureLength,
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
    /// Builds replacement-material fragments from contiguous overlapping runs.
    /// Uses the road's JunctionReplacement* values for material, width, and textureLength.
    /// Only emits fragments for INTERIOR overlapping runs (non-overlapping nodes on both sides).
    /// Runs touching the start or end of the road are terminating roads entering/exiting the
    /// junction — those are still fully interrupted (discarded), not replaced.
    /// </summary>
    private static List<GeneratedDecalRoad> BuildReplacementFragments(
        GeneratedDecalRoad road, List<float[]> nodes, bool[] isOverlapping)
    {
        var fragments = new List<GeneratedDecalRoad>();
        int segIndex = 0;
        int i = 0;

        // Resolve replacement values (0 = keep original)
        var replWidth = road.JunctionReplacementWidth > 0
            ? road.JunctionReplacementWidth
            : nodes[0][3]; // use first node's width as fallback

        while (i < nodes.Count)
        {
            if (!isOverlapping[i])
            {
                i++;
                continue;
            }

            int start = i;
            while (i < nodes.Count && isOverlapping[i])
                i++;

            int runLength = i - start;
            if (runLength < 3) continue;

            // Skip runs at the start or end of the road — these are terminating roads
            // entering/exiting the junction. Only interior overlapping runs (through-roads
            // passing through the junction) get replacement material.
            if (start == 0 || i == nodes.Count) continue;

            // Clone nodes with replacement width
            var fragmentNodes = new List<float[]>(runLength);
            for (int n = start; n < start + runLength; n++)
            {
                var orig = nodes[n];
                fragmentNodes.Add([orig[0], orig[1], orig[2], replWidth]);
            }

            fragments.Add(new GeneratedDecalRoad
            {
                Name = $"{road.Name}_jrepl{segIndex}",
                ParentGroupName = road.ParentGroupName,
                Material = road.JunctionReplacementMaterial,
                TextureLength = road.JunctionReplacementTextureLength > 0
                    ? road.JunctionReplacementTextureLength : road.TextureLength,
                RenderPriority = road.RenderPriority,
                StartEndFade = [0f, 0f],
                DistanceFade = road.DistanceFade,
                Drivability = road.Drivability,
                Nodes = fragmentNodes,
                SplineId = road.SplineId,
                JunctionConstraint = JunctionConstraintMode.None, // replacement fragments are final
                IsRoundaboutRoad = road.IsRoundaboutRoad,
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

        // All overlapping → discard (or emit replacement fragments for Replace mode)
        if (isOverlapping.All(x => x))
        {
            if (road.JunctionConstraint == JunctionConstraintMode.Replace
                && !string.IsNullOrEmpty(road.JunctionReplacementMaterial))
                return BuildReplacementFragments(road, nodes, isOverlapping);
            return [];
        }

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
                JunctionConstraint = road.JunctionConstraint,
                JunctionReplacementMaterial = road.JunctionReplacementMaterial,
                JunctionReplacementWidth = road.JunctionReplacementWidth,
                JunctionReplacementTextureLength = road.JunctionReplacementTextureLength,
                IsRoundaboutRoad = road.IsRoundaboutRoad,
                PreserveContinuity = road.PreserveContinuity,
                OverObjects = road.OverObjects,
                ImprovedSpline = road.ImprovedSpline,
                Smoothness = road.Smoothness,
                Detail = road.Detail,
            });
            segIndex++;
        }

        // For Replace mode, also emit replacement fragments for overlapping runs
        if (road.JunctionConstraint == JunctionConstraintMode.Replace
            && !string.IsNullOrEmpty(road.JunctionReplacementMaterial))
            fragments.AddRange(BuildReplacementFragments(road, nodes, isOverlapping));

        return fragments;
    }

    /// <summary>
    /// Computes per-node overlap mask. A node is overlapping if it falls inside
    /// another road's surface footprint — in plan AND vertically (a bridge deck
    /// crossing above a road is not a junction) — unless continuity exempts it.
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
                index.CheckPoint(nodes[i][0], nodes[i][1], nodes[i][2], ownSplineId);

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
