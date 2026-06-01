# DecalRoad Generation Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate DecalRoad scene objects (road markings, edge lines, edge blends) from the existing `UnifiedRoadNetwork` splines during terrain generation, with junction interruption and configurable layer sets.

**Architecture:** DecalRoad generation runs as a post-processing step inside `TerrainCreator.CreateTerrainFileAsync()`, after road smoothing produces the `UnifiedRoadNetwork` and `heightMap2D`. Each `ParameterizedRoadSpline` is processed against its resolved `DecalRoadLayerSet` (cascade: OSM type → material → AppData defaults). Layers are expanded (mirroring, per-lane), sampled along the spline with lateral offsets, interrupted at junction exclusion zones, chunked to ≤100 nodes, and written as NDJSON scene files.

**Tech Stack:** .NET 9, C#, System.Numerics (Vector2/Vector3), System.Text.Json, Grille.BeamNG.Lib (JsonDict, SimItemsJsonSerializer), xUnit (new test project for pure logic)

**Spec:** `docs/superpowers/specs/2026-03-12-decalroad-generation-design.md`

**Skills:** @beamng-decalroad-format, @beamng-decalroad-generation, @beamng-road-layers

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Single layer data model (material, width, position, flags) |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs` | Named collection of layers for a road type |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerType.cs` | Enum: CenterLine, LaneMarking, EdgeLine, EdgeBlend, TreadMarks, AIRoad, Custom |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs` | Top-level settings container (enabled, spacing, margin, layer set dictionaries) |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs` | Output model: one DecalRoad object with name, material, nodes, metadata |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerSetResolver.cs` | Override cascade: OSM type → material → AppData defaults |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Core engine: per-spline × per-layer generation with lateral offsets |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionInterruptionRule.cs` | Per-spline junction rule record + InterruptionSide enum |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterruptionRuleBuilder.cs` | Builds per-spline rules from network junctions |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterrupter.cs` | Rule-based junction-aware segment splitting |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadSceneWriter.cs` | Write NDJSON to MT_decalroads/ hierarchy |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | Hardcoded fallback default layer sets per OSM type |
| `BeamNG_LevelCleanUp/Utils/DecalRoadDefaultsManager.cs` | AppData `decalroad-defaults.json` file management |
| `BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj` | New xUnit test project for terrain logic |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs` | Tests for rule-based junction interruption |
| `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs` | Tests for rule building and side determination |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerSetResolverTests.cs` | Tests for cascade resolution |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorTests.cs` | Tests for lane positions, lateral offsets, chunking |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs` | Add `Dictionary<string,string>? OsmTags` property |
| `BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs` | Add computed `Lanes` property |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs` | Propagate OsmTags from source features to ParameterizedRoadSpline |
| `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs` | Add `DecalRoadSettings?` and output properties for network/heightmap |
| `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` | Add DecalRoad generation step after smoothing, populate output properties |
| `BeamNG_LevelCleanUp/Utils/AppPaths.cs` | Add `DecalRoadDefaultsPath` property |
| `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` | Add DecalRoad enabled flag, cached network/heightmap references |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` | Export `decalRoadSettings` section |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` | Import `decalRoadSettings` section |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs` | Add DecalRoad settings properties |
| `BeamNgTerrainPoc.sln` (solution file) | Add new test project reference |

---

## Chunk 1: Data Models & OSM Tag Propagation

### Task 1: Create DecalRoad data models

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerType.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs`

- [ ] **Step 1: Create DecalRoadLayerType enum**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerType.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public enum DecalRoadLayerType
{
    CenterLine,
    LaneMarking,
    EdgeLine,
    EdgeBlend,
    TreadMarks,
    AIRoad,
    Custom
}
```

- [ ] **Step 2: Create DecalRoadLayerDefinition**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerDefinition
{
    public string Name { get; set; } = string.Empty;
    public DecalRoadLayerType LayerType { get; set; } = DecalRoadLayerType.Custom;
    public bool IsEnabled { get; set; } = true;
    public string Material { get; set; } = string.Empty;
    public float Width { get; set; } = 0.2f;
    public float TextureLength { get; set; } = 10.0f;
    public int RenderPriority { get; set; } = 10;
    public float Position { get; set; } // -1.0 = left edge, 0.0 = center, +1.0 = right edge
    public bool IsTrackWidth { get; set; }
    public bool IsMirrored { get; set; }
    public bool IsPerLane { get; set; }
    public float FadeIn { get; set; }
    public float FadeOut { get; set; }
    public float[] DistanceFade { get; set; } = [1000f, 1500f];
    public bool InterruptAtJunctions { get; set; } = true;

    // AI Road properties (only relevant for LayerType == AIRoad)
    public float Drivability { get; set; } = -1.0f; // -1.0 = non-drivable, 1.0 = AI drivable
    public int LanesLeft { get; set; } = 1;
    public int LanesRight { get; set; } = 1;
    public bool OneWay { get; set; }
    public bool FlipDirection { get; set; }
}
```

- [ ] **Step 3: Create DecalRoadLayerSet**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerSet
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DefaultLaneCount { get; set; } = 2;
    public float DefaultLaneWidth { get; set; } = 3.5f;
    public List<DecalRoadLayerDefinition> Layers { get; set; } = [];
}
```

- [ ] **Step 4: Create DecalRoadSettings**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs
using System.Text.Json.Serialization;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadSettings
{
    public bool Enabled { get; set; }
    public float NodeSpacingMeters { get; set; } = 2.0f;
    public float JunctionExclusionMarginMeters { get; set; } = 5.0f;
    public Dictionary<string, DecalRoadLayerSet> MaterialLayerSets { get; set; } = new();
    public Dictionary<string, DecalRoadLayerSet> OsmLayerSets { get; set; } = new();
}
```

- [ ] **Step 5: Create GeneratedDecalRoad output model**

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// A single generated DecalRoad object ready for scene writing.
/// Each node is [x, y, z, width] in BeamNG world coordinates.
/// </summary>
public class GeneratedDecalRoad
{
    public required string Name { get; init; }
    public required string ParentGroupName { get; init; }
    public required string Material { get; init; }
    public float TextureLength { get; init; } = 10.0f;
    public int RenderPriority { get; init; } = 10;
    public float[] StartEndFade { get; init; } = [0f, 0f];
    public float[] DistanceFade { get; init; } = [1000f, 1500f];
    public float Drivability { get; init; } = -1.0f;
    public required List<float[]> Nodes { get; init; } // Each: [x, y, z, width]
    public Vector3 Position => Nodes.Count > 0
        ? new Vector3(Nodes[0][0], Nodes[0][1], Nodes[0][2])
        : Vector3.Zero;

