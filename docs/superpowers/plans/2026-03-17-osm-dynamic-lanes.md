# OSM Dynamic Lanes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dynamically derive lane count, direction, and one-way status from OSM tags and use them to override DecalRoad AI road properties during generation, with per-segment lane changes splitting lane-dependent layers at boundaries.

**Architecture:** Lane data is parsed from OSM tags into `OsmLaneInfo` at `PathWithMetadata` creation, stored as `LaneSegment` lists that survive merges (swap forward/backward on reversal, combine lists on merge). `LaneSegments` propagate through `RoadSpline` → `ParameterizedRoadSpline` → `DecalRoadGenerator`, where lane-dependent layers (AI roads, IsPerLane, CenterLine) are split at lane-change boundaries into separate DecalRoad objects with per-segment properties. Lane-independent layers render continuously.

**Tech Stack:** .NET 9, C#, xUnit, System.Numerics

**Spec:** `docs/superpowers/specs/2026-03-17-osm-dynamic-lanes-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs` | Lane/direction data model with `Reversed()` and `TryParse()` |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegment.cs` | Position marker along a path: StartPointIndex, StartDistance, OsmLaneInfo |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs` | Static helpers: `ReverseSegments`, `MergeSegments`, `Consolidate` |
| `BeamNgTerrainPoc.Tests/DecalRoad/OsmLaneInfoTests.cs` | Parsing fallback chain + reversal tests |
| `BeamNgTerrainPoc.Tests/DecalRoad/LaneSegmentMergeTests.cs` | Merge, reverse, consolidate operation tests |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs` | Lane-aware generation: splitting, AI properties, fallback |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs` | Add `List<LaneSegment> LaneSegments` property |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Parse OsmLaneInfo when creating PathWithMetadata; convert StartPointIndex→StartDistance when creating RoadSpline |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs` | Lane segment propagation in all 4 merge methods + ClonePath deep-copy |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs` | Lane segment propagation in all 4 merge methods + ClonePath deep-copy |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs` | Add `List<LaneSegment>? LaneSegments` property |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs` | Add `List<LaneSegment>? LaneSegments`, remove `OsmTags` |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs` | Propagate LaneSegments from RoadSpline to ParameterizedRoadSpline |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Lane-aware generation: segment splitting, AI property derivation, replace `GetLaneCount()` |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Update `GetLaneCount()` to use LaneSegments (max across segments) |

---

## Chunk 1: Data Models — OsmLaneInfo + LaneSegment + LaneSegmentOps

### Task 1: OsmLaneInfo — TryParse and Reversed

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/OsmLaneInfoTests.cs`

