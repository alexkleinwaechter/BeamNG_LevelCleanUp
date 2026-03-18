# DecalRoad Layer Constraints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add curve-only and randomizer constraint filters to DecalRoad layer generation, allowing layers to be restricted to curves or scattered as random patches along roads.

**Architecture:** Two sequential filters inserted into `DecalRoadGenerator.GenerateForSpline` between layer expansion and node generation. The curve filter narrows eligible cross-section ranges to curve zones (using existing `UnifiedCrossSection.Curvature`), the randomizer subdivides ranges into random patches. Both operate on index ranges and compose naturally: curve first, then randomize within curves. Filters are applied per-layer within each phase's per-range loop, intersecting with existing lane-change ranges.

**Tech Stack:** .NET 9, C#, System.Numerics, xUnit

**Spec:** `docs/superpowers/specs/2026-03-18-decalroad-layer-constraints-design.md`

**Skills:** @beamng-decalroad-format, @beamng-decalroad-generation, @beamng-road-layers

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs` | Static class: `ApplyCurveFilter` + `ApplyRandomizer` methods |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs` | Unit tests for both filters and composition |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Add 8 constraint properties |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs` | Add `RandomSeed` property |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Insert filter pipeline call site in both Phase A and Phase B loops |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Add curve constraint and randomizer UI sections |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Update `DeepCopyLayer` with new properties |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Add `RandomSeed` field to DecalRoad section |

### Unchanged Files (Already Support This)

| File | Why Unchanged |
|------|---------------|
| `DecalRoadDefaultLayerSets.cs` | New properties default to `false`/disabled — current behavior preserved |
| `DecalRoadDefaultsManager.cs` | JSON serialization picks up new properties automatically |
| `TerrainPresetExporter/Importer` | Serialization picks up new properties automatically |
| `DecalRoadSceneWriter.cs` | Writes `GeneratedDecalRoad` objects — unaware of filtering |
| `JunctionInterrupter.cs` / `RoadCorridorOverlapChecker.cs` | Runs after filtering — no changes |
| `DecalRoadLayerSetEditorDialog.razor` | Dialog wrapper — inner editor handles new fields |

---

## Task 1: Add Constraint Properties to Data Models

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs:1-31`
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs:1-10`

- [ ] **Step 1: Add 8 constraint properties to DecalRoadLayerDefinition**

After the existing `OverObjects` property (line 30), add:

```csharp
// ========================================
// CURVE-ONLY CONSTRAINT
// ========================================

/// <summary>
/// When true, this layer is only generated in road sections where curvature
/// exceeds CurveMinCurvature. Straight sections are skipped.
/// </summary>
public bool CurveOnly { get; set; }

/// <summary>
/// Minimum curvature threshold (1/radius in 1/meters) for curve detection.
/// Default 0.01 = curves tighter than 100m radius.
/// Uses absolute value of UnifiedCrossSection.Curvature.
/// </summary>
public float CurveMinCurvature { get; set; } = 0.01f;

/// <summary>
/// Distance in meters to extend the generated zone before and after the detected curve.
/// Creates a lead-in/lead-out zone. FadeIn/FadeOut control visual fade independently.
/// </summary>
public float CurveTransitionLength { get; set; } = 15.0f;

// ========================================
// RANDOMIZER CONSTRAINT
// ========================================

/// <summary>
/// When true, this layer is generated as random patches with gaps instead of continuously.
/// </summary>
public bool Randomize { get; set; }

/// <summary>
/// Minimum length of each generated patch in meters.
/// </summary>
public float RandomMinPatchLength { get; set; } = 10.0f;

/// <summary>
/// Maximum length of each generated patch in meters.
/// </summary>
public float RandomMaxPatchLength { get; set; } = 50.0f;

/// <summary>
/// Minimum gap between patches in meters.
/// </summary>
public float RandomMinGapLength { get; set; } = 20.0f;