    // AI Road properties
    public bool IsAIRoad { get; init; }
    public int LanesLeft { get; init; } = 1;
    public int LanesRight { get; init; } = 1;
    public bool OneWay { get; init; }
    public bool FlipDirection { get; init; }
}
```

- [ ] **Step 6: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/
git commit -m "feat: add DecalRoad data models (layer definition, layer set, settings)"
```

---

### Task 2: Add OsmTags propagation

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

- [ ] **Step 1: Add computed Lanes property to OsmFeature**

In `OsmFeature.cs`, add after the existing computed properties (near `IsRoad`, `IsBridge`, etc.):

```csharp
/// <summary>
/// Lane count parsed from OSM "lanes" tag. Returns null if tag is missing or unparseable.
/// </summary>
public int? Lanes => Tags.TryGetValue("lanes", out var val) && int.TryParse(val, out var n) ? n : null;
```

- [ ] **Step 2: Add OsmTags property to ParameterizedRoadSpline**

In `ParameterizedRoadSpline.cs`, add after `OsmRoadType`:

```csharp
/// <summary>
/// Subset of OSM tags relevant to road rendering (lanes, surface, oneway, etc.).
/// Null for PNG-sourced splines.
/// </summary>
public Dictionary<string, string>? OsmTags { get; init; }
```

- [ ] **Step 3: Propagate OsmTags in UnifiedRoadNetworkBuilder**

In `UnifiedRoadNetworkBuilder.cs`, find where `ParameterizedRoadSpline` is constructed (around line 95-130 where `OsmRoadType` is set). Add `OsmTags` to the initializer.

The OSM tags are available through the material's road parameters which carry the original `OsmFeature` data. Find where `osmRoadType` is extracted and add:

```csharp
OsmTags = osmFeature?.Tags?.Where(t =>
    t.Key is "lanes" or "surface" or "oneway" or "maxspeed" or "name" or "ref" or "highway" or "junction")
    .ToDictionary(t => t.Key, t => t.Value),
```

The exact location depends on how `osmRoadType` is derived — follow the same data path. Read the file to find the exact insertion point.

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs
git commit -m "feat: propagate OSM tags (lanes, surface, etc.) to ParameterizedRoadSpline"
```

---

### Task 3: Create xUnit test project

**Files:**
- Create: `BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
- Modify: `BeamNG_LevelCleanUp.sln` (add project to solution)

- [ ] **Step 1: Create test project via dotnet CLI**

```bash
cd c:/SourcesPrivate/beamng_mapping_pro
dotnet new xunit -n BeamNgTerrainPoc.Tests -o BeamNgTerrainPoc.Tests
dotnet sln add BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj
dotnet add BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj reference BeamNgTerrainPoc/BeamNgTerrainPoc.csproj
```

- [ ] **Step 2: Verify test project builds and runs**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj
```
Expected: 0 tests found (empty project), build succeeded

- [ ] **Step 3: Remove auto-generated UnitTest1.cs**

Delete `BeamNgTerrainPoc.Tests/UnitTest1.cs` if it was created.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/ BeamNG_LevelCleanUp.sln
git commit -m "chore: add xUnit test project for BeamNgTerrainPoc"
```

---

## Chunk 2: Core Generation Engine

### Task 4: Junction Interruption

**Current status**: Simple circular exclusion zones (Phase 1). Junction-aware interruption (Phase 1b) is a TODO — see spec for details.

**Implemented files:**
- `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionInterruptionRule.cs` — rule record + InterruptionSide enum
- `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterruptionRuleBuilder.cs` — builds per-spline rules from network junctions
- `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionInterrupter.cs` — rule-based segment splitting
- `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterrupterTests.cs` — tests for interruption logic
- `BeamNgTerrainPoc.Tests/DecalRoad/JunctionInterruptionRuleBuilderTests.cs` — tests for rule building

**What works:**
- [x] Terminating roads: all visual layers (edge lines, edge blends, center lines) are cut within the junction exclusion radius. AI roads are preserved for pathfinding.
- [x] Cutback radius accounts for both continuous and terminating road widths
- [x] Centerline-based distance checks (not offset positions) for consistent cutback on both sides

**TODO — Phase 1b: Junction-Aware Side Detection (rolled back)**

The junction-aware system that selectively interrupts only the junction-facing side of continuous roads was attempted but rolled back. Known problems:

1. **Side determination unreliable**: Dot-product and proximity-based approaches both fail on curved roads, acute-angle junctions, and rest-area geometry. The L/R classification is wrong in too many cases.
2. **Edge blends from terminating roads**: When `InterruptAtJunctions` flag is removed, edge blends that should be preserved on continuous roads also get incorrectly cut.
3. **Junction centroid offset**: Distorts distance-based side checks.

**Possible future approaches:**
- Road-surface polygon mask to check geometric overlap
- Per-node overlap check against nearby road width corridors
- Improved side detection using offset node positions relative to all nearby road splines

---

### Task 5: Implement DecalRoadLayerSetResolver

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerSetResolver.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerSetResolverTests.cs`

- [ ] **Step 1: Write failing tests for resolver cascade**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerSetResolverTests.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadLayerSetResolverTests
{
    [Fact]
    public void OsmTypeOverride_TakesPrecedence()
    {
        var settings = new DecalRoadSettings
        {
            OsmLayerSets = { ["motorway"] = new DecalRoadLayerSet { Name = "Motorway Override" } },
            MaterialLayerSets = { ["Asphalt"] = new DecalRoadLayerSet { Name = "Asphalt Fallback" } }
        };
        var defaults = new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = new() { Name = "Default Motorway" }
        };

        var result = DecalRoadLayerSetResolver.Resolve("motorway", "Asphalt", settings, defaults);

        Assert.NotNull(result);
        Assert.Equal("Motorway Override", result!.Name);
    }

    [Fact]
    public void MaterialFallback_WhenNoOsmOverride()
    {
        var settings = new DecalRoadSettings
        {
            MaterialLayerSets = { ["Asphalt"] = new DecalRoadLayerSet { Name = "Asphalt Material" } }
        };

        var result = DecalRoadLayerSetResolver.Resolve("residential", "Asphalt", settings, new());

        Assert.NotNull(result);
        Assert.Equal("Asphalt Material", result!.Name);
    }

    [Fact]
    public void AppDataDefaults_WhenNoProjectOverrides()
    {
        var settings = new DecalRoadSettings();
        var defaults = new Dictionary<string, DecalRoadLayerSet>
        {
            ["primary"] = new() { Name = "Default Primary" }
        };

        var result = DecalRoadLayerSetResolver.Resolve("primary", "Unknown", settings, defaults);

        Assert.NotNull(result);
        Assert.Equal("Default Primary", result!.Name);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var settings = new DecalRoadSettings();

        var result = DecalRoadLayerSetResolver.Resolve("footway", "GrassMaterial", settings, new());

        Assert.Null(result);
    }

    [Fact]
    public void NullOsmType_SkipsOsmLookup()
    {
        var settings = new DecalRoadSettings
        {
            MaterialLayerSets = { ["DirtRoad"] = new DecalRoadLayerSet { Name = "Dirt" } }
        };

        var result = DecalRoadLayerSetResolver.Resolve(null, "DirtRoad", settings, new());

        Assert.NotNull(result);
        Assert.Equal("Dirt", result!.Name);
    }

    [Fact]
    public void DisabledLayerSet_StillReturned()
    {
        // The resolver returns the set; the generator checks IsEnabled
        var settings = new DecalRoadSettings
        {
            OsmLayerSets = { ["motorway"] = new DecalRoadLayerSet { Name = "MW", IsEnabled = false } }
        };

        var result = DecalRoadLayerSetResolver.Resolve("motorway", "Asphalt", settings, new());

        Assert.NotNull(result);
        Assert.False(result!.IsEnabled);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~LayerSetResolver" -v n`