- [ ] **Step 1: Write failing tests for OsmLaneInfo.TryParse fallback chain**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/OsmLaneInfoTests.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class OsmLaneInfoTests
{
    // Priority 1: lanes:forward + lanes:backward
    [Fact]
    public void TryParse_ForwardAndBackward_UsesDirectly()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "4", ["lanes:forward"] = "3", ["lanes:backward"] = "1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(4, info.TotalLanes);
        Assert.Equal(3, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
        Assert.False(info.IsOneWay);
    }

    // Priority 2: oneway=yes + lanes
    [Fact]
    public void TryParse_OnewayYes_AllForward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["oneway"] = "yes"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(3, info.TotalLanes);
        Assert.Equal(3, info.LanesForward);
        Assert.Equal(0, info.LanesBackward);
        Assert.True(info.IsOneWay);
    }

    // Priority 3: oneway=-1 + lanes
    [Fact]
    public void TryParse_OnewayReverse_AllBackward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "2", ["oneway"] = "-1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.TotalLanes);
        Assert.Equal(0, info.LanesForward);
        Assert.Equal(2, info.LanesBackward);
        Assert.True(info.IsOneWay);
    }

    // Priority 4: lanes:forward + lanes (no backward)
    [Fact]
    public void TryParse_ForwardAndTotal_ComputesBackward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["lanes:forward"] = "2"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 5: lanes:backward + lanes (no forward)
    [Fact]
    public void TryParse_BackwardAndTotal_ComputesForward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["lanes:backward"] = "1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 6: lanes only (two-way, even)
    [Fact]
    public void TryParse_LanesOnlyEven_EvenSplit()
    {
        var tags = new Dictionary<string, string> { ["lanes"] = "4" };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(2, info.LanesBackward);
        Assert.False(info.IsOneWay);
    }

    // Priority 6: lanes only (two-way, odd — extra to forward)
    [Fact]
    public void TryParse_LanesOnlyOdd_ExtraToForward()
    {
        var tags = new Dictionary<string, string> { ["lanes"] = "3" };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 7: no lane tags
    [Fact]
    public void TryParse_NoLaneTags_ReturnsNull()
    {
        var tags = new Dictionary<string, string> { ["highway"] = "residential" };
        Assert.Null(OsmLaneInfo.TryParse(tags));
    }

    [Fact]
    public void TryParse_EmptyTags_ReturnsNull()
    {
        Assert.Null(OsmLaneInfo.TryParse(new Dictionary<string, string>()));
    }

    // Future-use fields parsed
    [Fact]
    public void TryParse_ParsesFutureFields()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "2",
            ["turn:lanes:forward"] = "left|through",
            ["turn:lanes:backward"] = "through|right",
            ["maxspeed"] = "50",
            ["surface"] = "asphalt"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal("left|through", info.TurnLanesForward);
        Assert.Equal("through|right", info.TurnLanesBackward);
        Assert.Equal("50", info.MaxSpeed);
        Assert.Equal("asphalt", info.Surface);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~OsmLaneInfoTests" --no-build 2>&1 | head -5`
Expected: Build error — `OsmLaneInfo` does not exist.

- [ ] **Step 3: Implement OsmLaneInfo**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class OsmLaneInfo
{
    public int TotalLanes { get; set; }
    public int LanesForward { get; set; }
    public int LanesBackward { get; set; }
    public int LanesBothWays { get; set; }
    public bool IsOneWay { get; set; }

    // Stored for future use
    public string? TurnLanesForward { get; set; }
    public string? TurnLanesBackward { get; set; }
    public string? MaxSpeed { get; set; }
    public string? Surface { get; set; }
    public string? BusLanes { get; set; }
    public string? HgvLanes { get; set; }
    public string? Access { get; set; }

    public OsmLaneInfo Reversed() => new OsmLaneInfo
    {
        TotalLanes = TotalLanes,
        LanesForward = LanesBackward,
        LanesBackward = LanesForward,
        LanesBothWays = LanesBothWays,
        IsOneWay = IsOneWay,
        TurnLanesForward = TurnLanesBackward,
        TurnLanesBackward = TurnLanesForward,
        MaxSpeed = MaxSpeed,
        Surface = Surface,
        BusLanes = BusLanes,
        HgvLanes = HgvLanes,
        Access = Access
    };

    public static OsmLaneInfo? TryParse(Dictionary<string, string> tags)
    {
        tags.TryGetValue("lanes", out var lanesStr);
        tags.TryGetValue("lanes:forward", out var fwdStr);
        tags.TryGetValue("lanes:backward", out var bwdStr);
        tags.TryGetValue("oneway", out var oneway);

        int.TryParse(lanesStr, out var totalLanes);
        int.TryParse(fwdStr, out var fwd);
        int.TryParse(bwdStr, out var bwd);

        bool hasFwd = fwdStr != null && fwd > 0;
        bool hasBwd = bwdStr != null && bwd > 0;
        bool isOneWayYes = oneway is "yes" or "true" or "1";
        bool isOneWayReverse = oneway == "-1";

        int lanesForward, lanesBackward;
        bool isOneWay;

        // Priority 1: both forward + backward explicit
        if (hasFwd && hasBwd)
        {
            lanesForward = fwd;
            lanesBackward = bwd;
            if (totalLanes <= 0) totalLanes = fwd + bwd;
            isOneWay = false;
        }
        // Priority 2: oneway=yes + lanes
        else if (isOneWayYes && totalLanes > 0)
        {
            lanesForward = totalLanes;
            lanesBackward = 0;
            isOneWay = true;
        }
        // Priority 3: oneway=-1 + lanes
        else if (isOneWayReverse && totalLanes > 0)
        {
            lanesForward = 0;
            lanesBackward = totalLanes;
            isOneWay = true;
        }
        // Priority 4: lanes:forward + lanes
        else if (hasFwd && totalLanes > 0)
        {
            lanesForward = fwd;
            lanesBackward = totalLanes - fwd;
            isOneWay = false;
        }
        // Priority 5: lanes:backward + lanes
        else if (hasBwd && totalLanes > 0)
        {
            lanesForward = totalLanes - bwd;
            lanesBackward = bwd;
            isOneWay = false;
        }
        // Priority 6: lanes only (two-way)
        else if (totalLanes > 0)
        {
            lanesBackward = totalLanes / 2;
            lanesForward = totalLanes - lanesBackward; // odd extra to forward
            isOneWay = false;
        }
        // Priority 7: no lane tags
        else
        {
            return null;
        }

        var info = new OsmLaneInfo
        {
            TotalLanes = totalLanes,
            LanesForward = lanesForward,
            LanesBackward = lanesBackward,
            IsOneWay = isOneWay
        };

        // Parse future-use fields
        if (tags.TryGetValue("turn:lanes:forward", out var tlf))
            info.TurnLanesForward = tlf;
        else if (tags.TryGetValue("turn:lanes", out var tl) && !isOneWayReverse)
            info.TurnLanesForward = tl;

        if (tags.TryGetValue("turn:lanes:backward", out var tlb))
            info.TurnLanesBackward = tlb;

        if (tags.TryGetValue("maxspeed", out var ms)) info.MaxSpeed = ms;
        if (tags.TryGetValue("surface", out var sf)) info.Surface = sf;
        if (tags.TryGetValue("bus:lanes", out var bl)) info.BusLanes = bl;
        if (tags.TryGetValue("hgv:lanes", out var hl)) info.HgvLanes = hl;
        if (tags.TryGetValue("access", out var ac)) info.Access = ac;

        return info;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~OsmLaneInfoTests" -v minimal`
Expected: All 9 tests pass.

- [ ] **Step 5: Add reversal tests**

Append to `OsmLaneInfoTests.cs`:

```csharp
    [Fact]
    public void Reversed_SwapsForwardBackward()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 4, LanesForward = 3, LanesBackward = 1,
            IsOneWay = false,
            TurnLanesForward = "left|through|right",
            TurnLanesBackward = "through"
        };

        var reversed = info.Reversed();

        Assert.Equal(4, reversed.TotalLanes);
        Assert.Equal(1, reversed.LanesForward);
        Assert.Equal(3, reversed.LanesBackward);
        Assert.False(reversed.IsOneWay);
        Assert.Equal("through", reversed.TurnLanesForward);
        Assert.Equal("left|through|right", reversed.TurnLanesBackward);
    }

    [Fact]
    public void Reversed_Twice_ReturnsOriginalValues()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 3, LanesForward = 2, LanesBackward = 1, IsOneWay = false
        };
        var roundTrip = info.Reversed().Reversed();

        Assert.Equal(info.TotalLanes, roundTrip.TotalLanes);
        Assert.Equal(info.LanesForward, roundTrip.LanesForward);
        Assert.Equal(info.LanesBackward, roundTrip.LanesBackward);
    }
```

- [ ] **Step 6: Run all OsmLaneInfo tests**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~OsmLaneInfoTests" -v minimal`
Expected: All 11 tests pass.

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs BeamNgTerrainPoc.Tests/DecalRoad/OsmLaneInfoTests.cs
git commit -m "feat: add OsmLaneInfo with TryParse fallback chain and Reversed()"
```

---

### Task 2: LaneSegment + LaneSegmentOps

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegment.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/LaneSegmentMergeTests.cs`

- [ ] **Step 1: Write failing tests for LaneSegmentOps**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/LaneSegmentMergeTests.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class LaneSegmentMergeTests
{
    private static OsmLaneInfo TwoLane() => new()
        { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 };
    private static OsmLaneInfo ThreeLane() => new()
        { TotalLanes = 3, LanesForward = 2, LanesBackward = 1 };

    // --- ReverseSegments ---

    [Fact]
    public void ReverseSegments_SingleSegment_ReversesLaneInfo()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        var reversed = LaneSegmentOps.ReverseSegments(segs, totalPointCount: 50);

        Assert.Single(reversed);
        Assert.Equal(0, reversed[0].StartPointIndex);
        // Forward and backward should be swapped
        Assert.Equal(1, reversed[0].LaneInfo.LanesForward);
        Assert.Equal(2, reversed[0].LaneInfo.LanesBackward);
    }

    [Fact]
    public void ReverseSegments_MultipleSegments_CorrectIndicesAndOrder()
    {
        // N=100, segments at [0, 48, 93]
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 48, LaneInfo = ThreeLane() },
            new() { StartPointIndex = 93, LaneInfo = TwoLane() }
        };

        var reversed = LaneSegmentOps.ReverseSegments(segs, totalPointCount: 100);

        // Expected: [0(was Seg2), 7(was Seg1), 52(was Seg0)]
        Assert.Equal(3, reversed.Count);
        Assert.Equal(0, reversed[0].StartPointIndex);   // was Seg2: 100-1-99=0
        Assert.Equal(7, reversed[1].StartPointIndex);   // was Seg1: 100-1-92=7
        Assert.Equal(52, reversed[2].StartPointIndex);  // was Seg0: 100-1-47=52

        // Seg2 (TwoLane) reversed
        Assert.Equal(1, reversed[0].LaneInfo.LanesForward);
        Assert.Equal(1, reversed[0].LaneInfo.LanesBackward);
        // Seg1 (ThreeLane) reversed
        Assert.Equal(1, reversed[1].LaneInfo.LanesForward);
        Assert.Equal(2, reversed[1].LaneInfo.LanesBackward);
    }

    // --- MergeSegments ---

    [Fact]
    public void MergeSegments_EndToStart_CombinesWithOffset()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        // path1 has 50 points, overlap by 1 → offset = 49
        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Equal(2, merged.Count);
        Assert.Equal(0, merged[0].StartPointIndex);
        Assert.Equal(49, merged[1].StartPointIndex);
        Assert.Equal(2, merged[0].LaneInfo.TotalLanes);
        Assert.Equal(3, merged[1].LaneInfo.TotalLanes);
    }

    [Fact]
    public void MergeSegments_IdenticalAdjacentSegments_Consolidated()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        // Both are TwoLane → consolidated to single segment
        Assert.Single(merged);
        Assert.Equal(0, merged[0].StartPointIndex);
    }

    [Fact]
    public void MergeSegments_EmptyFirst_ReturnsSecondWithOffset()
    {
        var segs1 = new List<LaneSegment>();
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Single(merged);
        Assert.Equal(49, merged[0].StartPointIndex);
    }

    [Fact]
    public void MergeSegments_EmptySecond_ReturnsFirst()
    {
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>();

        var merged = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 49);

        Assert.Single(merged);
    }

    [Fact]
    public void MergeSegments_MultipleMerges_PreserveBoundaries()
    {
        // Simulate: path1(2-lane) + path2(3-lane) + path3(2-lane)
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };
        var segs3 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };

        var merged12 = LaneSegmentOps.MergeSegments(segs1, segs2, pointOffset: 29);
        var merged123 = LaneSegmentOps.MergeSegments(merged12, segs3, pointOffset: 58);

        Assert.Equal(3, merged123.Count);
        Assert.Equal(0, merged123[0].StartPointIndex);
        Assert.Equal(29, merged123[1].StartPointIndex);
        Assert.Equal(58, merged123[2].StartPointIndex);
    }

    // --- Consolidate ---

    [Fact]
    public void Consolidate_RemovesAdjacentIdentical()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 30, LaneInfo = TwoLane() },
            new() { StartPointIndex = 60, LaneInfo = ThreeLane() }
        };

        var result = LaneSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].StartPointIndex);
        Assert.Equal(60, result[1].StartPointIndex);
    }

    [Fact]
    public void Consolidate_NoIdentical_Unchanged()
    {
        var segs = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() },
            new() { StartPointIndex = 30, LaneInfo = ThreeLane() }
        };

        var result = LaneSegmentOps.Consolidate(segs);

        Assert.Equal(2, result.Count);
    }

    // --- EndToEnd merge with reversal ---

    [Fact]
    public void EndToEnd_ReversesThenMerges()
    {
        // Simulates TryEndToEnd: path1 forward + reversed(path2)
        var segs1 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = TwoLane() }
        };
        // path2 has 3 lanes forward before reversal
        var segs2 = new List<LaneSegment>
        {
            new() { StartPointIndex = 0, LaneInfo = ThreeLane() }
        };

        // Reverse path2 (40 points), then merge
        var reversed2 = LaneSegmentOps.ReverseSegments(segs2, totalPointCount: 40);
        var merged = LaneSegmentOps.MergeSegments(segs1, reversed2, pointOffset: 49);

        Assert.Equal(2, merged.Count);
        // After reversal, ThreeLane becomes 1 forward, 2 backward
        Assert.Equal(1, merged[1].LaneInfo.LanesForward);
        Assert.Equal(2, merged[1].LaneInfo.LanesBackward);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~LaneSegmentMergeTests" --no-build 2>&1 | head -5`
