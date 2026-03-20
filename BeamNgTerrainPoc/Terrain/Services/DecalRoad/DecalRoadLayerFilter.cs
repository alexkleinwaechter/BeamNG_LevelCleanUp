using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Sequential filter pipeline for DecalRoad layer generation.
/// Filters operate on cross-section index ranges and compose sequentially:
/// curve filter narrows to curve zones, randomizer subdivides into patches.
/// </summary>
public static class DecalRoadLayerFilter
{
    /// <summary>
    /// Filters cross-section ranges to only include sections where curvature exceeds threshold,
    /// with configurable transition extensions before and after each curve zone.
    /// Uses absolute curvature — both left and right curves qualify.
    /// </summary>
    public static List<(int Start, int End)> ApplyCurveFilter(
        IReadOnlyList<UnifiedCrossSection> sections,
        IReadOnlyList<float> csDistances,
        float minCurvature,
        float transitionLength,
        int rangeStart,
        int rangeEnd)
    {
        // Step 1: Mark sections exceeding curvature threshold
        // Step 2: Group consecutive marked indices into raw ranges
        var rawRanges = new List<(int Start, int End)>();
        int? currentStart = null;

        for (int i = rangeStart; i <= rangeEnd; i++)
        {
            if (MathF.Abs(sections[i].Curvature) >= minCurvature)
            {
                currentStart ??= i;
            }
            else
            {
                if (currentStart.HasValue)
                {
                    rawRanges.Add((currentStart.Value, i - 1));
                    currentStart = null;
                }
            }
        }
        if (currentStart.HasValue)
            rawRanges.Add((currentStart.Value, rangeEnd));

        if (rawRanges.Count == 0)
            return [];

        // Step 3: Extend each raw range by transition distance using csDistances
        var extendedRanges = new List<(int Start, int End)>();
        foreach (var (rawStart, rawEnd) in rawRanges)
        {
            // Extend start: find last index where distance from rawStart <= transitionLength
            int extStart = rawStart;
            float startDist = csDistances[rawStart];
            for (int i = rawStart - 1; i >= rangeStart; i--)
            {
                if (startDist - csDistances[i] <= transitionLength)
                    extStart = i;
                else
                    break;
            }

            // Extend end: find first index where distance from rawEnd <= transitionLength
            int extEnd = rawEnd;
            float endDist = csDistances[rawEnd];
            for (int i = rawEnd + 1; i <= rangeEnd; i++)
            {
                if (csDistances[i] - endDist <= transitionLength)
                    extEnd = i;
                else
                    break;
            }

            extendedRanges.Add((extStart, extEnd));
        }

        // Step 4: Merge overlapping or adjacent ranges
        return MergeRanges(extendedRanges);
    }

    /// <summary>
    /// Subdivides input ranges into random patches with gaps.
    /// Starts each range with a gap, then alternates patch/gap.
    /// Deterministic for the same seed.
    /// </summary>
    public static List<(int Start, int End)> ApplyRandomizer(
        IReadOnlyList<(int Start, int End)> inputRanges,
        IReadOnlyList<float> csDistances,
        float minPatchLength,
        float maxPatchLength,
        float minGapLength,
        float maxGapLength,
        int seed)
    {
        // Clamp max >= min defensively
        var effectiveMaxPatch = MathF.Max(maxPatchLength, minPatchLength);
        var effectiveMaxGap = MathF.Max(maxGapLength, minGapLength);

        var rng = new Random(seed);
        var patches = new List<(int Start, int End)>();

        foreach (var (rangeStart, rangeEnd) in inputRanges)
        {
            if (rangeEnd - rangeStart < 2) continue;

            float rangeStartDist = csDistances[rangeStart];
            float rangeEndDist = csDistances[rangeEnd];
            int cursor = rangeStart;

            while (true)
            {
                // Gap
                float gapLen = minGapLength + rng.NextSingle() * (effectiveMaxGap - minGapLength);
                float gapTargetDist = csDistances[cursor] + gapLen;

                // Find index past gap
                int gapEnd = cursor;
                while (gapEnd <= rangeEnd && csDistances[gapEnd] < gapTargetDist)
                    gapEnd++;

                if (gapEnd > rangeEnd) break;

                // Check if remaining distance can fit minimum patch
                float remaining = rangeEndDist - csDistances[gapEnd];
                if (remaining < minPatchLength) break;

                // Patch
                float patchLen = minPatchLength + rng.NextSingle() * (effectiveMaxPatch - minPatchLength);
                float patchTargetDist = csDistances[gapEnd] + patchLen;

                // Find index at patch end
                int patchEnd = gapEnd;
                while (patchEnd <= rangeEnd && csDistances[patchEnd] < patchTargetDist)
                    patchEnd++;

                // Clamp to range end
                patchEnd = Math.Min(patchEnd, rangeEnd);

                // Discard patches shorter than 2 indices
                if (patchEnd - gapEnd >= 2)
                    patches.Add((gapEnd, patchEnd));

                cursor = patchEnd;
                if (cursor >= rangeEnd) break;
            }
        }

        return patches;
    }

    /// <summary>
    /// Inverts curve ranges to produce straight ranges within [fullStart, fullEnd].
    /// Given curve ranges [(10,20), (40,60)] and full range (0,100):
    /// Returns straight ranges [(0,9), (21,39), (61,100)].
    /// Zero-length segments are excluded.
    /// </summary>
    public static List<(int Start, int End)> InvertRanges(
        IReadOnlyList<(int Start, int End)> curveRanges,
        int fullStart,
        int fullEnd)
    {
        if (curveRanges.Count == 0)
            return [(fullStart, fullEnd)];

        var straights = new List<(int Start, int End)>();
        int cursor = fullStart;

        foreach (var (cStart, cEnd) in curveRanges)
        {
            if (cursor < cStart)
                straights.Add((cursor, cStart - 1));
            cursor = cEnd + 1;
        }

        if (cursor <= fullEnd)
            straights.Add((cursor, fullEnd));

        // Filter zero-length segments
        return straights.Where(r => r.End > r.Start).ToList();
    }

    /// <summary>
    /// Merges overlapping or adjacent (int Start, int End) ranges.
    /// Input must be sorted by Start (our callers produce sorted output).
    /// </summary>
    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count <= 1) return ranges;

        var merged = new List<(int Start, int End)> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            if (ranges[i].Start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, ranges[i].End));
            else
                merged.Add(ranges[i]);
        }
        return merged;
    }
}