Expected: FAIL

- [ ] **Step 3: Implement DecalRoadLayerSetResolver**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerSetResolver.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Resolves which DecalRoadLayerSet applies to a given spline using a 3-level cascade:
/// 1. OSM type override (project preset)
/// 2. Material name fallback (project preset)
/// 3. AppData defaults (per OSM type)
/// Returns null if no match at any level.
/// </summary>
public static class DecalRoadLayerSetResolver
{
    public static DecalRoadLayerSet? Resolve(
        string? osmRoadType,
        string materialName,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults)
    {
        // 1. OSM type override in project preset
        if (osmRoadType != null &&
            settings.OsmLayerSets.TryGetValue(osmRoadType, out var osmOverride))
            return osmOverride;

        // 2. Material name fallback in project preset
        if (settings.MaterialLayerSets.TryGetValue(materialName, out var materialFallback))
            return materialFallback;

        // 3. AppData defaults by OSM type
        if (osmRoadType != null &&
            appDataDefaults.TryGetValue(osmRoadType, out var appDefault))
            return appDefault;

        // No match
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~LayerSetResolver" -v n`
Expected: All 6 tests PASS

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerSetResolver.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerSetResolverTests.cs
git commit -m "feat: add DecalRoadLayerSetResolver with cascade resolution tests"
```

---

### Task 6: Implement DecalRoadGenerator (core engine)

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorTests.cs`

- [ ] **Step 1: Write failing tests for lane position calculation and node chunking**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorTests.cs
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadGeneratorTests
{
    [Theory]
    [InlineData(2, new[] { 0.0f })]          // 2 lanes → 1 boundary at center
    [InlineData(3, new[] { -0.333f, 0.333f })] // 3 lanes → 2 boundaries
    [InlineData(4, new[] { -0.5f, 0.0f, 0.5f })] // 4 lanes → 3 boundaries
    [InlineData(1, new float[0])]             // 1 lane → no boundaries
    public void CalculateLaneBoundaryPositions_ReturnsCorrectPositions(
        int laneCount, float[] expectedApprox)
    {
        var positions = DecalRoadGenerator.CalculateLaneBoundaryPositions(laneCount);

        Assert.Equal(expectedApprox.Length, positions.Length);
        for (int i = 0; i < expectedApprox.Length; i++)
            Assert.Equal(expectedApprox[i], positions[i], precision: 2);
    }

    [Fact]
    public void ChunkNodes_SplitsAtMaxSize()
    {
        var nodes = Enumerable.Range(0, 250)
            .Select(i => new float[] { i, 0, 0, 1.0f })
            .ToList();

        var chunks = DecalRoadGenerator.ChunkNodes(nodes, maxNodesPerChunk: 100);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(100, chunks[0].Count);
        Assert.Equal(100, chunks[1].Count);
        Assert.Equal(50, chunks[2].Count);
    }

    [Fact]
    public void ChunkNodes_UnderLimit_ReturnsSingleChunk()
    {
        var nodes = Enumerable.Range(0, 50)
            .Select(i => new float[] { i, 0, 0, 1.0f })
            .ToList();

        var chunks = DecalRoadGenerator.ChunkNodes(nodes, maxNodesPerChunk: 100);

        Assert.Single(chunks);
        Assert.Equal(50, chunks[0].Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~DecalRoadGenerator" -v n`
Expected: FAIL

- [ ] **Step 3: Implement DecalRoadGenerator**

This is the largest file. Key responsibilities:
1. `Generate()` — main entry point: takes network, heightmap, settings, returns `List<GeneratedDecalRoad>`. Calls `JunctionInterruptionRuleBuilder.BuildRules()` once, then processes each spline with its per-spline rules.
2. `GenerateForSpline()` — per-spline processing: uses cross-section data (same as MasterSplineExporter) for centerline alignment, expand layers, lateral offset via `cs.NormalDirection`, elevation via `cs.TargetElevation`, junction-aware interruption via `JunctionInterrupter.InterruptWithRules()`, chunk
3. `SubSampleCrossSections()` — sub-samples dense cross-section array at desired node spacing (same step logic as `MasterSplineExporter.SampleNodesFromUnifiedCrossSections`)
4. `CalculateLaneBoundaryPositions()` — static helper for lane math
5. `ChunkNodes()` — static helper for splitting long node lists
6. `ExpandLayers()` — expands IsMirrored/IsPerLane into positioned layer instances

**Road width for lateral offset**: Uses `spline.Parameters.EffectiveMasterSplineWidthMeters` (cascade: MasterSplineWidth → RoadSurfaceWidth → RoadWidth). MasterSplineWidth is intentionally narrower than RoadSurfaceWidth to account for terrain material dither.

**Centerline alignment**: Uses unified cross-section `CenterPoint` and `NormalDirection` (same data as MasterSplineExporter) instead of raw `spline.Spline.SampleByDistance()`. This ensures DecalRoad paths exactly match exported master splines. Elevation uses `TargetElevation` (smoothed/harmonized) with heightmap fallback.

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Generates DecalRoad objects from a UnifiedRoadNetwork.
/// For each spline, resolves layer set, samples centerline, applies lateral offsets,
/// interrupts at junction zones, chunks to ≤100 nodes, and produces GeneratedDecalRoad output.
/// </summary>
public class DecalRoadGenerator
{
    /// <summary>
    /// Generate all DecalRoad objects for the given network.
    /// </summary>
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

        // Build per-spline junction interruption rules
        var allRules = JunctionInterruptionRuleBuilder.BuildRules(
            network, settings.JunctionExclusionMarginMeters);

        foreach (var spline in network.Splines)
        {
            // Skip bridges/tunnels — DecalRoads project onto terrain surface
            if (spline.IsBridge || spline.IsTunnel)
                continue;

            // Resolve layer set via cascade
            var layerSet = DecalRoadLayerSetResolver.Resolve(
                spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            // Fetch cross-sections from unified pipeline (same data MasterSplineExporter uses)
            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            // Get rules for this spline (empty list if none)
            var splineRules = allRules.TryGetValue(spline.SplineId, out var r) ? r : [];

            var splineResults = GenerateForSpline(
                spline, layerSet, crossSections, splineRules,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                settings.NodeSpacingMeters);
            results.AddRange(splineResults);
        }

        return results;
    }

    internal static List<GeneratedDecalRoad> GenerateForSpline(
        ParameterizedRoadSpline spline,
        DecalRoadLayerSet layerSet,
        IReadOnlyList<UnifiedCrossSection> crossSections,
        IReadOnlyList<JunctionInterruptionRule> interruptionRules,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeSpacingMeters)
    {
        var results = new List<GeneratedDecalRoad>();
        // Use master spline width for lateral offsets (cascade: MasterSplineWidth → RoadSurfaceWidth → RoadWidth)
        var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
        var laneCount = GetLaneCount(spline, layerSet);
        var splineName = GetSplineName(spline);

        // Sub-sample cross-sections at desired node spacing
        // (same approach as MasterSplineExporter.SampleNodesFromUnifiedCrossSections)
        var sampledSections = SubSampleCrossSections(crossSections, nodeSpacingMeters);
        if (sampledSections.Count < 2) return results;

        // Expand layers (mirroring, per-lane replication)
        var expandedLayers = ExpandLayers(layerSet.Layers, laneCount);

        foreach (var (layer, position, side, laneIndex) in expandedLayers)
        {
            if (!layer.IsEnabled) continue;

            // Calculate laterally offset nodes using cross-section normals
            var offsetNodes2D = new List<Vector2>(sampledSections.Count);

            foreach (var cs in sampledSections)
            {
                // Lateral offset: position * 0.5 * roadWidth along cross-section normal
                var offset = position * 0.5f * roadWidth;
                var offsetPos = cs.CenterPoint + cs.NormalDirection * offset;
                offsetNodes2D.Add(offsetPos);
            }

            // Junction-aware interruption using per-spline rules
            List<List<(Vector2 Pos, int SectionIndex)>> segments;
            if (layer.InterruptAtJunctions)
            {
                segments = JunctionInterrupter.InterruptWithRules(
                    offsetNodes2D, interruptionRules, layer.LayerType, side);
            }
            else
            {
                var allIndices = Enumerable.Range(0, offsetNodes2D.Count)
                    .Select(i => (offsetNodes2D[i], i)).ToList();
                segments = [allIndices];
            }

            // Process each segment
            int chunkIndex = 0;
            foreach (var segment in segments)
            {
                // Convert to world coordinates with elevation from cross-sections
                var worldNodesSegment = new List<float[]>(segment.Count);
                foreach (var (pos, sectionIdx) in segment)
                {
                    var cs = sampledSections[sectionIdx];

                    // Use TargetElevation from unified pipeline (smoothed/harmonized),
                    // matching MasterSplineExporter behavior exactly
                    float elevation;
                    if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                        elevation = cs.TargetElevation;
                    else
                        elevation = GetHeightMapElevation(heightMap, pos.X, pos.Y, metersPerPixel);

                    var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                        pos.X, pos.Y, elevation + terrainBaseHeight,
                        terrainSizePixels, metersPerPixel);
                    worldNodesSegment.Add([worldPos.X, worldPos.Y, worldPos.Z, layer.Width]);
                }

                // Chunk into ≤100 nodes with boundary overlap
                var chunks = ChunkNodes(worldNodesSegment, maxNodesPerChunk: 100);
                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    chunkIndex++;
                    var name = $"{splineName}_{layer.Name}_{side}_{chunkIndex:D3}";
                    var startFade = (ci == 0) ? layer.FadeIn : 0f;
                    var endFade = (ci == chunks.Count - 1) ? layer.FadeOut : 0f;

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
    /// Calculates lane boundary positions as normalized values in [-1, +1].
    /// For N lanes, returns N-1 boundary positions.
    /// </summary>
    public static float[] CalculateLaneBoundaryPositions(int laneCount)
    {
        if (laneCount <= 1) return [];

        var positions = new float[laneCount - 1];
        for (int i = 1; i < laneCount; i++)
            positions[i - 1] = -1.0f + 2.0f * i / laneCount;

        return positions;
    }

    /// <summary>
    /// Splits a node list into chunks of at most maxNodesPerChunk.
    /// </summary>
    public static List<List<float[]>> ChunkNodes(List<float[]> nodes, int maxNodesPerChunk = 100)
    {
        var chunks = new List<List<float[]>>();
        for (int i = 0; i < nodes.Count; i += maxNodesPerChunk)
        {
            var count = Math.Min(maxNodesPerChunk, nodes.Count - i);
            chunks.Add(nodes.GetRange(i, count));
        }
        return chunks;
    }

    /// <summary>
    /// Expands layers by mirroring and per-lane replication.
    /// Returns tuples of (layer, normalizedPosition, sideLabel, laneIndex).
    /// </summary>
    internal static List<(DecalRoadLayerDefinition Layer, float Position, string Side, int LaneIndex)>
        ExpandLayers(IReadOnlyList<DecalRoadLayerDefinition> layers, int laneCount)
    {
        var expanded = new List<(DecalRoadLayerDefinition, float, string, int)>();

        foreach (var layer in layers)
        {
            if (layer.IsPerLane)
            {
                // Replicate at each lane boundary
                var boundaries = CalculateLaneBoundaryPositions(laneCount);
                for (int i = 0; i < boundaries.Length; i++)
                {
                    expanded.Add((layer, boundaries[i], $"C{i + 1}", i));
                }
            }
            else if (layer.IsMirrored)
            {
                // Left and right copies
                expanded.Add((layer, -MathF.Abs(layer.Position), "L", 0));
                expanded.Add((layer, MathF.Abs(layer.Position), "R", 0));
            }
            else
            {
                // Single placement at declared position
                var side = layer.Position < -0.01f ? "L" : layer.Position > 0.01f ? "R" : "C";
                expanded.Add((layer, layer.Position, side, 0));
            }
        }

        return expanded;
    }

    private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
    {
        // 1. OSM tags
        if (spline.OsmTags != null &&
            spline.OsmTags.TryGetValue("lanes", out var lanesStr) &&
            int.TryParse(lanesStr, out var lanes) && lanes > 0)
            return lanes;

        // 2. Layer set default
        return layerSet.DefaultLaneCount;
    }

    private static string GetSplineName(ParameterizedRoadSpline spline)
    {
        // Use material name + ID for unique naming (matches MasterSplineExporter pattern)
        return $"{spline.MaterialName}_{spline.SplineId:D3}";
    }

    // NOTE: BuildExclusionZones() removed — replaced by JunctionInterruptionRuleBuilder.BuildRules()
    // NOTE: InterruptWithIndices() removed — replaced by JunctionInterrupter.InterruptWithRules()

    private static float GetHeightMapElevation(
        float[,] heightMap, float terrainX, float terrainY, float metersPerPixel)
    {
        var pixelX = (int)(terrainX / metersPerPixel);
        var pixelY = (int)(terrainY / metersPerPixel);
        var size = heightMap.GetLength(0);
        pixelX = Math.Clamp(pixelX, 0, size - 1);
        pixelY = Math.Clamp(pixelY, 0, size - 1);
        return heightMap[pixelY, pixelX]; // [y, x] row-major
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~DecalRoadGenerator" -v n`
Expected: All tests PASS

- [ ] **Step 5: Verify full build**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorTests.cs
git commit -m "feat: add DecalRoadGenerator core engine with layer expansion and lateral offsets"
```

---

## Chunk 3: Scene Writing & Pipeline Integration

### Task 7: Implement DecalRoadSceneWriter

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadSceneWriter.cs`

- [ ] **Step 1: Implement scene writer following BuildingSceneWriter pattern**

The writer creates the `MT_decalroads/` folder hierarchy and writes NDJSON scene files.
Reference: `BuildingSceneWriter.cs` at `BeamNgTerrainPoc/Terrain/Building/BuildingSceneWriter.cs` for the `EnsureSimGroupInParent` pattern and `SimItemsJsonSerializer.Save()` usage.

Reference skills: @beamng-decalroad-format for the DecalRoad JSON properties, @beamng-decalroad-generation for the output structure.

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadSceneWriter.cs
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Writes GeneratedDecalRoad objects to BeamNG's MissionGroup scene hierarchy as NDJSON.
///
/// Output structure:
///   main/MissionGroup/items.level.json         ← SimGroup "MT_decalroads" entry
///   main/MissionGroup/MT_decalroads/
///     items.level.json                          ← per-spline SimGroup entries
///     {SplineName}/items.level.json             ← DecalRoad NDJSON lines
/// </summary>
public class DecalRoadSceneWriter
{
    public const string GroupName = "MT_decalroads";

    /// <summary>
    /// Writes all generated DecalRoads to the level directory.
    /// </summary>
    /// <param name="decalRoads">Generated DecalRoad objects.</param>
    /// <param name="levelPath">Path to the level's root directory
    /// (e.g., .../levels/myLevel).</param>
    /// <returns>Number of DecalRoad objects written.</returns>
    public int WriteAll(IReadOnlyList<GeneratedDecalRoad> decalRoads, string levelPath)
    {
        if (decalRoads.Count == 0) return 0;

        var missionGroupPath = Path.Combine(levelPath, "main", "MissionGroup");
        var parentItemsPath = Path.Combine(missionGroupPath, "items.level.json");
        var groupDir = Path.Combine(missionGroupPath, GroupName);

        // 1. Ensure MT_decalroads SimGroup exists in parent
        EnsureSimGroupInParent(parentItemsPath, "MissionGroup");

        // 2. Group DecalRoads by parent spline group
        var bySpline = decalRoads.GroupBy(d => d.ParentGroupName).ToList();

        // 3. Write per-spline SimGroup entries in MT_decalroads/items.level.json
        var splineGroupItems = new List<JsonDict>();
        foreach (var group in bySpline)
        {
            var dict = new JsonDict();
            dict["name"] = group.Key;
            dict["class"] = "SimGroup";
            dict["persistentId"] = Guid.NewGuid().ToString();
            dict["__parent"] = GroupName;
            splineGroupItems.Add(dict);
        }

        var groupItemsPath = Path.Combine(groupDir, "items.level.json");
        Directory.CreateDirectory(groupDir);
        SimItemsJsonSerializer.Save(groupItemsPath, splineGroupItems);

        // 4. Write DecalRoad entries per spline subfolder
        int totalWritten = 0;
        foreach (var group in bySpline)
        {
            var splineDir = Path.Combine(groupDir, group.Key);
            Directory.CreateDirectory(splineDir);

            var items = new List<JsonDict>();
            foreach (var dr in group)
            {
                items.Add(CreateDecalRoadEntry(dr));
                totalWritten++;
            }

            var itemsPath = Path.Combine(splineDir, "items.level.json");
            SimItemsJsonSerializer.Save(itemsPath, items);
        }

        Console.WriteLine(
            $"DecalRoadSceneWriter: Wrote {totalWritten} DecalRoads in {bySpline.Count} groups to {groupDir}");
        return totalWritten;
    }

    /// <summary>
    /// Removes existing MT_decalroads directory for re-generation.
    /// </summary>
    public static void CleanPrevious(string levelPath)
    {
        var groupDir = Path.Combine(levelPath, "main", "MissionGroup", GroupName);
        if (Directory.Exists(groupDir))
            Directory.Delete(groupDir, recursive: true);
    }

    private void EnsureSimGroupInParent(string parentItemsPath, string parentGroupName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(parentItemsPath)!);

        var lines = File.Exists(parentItemsPath)
            ? File.ReadAllLines(parentItemsPath).ToList()
            : new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("class", out var cls) && cls.GetString() == "SimGroup" &&
                    root.TryGetProperty("name", out var name) && name.GetString() == GroupName)
                    return; // Already exists
            }
            catch (JsonException) { }
        }

        var entry = new Dictionary<string, object>
        {
            { "name", GroupName },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", parentGroupName }
        };
        lines.Add(JsonSerializer.Serialize(entry));
        File.WriteAllLines(parentItemsPath, lines);
    }

    private static JsonDict CreateDecalRoadEntry(GeneratedDecalRoad dr)
    {
        var dict = new JsonDict();
        dict["class"] = "DecalRoad";
        dict["persistentId"] = Guid.NewGuid().ToString();
        dict["__parent"] = dr.ParentGroupName;
        dict["name"] = dr.Name;
        dict["material"] = dr.Material;
        dict["textureLength"] = dr.TextureLength;
        dict["breakAngle"] = 3.0f;
        dict["renderPriority"] = dr.RenderPriority;
        dict["startEndFade"] = dr.StartEndFade;
        dict["distanceFade"] = dr.DistanceFade;
        dict["drivability"] = dr.Drivability;
        dict["improvedSpline"] = true;
        dict["position"] = new float[] { dr.Position.X, dr.Position.Y, dr.Position.Z };

        // AI road pathfinding properties
        if (dr.IsAIRoad)
        {
            dict["autoLanes"] = true;
            dict["lanesLeft"] = dr.LanesLeft;
            dict["lanesRight"] = dr.LanesRight;
            dict["oneWay"] = dr.OneWay;
            dict["flipDirection"] = dr.FlipDirection;
            dict["gatedRoad"] = false;
            dict["autoJunction"] = true;
            dict["useSubdivisions"] = true;
        }

        // Nodes: array of [x, y, z, width] arrays
        dict["nodes"] = dr.Nodes.Select(n => (object)n).ToArray();

        return dict;
    }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadSceneWriter.cs
git commit -m "feat: add DecalRoadSceneWriter for NDJSON scene file output"
```

---

### Task 8: Implement default layer sets

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`
- Create: `BeamNG_LevelCleanUp/Utils/DecalRoadDefaultsManager.cs`
- Modify: `BeamNG_LevelCleanUp/Utils/AppPaths.cs`

- [ ] **Step 1: Add DecalRoadDefaultsPath to AppPaths**

In `AppPaths.cs`, add after the existing path properties:

```csharp
public static string DecalRoadDefaultsPath =>
    Path.Combine(SettingsFolder, "decalroad-defaults.json");
```

- [ ] **Step 2: Create DecalRoadDefaultLayerSets with hardcoded defaults**

Reference skill: @beamng-road-layers for material names and widths.

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Hardcoded fallback default DecalRoadLayerSet definitions per OSM road type.
/// Used when AppData defaults file is missing or corrupted.
/// </summary>
public static class DecalRoadDefaultLayerSets
{
    public static Dictionary<string, DecalRoadLayerSet> GetDefaults()
    {
        return new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = CreateHighwaySet("Motorway", 4),
            ["trunk"] = CreateHighwaySet("Trunk", 4),
            ["primary"] = CreateStandardRoadSet("Primary", 2),
            ["secondary"] = CreateStandardRoadSet("Secondary", 2),
            ["tertiary"] = CreateMinimalRoadSet("Tertiary", 2),
            ["unclassified"] = CreateMinimalRoadSet("Unclassified", 2),
            ["residential"] = CreateResidentialSet("Residential", 2),
            ["service"] = CreateServiceSet("Service", 1),
            ["track"] = CreateTrackSet("Track", 1),
        };
    }

    private static DecalRoadLayerSet CreateHighwaySet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.15f, Position = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.15f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_blend", Width = 1.5f, Position = 1.0f,
                     IsMirrored = true, RenderPriority = 5, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_blend_wide", Width = 3.0f, Position = 1.2f,
                     IsMirrored = true, RenderPriority = 4, InterruptAtJunctions = false },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = lanes / 2 },
        ]
    };

    private static DecalRoadLayerSet CreateStandardRoadSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.12f, Position = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "CenterLine", LayerType = DecalRoadLayerType.CenterLine,
                     Material = "m_line_white_discontinue", Width = 0.12f, Position = 0.0f,
                     InterruptAtJunctions = true },
            new() { Name = "EdgeBlend", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_blend", Width = 1.5f, Position = 1.0f,
                     IsMirrored = true, RenderPriority = 5, InterruptAtJunctions = false },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateMinimalRoadSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.10f, Position = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateResidentialSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeBlend", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_blend", Width = 1.0f, Position = 1.0f,
                     IsMirrored = true, RenderPriority = 5, InterruptAtJunctions = false },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateServiceSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeBlend", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_blend", Width = 0.8f, Position = 1.0f,
                     IsMirrored = true, RenderPriority = 5, InterruptAtJunctions = false },
        ]
    };

    private static DecalRoadLayerSet CreateTrackSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeBlend", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_dirt_edge_blend", Width = 0.6f, Position = 1.0f,
                     IsMirrored = true, RenderPriority = 5, InterruptAtJunctions = false },
        ]
    };
}
```

- [ ] **Step 3: Create DecalRoadDefaultsManager for AppData file**

```csharp
// BeamNG_LevelCleanUp/Utils/DecalRoadDefaultsManager.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Manages the AppData decalroad-defaults.json file.
/// Creates from hardcoded defaults on first run, loads/saves user modifications.
/// </summary>
public static class DecalRoadDefaultsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Dictionary<string, DecalRoadLayerSet> Load()
    {
        var path = AppPaths.DecalRoadDefaultsPath;

        if (!File.Exists(path))
        {
            var defaults = DecalRoadDefaultLayerSets.GetDefaults();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, DecalRoadLayerSet>>(json, JsonOptions)
                   ?? DecalRoadDefaultLayerSets.GetDefaults();
        }
        catch (JsonException)
        {
            // Corrupted file — recreate from hardcoded defaults
            var defaults = DecalRoadDefaultLayerSets.GetDefaults();
            Save(defaults);
            return defaults;
        }
    }

    public static void Save(Dictionary<string, DecalRoadLayerSet> layerSets)
    {
        var json = JsonSerializer.Serialize(layerSets, JsonOptions);
        File.WriteAllText(AppPaths.DecalRoadDefaultsPath, json);
    }
}
```

- [ ] **Step 4: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
git add BeamNG_LevelCleanUp/Utils/DecalRoadDefaultsManager.cs
git add BeamNG_LevelCleanUp/Utils/AppPaths.cs
git commit -m "feat: add default DecalRoad layer sets and AppData defaults manager"
```