/// <summary>
/// Maximum gap between patches in meters.
/// </summary>
public float RandomMaxGapLength { get; set; } = 100.0f;
```

- [ ] **Step 2: Add RandomSeed to DecalRoadSettings**

After the existing `OsmLayerSets` property in `DecalRoadSettings.cs`, add:

```csharp
/// <summary>
/// Global seed for randomizer. Combined with spline ID for per-spline deterministic
/// randomization. Same seed + same settings = same output.
/// </summary>
public int RandomSeed { get; set; } = 42;
```

- [ ] **Step 3: Update DeepCopyLayer in DecalRoadLayerSetEditor.razor.cs**

In the `DeepCopyLayer` method (line 105-131 of `DecalRoadLayerSetEditor.razor.cs`), add the missing `OverObjects` property and the 8 new constraint properties after `FlipDirection`:

```csharp
OverObjects = source.OverObjects,
CurveOnly = source.CurveOnly,
CurveMinCurvature = source.CurveMinCurvature,
CurveTransitionLength = source.CurveTransitionLength,
Randomize = source.Randomize,
RandomMinPatchLength = source.RandomMinPatchLength,
RandomMaxPatchLength = source.RandomMaxPatchLength,
RandomMinGapLength = source.RandomMinGapLength,
RandomMaxGapLength = source.RandomMaxGapLength,
```

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs
git commit -m "feat: add curve-only and randomizer constraint properties to DecalRoad layer model"
```

---

## Task 2: Implement Curve Filter

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs`
- Test: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`

- [ ] **Step 1: Write curve filter tests**

Create `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadLayerFilterCurveTests
{
    /// <summary>
    /// Helper: creates cross-sections spaced 1m apart along X axis with given curvatures.
    /// Returns (sections, csDistances).
    /// </summary>
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
        // 50 sections at 1m spacing. Curvature > threshold at indices 20-29.
        var curvatures = new float[50];
        for (int i = 20; i <= 29; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 5f,
            rangeStart: 0, rangeEnd: 49);

        Assert.Single(result);
        // Raw curve: 20-29. Transition extends 5m before (index 15) and after (index 34).
        Assert.Equal(15, result[0].Start);
        Assert.Equal(34, result[0].End);
    }

    [Fact]
    public void TwoNearbyCurves_MergeWhenTransitionsOverlap()
    {
        // Two curve zones close enough that their transitions overlap.
        var curvatures = new float[60];
        for (int i = 10; i <= 15; i++) curvatures[i] = 0.02f;
        for (int i = 22; i <= 27; i++) curvatures[i] = 0.02f;
        var (sections, distances) = CreateSections(curvatures);

        // Transition 5m: first raw 10-15 extends to 5-20, second raw 22-27 extends to 17-32.
        // They overlap → merge into single range 5-32.
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
        Assert.Equal(0, result[0].Start); // Clamped to 0
        Assert.Equal(15, result[0].End);  // 5 + 10 transition
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
        Assert.Equal(15, result[0].Start); // 25 - 10 transition
        Assert.Equal(29, result[0].End);   // Clamped to 29
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
        // Curvature sign indicates direction (left/right). Both should qualify.
        var curvatures = new float[30];
        for (int i = 10; i <= 15; i++) curvatures[i] = -0.02f; // Right curve
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
        for (int i = 5; i <= 10; i++) curvatures[i] = 0.02f;   // Outside range
        for (int i = 25; i <= 30; i++) curvatures[i] = 0.02f;  // Inside range
        var (sections, distances) = CreateSections(curvatures);

        var result = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, minCurvature: 0.01f, transitionLength: 0f,
            rangeStart: 20, rangeEnd: 40);

        Assert.Single(result);
        Assert.Equal(25, result[0].Start);
        Assert.Equal(30, result[0].End);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilterCurveTests" --no-build 2>&1 || true`
Expected: Build error — `DecalRoadLayerFilter` doesn't exist yet.

- [ ] **Step 3: Implement ApplyCurveFilter**

Create `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs`:

```csharp
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
```

