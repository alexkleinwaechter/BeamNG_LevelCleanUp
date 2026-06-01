# Roundabout DecalRoad Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix DecalRoad junction interruption at roundabouts so markings are cleanly suppressed where roads meet the ring, and make roundabout AI roads one-way with correct lane configuration.

**Architecture:** Three bugs prevent corridor-based suppression from working at roundabouts: (1) junction influence zones are too small per-connection instead of ring-wide, (2) closed-loop corridors have a gap at the wrap seam, (3) continuity lookup incorrectly exempts roundabout rings from suppression. Additionally, roundabout AI roads must be one-way with OSM lane data or sensible defaults.

**Tech Stack:** .NET 9, C#, System.Numerics (Vector2), xUnit

**Skills:** @beamng-decalroad-format, @beamng-decalroad-generation, @beamng-road-layers

---

## File Structure

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs` | Add `IsClosedLoop` flag |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | Set `IsClosedLoop` for roundabout splines |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs` | Handle closed-loop wrap-around in bracket check; add roundabout-wide influence zone builder |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Exclude roundabout rings from continuity lookup; override AI road properties for roundabout splines |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | Add `"roundabout"` layer set entry |
| `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs` | Add closed-loop corridor tests and roundabout influence zone tests |

---

## Task 1: Fix Roundabout Junction Influence Zones and Corridor Overlap

This task fixes the three bugs that prevent corridor suppression from working at roundabouts.

### Sub-task 1a: Add `IsClosedLoop` to `RoadCorridor`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs`

- [ ] **Step 1: Add IsClosedLoop property to RoadCorridor**

In `RoadCorridor.cs`, add a flag so the overlap checker knows to bridge the seam between last and first section:

```csharp
public class RoadCorridor
{
    public required int SplineId { get; init; }
    public required float RoadWidth { get; init; }
    public required float CorridorHalfWidth { get; init; }
    public required List<CorridorSection> Sections { get; init; }

    /// <summary>
    /// Whether this corridor forms a closed loop (e.g., roundabout ring).
    /// When true, the overlap checker bridges the gap between the last and first sections.
    /// </summary>
    public bool IsClosedLoop { get; init; }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

---

### Sub-task 1b: Set `IsClosedLoop` in `RoadCorridorBuilder`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs:26-63`

- [ ] **Step 1: Add roundabout-first layer set resolution in BuildCorridors**

In `BuildCorridors()`, the layer set resolution at line 31 uses `spline.OsmRoadType` directly. For roundabout ring splines, `OsmRoadType` is the underlying road type (e.g., "primary"), not "roundabout". This must match the resolution logic in `DecalRoadGenerator.Generate()` (sub-task 2c) to avoid corridor width mismatches.

Replace the resolver call at line 31-33 with:

```csharp
DecalRoadLayerSet? layerSet;
if (spline.IsRoundabout)
{
    layerSet = DecalRoadLayerSetResolver.Resolve(
        "roundabout", spline.MaterialName, settings, appDataDefaults);
    layerSet ??= DecalRoadLayerSetResolver.Resolve(
        spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
}
else
{
    layerSet = DecalRoadLayerSetResolver.Resolve(
        spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
}
if (layerSet == null || !layerSet.IsEnabled)
    continue;
```

- [ ] **Step 2: Set IsClosedLoop on the corridor**

When constructing the `RoadCorridor` (line 53), add the `IsClosedLoop` property:

```csharp
corridors[spline.SplineId] = new RoadCorridor
{
    SplineId = spline.SplineId,
    RoadWidth = roadWidth,
    CorridorHalfWidth = corridorHalfWidth,
    Sections = sections,
    IsClosedLoop = spline.IsRoundabout
};
```

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

---

### Sub-task 1c: Handle closed-loop wrap-around in corridor overlap checker

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs:27-59`

- [ ] **Step 1: Write failing test for closed-loop corridor**

Add to `BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs`:

```csharp
/// <summary>
/// Creates a circular corridor (closed loop) centered at origin with given radius.
/// Sections are sampled every ~spacing degrees around the circle.
/// Normal points outward (radially away from center).
/// </summary>
private static RoadCorridor CreateCircularCorridor(
    int splineId, float halfWidth, float radius = 30f, int sectionCount = 24)
{
    var sections = new List<CorridorSection>();
    for (int i = 0; i < sectionCount; i++)
    {
        float angle = 2f * MathF.PI * i / sectionCount;
        var center = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        var normal = Vector2.Normalize(center); // Points outward
        sections.Add(new CorridorSection(center, normal, i * (2f * MathF.PI * radius / sectionCount)));
    }
    return new RoadCorridor
    {
        SplineId = splineId,
        RoadWidth = halfWidth * 2,
        CorridorHalfWidth = halfWidth,
        Sections = sections,
        IsClosedLoop = true
    };
}