---

### Task 9: Integrate into TerrainCreator pipeline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`
- Modify: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

- [ ] **Step 1: Add DecalRoad properties to TerrainCreationParameters**

In `TerrainCreationParameters.cs`, add a new section after the structure elevation parameters:

```csharp
// ========================================
// DECALROAD GENERATION
// ========================================

/// <summary>
///     DecalRoad generation settings. When null or Enabled=false, no DecalRoads are generated.
/// </summary>
public DecalRoadSettings? DecalRoadSettings { get; set; }

/// <summary>
///     AppData default layer sets, resolved by the orchestrator (which has access to the
///     BeamNG_LevelCleanUp project's DecalRoadDefaultsManager). Falls back to hardcoded
///     defaults if null.
/// </summary>
public Dictionary<string, DecalRoadLayerSet>? DecalRoadAppDataDefaults { get; set; }

// ========================================
// OUTPUT PROPERTIES (populated during terrain generation)
// ========================================

/// <summary>
///     The unified road network produced during road smoothing.
///     Populated as an output after terrain generation for downstream use (DecalRoad re-generation).
///     This is an OUTPUT property - do not set manually.
/// </summary>
public UnifiedRoadNetwork? OutputNetwork { get; set; }

/// <summary>
///     The final heightmap produced during terrain generation (float[y,x] row-major).
///     Populated as an output for downstream use (DecalRoad re-generation).
///     This is an OUTPUT property - do not set manually.
/// </summary>
public float[,]? OutputHeightMap { get; set; }
```