- [ ] **Step 4: Run curve filter tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilterCurveTests" -v minimal`
Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs
git commit -m "feat: implement curve-only filter for DecalRoad layer constraints"
```

---

## Task 3: Implement Randomizer Filter

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs` (add randomizer test class)
- The implementation was already included in Task 2's `DecalRoadLayerFilter.cs`

- [ ] **Step 1: Add randomizer tests**

Append to `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`:

```csharp
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

        // Very unlikely to be identical with different seeds
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
            Assert.True(patchLength >= minPatch - 1f, // -1 for index discretization
                $"Patch length {patchLength} < minPatch {minPatch}");
            Assert.True(patchLength <= maxPatch + 1f, // +1 for index discretization
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
            // Must be within one of the input ranges
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
            (0, 99),    // 100m range
            (200, 299)  // 100m range
        };

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            input, distances, 5f, 15f, 5f, 15f, seed: 42);

        // Should have patches in both ranges
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
            minPatchLength: 20f, maxPatchLength: 5f,  // Inverted!
            minGapLength: 10f, maxGapLength: 3f,       // Inverted!
            seed: 42);

        // Should produce some result without exception
        Assert.NotNull(result);
    }
}
```

- [ ] **Step 2: Run randomizer tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilterRandomizerTests" -v minimal`
Expected: All 7 tests pass. (Implementation already exists from Task 2.)

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs
git commit -m "test: add randomizer filter tests for DecalRoad layer constraints"
```

---

## Task 4: Add Composition Tests

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`

- [ ] **Step 1: Add composition test class**

Append to `DecalRoadLayerFilterTests.cs`:

```csharp
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
        // 200m road with curve at 50-100
        var (sections, distances) = CreateSectionsWithCurve(200, 50, 100);

        // Curve filter with 10m transition
        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, 10f, 0, 199);

        // Then randomize within curve ranges
        var patches = DecalRoadLayerFilter.ApplyRandomizer(
            curveRanges, distances, 5f, 15f, 5f, 15f, seed: 42);

        // All patches must be within the curve range (40-110 with transitions)
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
        // At least one patch should start in the first half
        Assert.True(patches.Any(p => p.Start < 100));
    }

    [Fact]
    public void CurveOnly_NoPatchGaps_ContinuousCoverage()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 20, 60);

        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sections, distances, 0.01f, 5f, 0, 99);

        // Without randomizer, curve filter returns continuous ranges
        Assert.Single(curveRanges);
        // All indices within the range are covered (no internal gaps)
        Assert.True(curveRanges[0].End - curveRanges[0].Start >= 40);
    }

    [Fact]
    public void Randomizer_ZeroGapLargePatch_CoversFullRange()
    {
        // With zero gap and large patch length, the randomizer should produce
        // a single patch covering the entire range.
        var distances = Enumerable.Range(0, 50).Select(i => (float)i).ToList();
        var fullRange = new List<(int Start, int End)> { (0, 49) };

        var result = DecalRoadLayerFilter.ApplyRandomizer(
            fullRange, distances,
            minPatchLength: 100f, maxPatchLength: 100f,
            minGapLength: 0f, maxGapLength: 0f,
            seed: 42);

        // Gap of 0 means the first gap step finds gapEnd immediately.
        // Then 50m range with 100m min patch → single patch to end.
        Assert.NotEmpty(result);
    }
}
```

- [ ] **Step 2: Run all filter tests**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilter" -v minimal`
Expected: All tests pass (curve + randomizer + composition).

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs
git commit -m "test: add composition tests for curve + randomizer filter pipeline"
```

---

## Task 5: Integrate Filters into DecalRoadGenerator

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs:68-201`

This is the key integration point. The filter pipeline is inserted inside the two existing per-layer loops (Phase A and Phase B). Both phases already have a per-range loop structure — the filter wraps the call to `GenerateForLayerRange` with an additional sub-range loop.

- [ ] **Step 1: Add filter pipeline helper method**

Add a new private static method to `DecalRoadGenerator.cs` that computes filtered sub-ranges for a layer within a given range. Place it after `GenerateForLayerRange`:

```csharp
/// <summary>
/// Computes constraint-filtered sub-ranges for a layer within [rangeStart, rangeEnd].
/// Applies curve filter (if CurveOnly), then randomizer (if Randomize).
/// Returns the original range as-is when neither constraint is active.
/// </summary>
private static List<(int Start, int End)> ComputeFilteredRanges(
    DecalRoadLayerDefinition layer,
    IReadOnlyList<UnifiedCrossSection> sampledSections,
    IReadOnlyList<float> csDistances,
    int rangeStart,
    int rangeEnd,
    DecalRoadSettings settings,
    int splineId)
{
    var eligibleRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };

    if (layer.CurveOnly)
    {
        eligibleRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sampledSections, csDistances, layer.CurveMinCurvature,
            layer.CurveTransitionLength, rangeStart, rangeEnd);
    }

    if (layer.Randomize && eligibleRanges.Count > 0)
    {
        int splineSeed = settings.RandomSeed ^ splineId;
        eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
            eligibleRanges, csDistances,
            layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
            layer.RandomMinGapLength, layer.RandomMaxGapLength,
            splineSeed);
    }

    return eligibleRanges;
}
```

- [ ] **Step 2: Update GenerateForSpline to pass `settings` parameter**

The `GenerateForSpline` method currently does not receive a `DecalRoadSettings` parameter. Add it to the signature.

In `DecalRoadGenerator.cs`, change the `GenerateForSpline` signature (line 68) to add `DecalRoadSettings settings` parameter:

```csharp
internal static List<GeneratedDecalRoad> GenerateForSpline(
    ParameterizedRoadSpline spline,
    DecalRoadLayerSet layerSet,
    IReadOnlyList<UnifiedCrossSection> crossSections,
    IReadOnlyDictionary<int, RoadCorridor> corridors,
    IReadOnlyList<JunctionInfluenceZone> junctionZones,
    IReadOnlyDictionary<int, HashSet<int>>? continuityLookup,
    float[,] heightMap,
    float metersPerPixel,
    int terrainSizePixels,
    float terrainBaseHeight,
    float nodeSpacingMeters,
    DecalRoadSettings settings)
```

Update the call site in `Generate()` (line 57-61) to pass `settings`:

```csharp
var splineResults = GenerateForSpline(
    spline, layerSet, crossSections,
    corridors, junctionZones, continuityLookup,
    heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
    settings.NodeSpacingMeters, settings);
```

- [ ] **Step 3: Modify Phase A loop to apply filters**

In the Phase A `foreach` loop (line 125), for the **lane-independent** branch (the `else` at line 155), wrap the existing `GenerateForLayerRange` call with the filter pipeline. Replace the lane-independent else block:

```csharp
else
{
    // Lane-independent or no lane changes: apply constraint filters then generate
    var filteredRanges = ComputeFilteredRanges(
        layer, sampledSections, csDistances,
        0, sampledSections.Count - 1, settings, spline.SplineId);

    foreach (var (subStart, subEnd) in filteredRanges)
    {
        var subSections = sampledSections.GetRange(subStart, subEnd - subStart + 1);
        GenerateForLayerRange(
            layer, position, side, laneIndex, isFlipped,
            subSections, baseLaneInfo, laneCount,
            spline, roadWidth, splineName,
            corridors, junctionZones, continuityLookup,
            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
            ref chunkIndex, results);
    }
}
```

For the **lane-dependent split** branch (line 134), similarly wrap each range:

```csharp
for (int r = 0; r < rangeStarts!.Count; r++)
{
    var rangeStart = rangeStarts[r];
    var rangeEnd = rangeEnds![r];
    if (rangeEnd - rangeStart < 2) continue;

    var filteredRanges = ComputeFilteredRanges(
        layer, sampledSections, csDistances,
        rangeStart, rangeEnd - 1, settings, spline.SplineId);

    foreach (var (subStart, subEnd) in filteredRanges)
    {
        var subSections = sampledSections.GetRange(subStart, subEnd - subStart + 1);
        var rangeDist = csDistances[subStart];
        var segInfo = ResolveLaneInfo(spline.LaneSegments!, rangeDist);
        var segLaneCount = segInfo.TotalLanes;

        GenerateForLayerRange(
            layer, position, side, laneIndex, isFlipped,
            subSections, segInfo, segLaneCount,
            spline, roadWidth, splineName,
            corridors, junctionZones, continuityLookup,
            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
            ref chunkIndex, results);
    }
}
```