Expected: Build error — `LaneSegment` and `LaneSegmentOps` do not exist.

- [ ] **Step 3: Implement LaneSegment**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegment.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class LaneSegment
{
    public int StartPointIndex { get; set; }
    public float StartDistance { get; set; }
    public OsmLaneInfo LaneInfo { get; set; } = null!;
}
```

- [ ] **Step 4: Implement LaneSegmentOps**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public static class LaneSegmentOps
{
    /// <summary>
    /// Reverses a segment list when the underlying point array is reversed.
    /// Each segment's LaneInfo is also reversed (forward ↔ backward).
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
            && a.IsOneWay == b.IsOneWay;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~LaneSegmentMergeTests" -v minimal`
Expected: All 9 tests pass.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegment.cs \
        BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs \
        BeamNgTerrainPoc.Tests/DecalRoad/LaneSegmentMergeTests.cs
git commit -m "feat: add LaneSegment and LaneSegmentOps (reverse, merge, consolidate)"
```

---

## Chunk 2: Pipeline Integration — PathWithMetadata Through to ParameterizedRoadSpline

### Task 3: Add LaneSegments to PathWithMetadata + Parse in OsmGeometryProcessor

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`

- [ ] **Step 1: Add LaneSegments property to PathWithMetadata**

In `PathWithMetadata.cs`, add after existing properties:

```csharp
/// <summary>
/// Per-segment lane configuration parsed from OSM tags.
/// Empty list means no lane data available (use defaults at generation time).
/// Segments survive merges via LaneSegmentOps.
/// </summary>
public List<LaneSegment> LaneSegments { get; set; } = [];
```

- [ ] **Step 2: Parse OsmLaneInfo in OsmGeometryProcessor.ConvertLinesToSplines()**

Find the `PathWithMetadata` constructor call (around line 772) where `allPathsMeta.Add(new PathWithMetadata(...))` is called. After the `PathWithMetadata` is created, parse lane info and assign:

```csharp
// After: allPathsMeta.Add(new PathWithMetadata(...));
// Add lane segment parsing:
var laneInfo = OsmLaneInfo.TryParse(feature.Tags);
if (laneInfo != null)
{
    allPathsMeta[^1].LaneSegments = [new LaneSegment { StartPointIndex = 0, LaneInfo = laneInfo }];
}
```

Note: Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` at the top of the file.

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs \
        BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs
git commit -m "feat: add LaneSegments to PathWithMetadata, parse from OSM tags"
```

---

### Task 4: Lane Segment Propagation in RouteRelationAssembler

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs`

All 4 merge methods follow a pattern. The merged `PathWithMetadata` is constructed near the end of each method. After the merged path is created, assign `LaneSegments` using the appropriate `LaneSegmentOps` call. Also update `ClonePath()` to deep-copy `LaneSegments`.

- [ ] **Step 1: Add using directive**

Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` at the top of `RouteRelationAssembler.cs`.

- [ ] **Step 2: Update TryEndToStart — no reversal**

After the merged `PathWithMetadata` is returned (around line 238), before the `return` statement, add:

```csharp
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path1.LaneSegments, path2.LaneSegments, path1.Points.Count - 1);
```

Where `merged` is the newly created `PathWithMetadata`. (The variable name may differ — use whatever the local variable is called.)

- [ ] **Step 3: Update TryEndToEnd — reverse path2**

```csharp
var reversedSegs = LaneSegmentOps.ReverseSegments(path2.LaneSegments, path2.Points.Count);
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path1.LaneSegments, reversedSegs, path1.Points.Count - 1);
```

- [ ] **Step 4: Update TryStartToEnd — path2 first, no reversal**

```csharp
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path2.LaneSegments, path1.LaneSegments, path2.Points.Count - 1);
```

- [ ] **Step 5: Update TryStartToStart — reverse path2, path2 first**

```csharp
var reversedSegs = LaneSegmentOps.ReverseSegments(path2.LaneSegments, path2.Points.Count);
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    reversedSegs, path1.LaneSegments, path2.Points.Count - 1);
```

- [ ] **Step 6: Update ClonePath() to deep-copy LaneSegments**

After the existing `PathWithMetadata` clone construction, add:

```csharp
clone.LaneSegments = path.LaneSegments
    .Select(s => new LaneSegment { StartPointIndex = s.StartPointIndex, LaneInfo = s.LaneInfo })
    .ToList();
```

- [ ] **Step 7: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs
git commit -m "feat: propagate LaneSegments through RouteRelationAssembler merge methods"
```

---

### Task 5: Lane Segment Propagation in NodeBasedPathConnector

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs`

Same pattern as Task 4, applied to all 4 merge methods + ClonePath.

- [ ] **Step 1: Add using directive**

Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` at the top.

- [ ] **Step 2: Update MergeEndToStart — no reversal**

After merged `PathWithMetadata` creation (around line 483), add:

```csharp
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path1.LaneSegments, path2.LaneSegments, path1.Points.Count - 1);
```

- [ ] **Step 3: Update MergeEndToEnd — reverse path2**

```csharp
var reversedSegs = LaneSegmentOps.ReverseSegments(path2.LaneSegments, path2.Points.Count);
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path1.LaneSegments, reversedSegs, path1.Points.Count - 1);
```

- [ ] **Step 4: Update MergeStartToEnd — path2 first, no reversal**

```csharp
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    path2.LaneSegments, path1.LaneSegments, path2.Points.Count - 1);
```

- [ ] **Step 5: Update MergeStartToStart — reverse path2, path2 first**

```csharp
var reversedSegs = LaneSegmentOps.ReverseSegments(path2.LaneSegments, path2.Points.Count);
merged.LaneSegments = LaneSegmentOps.MergeSegments(
    reversedSegs, path1.LaneSegments, path2.Points.Count - 1);
```

- [ ] **Step 6: Update ClonePath() to deep-copy LaneSegments**

```csharp
clone.LaneSegments = path.LaneSegments
    .Select(s => new LaneSegment { StartPointIndex = s.StartPointIndex, LaneInfo = s.LaneInfo })
    .ToList();
```

- [ ] **Step 7: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs
git commit -m "feat: propagate LaneSegments through NodeBasedPathConnector merge methods"
```

---

### Task 6: Propagate LaneSegments to RoadSpline + ParameterizedRoadSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

- [ ] **Step 1: Add LaneSegments to RoadSpline**

In `RoadSpline.cs`, add after existing properties:

```csharp
public List<LaneSegment>? LaneSegments { get; set; }
```

Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` at the top.

- [ ] **Step 2: Add LaneSegments to ParameterizedRoadSpline, remove OsmTags**

In `ParameterizedRoadSpline.cs`:
- Add property: `public List<LaneSegment>? LaneSegments { get; init; }`
- Remove (or mark `[Obsolete]`) the existing `Dictionary<string, string>? OsmTags` property.
- Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` at the top.

- [ ] **Step 3: Convert StartPointIndex → StartDistance in OsmGeometryProcessor**

In `OsmGeometryProcessor.ConvertLinesToSplines()`, after the `RoadSpline` is created from a `PathWithMetadata` (around lines 861-870 for structures and 905-916 for regular), propagate lane segments with distance conversion:

```csharp
if (pm.LaneSegments.Count > 0)
{
    // Convert StartPointIndex to StartDistance (cumulative Euclidean distance)
    var distances = new float[pm.Points.Count];
    distances[0] = 0f;
    for (int d = 1; d < pm.Points.Count; d++)
        distances[d] = distances[d - 1] + Vector2.Distance(pm.Points[d - 1], pm.Points[d]);

    spline.LaneSegments = pm.LaneSegments.Select(seg => new LaneSegment
    {
        StartPointIndex = seg.StartPointIndex,
        StartDistance = seg.StartPointIndex < distances.Length
            ? distances[seg.StartPointIndex]
            : distances[^1],
        LaneInfo = seg.LaneInfo
    }).ToList();
}
```

This must be done at **both** places where `RoadSpline` objects are created from `PathWithMetadata` — once for structure splines and once for regular (merged) splines.

- [ ] **Step 4: Propagate in UnifiedRoadNetworkBuilder.BuildNetwork()**

In `UnifiedRoadNetworkBuilder.cs`, where `ParameterizedRoadSpline` is constructed (around line 97-112), add:

```csharp
LaneSegments = spline.LaneSegments,
```

to the object initializer.

- [ ] **Step 5: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded. If `OsmTags` was removed and there are references in `DecalRoadGenerator.cs` or `RoadCorridorBuilder.cs`, those will be fixed in Task 7.

- [ ] **Step 6: Fix any OsmTags references that broke**