Add required using at top:
```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
```

- [ ] **Step 2: Add DecalRoad generation step in TerrainCreator**

In `TerrainCreator.cs`, after the spawn point extraction section (around line 310, after the `ExtractedSpawnPoint` block) and before material layer processing (section "4. Process material layers"), add:

```csharp
// 3c. Generate DecalRoads (requires unified network and heightmap)
if (unifiedResult?.Network != null &&
    parameters.DecalRoadSettings is { Enabled: true })
{
    perfLog.LogSection("DecalRoad Generation");
    sw.Restart();

    // Use AppData defaults passed via parameters (resolved by orchestrator)
    var appDataDefaults = parameters.DecalRoadAppDataDefaults
        ?? BeamNgTerrainPoc.Terrain.Services.DecalRoad
            .DecalRoadDefaultLayerSets.GetDefaults();

    var decalRoads = BeamNgTerrainPoc.Terrain.Services.DecalRoad
        .DecalRoadGenerator.Generate(
            unifiedResult.Network,
            heightMap2D,
            parameters.MetersPerPixel,
            parameters.Size,
            parameters.TerrainBaseHeight,
            parameters.DecalRoadSettings,
            appDataDefaults);

    if (decalRoads.Count > 0)
    {
        // outputPath is "{WorkingDirectory}/theTerrain.ter"
        // WorkingDirectory IS the level root, so one GetDirectoryName is correct
        var levelDir = Path.GetDirectoryName(outputPath)!;
        var writer = new BeamNgTerrainPoc.Terrain.Services.DecalRoad.DecalRoadSceneWriter();
        var written = writer.WriteAll(decalRoads, levelDir);
        perfLog.Info($"Generated {written} DecalRoad objects");
    }

    perfLog.Timing($"DecalRoad generation: {sw.ElapsedMilliseconds}ms");
}

// Populate output properties for downstream use (re-generation)
parameters.OutputNetwork = unifiedResult?.Network;
parameters.OutputHeightMap = heightMap2D;
```

