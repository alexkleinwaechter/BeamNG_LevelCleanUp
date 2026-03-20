using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadLayerFilterCurveTests
{
    /// Helper: creates cross-sections spaced 1m apart along X axis with given curvatures.
    private static (List<UnifiedCrossSection> Sections, List<float> Distances)
        CreateSections(float[] curvatures)
    {
        var sections = new List<UnifiedCrossSection>();
        var distances = new List<float>();
        for (int i = 0; i < curvatures.Length; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i, 0),
                NormalDirection = new Vector2(0, 1),
                TangentDirection = new Vector2(1, 0),
                Curvature = curvatures[i]
            });
            distances.Add(i); // 1m spacing
        }
        return (sections, distances);
    }

    [Fact]
    public void StraightRoad_AllBelowThreshold_ReturnsEmpty()
    {
        var curvatures = Enumerable.Repeat(0.005f, 20).ToArray();
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 15f,
            rangeStart: 0, rangeEnd: 19);

        Assert.Empty(result);
    }

    [Fact]
    public void SingleCurve_ReturnsOneRangeWithTransitions()
    {
        var curvatures = new float[50];
        for (int i = 20; i <= 29; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 5f,
            rangeStart: 0, rangeEnd: 49);

        Assert.Single(result);
        Assert.Equal(15, result[0].Start);
        Assert.Equal(34, result[0].End);
    }

    [Fact]
    public void TwoNearbyCurves_MergeWhenTransitionsOverlap()
    {
        var curvatures = new float[60];
        for (int i = 10; i <= 15; i++) curvatures[i] = 0.02f;
        for (int i = 22; i <= 27; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 5f,
            rangeStart: 0, rangeEnd: 59);

        Assert.Single(result);
        Assert.Equal(5, result[0].Start);
        Assert.Equal(32, result[0].End);
    }

    [Fact]
    public void EntireRoadIsCurve_ReturnsSingleFullRange()
    {
        var curvatures = Enumerable.Repeat(0.05f, 30).ToArray();
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 10f,
            rangeStart: 0, rangeEnd: 29);

        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(29, result[0].End);
    }

    [Fact]
    public void CurveAtStart_ClampsToRangeStart()
    {
        var curvatures = new float[30];
        for (int i = 0; i <= 5; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 10f,
            rangeStart: 0, rangeEnd: 29);

        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(15, result[0].End);
    }

    [Fact]
    public void CurveAtEnd_ClampsToRangeEnd()
    {
        var curvatures = new float[30];
        for (int i = 25; i <= 29; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 10f,
            rangeStart: 0, rangeEnd: 29);

        Assert.Single(result);
        Assert.Equal(15, result[0].Start);
        Assert.Equal(29, result[0].End);
    }

    [Fact]
    public void ZeroTransitionLength_RawCurveRangesOnly()
    {
        var curvatures = new float[30];
        for (int i = 10; i <= 15; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 0f,
            rangeStart: 0, rangeEnd: 29);

        Assert.Single(result);
        Assert.Equal(10, result[0].Start);
        Assert.Equal(15, result[0].End);
    }

    [Fact]
    public void NegativeCurvatureAlsoQualifies()
    {
        var curvatures = new float[30];
        for (int i = 10; i <= 15; i++) curvatures[i] = -0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 0f,
            rangeStart: 0, rangeEnd: 29);

        Assert.Single(result);
        Assert.Equal(10, result[0].Start);
        Assert.Equal(15, result[0].End);
    }

    [Fact]
    public void RespectsRangeStartEnd_FiltersOutsideSections()
    {
        var curvatures = new float[50];
        for (int i = 5; i <= 10; i++) curvatures[i] = 0.02f;
        for (int i = 25; i <= 30; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 0f,
            rangeStart: 20, rangeEnd: 40);

        Assert.Single(result);
        Assert.Equal(25, result[0].Start);
        Assert.Equal(30, result[0].End);
    }
}

public class DecalRoadLayerFilterRandomizerTests
{
    private static List<float> CreateDistances(int count)
    {
        // 1m spacing
        return Enumerable.Range(0, count).Select(i => (float)i).ToList();
    }

    [Fact]
    public void RangeShorterThanMinGapPlusMinPatch_ReturnsEmpty()
    {
        var distances = CreateDistances(10); // 10m total
        var input = new List<(int Start, int End)> { (0, 9) };

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances,
            minPatchLength: 5f, maxPatchLength: 10f,
            minGapLength: 8f, maxGapLength: 15f,
            seed: 42);

