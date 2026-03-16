# DecalRoad Corridor Junction Suppression Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace circular exclusion zone junction interruption with per-node corridor overlap checking that uses actual road surface geometry for precise, side-aware DecalRoad suppression at junctions.

**Architecture:** Two-pass generation: Pass 1 builds a `RoadCorridor` per spline (sampled cross-sections + corridor half-width computed from actual layer positions). Pass 2 generates DecalRoad nodes as before, but checks each laterally-offset node's 2D position against other roads' corridors — nodes inside another road's corridor are suppressed. A junction proximity filter limits checks to near-junction areas for performance.

**Tech Stack:** .NET 9, C#, System.Numerics (Vector2), xUnit

**Spec:** `ai_docs/decalroad_corridor_junction_suppression_2026-03-15.md`

**Skills:** @beamng-decalroad-generation, @beamng-road-layers

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs` | Data model: corridor sections + half-width per spline |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Builds corridors from network + resolved layer sets |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs` | Per-node overlap check with junction proximity filter |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorBuilderTests.cs` | Unit tests for corridor construction and half-width calculation |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs` | Unit tests for overlap detection logic |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Two-pass architecture: build corridors in Pass 1, use corridor overlap check instead of rule-based interruption in Pass 2 |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | Set `InterruptAtJunctions = true` for all EdgeBlend layers |

### Deleted Files

| File | Reason |
|------|--------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterruptionRuleBuilder.cs` | Replaced by corridor-based approach |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionInterruptionRule.cs` | No longer needed (no rules, no InterruptionSide enum) |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterrupter.cs` | Replaced by `RoadCorridorOverlapChecker` |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs` | Tests for deleted code |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs` | Tests for deleted code |

---

## Chunk 1: RoadCorridor Data Model & Builder

### Task 1: Create RoadCorridor data model

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs`

- [ ] **Step 1: Create RoadCorridor and CorridorSection**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// A single sampled point along a road corridor, used for overlap checks.
/// </summary>
public readonly record struct CorridorSection(
    Vector2 Center,
    Vector2 Normal,
    float DistanceAlongSpline);