- [ ] **Step 4: Modify Phase B loop to apply filters**

In the Phase B loop (line 174), wrap the inner `GenerateForLayerRange` call similarly. For each `(rangeStart, rangeEnd)` within Phase B's range loop:

```csharp
for (int r = 0; r < rangeStarts!.Count; r++)
{
    var rangeStart = rangeStarts[r];
    var rangeEnd = rangeEnds![r];
    if (rangeEnd - rangeStart < 2) continue;

    var rangeSections = sampledSections.GetRange(rangeStart, rangeEnd - rangeStart);
    var rangeDist = csDistances[rangeStart];
    var segInfo = ResolveLaneInfo(spline.LaneSegments!, rangeDist);
    var segLaneCount = segInfo.TotalLanes;

    // Re-expand with segment-specific lane count and lane info
    var segExpanded = ExpandLayersWithLaneInfo(laneAwareLayers, segLaneCount, segInfo);
    foreach (var (layer, position, side, laneIndex, isFlipped) in segExpanded)
    {
        var filteredRanges = ComputeFilteredRanges(
            layer, sampledSections, csDistances,
            rangeStart, rangeEnd - 1, settings, spline.SplineId);

        foreach (var (subStart, subEnd) in filteredRanges)
        {
            var subSections = sampledSections.GetRange(subStart, subEnd - subStart + 1);
            var subDist = csDistances[subStart];
            var subSegInfo = ResolveLaneInfo(spline.LaneSegments!, subDist);
            var subLaneCount = subSegInfo.TotalLanes;

            GenerateForLayerRange(
                layer, position, side, laneIndex, isFlipped,
                subSections, subSegInfo, subLaneCount,
                spline, roadWidth, splineName,
                corridors, junctionZones, continuityLookup,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                ref chunkIndex, results);
        }
    }
}
```

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj && dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Both build succeeded.

- [ ] **Step 6: Run all existing tests to verify no regressions**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests pass (existing + new filter tests).

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: integrate constraint filter pipeline into DecalRoad generation"
```

---

## Task 6: Add UI Controls for Layer Constraints

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor:226-248` (after the checkboxes row)
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor:767-786` (DecalRoad settings grid)

- [ ] **Step 1: Add Curve Constraint and Randomizer sections to layer editor**

In `DecalRoadLayerSetEditor.razor`, after the existing "Row 4: Checkboxes" `MudItem` (the `</MudItem>` after the `InterruptAtJunctions` checkbox, around line 248), insert two new collapsible constraint sections. Add before `@* Row 5: Fades *@`:

```razor
@* Row 4b: Curve Constraint *@
<MudItem xs="12">
    <MudCheckBox T="bool" @bind-Value="layer.CurveOnly"
                 Label="Curve Only" Color="Color.Warning"
                 Dense="true" Disabled="@ReadOnly" />
</MudItem>
@if (layer.CurveOnly)
{
    <MudItem xs="12" sm="4">
        <MudNumericField T="float" @bind-Value="layer.CurveMinCurvature"
                         Label="Min Curvature (1/m)"
                         Variant="Variant.Outlined"
                         Min="0.001f" Max="1.0f" Step="0.001f"
                         Format="F3"
                         HelperText="@($"= {(layer.CurveMinCurvature > 0 ? (1.0f / layer.CurveMinCurvature).ToString("F0") : "∞")}m radius")"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="12" sm="4">
        <MudNumericField T="float" @bind-Value="layer.CurveTransitionLength"
                         Label="Transition Length (m)"
                         Variant="Variant.Outlined"
                         Min="0.0f" Max="200.0f" Step="5.0f"
                         HelperText="Lead-in/lead-out before/after curve"
                         Disabled="@ReadOnly" />
    </MudItem>
}