        // 10m range, min gap 8m + min patch 5m = 13m required. Not enough room.
        Assert.Empty(result);
    }

    [Fact]
    public void DeterministicWithSameSeed()
    {
        var distances = CreateDistances(200);
        var input = new List<(int Start, int End)> { (0, 199) };

        var result1 = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 10f, 30f, 20f, 50f, seed: 123);
        var result2 = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 10f, 30f, 20f, 50f, seed: 123);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i].Start, result2[i].Start);
            Assert.Equal(result1[i].End, result2[i].End);
        }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentOutput()
    {
        var distances = CreateDistances(500);
        var input = new List<(int Start, int End)> { (0, 499) };

        var result1 = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 10f, 30f, 20f, 50f, seed: 1);
        var result2 = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 10f, 30f, 20f, 50f, seed: 2);

        Assert.False(result1.SequenceEqual(result2));
    }

    [Fact]
    public void AllPatchesWithinBounds()
    {
        var distances = CreateDistances(500);
        var input = new List<(int Start, int End)> { (0, 499) };
        float minPatch = 10f, maxPatch = 30f;

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, minPatch, maxPatch, 20f, 50f, seed: 42);

        Assert.NotEmpty(result);
        foreach (var (start, end) in result)
        {
            float patchLength = distances[end] - distances[start];
            Assert.True(patchLength >= minPatch - 1f,
                $"Patch length {patchLength} < minPatch {minPatch}");
            Assert.True(patchLength <= maxPatch + 1f,
                $"Patch length {patchLength} > maxPatch {maxPatch}");
        }
    }

    [Fact]
    public void PatchesDontExceedRangeBoundaries()
    {
        var distances = CreateDistances(100);
        var input = new List<(int Start, int End)> { (10, 50), (60, 90) };

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 5f, 15f, 5f, 15f, seed: 42);

        foreach (var (start, end) in result)
        {
            bool inRange = input.Any(r => start >= r.Start && end <= r.End);
            Assert.True(inRange,
                $"Patch ({start},{end}) outside all input ranges");
        }
    }

    [Fact]
    public void MultipleInputRanges_PatchesGeneratedPerRange()
    {
        var distances = CreateDistances(300);
        var input = new List<(int Start, int End)>
        {
            (0, 99),
            (200, 299)
        };

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 5f, 15f, 5f, 15f, seed: 42);

        bool hasRange1 = result.Any(p => p.Start >= 0 && p.End <= 99);
        bool hasRange2 = result.Any(p => p.Start >= 200 && p.End <= 299);
        Assert.True(hasRange1, "No patches in first range");
        Assert.True(hasRange2, "No patches in second range");
    }

    [Fact]
    public void InvertedMinMax_ClampedDefensively()
    {
        var distances = CreateDistances(200);
        var input = new List<(int Start, int End)> { (0, 199) };

        // maxPatch < minPatch — should not throw, clamps max = min
        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances,
            minPatchLength: 20f, maxPatchLength: 5f,
            minGapLength: 10f, maxGapLength: 3f,
            seed: 42);

        Assert.NotNull(result);
    }
}

public class DecalRoadLayerFilterCompositionTests
{
    private static (List<UnifiedCrossSection> Sections, List<float> Distances)
        CreateSectionsWithCurve(int count, int curveStart, int curveEnd, float curvature = 0.02f)
    {
        var sections = new List<UnifiedCrossSection>();
        var distances = new List<float>();
        for (int i = 0; i < count; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i, 0),
                NormalDirection = new Vector2(0, 1),
                TangentDirection = new Vector2(1, 0),
                Curvature = (i >= curveStart && i <= curveEnd) ? curvature : 0f
            });
            distances.Add(i);
        }
        return (sections, distances);
    }

    [Fact]
    public void CurvePlusRandomizer_PatchesOnlyWithinCurveZones()
    {
        var (sections, distances) = CreateSectionsWithCurve(200, 50, 100);

        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, 10f, 0, 199);

        var patches = DecalRoadLayerFilter.ApplyRandomizer(
            curveRanges, distances, 5f, 15f, 5f, 15f, seed: 42);

        Assert.NotEmpty(patches);
        foreach (var (start, end) in patches)
        {
            Assert.True(start >= curveRanges[0].Start,
                $"Patch start {start} before curve range {curveRanges[0].Start}");
            Assert.True(end <= curveRanges[0].End,
                $"Patch end {end} after curve range {curveRanges[0].End}");
        }
    }

    [Fact]
    public void RandomizerOnly_PatchesSpanFullRoad()
    {
        var distances = Enumerable.Range(0, 200).Select(i => (float)i).ToList();
        var fullRange = new List<(int Start, int End)> { (0, 199) };

        var patches = DecalRoadLayerFilter.ApplyRandomizer(
            fullRange, distances, 10f, 30f, 20f, 50f, seed: 42);

        Assert.NotEmpty(patches);
        Assert.True(patches.Any(p => p.Start < 100));
    }

    [Fact]
    public void CurveOnly_NoPatchGaps_ContinuousCoverage()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 20, 60);

        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, 5f, 0, 99);

        Assert.Single(curveRanges);
        Assert.True(curveRanges[0].End - curveRanges[0].Start >= 40);
    }

    [Fact]
    public void Randomizer_ZeroGapLargePatch_CoversFullRange()
    {
        var distances = Enumerable.Range(0, 50).Select(i => (float)i).ToList();
        var fullRange = new List<(int Start, int End)> { (0, 49) };

        // With zero gap and patch length fitting within the 49m range, expect one patch
        var result = DecalRoadLayerFilter.ApplyRandomizer(
            fullRange, distances,
            minPatchLength: 40f, maxPatchLength: 49f,
            minGapLength: 0f, maxGapLength: 0f,
            seed: 42);

        Assert.NotEmpty(result);
    }
}