**Important**: The `levelDir` calculation: `outputPath` is typically something like `.../levels/myLevel/theTerrain.ter`, so `Path.GetDirectoryName` twice gives the level root. Read the actual code to confirm the path structure and adjust if needed.

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs
git add BeamNgTerrainPoc/Terrain/TerrainCreator.cs
git commit -m "feat: integrate DecalRoad generation into TerrainCreator pipeline"
```

---

### Task 10: Wire up state and orchestrator

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`

- [ ] **Step 1: Add DecalRoad state to TerrainGenerationState**

In `TerrainGenerationState.cs`, first add the required using statements at the top:

```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
```

Then add in a new section:

```csharp
// ========================================
// DECALROAD SETTINGS
// ========================================

/// <summary>
///     Enable DecalRoad generation during terrain creation.
/// </summary>
public bool EnableDecalRoads { get; set; }

/// <summary>
///     DecalRoad generation settings (node spacing, junction margin, layer sets).
///     Populated from preset or defaults.
/// </summary>
public DecalRoadSettings? DecalRoadSettings { get; set; }

/// <summary>
///     Cached UnifiedRoadNetwork from last terrain generation.
///     Used for standalone DecalRoad re-generation.
///     Lost when navigating away from page.
/// </summary>
[System.Text.Json.Serialization.JsonIgnore]
public UnifiedRoadNetwork? CachedNetwork { get; set; }

/// <summary>
///     Cached heightmap from last terrain generation.
///     Used for standalone DecalRoad re-generation.
/// </summary>
[System.Text.Json.Serialization.JsonIgnore]
public float[,]? CachedHeightMap { get; set; }
```

