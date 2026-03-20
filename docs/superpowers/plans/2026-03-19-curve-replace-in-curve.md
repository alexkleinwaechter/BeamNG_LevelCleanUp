# Curve Constraint Enhancement: Replace in Curve — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the boolean `CurveOnly` on `DecalRoadLayerDefinition` with a `CurveConstraintMode` enum supporting three modes: None, CurveOnly, and ReplaceInCurve (swap material+width in curves).

**Architecture:** New enum + replacement properties on the model. `ComputeFilteredRanges` returns `List<GenerationSegment>` (tagged with material/width/textureLength) instead of `List<(int,int)>`. New `InvertRanges` method produces straight ranges from curve ranges. Three call sites in `GenerateForSpline` updated to consume per-segment overrides. UI restructured with parent checkbox → radio buttons → nested fields.

**Tech Stack:** .NET 9, C#, Blazor WebView, MudBlazor v8, xUnit

**Spec:** `docs/superpowers/specs/2026-03-19-curve-replace-in-curve-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/CurveConstraintMode.cs` | Enum: None, CurveOnly, ReplaceInCurve |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Remove `CurveOnly` bool, add `CurveConstraint` enum + 3 replacement properties |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs` | Add `InvertRanges` static method |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | New `GenerationSegment` record, `ComputeFilteredRanges` returns `List<GenerationSegment>`, callers pass per-segment material/width/textureLength to `GenerateForLayerRange` |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | Mechanical rename: `CurveOnly = true` → `CurveConstraint = CurveConstraintMode.CurveOnly` |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Restructure curve constraints section; update chip display |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Update `DeepCopyLayer` with new properties |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs` | Update `CurveOnly` refs; add InvertRanges + ReplaceInCurve tests |

---

## Chunk 1: Model + Enum + InvertRanges (Backend Foundation)

### Task 1: Create CurveConstraintMode enum

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/CurveConstraintMode.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Controls how curve detection affects layer generation.
/// </summary>
public enum CurveConstraintMode
{
    /// <summary>No curve constraint — layer generated everywhere.</summary>
    None,

    /// <summary>Layer generated only in curve sections (existing behavior).</summary>
    CurveOnly,

    /// <summary>Main material on straights, replacement material in curves.</summary>
    ReplaceInCurve
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

---

### Task 2: Replace CurveOnly bool with enum + replacement properties on DecalRoadLayerDefinition

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs:56-76`

- [ ] **Step 1: Replace the CurveOnly property and update section header**

In `DecalRoadLayerDefinition.cs`, replace the entire CURVE-ONLY CONSTRAINT section (lines 55-76):

```csharp
    // ========================================
    // CURVE CONSTRAINT
    // ========================================

    /// <summary>
    /// Curve constraint mode. None = no constraint, CurveOnly = curves only,
    /// ReplaceInCurve = swap material+width in curves.
    /// </summary>
    public CurveConstraintMode CurveConstraint { get; set; } = CurveConstraintMode.None;

    /// <summary>
    /// Material to use in curve sections when CurveConstraint == ReplaceInCurve.
    /// Empty = fall back to main material (degrades to None).
    /// </summary>
    public string CurveReplacementMaterial { get; set; } = string.Empty;

    /// <summary>
    /// Width to use in curve sections when CurveConstraint == ReplaceInCurve.
    /// 0 = use same width as main layer.
    /// </summary>
    public float CurveReplacementWidth { get; set; }