If `OsmTags` was removed and `DecalRoadGenerator.GetLaneCount()` or `RoadCorridorBuilder.GetLaneCount()` reference it, temporarily replace them with `return layerSet.DefaultLaneCount;` (they'll be properly rewritten in Task 7).

- [ ] **Step 7: Build to verify clean compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs \
        BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs \
        BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs \
        BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs \
        BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs \
        BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
git commit -m "feat: propagate LaneSegments through RoadSpline → ParameterizedRoadSpline pipeline"
```

---

## Chunk 3: Lane-Aware DecalRoad Generation

### Task 7: Update DecalRoadGenerator for Lane-Aware Generation

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs`

This is the largest task. The core change: `GenerateForSpline()` must handle lane-dependent layers differently from lane-independent layers. Lane-dependent layers are split at lane-change boundaries.

- [ ] **Step 1: Write failing tests for lane-aware generation**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadGeneratorLaneTests
{
    // --- Helper to resolve the active lane segment for a given distance ---

    [Fact]
    public void ResolveLaneSegment_SingleSegment_AlwaysReturns()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 4, LanesForward = 2, LanesBackward = 2 } }
        };

        var info = DecalRoadGenerator.ResolveLaneInfo(segments, 500f);

        Assert.Equal(4, info.TotalLanes);
        Assert.Equal(2, info.LanesForward);
    }

    [Fact]
    public void ResolveLaneSegment_MultipleSegments_ReturnsCorrectForDistance()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 } },
            new() { StartDistance = 200f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 3, LanesForward = 2, LanesBackward = 1 } },
            new() { StartDistance = 500f, LaneInfo = new OsmLaneInfo
                { TotalLanes = 2, LanesForward = 1, LanesBackward = 1 } }
        };

        // Before first boundary
        Assert.Equal(2, DecalRoadGenerator.ResolveLaneInfo(segments, 100f).TotalLanes);
        // At second segment
        Assert.Equal(3, DecalRoadGenerator.ResolveLaneInfo(segments, 200f).TotalLanes);
        Assert.Equal(3, DecalRoadGenerator.ResolveLaneInfo(segments, 400f).TotalLanes);
        // At third segment
        Assert.Equal(2, DecalRoadGenerator.ResolveLaneInfo(segments, 600f).TotalLanes);
    }

    // --- AI road property derivation ---

    [Fact]
    public void DeriveAIRoadProperties_TwoWay_CorrectMapping()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 4, LanesForward = 2, LanesBackward = 2, IsOneWay = false };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(2, lanesRight);   // forward = right
        Assert.Equal(2, lanesLeft);    // backward = left
        Assert.False(oneWay);
        Assert.False(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_OneWayForward()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 3, LanesForward = 3, LanesBackward = 0, IsOneWay = true };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(3, lanesRight);
        Assert.Equal(0, lanesLeft);
        Assert.True(oneWay);
        Assert.False(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_OneWayReverse_FlipDirection()
    {
        var info = new OsmLaneInfo
            { TotalLanes = 2, LanesForward = 0, LanesBackward = 2, IsOneWay = true };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        Assert.Equal(0, lanesRight);
        Assert.Equal(2, lanesLeft);
        Assert.True(oneWay);
        Assert.True(flipDirection);
    }

    [Fact]
    public void DeriveAIRoadProperties_LanesBothWays_AddedToForward()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 3, LanesForward = 1, LanesBackward = 1,
            LanesBothWays = 1, IsOneWay = false
        };

        var (lanesRight, lanesLeft, oneWay, flipDirection) =
            DecalRoadGenerator.DeriveAIRoadProperties(info);

        // LanesBothWays added to forward (right) for AI purposes
        Assert.Equal(2, lanesRight);  // 1 forward + 1 bothways
        Assert.Equal(1, lanesLeft);
    }

    // --- Lane-change boundary detection ---

    [Fact]
    public void FindLaneChangeBoundaryIndices_NoSegments_ReturnsEmpty()
    {
        var result = DecalRoadGenerator.FindLaneChangeBoundaryIndices(
            null, new List<float>());
        Assert.Empty(result);
    }

    [Fact]
    public void FindLaneChangeBoundaryIndices_SingleSegment_ReturnsEmpty()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } }
        };
        var distances = Enumerable.Range(0, 100).Select(i => i * 5f).ToList();

        var result = DecalRoadGenerator.FindLaneChangeBoundaryIndices(segments, distances);
        Assert.Empty(result);
    }

    [Fact]
    public void FindLaneChangeBoundaryIndices_TwoSegments_FindsBoundary()
    {
        var segments = new List<LaneSegment>
        {
            new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } },
            new() { StartDistance = 200f, LaneInfo = new OsmLaneInfo { TotalLanes = 3 } }
        };
        // Cross-sections every 5m from 0 to 495
        var distances = Enumerable.Range(0, 100).Select(i => i * 5f).ToList();

        var boundaries = DecalRoadGenerator.FindLaneChangeBoundaryIndices(
            segments, distances);

        Assert.Single(boundaries);
        // Boundary at cross-section index 40 (distance 200m)
        Assert.Equal(40, boundaries[0]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~DecalRoadGeneratorLaneTests" --no-build 2>&1 | head -5`
Expected: Build error — new methods don't exist yet.

- [ ] **Step 3: Add ResolveLaneInfo static method to DecalRoadGenerator**

Add to `DecalRoadGenerator.cs`:

```csharp
/// <summary>
/// Returns the OsmLaneInfo active at the given distance along the spline.
/// Segments are assumed sorted ascending by StartDistance.
/// </summary>
public static OsmLaneInfo ResolveLaneInfo(
    IReadOnlyList<LaneSegment> segments, float distance)
{
    // Walk backwards from end to find the last segment with StartDistance <= distance
    for (int i = segments.Count - 1; i >= 0; i--)
    {
        if (segments[i].StartDistance <= distance)
            return segments[i].LaneInfo;
    }
    return segments[0].LaneInfo;
}
```

- [ ] **Step 4: Add DeriveAIRoadProperties static method**

```csharp
/// <summary>
/// Derives BeamNG AI road properties from OsmLaneInfo.
/// lanesRight = forward direction, lanesLeft = backward direction.
/// LanesBothWays added to forward for AI pathfinding purposes.
/// </summary>
public static (int LanesRight, int LanesLeft, bool OneWay, bool FlipDirection)
    DeriveAIRoadProperties(OsmLaneInfo info)
{
    var lanesRight = info.LanesForward + info.LanesBothWays;
    var lanesLeft = info.LanesBackward;
    var oneWay = info.IsOneWay;
    var flipDirection = info.IsOneWay && info.LanesForward == 0 && info.LanesBackward > 0;

    return (lanesRight, lanesLeft, oneWay, flipDirection);
}
```

- [ ] **Step 5: Add FindLaneChangeBoundaryIndices static method**

```csharp
/// <summary>
/// Returns cross-section indices where lane configuration changes.
/// Used to split lane-dependent layers at lane-change boundaries.
/// </summary>
public static List<int> FindLaneChangeBoundaryIndices(
    IReadOnlyList<LaneSegment>? segments,
    IReadOnlyList<float> crossSectionDistances)
{
    if (segments == null || segments.Count <= 1)
        return [];

    var boundaries = new List<int>();
    // For each segment boundary (skip first), find the nearest cross-section
    for (int s = 1; s < segments.Count; s++)
    {
        var boundaryDist = segments[s].StartDistance;
        // Binary search or linear scan for nearest cross-section
        int bestIdx = 0;
        float bestDelta = float.MaxValue;
        for (int i = 0; i < crossSectionDistances.Count; i++)
        {
            var delta = MathF.Abs(crossSectionDistances[i] - boundaryDist);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIdx = i;
            }
        }
        // Avoid duplicate boundaries and out-of-range
        if (bestIdx > 0 && bestIdx < crossSectionDistances.Count - 1)
        {
            if (boundaries.Count == 0 || boundaries[^1] != bestIdx)
                boundaries.Add(bestIdx);
        }
    }

    return boundaries;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~DecalRoadGeneratorLaneTests" -v minimal`
Expected: All 8 tests pass.

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs \
        BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs
git commit -m "feat: add lane resolution, AI property derivation, and boundary detection"
```

---

### Task 8: Integrate Lane-Aware Splitting into GenerateForSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

This modifies the core `GenerateForSpline()` method to route lane-dependent layers through per-segment splitting, while lane-independent layers render as before.

- [ ] **Step 1: Replace GetLaneCount with lane-segment-aware version**

Replace the existing `GetLaneCount()` method (lines 441-451) with:

```csharp
/// <summary>
/// Returns the effective lane count for a spline. When lane segments exist,
/// returns the TotalLanes of the first segment (used for lane-independent layers).
/// </summary>
private static int GetDefaultLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
{
    if (spline.LaneSegments != null && spline.LaneSegments.Count > 0)
        return spline.LaneSegments[0].LaneInfo.TotalLanes;
    return layerSet.DefaultLaneCount;
}
```

Update the call site in `GenerateForSpline()` from `GetLaneCount(spline, layerSet)` to `GetDefaultLaneCount(spline, layerSet)`.

- [ ] **Step 2: Compute cross-section distances for boundary detection**

In `GenerateForSpline()`, after `sampledSections` is computed (around line 90), add:

```csharp
// Compute cumulative distances along sampled cross-sections for lane boundary detection
var csDistances = new List<float>(sampledSections.Count);
if (sampledSections.Count > 0)
{
    csDistances.Add(0f);
    for (int i = 1; i < sampledSections.Count; i++)
        csDistances.Add(csDistances[i - 1] +
            Vector2.Distance(sampledSections[i - 1].CenterPoint, sampledSections[i].CenterPoint));
}
```

Also compute lane-change boundary indices:

```csharp
var laneChangeBoundaries = FindLaneChangeBoundaryIndices(spline.LaneSegments, csDistances);
```

- [ ] **Step 3: Add helper to check if a layer is lane-dependent**

```csharp
private static bool IsLaneDependent(DecalRoadLayerDefinition layer)
{
    return layer.IsPerLane
        || layer.LayerType == DecalRoadLayerType.AIRoad
        || layer.LayerType == DecalRoadLayerType.CenterLine;
}
```

- [ ] **Step 4: Modify the per-layer loop in GenerateForSpline**

The current loop iterates over `expandedLayers` and processes all cross-sections uniformly. The change: for lane-dependent layers with lane-change boundaries, split processing into sub-ranges.

Replace the existing `foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)` loop body. The key change is that when `laneChangeBoundaries` is non-empty and the layer is lane-dependent, instead of processing all `sampledSections` at once, we partition them at boundaries and process each partition separately.

The high-level structure becomes:

```csharp
foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)
{
    if (!layer.IsEnabled) continue;

    if (IsLaneDependent(layer) && laneChangeBoundaries.Count > 0 && spline.LaneSegments != null)
    {
        // Split into sub-ranges at lane-change boundaries
        var rangeStarts = new List<int> { 0 };
        rangeStarts.AddRange(laneChangeBoundaries);
        var rangeEnds = new List<int>(laneChangeBoundaries);
        rangeEnds.Add(sampledSections.Count);

        for (int r = 0; r < rangeStarts.Count; r++)
        {
            var rangeStart = rangeStarts[r];
            var rangeEnd = rangeEnds[r];
            if (rangeEnd - rangeStart < 2) continue;

            var rangeSections = sampledSections.GetRange(rangeStart, rangeEnd - rangeStart);
            var rangeDist = csDistances[rangeStart];
            var segInfo = ResolveLaneInfo(spline.LaneSegments, rangeDist);

            // Re-expand this layer for the segment-specific lane count
            var segLaneCount = segInfo.TotalLanes;

            // For AI road layers, override properties from segment
            // For IsPerLane layers, recompute boundaries for segment lane count
            // Generate DecalRoad for this sub-range using existing node-building logic
            // (extract to helper method to avoid duplication)

            GenerateForLayerRange(
                layer, position, side, laneIndex, isFlipped,
                rangeSections, segInfo, segLaneCount,
                spline, roadWidth, splineName,
                corridors, junctionZones, continuityLookup,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                ref chunkIndex, results);
        }
    }
    else
    {
        // Lane-independent or no lane changes: process all sections as before
        GenerateForLayerRange(
            layer, position, side, laneIndex, isFlipped,
            sampledSections, null, laneCount,
            spline, roadWidth, splineName,
            corridors, junctionZones, continuityLookup,
            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
            ref chunkIndex, results);
    }
}
```

- [ ] **Step 5: Extract GenerateForLayerRange helper and handle IsPerLane re-expansion**

Extract the existing per-layer processing (lateral offset → corridor check → world coord conversion → chunking → GeneratedDecalRoad creation) into a `GenerateForLayerRange` private method. This avoids duplicating the ~40-line body between the lane-dependent split path and the lane-independent path.

**GenerateForLayerRange signature:**

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
    ref int chunkIndex, List<GeneratedDecalRoad> results)
```