/// <summary>
/// Represents a road's surface corridor for overlap checking.
/// The corridor extends CorridorHalfWidth on each side of the centerline
/// along the entire length of the sampled sections.
/// CorridorHalfWidth is the maximum outer extent of any enabled DecalRoad layer,
/// computed as: max(|position| * 0.5 * roadWidth + nodeWidth / 2) + margin.
/// </summary>
public class RoadCorridor
{
    public required int SplineId { get; init; }
    public required float RoadWidth { get; init; }
    public required float CorridorHalfWidth { get; init; }
    public required List<CorridorSection> Sections { get; init; }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs
git commit -m "feat: add RoadCorridor data model for corridor overlap checking"
```

---

### Task 2: Create RoadCorridorBuilder with tests

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorBuilderTests.cs`

- [ ] **Step 1: Write failing tests for corridor half-width calculation**

The half-width formula per layer is: `|expandedPosition| * 0.5 * roadWidth + nodeWidth / 2`
The corridor half-width is `max(layerOuterExtent) + marginMeters` across all enabled layers.

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorBuilderTests.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class RoadCorridorBuilderTests
{
    [Fact]
    public void CalculateCorridorHalfWidth_MirroredEdgeBlend_UsesOuterExtent()
    {
        // EdgeBlend at position 1.25, width 2.0m, mirrored
        // roadWidth = 7.0m, margin = 1.0m
        // |1.25| * 0.5 * 7.0 + 2.0/2 + 1.0 = 4.375 + 1.0 + 1.0 = 6.375
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeBlend", Position = 1.25f, Width = 2.0f,
                     IsMirrored = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 1.0f);
        Assert.Equal(6.375f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_TrackWidthLayer_UsesFullRoadWidth()
    {
        // AIRoad: IsTrackWidth=true, position=0.0
        // nodeWidth = roadWidth = 8.0
        // |0.0| * 0.5 * 8.0 + 8.0/2 = 0 + 4.0 = 4.0 (+ margin 0)
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "AIRoad", Position = 0.0f, IsTrackWidth = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(4.0f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_LaneWidthTreadMarks_ExtendToRoadEdge()
    {
        // TreadMarks: IsLaneWidth=true, 2 lanes
        // Lane centers at -0.5, +0.5 (from CalculateLaneCenterPositions)
        // nodeWidth = 8.0 / 2 = 4.0
        // Outermost: |0.5| * 0.5 * 8.0 + 4.0/2 = 2.0 + 2.0 = 4.0 (= roadWidth/2)
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "TreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     IsLaneWidth = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(4.0f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_PerLaneBoundary_UsesOutermostBoundary()
    {
        // LaneMarking: IsPerLane=true, 4 lanes, width 0.2m
        // Boundaries at -0.5, 0.0, +0.5
        // Outermost: |0.5| * 0.5 * 8.0 + 0.2/2 = 2.0 + 0.1 = 2.1
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "LaneMarking", IsPerLane = true, Width = 0.2f, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 8.0f, laneCount: 4, marginMeters: 0f);
        Assert.Equal(2.1f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_MultipleLayers_TakesMax()
    {
        // EdgeLine position=1.0, width=0.25 → |1.0|*0.5*7 + 0.25/2 = 3.625
        // EdgeBlend position=1.1, width=1.0 → |1.1|*0.5*7 + 1.0/2 = 4.35
        // Max is 4.35, + margin 1.0 = 5.35
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", Position = 1.0f, Width = 0.25f,
                     IsMirrored = true, IsEnabled = true },
            new() { Name = "EdgeBlend", Position = 1.1f, Width = 1.0f,
                     IsMirrored = true, IsEnabled = true }
        };
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 1.0f);
        Assert.Equal(5.35f, result, precision: 3);
    }

    [Fact]
    public void CalculateCorridorHalfWidth_DisabledLayers_AreSkipped()
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "Big", Position = 2.0f, Width = 5.0f,
                     IsMirrored = true, IsEnabled = false },
            new() { Name = "Small", Position = 1.0f, Width = 0.25f,
                     IsMirrored = true, IsEnabled = true }
        };
        // Only "Small": |1.0|*0.5*7 + 0.25/2 = 3.625
        var result = RoadCorridorBuilder.CalculateCorridorHalfWidth(
            layers, roadWidth: 7.0f, laneCount: 2, marginMeters: 0f);
        Assert.Equal(3.625f, result, precision: 3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorBuilder" -v n`
Expected: FAIL (class doesn't exist)

- [ ] **Step 3: Implement RoadCorridorBuilder**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Builds RoadCorridor objects from the unified road network.
/// Each corridor contains sampled cross-sections and a corridor half-width
/// computed from the road's resolved DecalRoad layer set.
/// </summary>
public static class RoadCorridorBuilder
{
    /// <summary>
    /// Builds corridors for all eligible splines in the network.
    /// Must be called before DecalRoad generation (Pass 1 of two-pass architecture).
    /// </summary>
    public static Dictionary<int, RoadCorridor> BuildCorridors(
        UnifiedRoadNetwork network,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults,
        float nodeSpacingMeters)
    {
        var corridors = new Dictionary<int, RoadCorridor>();

        foreach (var spline in network.Splines)
        {
            if (spline.IsBridge || spline.IsTunnel)
                continue;

            var layerSet = DecalRoadLayerSetResolver.Resolve(
                spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
            var laneCount = GetLaneCount(spline, layerSet);

            var corridorHalfWidth = CalculateCorridorHalfWidth(
                layerSet.Layers, roadWidth, laneCount,
                settings.JunctionExclusionMarginMeters);

            var sampledSections = DecalRoadGenerator.SubSampleCrossSections(
                crossSections, nodeSpacingMeters);

            var sections = sampledSections.Select(cs => new CorridorSection(
                cs.CenterPoint, cs.NormalDirection, cs.DistanceAlongSpline)).ToList();

            corridors[spline.SplineId] = new RoadCorridor
            {
                SplineId = spline.SplineId,
                RoadWidth = roadWidth,
                CorridorHalfWidth = corridorHalfWidth,
                Sections = sections
            };
        }

        return corridors;
    }

    /// <summary>
    /// Calculates the corridor half-width as the maximum outer extent of any enabled layer.
    /// Formula per layer: |expandedPosition| * 0.5 * roadWidth + nodeWidth / 2
    /// The margin is added on top for configurable tolerance.
    /// </summary>
    public static float CalculateCorridorHalfWidth(
        IReadOnlyList<DecalRoadLayerDefinition> layers,
        float roadWidth,
        int laneCount,
        float marginMeters)
    {
        float maxExtent = 0f;

        foreach (var layer in layers)
        {
            if (!layer.IsEnabled) continue;

            // Determine the outermost |expandedPosition| for this layer
            float maxAbsPosition;
            if (layer.LayerType == DecalRoadLayerType.TreadMarks)
            {
                var centers = DecalRoadGenerator.CalculateLaneCenterPositions(laneCount);
                maxAbsPosition = centers.Length > 0
                    ? centers.Max(c => MathF.Abs(c))
                    : 0f;
            }
            else if (layer.IsPerLane)
            {
                var boundaries = DecalRoadGenerator.CalculateLaneBoundaryPositions(laneCount);
                maxAbsPosition = boundaries.Length > 0
                    ? boundaries.Max(b => MathF.Abs(b))
                    : 0f;
            }
            else // Mirrored or single placement
            {
                maxAbsPosition = MathF.Abs(layer.Position);
            }

            // Resolve node width (same logic as DecalRoadGenerator)
            float nodeWidth;
            if (layer.IsTrackWidth)
                nodeWidth = roadWidth;
            else if (layer.IsLaneWidth)
                nodeWidth = roadWidth / MathF.Max(1, laneCount);
            else
                nodeWidth = layer.Width;

            var extent = maxAbsPosition * 0.5f * roadWidth + nodeWidth / 2f;
            maxExtent = MathF.Max(maxExtent, extent);
        }

        return maxExtent + marginMeters;
    }

    private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
    {
        if (spline.OsmTags != null &&
            spline.OsmTags.TryGetValue("lanes", out var lanesStr) &&
            int.TryParse(lanesStr, out var lanes) && lanes > 0)
            return lanes;
        return layerSet.DefaultLaneCount;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorBuilder" -v n`
Expected: All 6 tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorBuilderTests.cs
git commit -m "feat: add RoadCorridorBuilder with corridor half-width calculation"
```

---

## Chunk 2: RoadCorridorOverlapChecker

### Task 3: Create RoadCorridorOverlapChecker with tests

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs`

- [ ] **Step 1: Write failing tests for overlap detection**

Tests cover: point inside corridor, point outside corridor, point on opposite side, point past corridor ends, two-section corridor.

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class RoadCorridorOverlapCheckerTests
{
    /// <summary>
    /// Creates a straight horizontal corridor along X axis from (0,0) to (100,0),
    /// with normal pointing up (+Y), and given half-width.
    /// </summary>
    private static RoadCorridor CreateStraightCorridor(
        int splineId, float halfWidth, float length = 100f, int sectionCount = 11)
    {
        var sections = new List<CorridorSection>();
        for (int i = 0; i < sectionCount; i++)
        {
            float x = length * i / (sectionCount - 1);
            sections.Add(new CorridorSection(
                new Vector2(x, 0), new Vector2(0, 1), x));
        }
        return new RoadCorridor
        {
            SplineId = splineId,
            RoadWidth = halfWidth * 2,
            CorridorHalfWidth = halfWidth,
            Sections = sections
        };
    }

    [Fact]
    public void PointInsideCorridor_ReturnsOverlapping()
    {
        // Corridor along X from (0,0) to (100,0), half-width = 5m
        // Point at (50, 3) is 3m from centerline, inside 5m corridor
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 3), corridor);
        Assert.True(result.IsOverlapping);
        Assert.Equal(1, result.OverlappingSplineId);
    }

    [Fact]
    public void PointOutsideCorridor_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, 7) is 7m from centerline, outside 5m corridor
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 7), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointOnOppositeSide_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, -7) is 7m from centerline on opposite side
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, -7), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointPastCorridorEnd_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
        // Point at (110, 0) is past the end of the corridor
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(110, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointBeforeCorridorStart_ReturnsNotOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
        // Point at (-10, 0) is before the start
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(-10, 0), corridor);
        Assert.False(result.IsOverlapping);
    }