    /// <summary>
    /// Texture length for replacement material when CurveConstraint == ReplaceInCurve.
    /// 0 = use same texture length as main layer.
    /// </summary>
    public float CurveReplacementTextureLength { get; set; }

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
```

- [ ] **Step 2: Build — expect errors in files still referencing CurveOnly**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build errors in `DecalRoadGenerator.cs` (line 383) — this is expected, fixed in Task 5.

---

### Task 3: Add InvertRanges to DecalRoadLayerFilter — tests first

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs`

- [ ] **Step 1: Add InvertRanges test class**

Append to end of `DecalRoadLayerFilterTests.cs` (before the file's closing — it has no namespace braces, each class is top-level):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilterInvertRangesTests" -v n`
Expected: FAIL — `InvertRanges` method not found

- [ ] **Step 3: Implement InvertRanges**

Add to `DecalRoadLayerFilter.cs`, after `ApplyRandomizer` method (before `MergeRanges`):

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "DecalRoadLayerFilterInvertRangesTests" -v n`
Expected: All 8 tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/CurveConstraintMode.cs
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs
git commit -m "feat: add CurveConstraintMode enum, replacement properties, and InvertRanges filter"
```

---

## Chunk 2: Backend — GenerationSegment + ComputeFilteredRanges + GenerateForSpline

### Task 4: Add GenerationSegment record and update ComputeFilteredRanges

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

- [ ] **Step 1: Add GenerationSegment record**

Add inside `DecalRoadGenerator` class, before the `Generate` method (after line 15):

```csharp
    /// <summary>
    /// A filtered sub-range tagged with material/width/textureLength overrides.
    /// For None/CurveOnly: uses the layer's own values.
    /// For ReplaceInCurve: straight segments use main values, curve segments use replacement values.
    /// </summary>
    internal record struct GenerationSegment(
        int Start,
        int End,
        string Material,
        float Width,
        float TextureLength
    );
```

- [ ] **Step 2: Rewrite ComputeFilteredRanges to return List\<GenerationSegment\>**

Replace the existing `ComputeFilteredRanges` method (lines 371-401) with:

```csharp
    /// <summary>
    /// Computes constraint-filtered sub-ranges for a layer within [rangeStart, rangeEnd].
    /// Returns GenerationSegments tagged with the appropriate material/width/textureLength.
    /// For ReplaceInCurve: returns interleaved straight (main) + curve (replacement) segments.
    /// Randomizer applies only to straight segments when in ReplaceInCurve mode.
    /// </summary>
    internal static List<GenerationSegment> ComputeFilteredRanges(
        DecalRoadLayerDefinition layer,
        IReadOnlyList<UnifiedCrossSection> sampledSections,
        IReadOnlyList<float> csDistances,
        int rangeStart,
        int rangeEnd,
        DecalRoadSettings settings,
        int splineId)
    {
        // Helper to wrap (int,int) ranges into GenerationSegments with given overrides
        static List<GenerationSegment> Wrap(
            List<(int Start, int End)> ranges, string material, float width, float textureLength)
        {
            return ranges.Select(r => new GenerationSegment(r.Start, r.End, material, width, textureLength)).ToList();
        }

        var mainMaterial = layer.Material;
        var mainWidth = layer.Width;
        var mainTextureLength = layer.TextureLength;

        if (layer.CurveConstraint == CurveConstraintMode.None)
        {
            var eligibleRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };
            if (layer.Randomize && eligibleRanges.Count > 0)
            {
                int splineSeed = settings.RandomSeed ^ splineId;
                eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    eligibleRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }
            return Wrap(eligibleRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        // Both CurveOnly and ReplaceInCurve need curve ranges
        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sampledSections, csDistances, layer.CurveMinCurvature,
            layer.CurveTransitionLength, rangeStart, rangeEnd);

        if (layer.CurveConstraint == CurveConstraintMode.CurveOnly)
        {
            var eligibleRanges = curveRanges;
            if (layer.Randomize && eligibleRanges.Count > 0)
            {
                int splineSeed = settings.RandomSeed ^ splineId;
                eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    eligibleRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }
            return Wrap(eligibleRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        // ReplaceInCurve mode
        // Validate replacement material — fall back to None behavior if empty
        if (string.IsNullOrEmpty(layer.CurveReplacementMaterial))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DecalRoad] ReplaceInCurve layer '{layer.Name}' has empty CurveReplacementMaterial — falling back to main material");

            var fallbackRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };
            if (layer.Randomize)
            {
                int splineSeed = settings.RandomSeed ^ splineId;
                fallbackRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    fallbackRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }
            return Wrap(fallbackRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        var replacementMaterial = layer.CurveReplacementMaterial;
        var replacementWidth = layer.CurveReplacementWidth > 0 ? layer.CurveReplacementWidth : mainWidth;
        var replacementTextureLength = layer.CurveReplacementTextureLength > 0 ? layer.CurveReplacementTextureLength : mainTextureLength;

        // Curve segments: replacement values, never randomized
        var curveSegments = Wrap(curveRanges, replacementMaterial, replacementWidth, replacementTextureLength);

        // Straight segments: main values, randomizer applies here
        var straightRanges = DecalRoadLayerFilter.InvertRanges(curveRanges, rangeStart, rangeEnd);
        if (layer.Randomize && straightRanges.Count > 0)
        {
            int splineSeed = settings.RandomSeed ^ splineId;
            straightRanges = DecalRoadLayerFilter.ApplyRandomizer(
                straightRanges, csDistances,
                layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                layer.RandomMinGapLength, layer.RandomMaxGapLength,
                splineSeed);
        }
        var straightSegments = Wrap(straightRanges, mainMaterial, mainWidth, mainTextureLength);

        // Merge and sort by Start
        var allSegments = new List<GenerationSegment>(curveSegments.Count + straightSegments.Count);
        allSegments.AddRange(curveSegments);
        allSegments.AddRange(straightSegments);
        allSegments.Sort((a, b) => a.Start.CompareTo(b.Start));

        return allSegments;
    }
```

- [ ] **Step 3: Update all three call sites in GenerateForSpline**

The three places that iterate `ComputeFilteredRanges` results need updating from `foreach (var (subStart, subEnd) in filteredRanges)` to `foreach (var seg in filteredRanges)`, and `GenerateForLayerRange` calls need material/width/textureLength override parameters.

**Call site 1 — Phase A, lane-dependent with lane changes (lines 143-161):**

Replace:
```csharp
                    foreach (var (subStart, subEnd) in filteredRanges)
                    {
                        var subSections = sampledSections.GetRange(subStart, subEnd - subStart + 1);
                        var subDist = csDistances[subStart];
                        var segInfo = ResolveLaneInfo(spline.LaneSegments!, subDist);
                        var segLaneCount = segInfo.TotalLanes;

                        GenerateForLayerRange(
                            layer, position, side, laneIndex, isFlipped,
                            subSections, segInfo, segLaneCount,
                            spline, roadWidth, splineName,
                            corridors, junctionZones, continuityLookup,
                            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                            ref chunkIndex, results);
                    }
```

With:
```csharp
                    foreach (var seg in filteredRanges)
                    {
                        var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);
                        var subDist = csDistances[seg.Start];
                        var segInfo = ResolveLaneInfo(spline.LaneSegments!, subDist);
                        var segLaneCount = segInfo.TotalLanes;

                        GenerateForLayerRange(
                            layer, position, side, laneIndex, isFlipped,
                            subSections, segInfo, segLaneCount,
                            spline, roadWidth, splineName,
                            corridors, junctionZones, continuityLookup,
                            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                            ref chunkIndex, results,
                            seg.Material, seg.Width, seg.TextureLength);
                    }
```

**Call site 2 — Phase A, lane-independent (lines 171-180):**

Replace:
```csharp
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
```

With:
```csharp
                foreach (var seg in filteredRanges)
                {
                    var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);
                    GenerateForLayerRange(
                        layer, position, side, laneIndex, isFlipped,
                        subSections, baseLaneInfo, laneCount,
                        spline, roadWidth, splineName,
                        corridors, junctionZones, continuityLookup,
                        heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                        ref chunkIndex, results,
                        seg.Material, seg.Width, seg.TextureLength);
                }
```

**Call site 3 — Phase B, per-lane + DirectionDivider (lines 211-224):**

Replace:
```csharp
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
```

With:
```csharp
                    foreach (var seg in filteredRanges)
                    {
                        var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);
                        var subDist = csDistances[seg.Start];
                        var subSegInfo = ResolveLaneInfo(spline.LaneSegments!, subDist);
                        var subLaneCount = subSegInfo.TotalLanes;

                        GenerateForLayerRange(
                            layer, position, side, laneIndex, isFlipped,
                            subSections, subSegInfo, subLaneCount,
                            spline, roadWidth, splineName,
                            corridors, junctionZones, continuityLookup,
                            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                            ref chunkIndex, results,
                            seg.Material, seg.Width, seg.TextureLength);
                    }
```

- [ ] **Step 4: Add override parameters to GenerateForLayerRange**

Update the `GenerateForLayerRange` signature (line 240) — add three optional parameters at the end:

```csharp
    private static void GenerateForLayerRange(
        DecalRoadLayerDefinition layer, float position, string side,
        int laneIndex, bool isFlipped,
        IReadOnlyList<UnifiedCrossSection> sections,
        OsmLaneInfo? segInfo, int segLaneCount,
        ParameterizedRoadSpline spline, float roadWidth, string splineName,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup,
        float[,] heightMap, float metersPerPixel, int terrainSizePixels,
        float terrainBaseHeight,
        ref int chunkIndex, List<GeneratedDecalRoad> results,
        string? overrideMaterial = null,
        float? overrideWidth = null,
        float? overrideTextureLength = null)
```

Then update the three usages inside `GenerateForLayerRange`:

**Width calculation (lines 253-259):** Replace:
```csharp
        float nodeWidth;
        if (layer.IsTrackWidth)
            nodeWidth = roadWidth;
        else if (layer.IsLaneWidth)
            nodeWidth = roadWidth / Math.Max(1, segLaneCount);
        else
            nodeWidth = layer.Width;
```

With:
```csharp
        // Note: IsTrackWidth and IsLaneWidth take precedence over overrideWidth.
        // These modes mean "fill the road/lane width" which is geometry-driven,
        // not material-driven. The override only applies to fixed-width layers.
        float baseWidth = overrideWidth ?? layer.Width;
        float nodeWidth;
        if (layer.IsTrackWidth)
            nodeWidth = roadWidth;
        else if (layer.IsLaneWidth)
            nodeWidth = roadWidth / Math.Max(1, segLaneCount);
        else
            nodeWidth = baseWidth;
```

**Material assignment (line 332):** Replace:
```csharp
                    Material = layer.Material,
```

With:
```csharp
                    Material = overrideMaterial ?? layer.Material,
```

**TextureLength assignment (line 333):** Replace:
```csharp
                    TextureLength = layer.TextureLength,
```

With:
```csharp
                    TextureLength = overrideTextureLength ?? layer.TextureLength,
```

- [ ] **Step 5: Update the ComputeFilteredRanges doc comment**

Replace the existing XML doc above `ComputeFilteredRanges` (line 367-370):
```csharp
    /// <summary>
    /// Computes constraint-filtered sub-ranges for a layer within [rangeStart, rangeEnd].
    /// Applies curve filter (if CurveOnly), then randomizer (if Randomize).
    /// Returns the original range as-is when neither constraint is active.
    /// </summary>
```

(Already done in step 2 — the new method has the updated doc.)

- [ ] **Step 6: Build the library**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded (the main app project may still fail until defaults are updated)

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: GenerationSegment record + ComputeFilteredRanges returns tagged segments for ReplaceInCurve"
```

---

## Chunk 3: Update Default Layer Sets + Fix Remaining References

### Task 5: Mechanical rename CurveOnly → CurveConstraint in DecalRoadDefaultLayerSets

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`

- [ ] **Step 1: Replace all CurveOnly = true with CurveConstraint = CurveConstraintMode.CurveOnly**

Three occurrences in `CreateAsphaltRoadSet`:

**HeavyTreadMarks (line 77):** Replace:
```csharp
                     CurveOnly = true, CurveMinCurvature = 0.05f, CurveTransitionLength = 15.0f },
```
With:
```csharp
                     CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.05f, CurveTransitionLength = 15.0f },
```

**Wear2 (line 88):** Replace:
```csharp
                     CurveOnly = true, CurveMinCurvature = 0.05f, CurveTransitionLength = 20.0f },
```
With:
```csharp
                     CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.05f, CurveTransitionLength = 20.0f },
```

**Skidmarks (line 94):** Replace:
```csharp
                     CurveOnly = true, CurveMinCurvature = 0.07f, CurveTransitionLength = 15.0f },
```
With:
```csharp
                     CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.07f, CurveTransitionLength = 15.0f },
```

- [ ] **Step 2: Add using directive if needed**

Add at top of file if not present:
```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
```

(Check — the existing `using` already imports the namespace since `DecalRoadLayerSet` is in it. The enum is in the same namespace, so no new using needed.)

- [ ] **Step 3: Build full solution**

Run: `dotnet build`
Expected: The library projects build. The main app project (`BeamNG_LevelCleanUp`) will have errors in `DecalRoadLayerSetEditor.razor` and `.razor.cs` — those are fixed in Chunk 4.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
git commit -m "feat: rename CurveOnly to CurveConstraint enum in default layer sets"
```

---

## Chunk 4: Update Tests

### Task 6: Update existing test references and add ReplaceInCurve tests

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`

- [ ] **Step 1: Verify existing tests still pass**

The existing tests in `DecalRoadLayerFilterTests.cs` test `ApplyCurveFilter` and `ApplyRandomizer` directly — they don't reference `CurveOnly` at all (no model objects in these tests). They should still pass.

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All existing tests PASS

- [ ] **Step 2: Add ReplaceInCurve integration tests**

Add a new test class at end of `DecalRoadLayerFilterTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS (including new InvertRanges and ReplaceInCurve tests)

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs
git commit -m "test: add InvertRanges and ReplaceInCurve integration tests"
```

---

### Task 6b: Add ComputeFilteredRanges unit tests for ReplaceInCurve segments

**Files:**
- Modify: `BeamNgTerrainPoc/BeamNgTerrainPoc.csproj` (add InternalsVisibleTo)
- Create or Modify: `BeamNgTerrainPoc.Tests/DecalRoad/ComputeFilteredRangesTests.cs`

- [ ] **Step 1: Add InternalsVisibleTo to BeamNgTerrainPoc.csproj**

Add inside the `<PropertyGroup>` block:
```xml
    <InternalsVisibleTo Include="BeamNgTerrainPoc.Tests" />
```

- [ ] **Step 2: Create ComputeFilteredRangesTests.cs**

Create `BeamNgTerrainPoc.Tests/DecalRoad/ComputeFilteredRangesTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class ComputeFilteredRangesTests
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

    private static DecalRoadSettings DefaultSettings => new() { RandomSeed = 42 };

    [Fact]
    public void ReplaceInCurve_StraightSegments_UseMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveReplacementTextureLength = 5f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f // no transition for simpler test
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var straightSegs = result.Where(s => s.Material == "main_mat").ToList();
        Assert.NotEmpty(straightSegs);
        Assert.All(straightSegs, s =>
        {
            Assert.Equal(0.25f, s.Width);
            Assert.Equal(10f, s.TextureLength);
        });
    }

    [Fact]
    public void ReplaceInCurve_CurveSegments_UseReplacementMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveReplacementTextureLength = 5f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        Assert.All(curveSegs, s =>
        {
            Assert.Equal(0.15f, s.Width);
            Assert.Equal(5f, s.TextureLength);
        });
    }

    [Fact]
    public void ReplaceInCurve_ZeroReplacementWidth_FallsBackToMainWidth()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0f, // should fall back to 0.25
            CurveReplacementTextureLength = 0f, // should fall back to 10
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        Assert.All(curveSegs, s =>
        {
            Assert.Equal(0.25f, s.Width); // fell back to main
            Assert.Equal(10f, s.TextureLength); // fell back to main
        });
    }

    [Fact]
    public void ReplaceInCurve_EmptyReplacementMaterial_FallsBackToMainEverywhere()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "", // empty — should degrade to None
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        // Should produce a single segment with main material covering full range
        Assert.All(result, s => Assert.Equal("main_mat", s.Material));
    }

    [Fact]
    public void ReplaceInCurve_Randomize_OnlyAffectsStraightSegments()
    {
        var (sections, distances) = CreateSectionsWithCurve(200, 80, 120);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f,
            Randomize = true,
            RandomMinPatchLength = 5f,
            RandomMaxPatchLength = 15f,
            RandomMinGapLength = 5f,
            RandomMaxGapLength = 15f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 199, DefaultSettings, splineId: 1);

        // Curve segments should be continuous (not randomized)
        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        // The curve zone should be one continuous segment (no gaps from randomizer)
        Assert.Single(curveSegs);

        // Straight segments may have gaps (randomizer applied)
        var straightSegs = result.Where(s => s.Material == "main_mat").ToList();
        // With randomizer, there should be multiple patches (not one continuous range)
        // The exact count depends on RNG, but with 200m and these params we expect multiple
        Assert.True(straightSegs.Count >= 1);
    }

    [Fact]
    public void CurveOnly_ReturnsOnlyCurveZones_WithMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.CurveOnly,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        // Should only cover the curve zone, using main material
        Assert.All(result, s =>
        {
            Assert.Equal("main_mat", s.Material);
            Assert.True(s.Start >= 40 && s.End <= 60,
                $"Segment ({s.Start},{s.End}) outside curve zone (40,60)");
        });
    }

    [Fact]
    public void None_ReturnsFullRange_WithMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.None
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(99, result[0].End);
        Assert.Equal("main_mat", result[0].Material);
    }
}
```

- [ ] **Step 3: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/BeamNgTerrainPoc.csproj
git add BeamNgTerrainPoc.Tests/DecalRoad/ComputeFilteredRangesTests.cs
git commit -m "test: add ComputeFilteredRanges unit tests for ReplaceInCurve segment tagging"
```

---

## Chunk 5: UI — Update DecalRoadLayerSetEditor

### Task 7: Update DeepCopyLayer in code-behind

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs:105-143`

- [ ] **Step 1: Update DeepCopyLayer to copy new properties and replace CurveOnly**

Replace `CurveOnly = source.CurveOnly,` (line 134) with:

```csharp
            CurveConstraint = source.CurveConstraint,
            CurveReplacementMaterial = source.CurveReplacementMaterial,
            CurveReplacementWidth = source.CurveReplacementWidth,
            CurveReplacementTextureLength = source.CurveReplacementTextureLength,
```

- [ ] **Step 2: Verify build of just the code-behind**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Errors in razor file (still references `CurveOnly`) — fixed in Task 8.

---

### Task 8: Restructure curve constraints UI in razor markup

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor`

- [ ] **Step 1: Update collapsed header chip (lines 109-113)**

Replace:
```razor
                @if (layer.CurveOnly)
                {
                    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
                             Color="Color.Warning">curve</MudChip>
                }
```

With:
```razor
                @if (layer.CurveConstraint == CurveConstraintMode.CurveOnly)
                {
                    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
                             Color="Color.Warning">curve</MudChip>
                }
                @if (layer.CurveConstraint == CurveConstraintMode.ReplaceInCurve)
                {
                    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
                             Color="Color.Warning">crv-repl</MudChip>
                }
```

- [ ] **Step 2: Replace the curve constraints section in expanded view (lines 386-423)**

Replace the section from the `<MudItem xs="12" sm="4">` containing the `CurveOnly` checkbox through the closing `}` of `@if (layer.CurveOnly)` (lines 387-423) with:

```razor
                    <MudItem xs="12">
                        <div class="d-flex align-center gap-1">
                            <MudCheckBox T="bool" Value="@(layer.CurveConstraint != CurveConstraintMode.None)"
                                         ValueChanged="@(v => { layer.CurveConstraint = v ? CurveConstraintMode.CurveOnly : CurveConstraintMode.None; })"
                                         Label="Curve Constraints" Color="Color.Warning"
                                         Dense="true" Disabled="@ReadOnly" />
                            <MudTooltip Text="Apply curve-based filtering to this layer. Controls how curves affect generation.">
                                <MudIcon Icon="@Icons.Material.Filled.HelpOutline" Size="Size.Small" Color="Color.Default" Style="opacity:0.6" />
                            </MudTooltip>
                        </div>
                    </MudItem>
                    @if (layer.CurveConstraint != CurveConstraintMode.None)
                    {
                        <MudItem xs="12">
                            <MudRadioGroup T="CurveConstraintMode" @bind-Value="layer.CurveConstraint">
                                <MudRadio T="CurveConstraintMode" Value="CurveConstraintMode.CurveOnly"
                                          Color="Color.Warning" Dense="true" Disabled="@ReadOnly">
                                    Curve Only
                                    <MudText Typo="Typo.caption" Color="Color.Secondary">
                                        Layer appears only in curves, hidden on straight sections
                                    </MudText>
                                </MudRadio>
                                <MudRadio T="CurveConstraintMode" Value="CurveConstraintMode.ReplaceInCurve"
                                          Color="Color.Warning" Dense="true" Disabled="@ReadOnly">
                                    Replace in Curve
                                </MudRadio>
                            </MudRadioGroup>
                        </MudItem>
                        @if (layer.CurveConstraint == CurveConstraintMode.ReplaceInCurve)
                        {
                            <MudItem xs="12" sm="4">
                                <MudTextField @bind-Value="layer.CurveReplacementMaterial"
                                              Label="Replacement Material"
                                              Variant="Variant.Outlined"
                                              Disabled="@ReadOnly" />
                            </MudItem>
                            <MudItem xs="6" sm="4">
                                <MudNumericField T="float" @bind-Value="layer.CurveReplacementWidth"
                                                 Label="Replacement Width (m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.0f" Step="0.05f"
                                                 HelperText="0 = same as main"
                                                 Disabled="@ReadOnly" />
                            </MudItem>
                            <MudItem xs="6" sm="4">
                                <MudNumericField T="float" @bind-Value="layer.CurveReplacementTextureLength"
                                                 Label="Replacement Tex Length (m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.0f" Step="1.0f"
                                                 HelperText="0 = same as main"
                                                 Disabled="@ReadOnly" />
                            </MudItem>
                        }
                        <MudItem xs="6" sm="4">
                            <div class="d-flex align-start gap-1">
                                <MudNumericField T="float" @bind-Value="layer.CurveMinCurvature"
                                                 Label="Min Curvature (1/m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.001f" Max="1.0f" Step="0.001f"
                                                 Format="F3"
                                                 HelperText="@($"= {(layer.CurveMinCurvature > 0 ? (1.0f / layer.CurveMinCurvature).ToString("F0") : "∞")}m radius")"
                                                 Disabled="@ReadOnly"
                                                 Class="flex-grow-1" />
                                <HelpAdornment TooltipText="Minimum curvature (1/radius) to trigger. 0.01 = 100m radius, 0.05 = 20m radius." />
                            </div>
                        </MudItem>
                        <MudItem xs="6" sm="4">
                            <div class="d-flex align-start gap-1">
                                <MudNumericField T="float" @bind-Value="layer.CurveTransitionLength"
                                                 Label="Transition Length (m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.0f" Max="200.0f" Step="5.0f"
                                                 Disabled="@ReadOnly"
                                                 Class="flex-grow-1" />
                                <HelpAdornment TooltipText="Extends the zone before and after the curve by this distance (meters). Creates a lead-in/lead-out." />
                            </div>
                        </MudItem>
                    }
```

- [ ] **Step 3: Build full solution**

Run: `dotnet build`
Expected: Build succeeded — all `CurveOnly` references are now replaced

- [ ] **Step 4: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs
git commit -m "feat: restructure curve constraints UI with CurveOnly/ReplaceInCurve radio buttons"
```

---

## Chunk 6: Final Verification

### Task 9: Full build + test + grep for stale references

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 3: Grep for any remaining CurveOnly references**

Run: `grep -r "CurveOnly" --include="*.cs" --include="*.razor" -l`

Expected: Only found in:
- `docs/` files (specs, old plans — not code)

If any `.cs` or `.razor` files still reference `CurveOnly`, fix them.

- [ ] **Step 4: Final commit if needed**

```bash
git add -A
git commit -m "fix: resolve any remaining CurveOnly references"
```

---

## Post-Implementation Notes

### Manual Testing Checklist

1. Open GenerateTerrain page, enable DecalRoads, click "Edit Default Layer Sets"
2. Expand HeavyTreadMarks layer — verify "Curve Constraints" checkbox is checked, "Curve Only" radio selected
3. Switch to "Replace in Curve" radio — verify replacement fields appear (material, width, tex length)
4. Enter replacement material name, set width to 0 — verify helper text "0 = same as main"
5. Switch back to "Curve Only" — verify replacement fields hidden
6. Uncheck "Curve Constraints" — verify all curve fields hidden, curvature params hidden
7. Check collapsed chip shows "curve" for CurveOnly, "crv-repl" for ReplaceInCurve, nothing for None
8. Save and generate terrain — verify CurveOnly layers still work correctly (regression)
9. Configure a layer with ReplaceInCurve + test material — generate and verify two DecalRoad sets per curve boundary

### What's NOT in this plan

- No JSON migration — active development, no saved presets to migrate
- No fade/overlap between segments — hard cuts at curve boundaries
- No material browser for replacement material — text field (consistent with existing UI)