@* Row 4c: Randomizer Constraint *@
<MudItem xs="12">
    <MudCheckBox T="bool" @bind-Value="layer.Randomize"
                 Label="Randomize" Color="Color.Warning"
                 Dense="true" Disabled="@ReadOnly" />
</MudItem>
@if (layer.Randomize)
{
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="layer.RandomMinPatchLength"
                         Label="Min Patch (m)"
                         Variant="Variant.Outlined"
                         Min="1.0f" Max="500.0f" Step="5.0f"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="layer.RandomMaxPatchLength"
                         Label="Max Patch (m)"
                         Variant="Variant.Outlined"
                         Min="1.0f" Max="500.0f" Step="5.0f"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="layer.RandomMinGapLength"
                         Label="Min Gap (m)"
                         Variant="Variant.Outlined"
                         Min="1.0f" Max="500.0f" Step="5.0f"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="layer.RandomMaxGapLength"
                         Label="Max Gap (m)"
                         Variant="Variant.Outlined"
                         Min="1.0f" Max="500.0f" Step="5.0f"
                         Disabled="@ReadOnly" />
    </MudItem>
    @if (layer.RandomMaxPatchLength < layer.RandomMinPatchLength ||
         layer.RandomMaxGapLength < layer.RandomMinGapLength)
    {
        <MudItem xs="12">
            <MudAlert Severity="Severity.Warning" Dense="true">
                Max value is less than Min value — will be clamped during generation.
            </MudAlert>
        </MudItem>
    }
}
```

- [ ] **Step 2: Add collapsed-header chips for constraint indicators**

In the collapsed header row (around line 96-109), after the existing `perLn` chip and before the closing `</div>`, add constraint indicator chips:

```razor
@if (layer.CurveOnly)
{
    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
             Color="Color.Warning">curve</MudChip>
}
@if (layer.Randomize)
{
    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
             Color="Color.Warning">rnd</MudChip>
}
```

- [ ] **Step 3: Add RandomSeed field to GenerateTerrain.razor**

In `GenerateTerrain.razor`, within the DecalRoad settings `MudGrid` (around line 767-786), add a third `MudItem` for RandomSeed after Junction Margin:

```razor
<MudItem xs="12" sm="4">
    <MudNumericField T="int"
                     Value="@GetDecalRoadRandomSeed()"
                     ValueChanged="SetDecalRoadRandomSeed"
                     Label="Random Seed"
                     Variant="Variant.Outlined"
                     Step="1"
                     HelperText="Seed for randomizer constraint" />
</MudItem>
```

- [ ] **Step 4: Add getter/setter methods in GenerateTerrain.razor.cs**

In the code-behind for `GenerateTerrain.razor` (or `@code` section), add the getter/setter methods following the existing `GetDecalRoadNodeSpacing`/`SetDecalRoadNodeSpacing` pattern. Search for those methods to find the right location:

```csharp
private int GetDecalRoadRandomSeed()
{
    EnsureDecalRoadSettings();
    return _state.DecalRoadSettings!.RandomSeed;
}

private void SetDecalRoadRandomSeed(int value)
{
    EnsureDecalRoadSettings();
    _state.DecalRoadSettings!.RandomSeed = value;
}
```

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor
git commit -m "feat: add curve constraint and randomizer UI controls to layer editor"
```

---

## Task 7: Final Verification

- [ ] **Step 1: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests pass.

- [ ] **Step 2: Build full solution**

Run: `dotnet build`
Expected: Build succeeded with no errors.

- [ ] **Step 3: Verify preset backward compatibility**

Confirm that old presets without constraint properties still deserialize correctly: all new properties default to `CurveOnly = false`, `Randomize = false`, `RandomSeed = 42`. This is guaranteed by the C# property default values and `System.Text.Json`'s behavior of skipping missing properties. No code change needed — just a verification checkpoint.

- [ ] **Step 4: Commit any remaining changes**

```bash
git add -A
git commit -m "chore: final verification of DecalRoad layer constraints feature"
```
