# Replace at Junctions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Replace at Junctions" mode that swaps material/width/textureLength for DecalRoad nodes inside junction overlap zones, instead of removing them entirely.

**Architecture:** Extend the existing `InterruptAtJunctions` boolean into a 3-mode enum (`JunctionConstraintMode`: None, Interrupt, Replace) — mirroring the proven `CurveConstraintMode` pattern. The post-processor's `ComputeOverlapMask` already identifies which nodes overlap; for Replace mode, instead of discarding those nodes we emit a second DecalRoad with the replacement material. The UI uses a radio-group toggle (same layout as Curve Constraints).

**Tech Stack:** C# / .NET 9, Blazor (MudBlazor v8), System.Text.Json serialization

---

## File Change Summary

| File | Action |
|------|--------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionConstraintMode.cs` | **NEW** — 3-value enum |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Replace `InterruptAtJunctions` bool with `JunctionConstraint` enum + 3 replacement fields |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs` | Replace `InterruptAtJunctions` bool with `JunctionConstraint` enum |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Propagate enum + replacement fields to `GeneratedDecalRoad` |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadOverlapPostProcessor.cs` | Add Replace logic alongside existing Interrupt logic |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | Migrate `InterruptAtJunctions = true` → `JunctionConstraint = Interrupt` |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Replace checkbox with radio-group + replacement fields panel |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Add new fields to `DeepCopyLayer()` |

---

## Step 1: Create `JunctionConstraintMode` enum

**File:** Create `BeamNgTerrainPoc/Terrain/Models/DecalRoad/JunctionConstraintMode.cs`

Mirrors `CurveConstraintMode` pattern exactly.

- [ ] Create the file:

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Controls how a layer behaves at road junctions where it overlaps
/// another road's surface footprint.
/// </summary>
public enum JunctionConstraintMode
{
    /// <summary>No junction handling — layer continues uninterrupted through junctions.</summary>
    None,

    /// <summary>Layer is removed (split) where it overlaps another road's surface.</summary>
    Interrupt,

    /// <summary>Layer's material/width/textureLength are replaced in junction overlap zones.</summary>
    Replace
}
```

---

## Step 2: Update `DecalRoadLayerDefinition`

**File:** Modify `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs`

- [ ] Replace the `InterruptAtJunctions` property with the new enum and replacement fields:

```csharp
// Replace this line:
//   public bool InterruptAtJunctions { get; set; } = true;
// With:
public JunctionConstraintMode JunctionConstraint { get; set; } = JunctionConstraintMode.Interrupt;

// Junction replacement fields (only used when JunctionConstraint == Replace)
public string JunctionReplacementMaterial { get; set; } = string.Empty;
public float JunctionReplacementWidth { get; set; }      // 0 = same as main
public float JunctionReplacementTextureLength { get; set; } // 0 = same as main
```

- [ ] Add a backwards-compatible deserialization property for old JSON files. Old saved configs have `"interruptAtJunctions": true/false` — this setter-only property converts the bool to the new enum during deserialization:

```csharp
/// <summary>
/// Backwards-compat: deserializes old "interruptAtJunctions" bool from saved JSON.
/// Maps true → Interrupt, false → None. New serialization uses JunctionConstraint enum.
/// </summary>
[System.Text.Json.Serialization.JsonInclude]
[System.Text.Json.Serialization.JsonPropertyName("interruptAtJunctions")]
public bool InterruptAtJunctionsCompat
{
    set => JunctionConstraint = value ? JunctionConstraintMode.Interrupt : JunctionConstraintMode.None;
}
```

**Why:** `DecalRoadDefaultsManager` uses `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`. Old JSON has `"interruptAtJunctions": true` (bool), new JSON will have `"junctionConstraint": "interrupt"` (enum). The setter-only property ensures old configs deserialize correctly. It does NOT serialize (no getter).

---

## Step 3: Update `GeneratedDecalRoad` metadata

**File:** Modify `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs`

The post-processor needs to know whether to interrupt or replace, and what replacement values to use.

- [ ] Replace and add fields:

```csharp
// Replace:
//   public bool InterruptAtJunctions { get; init; }
// With:
public JunctionConstraintMode JunctionConstraint { get; init; }

// Add junction replacement values (set during generation, consumed by post-processor):
public string JunctionReplacementMaterial { get; init; } = string.Empty;
public float JunctionReplacementWidth { get; init; }
public float JunctionReplacementTextureLength { get; init; }
```

Note: `GeneratedDecalRoad` is never serialized to JSON user configs — it only lives in memory during generation. No backwards-compat property needed here.

---

## Step 4: Update `DecalRoadGenerator` to propagate new fields