The body is the existing code from the `foreach (var (layer, ...) in expandedLayers)` loop — everything from `float nodeWidth` through the `chunks` loop that creates `GeneratedDecalRoad` objects. Move it verbatim into `GenerateForLayerRange`, then add the AI road override at the end:

```csharp
// After creating the GeneratedDecalRoad (inside the chunks loop):
if (layer.LayerType == DecalRoadLayerType.AIRoad && segInfo != null)
{
    var (lanesRight, lanesLeft, oneWay, flipDirection) = DeriveAIRoadProperties(segInfo);
    road.LanesRight = lanesRight;
    road.LanesLeft = lanesLeft;
    road.OneWay = oneWay;
    road.FlipDirection = flipDirection;
}
```

**IsPerLane re-expansion for lane-dependent split path:**

`IsPerLane` layers need different numbers of boundary copies per segment (e.g., 2 lanes → 1 boundary, 3 lanes → 2 boundaries). The initial `ExpandLayers()` call used the global `laneCount`, so pre-expanded IsPerLane entries have the wrong count for segments with different lane counts.

Solution: split the outer loop into two phases when `laneChangeBoundaries.Count > 0`:

```csharp
bool hasLaneChanges = laneChangeBoundaries.Count > 0 && spline.LaneSegments != null;

// Phase A: non-IsPerLane layers use expandedLayers as normal
foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)
{
    if (!layer.IsEnabled) continue;

    // Skip IsPerLane in this phase when lane changes exist — handled in Phase B
    if (hasLaneChanges && layer.IsPerLane) continue;

    if (IsLaneDependent(layer) && hasLaneChanges)
    {
        // Split at boundaries (AI road, CenterLine — not IsPerLane)
        for (int r = 0; r < rangeStarts.Count; r++)
        {
            // ... sub-range logic as in Step 4 ...
            GenerateForLayerRange(layer, position, side, laneIndex, isFlipped,
                rangeSections, segInfo, segLaneCount, ...);
        }
    }
    else
    {
        // Lane-independent or no lane changes
        GenerateForLayerRange(layer, position, side, laneIndex, isFlipped,
            sampledSections, null, laneCount, ...);
    }
}

// Phase B: IsPerLane layers — re-expand per range with segment-specific lane count
if (hasLaneChanges)
{
    var perLaneLayers = layerSet.Layers.Where(l => l.IsPerLane && l.IsEnabled).ToList();
    for (int r = 0; r < rangeStarts.Count; r++)
    {
        var rangeStart = rangeStarts[r];
        var rangeEnd = rangeEnds[r];
        if (rangeEnd - rangeStart < 2) continue;

        var rangeSections = sampledSections.GetRange(rangeStart, rangeEnd - rangeStart);
        var rangeDist = csDistances[rangeStart];
        var segInfo = ResolveLaneInfo(spline.LaneSegments!, rangeDist);
        var segLaneCount = segInfo.TotalLanes;

        // Re-expand with segment-specific lane count
        var segExpanded = ExpandLayers(perLaneLayers, segLaneCount);
        foreach (var (layer, position, side, laneIndex, isFlipped) in segExpanded)
        {
            GenerateForLayerRange(layer, position, side, laneIndex, isFlipped,
                rangeSections, segInfo, segLaneCount, ...);
        }
    }
}
```

This ensures that a 2-lane section gets 1 lane-boundary marking while a 3-lane section gets 2, each generated only for its sub-range of cross-sections.

- [ ] **Step 6: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -5`
Expected: Build succeeded.

- [ ] **Step 7: Run all existing tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All existing tests still pass.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: lane-aware DecalRoad generation with per-segment splitting"
```

---

### Task 9: Update RoadCorridorBuilder.GetLaneCount

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`

- [ ] **Step 1: Replace GetLaneCount to use max lane count across segments**

Replace the existing `GetLaneCount()` method (lines 119-126) with:

```csharp
private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
{
    if (spline.LaneSegments != null && spline.LaneSegments.Count > 0)
        return spline.LaneSegments.Max(s => s.LaneInfo.TotalLanes);
    return layerSet.DefaultLaneCount;
}
```

Add `using BeamNgTerrainPoc.Terrain.Models.DecalRoad;` if not already present (it should be via existing `DecalRoadLayerSet` usage, but the `LaneSegment` type needs the using).

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 3: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
git commit -m "feat: RoadCorridorBuilder uses max lane count across LaneSegments"
```