    [Fact]
    public void PointOnCorridorEdge_ReturnsOverlapping()
    {
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f);
        // Point at (50, 4.9) is just inside
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 4.9f), corridor);
        Assert.True(result.IsOverlapping);
    }

    [Fact]
    public void PerpendicularCorridor_OverlapsAtCrossing()
    {
        // Road A: horizontal along X axis, half-width 5m
        var corridorA = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);

        // Road B: vertical corridor along Y axis from (50,-50) to (50,50)
        var sectionsB = new List<CorridorSection>();
        for (int i = 0; i < 11; i++)
        {
            float y = -50f + 100f * i / 10;
            sectionsB.Add(new CorridorSection(
                new Vector2(50, y), new Vector2(1, 0), i * 10f));
        }
        var corridorB = new RoadCorridor
        {
            SplineId = 2, RoadWidth = 8f, CorridorHalfWidth = 4f, Sections = sectionsB
        };

        // Point on road B's left edge at (46, 0) — should be inside road A's corridor
        // (it's at Y=0 which is road A's centerline, and X=46 is within road A's length)
        var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(46, 0), corridorA);
        Assert.True(result.IsOverlapping);

        // Point on road B's left edge at (46, 20) — outside road A's corridor
        // (Y=20 is way outside road A's 5m half-width)
        var result2 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(46, 20), corridorA);
        Assert.False(result2.IsOverlapping);
    }

    [Fact]
    public void TwoSectionCorridor_WorksCorrectly()
    {
        // Minimal corridor with just 2 sections
        var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f,
            length: 50f, sectionCount: 2);
        // Point inside
        var r1 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(25, 2), corridor);
        Assert.True(r1.IsOverlapping);
        // Point outside
        var r2 = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(25, 7), corridor);
        Assert.False(r2.IsOverlapping);
    }

    [Fact]
    public void CheckAgainstAllCorridors_SkipsOwnSpline()
    {
        var corridors = new Dictionary<int, RoadCorridor>
        {
            [1] = CreateStraightCorridor(1, 5f),
            [2] = CreateStraightCorridor(2, 5f)
        };
        // Point at (50, 0) is inside both corridors, but checking for splineId=1
        // should skip corridor 1 and only check corridor 2
        var result = RoadCorridorOverlapChecker.CheckAgainstAllCorridors(
            new Vector2(50, 0), ownSplineId: 1, corridors);
        Assert.True(result.IsOverlapping);
        Assert.Equal(2, result.OverlappingSplineId);
    }

    [Fact]
    public void SideSpecificSuppression_LeftEdgeUnaffectedByRightSideRoad()
    {
        // Road A: horizontal along X axis, half-width 5m, normal pointing +Y
        var corridorA = CreateStraightCorridor(splineId: 1, halfWidth: 5f);

        // Road B connects from the +Y side (right/above) at X=50
        var sectionsB = new List<CorridorSection>();
        for (int i = 0; i < 6; i++)
        {
            float y = 10f + 20f * i;  // from (50,10) to (50,110)
            sectionsB.Add(new CorridorSection(
                new Vector2(50, y), new Vector2(1, 0), i * 20f));
        }
        var corridorB = new RoadCorridor
        {
            SplineId = 2, RoadWidth = 6f, CorridorHalfWidth = 4f, Sections = sectionsB
        };

        // Road A's LEFT edge node at (50, -4.5) — opposite side from road B
        // Should NOT be inside road B's corridor (road B is at Y=10..110)
        var leftResult = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, -4.5f), corridorB);
        Assert.False(leftResult.IsOverlapping);

        // Road A's RIGHT edge node at (50, 4.5) — same side as road B
        // Should be inside road B's corridor (road B starts at Y=10, halfWidth=4, extends to Y=6)
        var rightResult = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(
            new Vector2(50, 12f), corridorB);
        Assert.True(rightResult.IsOverlapping);
    }

    [Fact]
    public void CheckWithJunctionFilter_OnlyChecksNearbyCorridors()
    {
        var corridors = new Dictionary<int, RoadCorridor>
        {
            [1] = CreateStraightCorridor(1, 5f),  // along X axis
            [2] = CreateStraightCorridor(2, 5f)   // same path (overlapping for test)
        };

        // Junction at (50, 0) with only spline 1 and 2 contributing
        var zones = new List<JunctionInfluenceZone>
        {
            new(new Vector2(50, 0), 15f, 225f, new List<int> { 1, 2 })
        };

        // Point at (50, 3) — near junction, inside corridor 2
        var r1 = RoadCorridorOverlapChecker.CheckWithJunctionFilter(
            new Vector2(50, 3), ownSplineId: 1, corridors, zones);
        Assert.True(r1.IsOverlapping);

        // Point at (200, 3) — far from junction, should NOT be checked
        var r2 = RoadCorridorOverlapChecker.CheckWithJunctionFilter(
            new Vector2(200, 3), ownSplineId: 1, corridors, zones);
        Assert.False(r2.IsOverlapping);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorOverlapChecker" -v n`
Expected: FAIL

- [ ] **Step 3: Implement RoadCorridorOverlapChecker**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Result of a corridor overlap check.
/// </summary>
public readonly record struct OverlapResult(bool IsOverlapping, int? OverlappingSplineId);

/// <summary>
/// Checks whether a 2D point falls inside a road's surface corridor.
/// Uses closest-section lookup and bracketing pair interpolation for
/// robust handling of curves and varying tangent directions.
/// </summary>
public static class RoadCorridorOverlapChecker
{
    /// <summary>
    /// Checks a point against a single corridor.
    /// Algorithm:
    /// 1. Find the closest section center to P
    /// 2. Check bracketing pairs (k-1,k) and (k,k+1)
    /// 3. Interpolate center and normal at P's longitudinal position
    /// 4. Check lateral distance against corridor half-width
    /// </summary>
    public static OverlapResult CheckPointAgainstCorridor(Vector2 point, RoadCorridor corridor)
    {
        var sections = corridor.Sections;
        if (sections.Count < 2)
            return new OverlapResult(false, null);

        // Step 1: Find closest section
        int closestIdx = 0;
        float closestDistSq = float.MaxValue;
        for (int i = 0; i < sections.Count; i++)
        {
            var distSq = Vector2.DistanceSquared(point, sections[i].Center);
            if (distSq < closestDistSq)
            {
                closestDistSq = distSq;
                closestIdx = i;
            }
        }

        // Quick reject: if closest section center is farther than corridor half-width + max section spacing,
        // the point can't be inside. Use a generous threshold.
        // (corridor half-width plus diagonal of one section segment)
        // Skip this optimization for now — the bracketing check handles it.

        // Step 2: Check bracketing pairs around closest section
        // Try (closestIdx-1, closestIdx) and (closestIdx, closestIdx+1)
        if (closestIdx > 0 &&
            TryBracketCheck(point, sections[closestIdx - 1], sections[closestIdx],
                corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);

        if (closestIdx < sections.Count - 1 &&
            TryBracketCheck(point, sections[closestIdx], sections[closestIdx + 1],
                corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);

        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Checks whether point P is longitudinally between sections A and B,
    /// and laterally within the corridor half-width.
    /// </summary>
    private static bool TryBracketCheck(
        Vector2 point, CorridorSection sA, CorridorSection sB, float halfWidth)
    {
        var ab = sB.Center - sA.Center;
        var abLenSq = ab.LengthSquared();
        if (abLenSq < 0.001f) return false; // Degenerate segment

        // Project P onto segment AB to get parameter t
        var ap = point - sA.Center;
        var t = Vector2.Dot(ap, ab) / abLenSq;

        // Must be longitudinally between A and B
        if (t < 0f || t > 1f) return false;

        // Interpolate center and normal
        var center = Vector2.Lerp(sA.Center, sB.Center, t);
        var normal = Vector2.Normalize(Vector2.Lerp(sA.Normal, sB.Normal, t));

        // Lateral distance
        var lateralDist = Vector2.Dot(point - center, normal);
        return MathF.Abs(lateralDist) < halfWidth;
    }

    /// <summary>
    /// Checks a point against all corridors except the point's own spline.
    /// Returns the first overlap found.
    /// </summary>
    public static OverlapResult CheckAgainstAllCorridors(
        Vector2 point,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors)
    {
        foreach (var (splineId, corridor) in corridors)
        {
            if (splineId == ownSplineId) continue;
            var result = CheckPointAgainstCorridor(point, corridor);
            if (result.IsOverlapping) return result;
        }
        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Checks a point against corridors, but only those contributing to
    /// junctions near the point. Uses junction proximity filter for performance.
    /// </summary>
    public static OverlapResult CheckWithJunctionFilter(
        Vector2 point,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones)
    {
        foreach (var zone in junctionZones)
        {
            if (Vector2.DistanceSquared(point, zone.Position) > zone.RadiusSquared)
                continue;

            // Point is near this junction — check contributing corridors
            foreach (var contributingSplineId in zone.ContributingSplineIds)
            {
                if (contributingSplineId == ownSplineId) continue;
                if (!corridors.TryGetValue(contributingSplineId, out var corridor)) continue;

                var result = CheckPointAgainstCorridor(point, corridor);
                if (result.IsOverlapping) return result;
            }
        }
        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Builds junction influence zones for the proximity filter.
    /// Each zone covers the area where corridor overlaps can occur.
    /// </summary>
    public static List<JunctionInfluenceZone> BuildJunctionInfluenceZones(
        IReadOnlyList<NetworkJunction> junctions,
        IReadOnlyDictionary<int, RoadCorridor> corridors)
    {
        var zones = new List<JunctionInfluenceZone>();

        foreach (var junction in junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type == JunctionType.Endpoint) continue;

            var contributingIds = junction.Contributors
                .Select(c => c.Spline.SplineId)
                .Distinct()
                .ToList();

            // Radius = max corridor half-width among contributors + some padding
            float maxHalfWidth = 0f;
            foreach (var id in contributingIds)
            {
                if (corridors.TryGetValue(id, out var c))
                    maxHalfWidth = MathF.Max(maxHalfWidth, c.CorridorHalfWidth);
            }

            if (maxHalfWidth <= 0f) continue;

            // Use 2x max half-width to account for the check needing to cover
            // both the corridor being checked AND the offset node position
            var radius = maxHalfWidth * 2f;

            zones.Add(new JunctionInfluenceZone(
                junction.Position, radius, radius * radius, contributingIds));
        }

        return zones;
    }
}

/// <summary>
/// Pre-computed junction influence zone for quick spatial filtering.
/// </summary>
public readonly record struct JunctionInfluenceZone(
    Vector2 Position,
    float Radius,
    float RadiusSquared,
    IReadOnlyList<int> ContributingSplineIds);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorOverlapChecker" -v n`
Expected: All 9 tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs
git commit -m "feat: add RoadCorridorOverlapChecker with per-node corridor overlap detection"
```

---

## Chunk 3: Integrate into DecalRoadGenerator & Update Defaults

### Task 4: Refactor DecalRoadGenerator to two-pass architecture

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

This is the core integration task. The `Generate()` method changes from single-pass with rule-based interruption to two-pass with corridor overlap.

- [ ] **Step 1: Refactor Generate() to two-pass architecture**

In `DecalRoadGenerator.cs`, replace the `Generate()` method. Key changes:
1. Remove `JunctionInterruptionRuleBuilder.BuildRules()` call
2. Add Pass 1: `RoadCorridorBuilder.BuildCorridors()` + `BuildJunctionInfluenceZones()`
3. Pass corridors and junction zones into `GenerateForSpline()`

```csharp
// Replace the entire Generate() method body (lines 18-62 in current file):

    public static List<GeneratedDecalRoad> Generate(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults)
    {
        var results = new List<GeneratedDecalRoad>();

        // Pass 1: Build road corridors for all eligible splines
        var corridors = RoadCorridorBuilder.BuildCorridors(
            network, settings, appDataDefaults, settings.NodeSpacingMeters);

        // Build junction influence zones for proximity filter
        var junctionZones = RoadCorridorOverlapChecker.BuildJunctionInfluenceZones(
            network.Junctions, corridors);

        // Pass 2: Generate DecalRoads with corridor overlap checking
        foreach (var spline in network.Splines)
        {
            if (spline.IsBridge || spline.IsTunnel)
                continue;

            var layerSet = DecalRoadLayerSetResolver.Resolve(
                spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            var splineResults = GenerateForSpline(
                spline, layerSet, crossSections,
                corridors, junctionZones,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                settings.NodeSpacingMeters);
            results.AddRange(splineResults);
        }

        return results;
    }
```

- [ ] **Step 2: Update GenerateForSpline() signature and interruption logic**

Replace the `GenerateForSpline()` method signature and the interruption section.

**Signature change**: Replace `IReadOnlyList<JunctionInterruptionRule> interruptionRules` parameter with `IReadOnlyDictionary<int, RoadCorridor> corridors` and `IReadOnlyList<JunctionInfluenceZone> junctionZones`.

**Interruption change**: Replace the `JunctionInterrupter.InterruptWithRules()` call (lines 121-124) with corridor overlap checking. The new code builds segments by checking each offset node against other corridors:

```csharp
    internal static List<GeneratedDecalRoad> GenerateForSpline(
        ParameterizedRoadSpline spline,
        DecalRoadLayerSet layerSet,
        IReadOnlyList<UnifiedCrossSection> crossSections,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeSpacingMeters)
    {
        var results = new List<GeneratedDecalRoad>();
        var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
        var laneCount = GetLaneCount(spline, layerSet);
        var splineName = GetSplineName(spline);

        var sampledSections = SubSampleCrossSections(crossSections, nodeSpacingMeters);
        if (sampledSections.Count < 2) return results;

        var expandedLayers = ExpandLayers(layerSet.Layers, laneCount);

        foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)
        {
            if (!layer.IsEnabled) continue;

            float nodeWidth;
            if (layer.IsTrackWidth)
                nodeWidth = roadWidth;
            else if (layer.IsLaneWidth)
                nodeWidth = roadWidth / Math.Max(1, laneCount);
            else
                nodeWidth = layer.Width;

            // Calculate laterally offset nodes using cross-section normals
            var offsetNodes2D = new List<Vector2>(sampledSections.Count);
            foreach (var cs in sampledSections)
            {
                var offset = position * 0.5f * roadWidth;
                var offsetPos = cs.CenterPoint + cs.NormalDirection * offset;
                offsetNodes2D.Add(offsetPos);
            }

            // Build segments using corridor overlap suppression
            List<List<(Vector2 Pos, int SectionIndex)>> segments;
            if (layer.InterruptAtJunctions)
            {
                segments = BuildSegmentsWithCorridorCheck(
                    offsetNodes2D, spline.SplineId, corridors, junctionZones);
            }
            else
            {
                // No interruption — single segment with all nodes
                var allNodes = new List<(Vector2, int)>();
                for (int i = 0; i < offsetNodes2D.Count; i++)
                    allNodes.Add((offsetNodes2D[i], i));
                segments = [allNodes];
            }

            // Process each segment (unchanged from current code)
            int chunkIndex = 0;
            foreach (var segment in segments)
            {
                var worldNodesSegment = new List<float[]>(segment.Count);
                foreach (var (pos, sectionIdx) in segment)
                {
                    var cs = sampledSections[sectionIdx];
                    float elevation;
                    if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                        elevation = cs.TargetElevation;
                    else
                        elevation = GetHeightMapElevation(heightMap, pos.X, pos.Y, metersPerPixel);

                    var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                        pos.X, pos.Y, elevation + terrainBaseHeight,
                        terrainSizePixels, metersPerPixel);
                    worldNodesSegment.Add([worldPos.X, worldPos.Y, worldPos.Z, nodeWidth]);
                }

                if (isFlipped)
                    worldNodesSegment.Reverse();

                var chunks = ChunkNodes(worldNodesSegment, maxNodesPerChunk: 100);
                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    chunkIndex++;
                    var name = $"{splineName}_{layer.Name}_{side}_{chunkIndex:D3}";
                    var startFade = (ci == 0) ? (isFlipped ? layer.FadeOut : layer.FadeIn) : 0f;
                    var endFade = (ci == chunks.Count - 1) ? (isFlipped ? layer.FadeIn : layer.FadeOut) : 0f;

                    results.Add(new GeneratedDecalRoad
                    {
                        Name = name,
                        ParentGroupName = splineName,
                        Material = layer.Material,
                        TextureLength = layer.TextureLength,
                        RenderPriority = layer.RenderPriority,
                        StartEndFade = [startFade, endFade],
                        DistanceFade = layer.DistanceFade,
                        Drivability = layer.Drivability,
                        IsAIRoad = layer.LayerType == DecalRoadLayerType.AIRoad,
                        LanesLeft = layer.LanesLeft,
                        LanesRight = layer.LanesRight,
                        OneWay = layer.OneWay,
                        FlipDirection = layer.FlipDirection,
                        Nodes = chunks[ci]
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Builds continuous segments by suppressing nodes that fall inside another road's corridor.
    /// Each node's actual 2D position (after lateral offset) is checked — this naturally
    /// handles side-specific suppression without L/R classification.
    /// </summary>
    private static List<List<(Vector2 Pos, int SectionIndex)>> BuildSegmentsWithCorridorCheck(
        IReadOnlyList<Vector2> offsetNodes,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        int minSegmentNodes = 3)
    {
        var segments = new List<List<(Vector2, int)>>();
        var current = new List<(Vector2, int)>();

        for (int i = 0; i < offsetNodes.Count; i++)
        {
            var result = RoadCorridorOverlapChecker.CheckWithJunctionFilter(
                offsetNodes[i], ownSplineId, corridors, junctionZones);

            if (result.IsOverlapping)
            {
                if (current.Count >= minSegmentNodes)
                    segments.Add(current);
                current = [];
            }
            else
            {
                current.Add((offsetNodes[i], i));
            }
        }

        if (current.Count >= minSegmentNodes)
            segments.Add(current);

        return segments;
    }
```

- [ ] **Step 3: Remove old imports and unused centerlineNodes variable**

Remove the `using` or reference to `JunctionInterruptionRuleBuilder`, `JunctionInterruptionRule`, and `JunctionInterrupter` if they were imported. Also remove the `centerlineNodes` variable that was extracted for the old system (line 92 in current file: `var centerlineNodes = sampledSections.Select(cs => cs.CenterPoint).ToList();`).

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded (old files still exist but are no longer referenced)

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: refactor DecalRoadGenerator to two-pass corridor overlap architecture"
```

---

### Task 5: Update default layer sets and stage pending settings change

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`
- Stage: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs` (already has `JunctionExclusionMarginMeters` changed to `1.0f`)

- [ ] **Step 1: Set InterruptAtJunctions = true for all EdgeBlend layers**

In `DecalRoadDefaultLayerSets.cs`, change `InterruptAtJunctions = false` to `InterruptAtJunctions = true` for every EdgeBlend layer definition. There are EdgeBlend entries in:
- `CreateHighwaySet` (EdgeBlend1 at line 56, EdgeBlend2 at line 60)
- `CreateStandardRoadSet` (EdgeBlend1 at line 87, EdgeBlend2 at line 91)
- `CreateMinimalRoadSet` (EdgeBlend1 at line 114, EdgeBlend2 at line 118)
- `CreateResidentialSet` (EdgeBlend1 at line 137, EdgeBlend2 at line 141)
- `CreateServiceSet` (EdgeBlend1 at line 161, EdgeBlend2 at line 165)
- `CreateTrackSet` (EdgeBlend1 at line 175)

Change every `InterruptAtJunctions = false` to `InterruptAtJunctions = true` on EdgeBlend layers.

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs
git commit -m "feat: enable InterruptAtJunctions for EdgeBlend layers (corridor system is precise enough)"
```

---

### Task 6: Delete old junction interruption files

**Files:**
- Delete: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterruptionRuleBuilder.cs`
- Delete: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionInterruptionRule.cs`
- Delete: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterrupter.cs`
- Delete: `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs`
- Delete: `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs`

- [ ] **Step 1: Delete old files**

```bash
git rm BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterruptionRuleBuilder.cs
git rm BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionInterruptionRule.cs
git rm BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterrupter.cs
git rm BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs
git rm BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs
```

- [ ] **Step 2: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded with no references to deleted types

- [ ] **Step 3: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All remaining tests PASS (DecalRoadGeneratorTests, DecalRoadLayerSetResolverTests, new corridor tests)

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: remove old circular exclusion zone junction interruption system

Replaced by corridor overlap checking in RoadCorridorOverlapChecker.
Deleted: JunctionInterruptionRuleBuilder, JunctionInterruptionRule,
JunctionInterrupter, InterruptionSide enum, and their tests."
```

---

## Chunk 4: Phase 2 — Continuous Road Centerline Preservation

### Task 7: Add continuous road centerline preservation at T-junctions

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

At T-junctions, the continuous (primary) road's centerline should pass through unsuppressed. This requires:
1. Building a lookup: for each junction, which splines are continuous vs. terminating
2. When a centerline node is suppressed, check if the current spline is continuous at a junction involving the overlapping spline — if so, skip suppression

- [ ] **Step 1: Build junction continuity lookup**

Add a helper method and data structure to `DecalRoadGenerator.cs`:

```csharp
    /// <summary>
    /// For Phase 2: lookup of which splines are continuous at which junctions.
    /// Key = splineId, Value = set of splineIds that terminate at junctions where key is continuous.
    /// If spline A is continuous at a junction where spline B terminates,
    /// then ContinuityLookup[A] contains B.
    /// </summary>
    private static Dictionary<int, HashSet<int>> BuildContinuityLookup(
        IReadOnlyList<NetworkJunction> junctions)
    {
        var lookup = new Dictionary<int, HashSet<int>>();

        foreach (var junction in junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type == JunctionType.Endpoint) continue;

            var continuousIds = junction.GetContinuousRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();
            var terminatingIds = junction.GetTerminatingRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();

            // For each continuous road, record which terminating roads it can ignore
            foreach (var contId in continuousIds)
            {
                if (!lookup.TryGetValue(contId, out var set))
                {
                    set = [];
                    lookup[contId] = set;
                }
                foreach (var termId in terminatingIds)
                    set.Add(termId);
            }
        }

        return lookup;
    }
```

- [ ] **Step 2: Thread continuity lookup through Generate() and GenerateForSpline()**

In `Generate()`, add after building junction zones:
```csharp
var continuityLookup = BuildContinuityLookup(network.Junctions);
```

Pass it to `GenerateForSpline()` as an additional parameter.

- [ ] **Step 3: Update BuildSegmentsWithCorridorCheck to accept layer type and continuity lookup**

Add `DecalRoadLayerType layerType`, `int ownSplineId`, and `IReadOnlyDictionary<int, HashSet<int>>? continuityLookup` parameters. When a node overlaps and `layerType == CenterLine`, check if the overlapping spline is in `continuityLookup[ownSplineId]` — if so, skip suppression:

```csharp
    private static List<List<(Vector2 Pos, int SectionIndex)>> BuildSegmentsWithCorridorCheck(
        IReadOnlyList<Vector2> offsetNodes,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones,
        DecalRoadLayerType layerType,
        IReadOnlyDictionary<int, HashSet<int>>? continuityLookup,
        int minSegmentNodes = 3)
    {
        var segments = new List<List<(Vector2, int)>>();
        var current = new List<(Vector2, int)>();

        // Phase 2: check if this spline is continuous somewhere
        HashSet<int>? terminatorsWeCanIgnore = null;
        if (layerType == DecalRoadLayerType.CenterLine && continuityLookup != null)
            continuityLookup.TryGetValue(ownSplineId, out terminatorsWeCanIgnore);

        for (int i = 0; i < offsetNodes.Count; i++)
        {
            var result = RoadCorridorOverlapChecker.CheckWithJunctionFilter(
                offsetNodes[i], ownSplineId, corridors, junctionZones);

            bool suppress = result.IsOverlapping;

            // Phase 2: Continuous road centerline preservation
            if (suppress && terminatorsWeCanIgnore != null &&
                result.OverlappingSplineId.HasValue &&
                terminatorsWeCanIgnore.Contains(result.OverlappingSplineId.Value))
            {
                suppress = false; // This spline is continuous, overlapping road terminates here
            }

            if (suppress)
            {
                if (current.Count >= minSegmentNodes)
                    segments.Add(current);
                current = [];
            }
            else
            {
                current.Add((offsetNodes[i], i));
            }
        }

        if (current.Count >= minSegmentNodes)
            segments.Add(current);

        return segments;
    }
```

- [ ] **Step 4: Update the call site in GenerateForSpline()**

Pass `layer.LayerType` and `continuityLookup` to `BuildSegmentsWithCorridorCheck()`.

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: Phase 2 — preserve continuous road centerlines through T-junctions"
```

---

## Chunk 5: Full Build & Test Verification

### Task 8: Run full test suite and verify build

- [ ] **Step 1: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 2: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 3: Verify no references to deleted types**

Run a search to confirm no remaining references to the old types:

```bash
grep -r "JunctionInterruptionRule\|JunctionInterrupter\b\|InterruptionSide" BeamNgTerrainPoc/ BeamNgTerrainPoc.Tests/ --include="*.cs" -l
```
Expected: No files found (only the spec/plan docs reference these names)

- [ ] **Step 4: Commit any final fixes**

If any build issues or test failures were found and fixed:

```bash
git add -A
git commit -m "fix: resolve build issues from corridor junction suppression integration"
```

---

## Post-Implementation Notes

### What's NOT in this plan (deferred):

1. **Junction-specific layer replacement** — replacing suppressed edge lines with dashed versions at junctions. Noted as future extension point in spec.
2. **Dual carriageway handling** — inner edge line suppression between parallel one-way roads. May need a "same-direction parallel" detection.
3. **Binary search optimization** — for long corridors with many sections, the closest-section linear scan could be replaced with spatial indexing. Only needed if performance is an issue.

### Manual Testing Checklist

After implementation, verify in BeamNG:
1. Generate terrain with OSM data containing T-junctions, Y-junctions, crossroads
2. Check edge blends: should stop cleanly where another road's surface begins
3. Check edge lines: same behavior as edge blends, clean cuts at road boundaries
4. Check side-specific suppression: edge layers on the opposite side of a junction should remain intact
5. Check centerline preservation: primary road centerline passes through T-junctions
6. Check AI roads: should remain continuous through all junctions
7. Check roundabout connections: edge layers suppressed where connecting roads meet ring
8. Compare before/after with the old circular exclusion system