- [ ] **Step 2: Wire DecalRoadSettings into TerrainGenerationOrchestrator**

In `TerrainGenerationOrchestrator.cs`, find the `BuildTerrainParametersAsync` method (starts around line 931, where `TerrainCreationParameters` is constructed). Add:

```csharp
DecalRoadSettings = state.EnableDecalRoads ? state.DecalRoadSettings : null,
DecalRoadAppDataDefaults = state.EnableDecalRoads
    ? DecalRoadDefaultsManager.Load()
    : null,
```

Add using at the top of the file:
```csharp
using BeamNG_LevelCleanUp.Utils;
```

Then after the generation succeeds (where `result.Parameters` is available), cache the outputs:

In the `ExecuteInternalAsync` method, after `return (Success: generationSuccess, ...)` block processes successfully, look for where `UpdateStateFromParameters` is called. After that line, add:

```csharp
// Cache network and heightmap for standalone DecalRoad re-generation
if (terrainParameters != null)
{
    state.CachedNetwork = terrainParameters.OutputNetwork;
    state.CachedHeightMap = terrainParameters.OutputHeightMap;
}
```

- [ ] **Step 3: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs
git add BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs
git commit -m "feat: wire DecalRoad settings through state and orchestrator"
```

---

## Chunk 4: Preset Serialization

### Task 11: Add DecalRoad settings to preset export/import

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor`

- [ ] **Step 1: Add DecalRoadSettings property to TerrainPresetResult**

In `TerrainPresetResult.cs`, add a new property:

```csharp
public DecalRoadSettings? DecalRoadSettings { get; set; }
```

- [ ] **Step 2: Add export logic in TerrainPresetExporter**

In `TerrainPresetExporter.razor` (or its `.cs` code-behind), find where `_appSettings` JSON object is built (around the `BuildAppSettings` method or equivalent). After the existing settings sections, add:

```csharp
// DecalRoad settings
if (state.DecalRoadSettings != null)
{
    var drSettingsJson = JsonSerializer.SerializeToNode(state.DecalRoadSettings,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
    appSettings["decalRoadSettings"] = drSettingsJson;
}
```

Also bump the version to `"3.0"` in the export.

- [ ] **Step 3: Add import logic in TerrainPresetImporter**

In `TerrainPresetImporter.razor` (or its `.cs` code-behind), find where settings are deserialized. Add handling for the `decalRoadSettings` key:

```csharp
// DecalRoad settings (v3.0+) — gracefully handle missing section
if (appSettings.TryGetPropertyValue("decalRoadSettings", out var drNode) && drNode != null)
{
    result.DecalRoadSettings = drNode.Deserialize<DecalRoadSettings>(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    });
}
```

Then when applying the preset to `TerrainGenerationState`, add:

```csharp
if (result.DecalRoadSettings != null)
{
    state.DecalRoadSettings = result.DecalRoadSettings;
    state.EnableDecalRoads = result.DecalRoadSettings.Enabled;
}
```

- [ ] **Step 4: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor
git commit -m "feat: add DecalRoad settings to preset export/import with v3.0 version bump"
```

---

## Chunk 5: UI — Enable Checkbox & Re-generate Button

### Task 12: Add DecalRoad enable toggle and re-generate button to GenerateTerrain page

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

- [ ] **Step 1: Add DecalRoad section to GenerateTerrain.razor**

Read the existing `GenerateTerrain.razor` file to find the appropriate location (after the existing terrain generation controls, near the "Generate" button area). Add a checkbox and re-generate button:

```razor
@* DecalRoad Generation Section *@
<MudPaper Class="pa-4 mb-4">
    <MudText Typo="Typo.h6">DecalRoad Generation</MudText>
    <MudCheckBox @bind-Value="_state.EnableDecalRoads"
                 Label="Generate road markings and edge blends (DecalRoads)"
                 Color="Color.Primary" />
    @if (_state.EnableDecalRoads)
    {
        <MudText Typo="Typo.body2" Class="mt-2 mb-2">
            Generates visual road detail layers (edge lines, lane markings, edge blends)
            projected onto the terrain surface along road splines.
        </MudText>
        <MudButton Variant="Variant.Outlined"
                   Color="Color.Secondary"
                   StartIcon="@Icons.Material.Filled.Refresh"
                   Disabled="@(_state.CachedNetwork == null || _isGenerating)"
                   OnClick="RegenerateDecalRoads">
            Re-generate DecalRoads
        </MudButton>
        @if (_state.CachedNetwork == null)
        {
            <MudText Typo="Typo.caption" Color="Color.Warning" Class="mt-1">
                Generate terrain first to enable re-generation.
            </MudText>
        }
    }
</MudPaper>
```

- [ ] **Step 2: Add re-generation handler in GenerateTerrain.razor.cs**

Add to `GenerateTerrain.razor.cs`:

```csharp
private async Task RegenerateDecalRoads()
{
    if (_state.CachedNetwork == null || _state.CachedHeightMap == null) return;
    if (_state.DecalRoadSettings == null)
    {
        _state.DecalRoadSettings = new DecalRoadSettings { Enabled = true };
    }

    _isGenerating = true;
    StateHasChanged();

    try
    {
        await Task.Run(() =>
        {
            var appDataDefaults = DecalRoadDefaultsManager.Load();

            // Clean previous DecalRoad output
            var levelPath = _state.WorkingDirectory;
            DecalRoadSceneWriter.CleanPrevious(levelPath);

            var decalRoads = DecalRoadGenerator.Generate(
                _state.CachedNetwork,
                _state.CachedHeightMap,
                _state.MetersPerPixel,
                _state.TerrainSize,
                _state.TerrainBaseHeight,
                _state.DecalRoadSettings,
                appDataDefaults);

            if (decalRoads.Count > 0)
            {
                var writer = new DecalRoadSceneWriter();
                writer.WriteAll(decalRoads, levelPath);
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Re-generated {decalRoads.Count} DecalRoad objects");
        });

        await InvokeAsync(() =>
        {
            Snackbar.Add("DecalRoads re-generated successfully", Severity.Success);
            StateHasChanged();
        });
    }
    catch (Exception ex)
    {
        ShowException(ex);
        await InvokeAsync(() =>
        {
            Snackbar.Add($"DecalRoad generation failed: {ex.Message}", Severity.Error);
        });
    }
    finally
    {
        _isGenerating = false;
        await InvokeAsync(StateHasChanged);
    }
}
```

Add the required usings at the top of the file:
```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNG_LevelCleanUp.Utils;
```

- [ ] **Step 3: Initialize default DecalRoadSettings**

Find where `_state` is initialized (in `OnInitializedAsync` or `OnParametersSetAsync`). After existing initialization, add:

```csharp
// Initialize DecalRoad settings with defaults if not set from preset
_state.DecalRoadSettings ??= new DecalRoadSettings
{
    Enabled = _state.EnableDecalRoads,
    NodeSpacingMeters = 2.0f,
    JunctionExclusionMarginMeters = 5.0f
};
```

- [ ] **Step 4: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat: add DecalRoad enable toggle and re-generate button to GenerateTerrain page"
```

---

## Chunk 6: Run All Tests & Final Verification

### Task 13: Run full test suite and verify build

- [ ] **Step 1: Run all tests**

```bash
dotnet test BeamNgTerrainPoc.Tests -v n
```
Expected: All tests PASS

- [ ] **Step 2: Build entire solution**

```bash
dotnet build
```
Expected: Build succeeded, no warnings from new code

- [ ] **Step 3: Verify no build regressions**

```bash
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```
Expected: Build succeeded

- [ ] **Step 4: Commit any final fixes**

If any test failures or build issues were found and fixed:

```bash
git add -A
git commit -m "fix: resolve build issues from DecalRoad integration"
```

---

## Post-Implementation Notes

### What's NOT in this plan (deferred to future tasks):

1. **UI: DecalRoadLayerSetEditor dialog** — Full MudDialog for editing layer sets (drag-drop, add/remove layers). Can be added as a separate task.
2. **UI: DecalRoadOsmOverrides panel** — Per-OSM-type override cards in GenerateTerrain page. Can be added as a separate task.
3. **UI: TerrainMaterialSettings integration** — Inline DecalRoad summary in per-material panels. Can be added as a separate task.
4. **Phase 2: Contour-based junction edges** — Compute merged road surface contours for smooth curved edge markings at junctions.
5. **Material validation** — Verify that referenced DecalRoad materials (m_line_white, etc.) actually exist in the level.

### Manual Testing Checklist

After implementation, verify manually:
1. Generate terrain with OSM data that includes roads
2. Check `MT_decalroads/` folder is created with expected hierarchy
3. Open each `items.level.json` — verify valid NDJSON (one JSON object per line)
4. Load generated level in BeamNG editor — verify DecalRoads appear on roads
5. Check junction areas — markings should stop at intersections
6. Export preset with DecalRoad settings → reimport → verify round-trip
7. Delete `decalroad-defaults.json` → restart → verify file recreated
8. Load v2.0 preset (no decalRoadSettings) → verify DecalRoads disabled gracefully
9. Test re-generate button after terrain generation