---

## Chunk 4: Roundabout + Cleanup

### Task 10: Roundabout Lane Parsing + ConnectingRoadTrimmer Compatibility

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`

- [ ] **Step 1: Verify roundabout lane parsing**

Roundabouts go through `ConvertLinesToSplinesWithRoundabouts()` which creates `PathWithMetadata` for roundabout rings directly. Check that roundabout `OsmFeature.Tags` are parsed the same way as regular roads. If the roundabout ring `PathWithMetadata` is created in a different code path than regular roads, add the same lane parsing:

```csharp
var laneInfo = OsmLaneInfo.TryParse(feature.Tags);
if (laneInfo != null)
{
    roundaboutPath.LaneSegments = [new LaneSegment { StartPointIndex = 0, LaneInfo = laneInfo }];
}
```

- [ ] **Step 2: Verify ConnectingRoadTrimmer compatibility**

`ConnectingRoadTrimmer` operates on `OsmFeature.Coordinates` (geographic coordinates) **before** `PathWithMetadata` is created. The trimming happens before the feature → PathWithMetadata conversion. Therefore, `LaneSegments` (which are single-segment at creation time, always starting at index 0) are unaffected by trimming — the segment always starts at 0 and covers the entire (trimmed) path. No code changes needed.

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs
git commit -m "feat: parse lane info for roundabout rings"
```

---

### Task 11: Remove Obsolete OsmTags References

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`
- Possibly modify: any other files referencing `OsmTags`

- [ ] **Step 1: Search for remaining OsmTags references**

Run: `grep -rn "OsmTags" BeamNgTerrainPoc/ --include="*.cs"` to find any remaining references.

- [ ] **Step 2: Remove all OsmTags references**

- Remove the `OsmTags` property from `ParameterizedRoadSpline.cs` if not already done in Task 6.
- Remove any remaining `GetLaneCount` methods that reference `OsmTags`.
- Verify no other files reference it.

- [ ] **Step 3: Build to verify clean compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v minimal 2>&1 | tail -3`
Expected: Build succeeded.

- [ ] **Step 4: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add -u
git commit -m "refactor: remove obsolete OsmTags property, replaced by LaneSegments"
```

---

## Chunk 5: Integration Tests + Final Verification

### Task 12: Add Integration-Style Tests for Full Pipeline

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs`

Add tests that verify the full behavior described in the spec's testing strategy.

- [ ] **Step 1: Add test for no-split case (uniform lanes)**

```csharp
[Fact]
public void NoLaneSegments_FallsBackToDefaultLaneCount()
{
    // When LaneSegments is null, generation should use DecalRoadLayerSet.DefaultLaneCount
    // This is verified through GetDefaultLaneCount behavior
    var layerSet = new DecalRoadLayerSet { DefaultLaneCount = 2 };
    var spline = CreateTestSpline(laneSegments: null);

    // GetDefaultLaneCount should return 2
    // (Test indirectly via the expanded layer count)
    var layers = new List<DecalRoadLayerDefinition>
    {
        new() { Name = "lane_mark", IsPerLane = true, IsEnabled = true,
                Material = "test", LayerType = DecalRoadLayerType.LaneMarking }
    };

    var expanded = DecalRoadGenerator.ExpandLayers(layers, laneCount: 2);
    Assert.Single(expanded); // 2 lanes → 1 boundary
}
```

- [ ] **Step 2: Add test verifying lane-independent layers are NOT split**

```csharp
[Fact]
public void LaneIndependentLayers_NotSplitAtBoundaries()
{
    // Edge lines and edge blends should NOT be split even when lane segments change
    var segments = new List<LaneSegment>
    {
        new() { StartDistance = 0f, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } },
        new() { StartDistance = 200f, LaneInfo = new OsmLaneInfo { TotalLanes = 3 } }
    };

    var edgeLayer = new DecalRoadLayerDefinition
    {
        Name = "edge", LayerType = DecalRoadLayerType.EdgeLine,
        IsPerLane = false, IsEnabled = true, Material = "test"
    };

    // Edge line is NOT lane-dependent
    Assert.False(edgeLayer.IsPerLane);
    Assert.NotEqual(DecalRoadLayerType.AIRoad, edgeLayer.LayerType);
    Assert.NotEqual(DecalRoadLayerType.CenterLine, edgeLayer.LayerType);
}
```

- [ ] **Step 3: Add test for flipDirection derivation**

```csharp
[Fact]
public void DeriveAIRoadProperties_AsymmetricLanes()
{
    var info = new OsmLaneInfo
        { TotalLanes = 5, LanesForward = 3, LanesBackward = 2, IsOneWay = false };

    var (lanesRight, lanesLeft, oneWay, flipDirection) =
        DecalRoadGenerator.DeriveAIRoadProperties(info);

    Assert.Equal(3, lanesRight);
    Assert.Equal(2, lanesLeft);
    Assert.False(oneWay);
    Assert.False(flipDirection);
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorLaneTests.cs
git commit -m "test: add integration-style tests for lane-aware DecalRoad generation"
```

---

### Task 13: Final Build + Full Test Suite

- [ ] **Step 1: Full solution build**

Run: `dotnet build -v minimal 2>&1 | tail -5`
Expected: Build succeeded with 0 errors.

- [ ] **Step 2: Run complete test suite**

Run: `dotnet test -v minimal`
Expected: All tests pass.

- [ ] **Step 3: Review all changes**

Run: `git diff develop --stat` to review the scope of changes.

Verify:
- New files: `OsmLaneInfo.cs`, `LaneSegment.cs`, `LaneSegmentOps.cs`, 3 test files
- Modified files: `PathWithMetadata.cs`, `OsmGeometryProcessor.cs`, `RouteRelationAssembler.cs`, `NodeBasedPathConnector.cs`, `RoadSpline.cs`, `ParameterizedRoadSpline.cs`, `UnifiedRoadNetworkBuilder.cs`, `DecalRoadGenerator.cs`, `RoadCorridorBuilder.cs`
- No unintended changes

- [ ] **Step 4: Final commit if any uncommitted changes remain**

```bash
git status
# If clean, skip. Otherwise:
git add -u
git commit -m "chore: final cleanup for OSM dynamic lanes feature"
```