**File:** Modify `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

### 4a. `GenerateForLayerRange()` — set metadata on generated road

- [ ] In the `GeneratedDecalRoad` constructor (around line 391), replace and add:

```csharp
// Replace:
//   InterruptAtJunctions = layer.InterruptAtJunctions,
// With:
JunctionConstraint = layer.JunctionConstraint,
JunctionReplacementMaterial = layer.JunctionReplacementMaterial,
JunctionReplacementWidth = layer.JunctionReplacementWidth > 0
    ? layer.JunctionReplacementWidth : (overrideWidth ?? layer.Width),
JunctionReplacementTextureLength = layer.JunctionReplacementTextureLength > 0
    ? layer.JunctionReplacementTextureLength : (overrideTextureLength ?? layer.TextureLength),
```

Note: width/textureLength fallback to the segment's effective values (respecting curve overrides), not just the layer default.

### 4b. `Generate()` — propagate in chunking block

- [ ] In the chunking `new GeneratedDecalRoad` (around line 115), replace and add:

```csharp
// Replace:
//   InterruptAtJunctions = road.InterruptAtJunctions,
// With:
JunctionConstraint = road.JunctionConstraint,
JunctionReplacementMaterial = road.JunctionReplacementMaterial,
JunctionReplacementWidth = road.JunctionReplacementWidth,
JunctionReplacementTextureLength = road.JunctionReplacementTextureLength,
```

---

## Step 5: Update `DecalRoadOverlapPostProcessor`

**File:** Modify `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadOverlapPostProcessor.cs`

This is the core logic change. Currently, interruptable roads are split (overlapping nodes discarded). For Replace mode, overlapping nodes keep their positions but get a different material/width.

### 5a. Update classification in `Process()`

- [ ] Update the classification block to handle the new mode. Replace the `InterruptAtJunctions` checks:

```csharp
// Classification: Replace the current InterruptAtJunctions-based branching:
foreach (var road in allRoads)
{
    if (road.IsAIRoad)
    {
        aiRoads.Add(road);
    }
    else if (road.JunctionConstraint == JunctionConstraintMode.None)
    {
        surfaceRoads.Add(road);
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
```

Both `Interrupt` and `Replace` roads are classified as "interruptable" — they both need overlap detection. The difference is what happens to overlapping nodes.

### 5b. Update `SplitOpenRoad()` to support Replace mode

- [ ] Replace the `SplitOpenRoad` method. For Interrupt mode, behavior is unchanged (discard overlapping runs). For Replace mode, emit TWO roads: one with non-overlapping nodes (original material), one with overlapping nodes (replacement material). Both need ≥ 3 contiguous nodes.

```csharp
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
```

### 5c. Add helper methods

- [ ] Extract existing fragment-building into `BuildFragments()` and add `BuildReplacementFragments()`:

```csharp
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
```

### 5d. Update `SplitClosedLoopRoad()` for Replace mode

- [ ] Apply the same pattern to `SplitClosedLoopRoad()`. For Replace mode, after computing the overlap mask and if there are overlapping nodes, emit replacement fragments in addition to the non-overlapping fragments. Use the same rotated-view approach for collecting contiguous overlapping runs that wrap around the seam.

The simplest approach: after computing `isOverlapping[]`, call `BuildFragments` for non-overlapping runs (existing behavior), then call `BuildReplacementFragments` for overlapping runs. The rotation logic from the existing closed-loop handling only matters for the non-overlapping fragments (they wrap around the seam). The replacement fragments use a linear scan without wrap-around — this is safe because junction overlap zones on roundabouts are at connecting-road intersections and are always short, never spanning the full ring to wrap around the seam.

```csharp
private static List<GeneratedDecalRoad> SplitClosedLoopRoad(
    GeneratedDecalRoad road,
    SurfaceFootprintIndex index,
    IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
{
    var nodes = road.Nodes;
    var isOverlapping = ComputeOverlapMask(road, index, continuityLookup);

    if (isOverlapping.All(x => x))
    {
        // All overlapping: for Interrupt → discard, for Replace → emit single replacement road
        if (road.JunctionConstraint == JunctionConstraintMode.Replace)
            return BuildReplacementFragments(road, nodes, isOverlapping);
        return [];
    }

    if (isOverlapping.All(x => !x))
        return [road];

    // Existing rotation-based splitting for non-overlapping fragments...
    // [keep existing code for collecting non-overlapping runs via rotation]

    // For Replace mode, also emit replacement fragments for overlapping runs
    if (road.JunctionConstraint == JunctionConstraintMode.Replace)
        fragments.AddRange(BuildReplacementFragments(road, nodes, isOverlapping));

    return fragments;
}
```

### 5e. Update `ComputeOverlapMask()` — no changes needed

The mask computation is the same for both Interrupt and Replace modes. The `PreserveContinuity` check already handles DirectionDivider exemptions correctly.

---

## Step 6: Update `DecalRoadDefaultLayerSets`

**File:** Modify `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`

- [ ] Replace all `InterruptAtJunctions = true` with `JunctionConstraint = JunctionConstraintMode.Interrupt` and all `InterruptAtJunctions = false` with `JunctionConstraint = JunctionConstraintMode.None`.

Use find-and-replace:
- `InterruptAtJunctions = true` → `JunctionConstraint = JunctionConstraintMode.Interrupt`
- `InterruptAtJunctions = false` → `JunctionConstraint = JunctionConstraintMode.None`

---

## Step 7: Migrate all `InterruptAtJunctions` references

**Files:** All files referencing `InterruptAtJunctions` (search codebase-wide)

- [ ] Search all remaining references to `InterruptAtJunctions` across the codebase (excluding the `InterruptAtJunctionsCompat` deserialization property in `DecalRoadLayerDefinition`). Replace each with the appropriate `JunctionConstraint` comparison:
  - `layer.InterruptAtJunctions` → `layer.JunctionConstraint != JunctionConstraintMode.None`
  - `road.InterruptAtJunctions` → `road.JunctionConstraint != JunctionConstraintMode.None`
  - `road.InterruptAtJunctions` in `GeneratedDecalRoad.cs` → `road.JunctionConstraint != JunctionConstraintMode.None`

- [ ] Keep the `InterruptAtJunctionsCompat` setter-only property in `DecalRoadLayerDefinition.cs` — it is needed permanently for backwards-compatible JSON deserialization of old saved configs.

---

## Step 8: Update UI — `DecalRoadLayerSetEditor.razor`

**File:** Modify `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor`

### 8a. Replace checkbox with radio-group toggle

- [ ] Remove the `InterruptAtJunctions` checkbox from the "Geometry & Sizing" section (lines 286-288).

- [ ] Add a new "Junction Handling" section inside "Generation Constraints" (before the existing Curve Constraints block at line 393), modeled exactly on the existing Curve Constraints pattern:

```razor
@* ───── JUNCTION CONSTRAINT ───── *@
<MudItem xs="12">
    <div class="d-flex align-center gap-1">
        <MudCheckBox T="bool"
                     Value="@(layer.JunctionConstraint != JunctionConstraintMode.None)"
                     ValueChanged="@(v => { layer.JunctionConstraint = v ? JunctionConstraintMode.Interrupt : JunctionConstraintMode.None; })"
                     Label="Junction Handling" Color="Color.Info"
                     Dense="true" Disabled="@ReadOnly" />
        <MudTooltip Text="Controls how this layer behaves where it overlaps another road's surface at junctions.">
            <MudIcon Icon="@Icons.Material.Filled.HelpOutline" Size="Size.Small"
                     Color="Color.Default" Style="opacity:0.6" />
        </MudTooltip>
    </div>
</MudItem>
@if (layer.JunctionConstraint != JunctionConstraintMode.None)
{
    <MudItem xs="12">
        <MudRadioGroup T="JunctionConstraintMode" @bind-Value="layer.JunctionConstraint">
            <MudRadio T="JunctionConstraintMode" Value="JunctionConstraintMode.Interrupt"
                      Color="Color.Info" Dense="true" Disabled="@ReadOnly">
                Interrupt
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    Layer is removed where it overlaps another road
                </MudText>
            </MudRadio>
            <MudRadio T="JunctionConstraintMode" Value="JunctionConstraintMode.Replace"
                      Color="Color.Info" Dense="true" Disabled="@ReadOnly">
                Replace at Junction
            </MudRadio>
        </MudRadioGroup>
    </MudItem>
    @if (layer.JunctionConstraint == JunctionConstraintMode.Replace)
    {
        @* Replacement fields — identical layout to CurveReplacement panel *@
        <MudItem xs="12" sm="4">
            <div class="d-flex align-center gap-2">
                <div class="flex-grow-1">
                    @if (AvailableMaterials.Count > 0)
                    {
                        <MudAutocomplete T="string"
                                         @bind-Value="layer.JunctionReplacementMaterial"
                                         Label="Replacement Material"
                                         Variant="Variant.Outlined"
                                         SearchFunc="SearchMaterials"
                                         CoerceValue="false"
                                         Clearable="true" Dense="true" MaxItems="50"
                                         Disabled="@ReadOnly"
                                         AdornmentIcon="@Icons.Material.Filled.Search"
                                         Adornment="Adornment.Start">
                            <ItemTemplate>
                                <div class="d-flex align-center gap-2">
                                    <MudText Typo="Typo.body2">@context</MudText>
                                    @{
                                        var badge = GetMaterialSourceBadge(context);
                                    }
                                    @if (!string.IsNullOrEmpty(badge))
                                    {
                                        <MudChip T="string" Size="Size.Small"
                                                 Variant="Variant.Outlined"
                                                 Color="@(badge == "game" ? Color.Info : Color.Success)">
                                            @badge
                                        </MudChip>
                                    }
                                </div>
                            </ItemTemplate>
                        </MudAutocomplete>
                    }
                    else
                    {
                        <MudTextField @bind-Value="layer.JunctionReplacementMaterial"
                                      Label="Replacement Material"
                                      Variant="Variant.Outlined"
                                      Disabled="@ReadOnly" />
                    }
                </div>
                <MudIconButton Icon="@Icons.Material.Filled.Preview"
                               Color="Color.Primary" Size="Size.Small"
                               Disabled="@(string.IsNullOrEmpty(layer.JunctionReplacementMaterial))"
                               OnClick="() => PreviewMaterial(layer.JunctionReplacementMaterial)"
                               Title="Preview material in 3D viewer" />
            </div>
        </MudItem>
        <MudItem xs="6" sm="4">
            <MudNumericField T="float"
                             @bind-Value="layer.JunctionReplacementWidth"
                             Label="Replacement Width (m)"
                             Variant="Variant.Outlined"
                             Min="0.0f" Step="0.05f"
                             HelperText="0 = same as main"
                             Disabled="@ReadOnly" />
        </MudItem>
        <MudItem xs="6" sm="4">
            <MudNumericField T="float"
                             @bind-Value="layer.JunctionReplacementTextureLength"
                             Label="Replacement Tex Length (m)"
                             Variant="Variant.Outlined"
                             Min="0.0f" Step="1.0f"
                             HelperText="0 = same as main"
                             Disabled="@ReadOnly" />
        </MudItem>
    }
}
```

### 8b. Update collapsed header chip

- [ ] Replace the `InterruptAtJunctions` chip (lines 101-104) with mode-specific chips:

```razor
@if (layer.JunctionConstraint == JunctionConstraintMode.Interrupt)
{
    <MudChip T="string" Size="Size.Small" Variant="Variant.Text">jnc</MudChip>
}
@if (layer.JunctionConstraint == JunctionConstraintMode.Replace)
{
    <MudChip T="string" Size="Size.Small" Variant="Variant.Text"
             Color="Color.Info">jnc-repl</MudChip>
}
```

---

## Step 9: Update `DeepCopyLayer()`

**File:** Modify `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs`

- [ ] Replace `InterruptAtJunctions` and add the new fields in `DeepCopyLayer()` (around line 124):

```csharp
// Replace:
//   InterruptAtJunctions = source.InterruptAtJunctions,
// With:
JunctionConstraint = source.JunctionConstraint,
JunctionReplacementMaterial = source.JunctionReplacementMaterial,
JunctionReplacementWidth = source.JunctionReplacementWidth,
JunctionReplacementTextureLength = source.JunctionReplacementTextureLength,
```

---

## Step 10: Build and verify

- [ ] Run `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj` — expect 0 errors
- [ ] Run `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj` — expect 0 errors (DLL locks from running app are not code errors)
- [ ] Search for any remaining `InterruptAtJunctions` references that still use the old bool pattern — should be zero outside of the `[JsonIgnore]` shim (which was removed in Step 7)

---

## Verification (manual in-game)

1. Set a LaneMarking layer to `Replace at Junction` with `m_line_white` (solid) as replacement
2. Generate terrain with a T-junction
3. Verify: dashed line on straight sections, solid line through junction zone
4. Verify: edge blends still fully interrupted (Interrupt mode unchanged)
5. Verify: roundabout ring markings still interrupted at connecting roads
6. Verify: AI roads unaffected
7. Verify: existing saved layer sets with `InterruptAtJunctions: true` in JSON deserialize correctly to `JunctionConstraint: "Interrupt"` (backwards compat via the shim, or test that old JSON with `"InterruptAtJunctions": true` still loads)

---

## JSON Backwards Compatibility

Resolved in Step 2: the `InterruptAtJunctionsCompat` setter-only property in `DecalRoadLayerDefinition` handles deserialization of old JSON files containing `"interruptAtJunctions": true/false`. The `DecalRoadDefaultsManager` uses `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, so new JSON files will serialize as `"junctionConstraint": "interrupt"`. Both old and new formats are supported simultaneously — the setter maps `true` → `Interrupt`, `false` → `None`.