[Fact]
public void ClosedLoopCorridor_PointNearWrapSeam_ReturnsOverlapping()
{
    // Circular corridor with radius=30m, halfWidth=5m, 24 sections
    // Section 0 is at angle=0 (30,0), section 23 is at angle=345°
    // A point between section 23 and section 0 should still be detected
    var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f, sectionCount: 24);

    // Point on the ring between section 23 and section 0 at angle ~352.5°
    float testAngle = (23.5f / 24f) * 2f * MathF.PI;
    var testPoint = new Vector2(MathF.Cos(testAngle) * 30f, MathF.Sin(testAngle) * 30f);

    var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(testPoint, corridor);
    Assert.True(result.IsOverlapping);
}

[Fact]
public void ClosedLoopCorridor_PointInsideRing_ReturnsOverlapping()
{
    var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f);

    // Point at angle=90° (top), slightly inside the ring (radius=27m, inside 30±5)
    var testPoint = new Vector2(0, 27f);
    var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(testPoint, corridor);
    Assert.True(result.IsOverlapping);
}

[Fact]
public void ClosedLoopCorridor_PointFarOutside_ReturnsNotOverlapping()
{
    var corridor = CreateCircularCorridor(splineId: 1, halfWidth: 5f, radius: 30f);

    // Point at center of ring (0,0) — way outside the corridor (30 - 5 = 25m from closest section)
    var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(new Vector2(0, 0), corridor);
    Assert.False(result.IsOverlapping);
}