public class DecalRoadLayerFilterInvertRangesTests
{
    [Fact]
    public void NoCurves_ReturnsFullRange()
    {
        var curveRanges = new List<(int Start, int End)>();
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(100, result[0].End);
    }

    [Fact]
    public void EntireRangeIsCurve_ReturnsEmpty()
    {
        var curveRanges = new List<(int Start, int End)> { (0, 100) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Empty(result);
    }

    [Fact]
    public void SingleCurveInMiddle_ReturnsTwoStraightRanges()
    {
        var curveRanges = new List<(int Start, int End)> { (10, 20) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Equal(2, result.Count);
        Assert.Equal((0, 9), result[0]);
        Assert.Equal((21, 100), result[1]);
    }

    [Fact]
    public void MultipleCurves_ReturnsInterleaved()
    {
        var curveRanges = new List<(int Start, int End)> { (10, 20), (40, 60) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Equal(3, result.Count);
        Assert.Equal((0, 9), result[0]);
        Assert.Equal((21, 39), result[1]);
        Assert.Equal((61, 100), result[2]);
    }

    [Fact]
    public void CurveAtStart_ReturnsStraightAfter()
    {
        var curveRanges = new List<(int Start, int End)> { (0, 20) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Single(result);
        Assert.Equal((21, 100), result[0]);
    }

    [Fact]
    public void CurveAtEnd_ReturnsStraightBefore()
    {
        var curveRanges = new List<(int Start, int End)> { (80, 100) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Single(result);
        Assert.Equal((0, 79), result[0]);
    }

    [Fact]
    public void AdjacentCurves_NoZeroLengthStraights()
    {
        // Curves at (10,20) and (21,30) — adjacent, no gap
        var curveRanges = new List<(int Start, int End)> { (10, 20), (21, 30) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 100);

        Assert.Equal(2, result.Count);
        Assert.Equal((0, 9), result[0]);
        Assert.Equal((31, 100), result[1]);
        // No zero-length segments
        Assert.All(result, r => Assert.True(r.End > r.Start));
    }

    [Fact]
    public void RespectsCustomFullRange()
    {
        var curveRanges = new List<(int Start, int End)> { (30, 40) };
        var result = DecalRoadLayerFilter.InvertRanges(curveRanges, 20, 60);

        Assert.Equal(2, result.Count);
        Assert.Equal((20, 29), result[0]);
        Assert.Equal((41, 60), result[1]);
    }
}

public class DecalRoadLayerFilterReplaceInCurveTests
{
    private static (List<UnifiedCrossSection> Sections, List<float> Distances)
        CreateSectionsWithCurve(int count, int curveStart, int curveEnd, float curvature = 0.02f)
    {
        var sections = new List<UnifiedCrossSection>();
        var distances = new List<float>();
        for (int i = 0; i < count; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i, 0),
                NormalDirection = new Vector2(0, 1),
                TangentDirection = new Vector2(1, 0),
                Curvature = (i >= curveStart && i <= curveEnd) ? curvature : 0f
            });
            distances.Add(i);
        }
        return (sections, distances);
    }

    [Fact]
    public void CurveFilter_ThenInvert_ProducesComplementaryRanges()
    {
        // 100m road with curve from index 30-50
        var (sections, distances) = CreateSectionsWithCurve(100, 30, 50);

        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, transitionLength: 5f,
            rangeStart: 0, rangeEnd: 99);

        var straightRanges = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 99);

        // Verify no overlap between curve and straight ranges
        foreach (var curve in curveRanges)
        {
            foreach (var straight in straightRanges)
            {
                bool overlaps = curve.Start <= straight.End && straight.Start <= curve.End;
                Assert.False(overlaps,
                    $"Curve ({curve.Start},{curve.End}) overlaps straight ({straight.Start},{straight.End})");
            }
        }

        // Verify full coverage: all indices 0-99 are in exactly one range
        var covered = new HashSet<int>();
        foreach (var (s, e) in curveRanges)
            for (int i = s; i <= e; i++) covered.Add(i);
        foreach (var (s, e) in straightRanges)
            for (int i = s; i <= e; i++) covered.Add(i);

        for (int i = 0; i <= 99; i++)
            Assert.Contains(i, covered);
    }

    [Fact]
    public void NoCurves_InvertReturnsFullRange()
    {
        // All straight — no curvature
        var curvatures = Enumerable.Repeat(0.005f, 50).ToArray();
        var sections = new List<UnifiedCrossSection>();
        var distances = new List<float>();
        for (int i = 0; i < curvatures.Length; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i, 0),
                NormalDirection = new Vector2(0, 1),
                TangentDirection = new Vector2(1, 0),
                Curvature = curvatures[i]
            });
            distances.Add(i);
        }

        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, 5f, 0, 49);

        Assert.Empty(curveRanges);

        var straightRanges = DecalRoadLayerFilter.InvertRanges(curveRanges, 0, 49);
        Assert.Single(straightRanges);
        Assert.Equal((0, 49), straightRanges[0]);
    }
}