[Fact]
public void NonClosedLoop_PointNearEnd_ReturnsNotOverlapping()
{
    // Verify that non-closed-loop corridors still reject points past the end
    var corridor = CreateStraightCorridor(splineId: 1, halfWidth: 5f, length: 100f);
    var result = RoadCorridorOverlapChecker.CheckPointAgainstCorridor(new Vector2(110, 0), corridor);
    Assert.False(result.IsOverlapping);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~ClosedLoop" -v n`
Expected: `ClosedLoopCorridor_PointNearWrapSeam_ReturnsOverlapping` FAILS (the others may pass or fail)

- [ ] **Step 3: Add wrap-around check in CheckPointAgainstCorridor**

In `RoadCorridorOverlapChecker.cs`, modify `CheckPointAgainstCorridor` to add a wrap-around bracket check when the corridor is a closed loop. After the existing bracket checks (line 48-56), add the wrap-around check before the final return:

```csharp
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

    // Step 2: Check bracketing pairs around closest section
    if (closestIdx > 0 &&
        TryBracketCheck(point, sections[closestIdx - 1], sections[closestIdx],
            corridor.CorridorHalfWidth))
        return new OverlapResult(true, corridor.SplineId);

    if (closestIdx < sections.Count - 1 &&
        TryBracketCheck(point, sections[closestIdx], sections[closestIdx + 1],
            corridor.CorridorHalfWidth))
        return new OverlapResult(true, corridor.SplineId);

    // Step 3: Closed-loop wrap-around — bridge last↔first section gap
    if (corridor.IsClosedLoop)
    {
        int last = sections.Count - 1;
        // If closest is first or last, check the wrap pair
        if (closestIdx == 0 &&
            TryBracketCheck(point, sections[last], sections[0], corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);

        if (closestIdx == last &&
            TryBracketCheck(point, sections[last], sections[0], corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);
    }

    return new OverlapResult(false, null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorOverlapChecker" -v n`
Expected: All tests PASS (existing + new)

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/RoadCorridor.cs
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs
git commit -m "fix: handle closed-loop corridor wrap-around for roundabout overlap checking"
```

---

### Sub-task 1d: Add roundabout-wide junction influence zones

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs:138-173`

The current `BuildJunctionInfluenceZones` creates one small zone per `JunctionType.Roundabout` junction (each positioned at a connection point on the ring). This is too small — nodes on the ring far from a connection point won't be checked.

Instead, we need a single large zone per roundabout covering the entire ring + margin. We can detect roundabout groups by finding all `Roundabout`-type junctions that share the same roundabout ring spline ID (they all have the ring as a continuous contributor).

- [ ] **Step 1: Write failing test for roundabout influence zone**

Add to `RoadCorridorOverlapCheckerTests.cs`:

```csharp
/// <summary>
/// Creates a minimal RoadSpline for test construction.
/// ParameterizedRoadSpline requires a RoadSpline (required property).
/// </summary>
private static RoadSpline CreateDummySpline() =>
    new(Enumerable.Range(0, 5).Select(i => new Vector2(i * 10, 0)).ToList());

[Fact]
public void BuildJunctionInfluenceZones_RoundaboutCreatesRingWideZone()
{
    // Simulate a roundabout with 3 connecting roads
    // Ring spline = ID 10, connecting roads = IDs 1, 2, 3
    // Each Roundabout junction has ring (continuous) + connecting road (endpoint)
    var ringSpline = new ParameterizedRoadSpline
    {
        Spline = CreateDummySpline(),
        SplineId = 10, IsRoundabout = true, MaterialName = "Asphalt",
        Parameters = new RoadSmoothingParameters()
    };

    var junctions = new List<NetworkJunction>();
    for (int i = 1; i <= 3; i++)
    {
        float angle = 2f * MathF.PI * i / 3;
        var pos = new Vector2(MathF.Cos(angle) * 30f, MathF.Sin(angle) * 30f);
        var connSpline = new ParameterizedRoadSpline
        {
            Spline = CreateDummySpline(),
            SplineId = i, MaterialName = "Asphalt",
            Parameters = new RoadSmoothingParameters()
        };

        // NetworkJunction.Contributors is a get-only List — must use AddRange after construction
        var junction = new NetworkJunction
        {
            Type = JunctionType.Roundabout,
            Position = pos,
        };
        junction.Contributors.AddRange(new[]
        {
            new JunctionContributor
            {
                Spline = ringSpline,
                CrossSection = new UnifiedCrossSection { OwnerSplineId = 10 }
                // IsContinuous = true (not an endpoint)
            },
            new JunctionContributor
            {
                Spline = connSpline,
                CrossSection = new UnifiedCrossSection { OwnerSplineId = i },
                IsSplineStart = true // IsEndpoint = true
            }
        });
        junctions.Add(junction);
    }

    var corridors = new Dictionary<int, RoadCorridor>
    {
        [10] = CreateCircularCorridor(10, halfWidth: 5f, radius: 30f),
        [1] = CreateStraightCorridor(1, halfWidth: 4f),
        [2] = CreateStraightCorridor(2, halfWidth: 4f),
        [3] = CreateStraightCorridor(3, halfWidth: 4f),
    };

    var zones = RoadCorridorOverlapChecker.BuildJunctionInfluenceZones(junctions, corridors);

    // Should produce ONE roundabout-wide zone (merged from 3 individual junctions)
    // + possibly individual zones, but the roundabout zone must cover the full ring
    var roundaboutZones = zones.Where(z => z.ContributingSplineIds.Contains(10)).ToList();
    Assert.NotEmpty(roundaboutZones);

    // The roundabout zone must include all 4 spline IDs (ring + 3 connecting)
    var allContributors = roundaboutZones.SelectMany(z => z.ContributingSplineIds).Distinct().ToList();
    Assert.Contains(10, allContributors);
    Assert.Contains(1, allContributors);
    Assert.Contains(2, allContributors);
    Assert.Contains(3, allContributors);

    // The zone radius must cover the full ring (>= ring radius + at least one corridor half-width)
    var mainZone = roundaboutZones.OrderByDescending(z => z.Radius).First();
    Assert.True(mainZone.Radius >= 35f,
        $"Roundabout zone radius {mainZone.Radius}m should cover ring radius 30m + corridor padding");
}
```

NOTE: `ParameterizedRoadSpline` has `required RoadSpline Spline` — use `CreateDummySpline()`. `NetworkJunction.Contributors` is a get-only `List` — use `AddRange()` after construction, not collection initializer syntax.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoundaboutCreatesRingWideZone" -v n`
Expected: FAIL

- [ ] **Step 3: Modify BuildJunctionInfluenceZones to create ring-wide zones**

In `RoadCorridorOverlapChecker.cs`, modify `BuildJunctionInfluenceZones` to detect roundabout junction groups and create a single large zone per roundabout:

```csharp
public static List<JunctionInfluenceZone> BuildJunctionInfluenceZones(
    IReadOnlyList<NetworkJunction> junctions,
    IReadOnlyDictionary<int, RoadCorridor> corridors)
{
    var zones = new List<JunctionInfluenceZone>();

    // Group roundabout junctions by their ring spline ID to create one zone per roundabout
    var roundaboutGroups = new Dictionary<int, List<NetworkJunction>>();

    foreach (var junction in junctions)
    {
        if (junction.IsExcluded) continue;
        if (junction.Type == JunctionType.Endpoint) continue;

        if (junction.Type == JunctionType.Roundabout)
        {
            // Find the roundabout ring spline (continuous contributor with IsRoundabout)
            var ringContributor = junction.Contributors
                .FirstOrDefault(c => c.Spline.IsRoundabout);
            if (ringContributor != null)
            {
                var ringId = ringContributor.Spline.SplineId;
                if (!roundaboutGroups.TryGetValue(ringId, out var group))
                {
                    group = [];
                    roundaboutGroups[ringId] = group;
                }
                group.Add(junction);
                continue; // Don't create individual zone — handled below as group
            }
        }

        // Non-roundabout junctions: create individual zone (existing logic)
        var contributingIds = junction.Contributors
            .Select(c => c.Spline.SplineId)
            .Distinct()
            .ToList();

        float maxHalfWidth = 0f;
        foreach (var id in contributingIds)
        {
            if (corridors.TryGetValue(id, out var c))
                maxHalfWidth = MathF.Max(maxHalfWidth, c.CorridorHalfWidth);
        }

        if (maxHalfWidth <= 0f) continue;

        var radius = maxHalfWidth * 2f;
        zones.Add(new JunctionInfluenceZone(
            junction.Position, radius, radius * radius, contributingIds));
    }

    // Create one ring-wide zone per roundabout
    foreach (var (ringSplineId, roundaboutJunctions) in roundaboutGroups)
    {
        // Collect all contributing spline IDs (ring + all connecting roads)
        var allContributors = roundaboutJunctions
            .SelectMany(j => j.Contributors.Select(c => c.Spline.SplineId))
            .Distinct()
            .ToList();

        // Zone center = centroid of all roundabout junction positions
        var center = Vector2.Zero;
        foreach (var j in roundaboutJunctions)
            center += j.Position;
        center /= roundaboutJunctions.Count;

        // Zone radius = distance from center to farthest junction + max corridor half-width
        float maxDistFromCenter = 0f;
        foreach (var j in roundaboutJunctions)
            maxDistFromCenter = MathF.Max(maxDistFromCenter,
                Vector2.Distance(center, j.Position));

        float maxHalfWidth = 0f;
        foreach (var id in allContributors)
        {
            if (corridors.TryGetValue(id, out var c))
                maxHalfWidth = MathF.Max(maxHalfWidth, c.CorridorHalfWidth);
        }

        // Ring radius + max corridor half-width + connecting road corridor reach
        var radius = maxDistFromCenter + maxHalfWidth * 2f;
        zones.Add(new JunctionInfluenceZone(
            center, radius, radius * radius, allContributors));
    }

    return zones;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoadCorridorOverlapChecker" -v n`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorOverlapChecker.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/RoadCorridorOverlapCheckerTests.cs
git commit -m "fix: create ring-wide junction influence zones for roundabouts"
```

---

### Sub-task 1e: Exclude roundabout rings from continuity lookup

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs:852-881`

The `BuildContinuityLookup` method marks the roundabout ring as "continuous" at each roundabout junction, which prevents the ring's own markings from being suppressed by connecting roads' corridors. This is wrong — the ring's edge lines/markings SHOULD be interrupted where connecting roads attach.

- [ ] **Step 1: Modify BuildContinuityLookup to skip roundabout junctions**

In `DecalRoadGenerator.cs`, in `BuildContinuityLookup()`, add a filter to skip `JunctionType.Roundabout` junctions. The continuity exemption is designed for T-junctions where a through-road shouldn't have its center line interrupted — roundabouts are geometrically different:

```csharp
private static Dictionary<int, HashSet<int>> BuildContinuityLookup(
    IReadOnlyList<NetworkJunction> junctions)
{
    var lookup = new Dictionary<int, HashSet<int>>();

    foreach (var junction in junctions)
    {
        if (junction.IsExcluded) continue;
        if (junction.Type == JunctionType.Endpoint) continue;

        // Roundabout rings should NOT get continuity exemptions.
        // Their markings should be suppressed where connecting roads' corridors overlap.
        if (junction.Type == JunctionType.Roundabout) continue;

        var continuousIds = junction.GetContinuousRoads()
            .Select(c => c.Spline.SplineId).ToHashSet();
        var terminatingIds = junction.GetTerminatingRoads()
            .Select(c => c.Spline.SplineId).ToHashSet();

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

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Run all existing tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "fix: exclude roundabout junctions from continuity lookup to allow ring marking suppression"
```

---

## Task 2: Roundabout AI Road One-Way Configuration

Roundabout ring splines must produce one-way AI roads. Lane info should come from OSM data when available. When OSM data is missing, use defaults: `autoLanes = false, lanesLeft = 0, lanesRight = 1, oneWay = true`.

### Sub-task 2a: Add roundabout layer set with one-way AI road defaults

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`

- [ ] **Step 1: Add roundabout entry to GetDefaults**

In `DecalRoadDefaultLayerSets.cs`, add a `"roundabout"` key to the dictionary in `GetDefaults()` (after `"service"`):

```csharp
["roundabout"] = CreateRoundaboutSet("Roundabout", 1),
```

Then add the factory method. Roundabouts get: edge lines, edge blends, and a one-way AI road with `LanesLeft = 0, LanesRight = 1, OneWay = true`. No center line or direction divider (single carriageway loop). Lane markings only if `lanes >= 2`:

```csharp
private static DecalRoadLayerSet CreateRoundaboutSet(string name, int lanes)
{
    var layers = new List<DecalRoadLayerDefinition>
    {
        new()
        {
            Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
            Material = "m_line_white", Width = 0.25f, Position = 1.0f,
            TextureLength = 10.0f, RenderPriority = 6,
            FadeIn = 1.0f, FadeOut = 1.0f,
            IsMirrored = true, InterruptAtJunctions = true,
            ImprovedSpline = true, Detail = 0.1f, Smoothness = 0.5f
        },
        new()
        {
            Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
            Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
            TextureLength = 10.0f, RenderPriority = 7,
            FadeIn = 1.0f, FadeOut = 1.0f,
            IsMirrored = true, InterruptAtJunctions = true,
            ImprovedSpline = true, Detail = 0.2f, Smoothness = 0.5f
        },
        new()
        {
            Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
            Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
            TextureLength = 10.0f, RenderPriority = 8,
            FadeIn = 1.0f, FadeOut = 1.0f,
            IsMirrored = true, InterruptAtJunctions = true,
            ImprovedSpline = true, Detail = 0.2f, Smoothness = 0.5f
        },
        new()
        {
            Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
            Material = "road_invisible", Width = 0, Position = 0.0f,
            IsTrackWidth = true, RenderPriority = 1,
            FadeIn = 1.0f, FadeOut = 1.0f,
            InterruptAtJunctions = false,
            Drivability = 1.0f, LanesLeft = 0, LanesRight = lanes,
            OneWay = true,
            ImprovedSpline = false, Detail = 0.1f, Smoothness = 0.5f
        }
    };

    return new DecalRoadLayerSet
    {
        Name = name, DefaultLaneCount = lanes, Layers = layers
    };
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
git commit -m "feat: add roundabout default layer set with one-way AI road configuration"
```

---

### Sub-task 2b: Override AI road properties for roundabout splines in generator

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

When generating DecalRoads for a spline with `IsRoundabout = true`, the AI road layer must always be one-way. If the spline has `LaneSegments` with `OsmLaneInfo`, use that data (OSM may provide `lanes=2` for multi-lane roundabouts). If not, force `autoLanes = false, lanesLeft = 0, lanesRight = 1, oneWay = true`.

The key modification is in `GenerateForLayerRange` (around line 374-383), where AI road properties are overridden from lane segment data. We add a roundabout-specific override path.

- [ ] **Step 1: Add roundabout AI road override in GenerateForLayerRange**

In `DecalRoadGenerator.cs`, in `GenerateForLayerRange()`, find the block at line 374-383 where AI road properties are overridden from `segInfo`. After that block, add a roundabout override:

```csharp
// Override AI road properties from lane segment data
if (layer.LayerType == DecalRoadLayerType.AIRoad && segInfo != null)
{
    var (lanesRight, lanesLeft, oneWay, flipDirection) = DeriveAIRoadProperties(segInfo);
    road.LanesRight = lanesRight;
    road.LanesLeft = lanesLeft;
    road.OneWay = oneWay;
    road.FlipDirection = flipDirection;
    road.AutoLanes = false;
}

// Roundabout AI road: always one-way, use OSM lanes or default to 1
// Must run AFTER the segInfo override above (it overwrites those values)
if (layer.LayerType == DecalRoadLayerType.AIRoad && spline.IsRoundabout)
{
    road.OneWay = true;
    road.LanesLeft = 0;
    // If we have lane info from OSM, use total lanes as right lanes
    // Otherwise keep the layer default (1 lane)
    if (segInfo != null)
        road.LanesRight = segInfo.TotalLanes;
    // Always disable auto-lane computation — we set lanes explicitly.
    // Without this, BeamNG's auto-lane logic overrides OneWay and LanesLeft at runtime.
    road.AutoLanes = false;
}
```

Note that `spline` is already available as a parameter in `GenerateForLayerRange`. Check the method signature — it receives `ParameterizedRoadSpline spline` (line 261).

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: force one-way AI road properties for roundabout splines"
```

---

### Sub-task 2c: Resolve roundabout layer set via OsmRoadType

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

The layer set resolver cascade uses `spline.OsmRoadType` to look up layer sets. Roundabout ring splines need their `OsmRoadType` to be `"roundabout"` for the resolver to find the roundabout layer set.

Check how `OsmRoadType` is set on roundabout splines. It's set in `UnifiedRoadNetworkBuilder` based on the source OSM feature's `highway` tag. For roundabouts, the OSM `highway` tag is the road type of the roundabout ways (e.g., `primary`, `secondary`), NOT `"roundabout"`. The `junction=roundabout` tag identifies it as a roundabout.

We need the resolver to check `IsRoundabout` on the spline and use the `"roundabout"` layer set when it matches.

- [ ] **Step 1: Add roundabout check before resolver cascade**

In `DecalRoadGenerator.cs`, in the `Generate()` method, in the Pass 2 loop (around line 61-64 where `DecalRoadLayerSetResolver.Resolve` is called), add a roundabout-specific resolution before the cascade:

```csharp
// Resolve layer set — roundabout splines use "roundabout" key
DecalRoadLayerSet? layerSet;
if (spline.IsRoundabout)
{
    // Try "roundabout" key first, then fall back to regular cascade
    layerSet = DecalRoadLayerSetResolver.Resolve(
        "roundabout", spline.MaterialName, settings, appDataDefaults);
    // If no roundabout-specific set found, try the road's own OSM type
    layerSet ??= DecalRoadLayerSetResolver.Resolve(
        spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
}
else
{
    layerSet = DecalRoadLayerSetResolver.Resolve(
        spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
}
if (layerSet == null || !layerSet.IsEnabled)
    continue;
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: resolve roundabout-specific layer set for ring splines"
```

---

## Run All Tests & Final Verification

- [ ] **Step 1: Run all tests**

```bash
dotnet test BeamNgTerrainPoc.Tests -v n
```
Expected: All tests PASS

- [ ] **Step 2: Build entire solution**

```bash
dotnet build
```
Expected: Build succeeded

---

## Summary of Changes

| Problem | Fix | Files |
|---------|-----|-------|
| Junction influence zones too small for roundabouts | Create one ring-wide zone per roundabout, centered on ring center with radius covering entire ring + corridors | `RoadCorridorOverlapChecker.cs` |
| Closed-loop corridor gap at wrap seam | Add `IsClosedLoop` flag + wrap-around bracket check `(S_last, S_0)` | `RoadCorridor.cs`, `RoadCorridorBuilder.cs`, `RoadCorridorOverlapChecker.cs` |
| Corridor builder uses wrong layer set for roundabouts | Add roundabout-first resolution in `BuildCorridors()` to match generator | `RoadCorridorBuilder.cs` |
| Continuity lookup prevents ring marking suppression | Skip `JunctionType.Roundabout` in `BuildContinuityLookup` | `DecalRoadGenerator.cs` |
| Roundabout AI road not one-way | Add `"roundabout"` default layer set with `OneWay=true, LanesLeft=0`; force one-way in generator | `DecalRoadDefaultLayerSets.cs`, `DecalRoadGenerator.cs` |
| Roundabout uses wrong layer set | Resolve `"roundabout"` key before regular cascade for `IsRoundabout` splines | `DecalRoadGenerator.cs` |
