# Per-Segment Road Width Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make road width vary per-segment based on OSM lane counts and width tags, so the entire terrain generation pipeline uses dynamic widths instead of global per-spline values.

**Architecture:** Pre-computed `RoadWidthProfile` attached to each `ParameterizedRoadSpline`, built during network construction from a 5-level priority chain (OSM width tag > est_width > lane calc > layerset defaults > parameters). All pipeline consumers query width at a distance instead of reading global parameters.

**Tech Stack:** .NET 9, C#, Blazor/MudBlazor v8, BeamNG terrain generation pipeline

**Spec:** `docs/superpowers/specs/2026-03-24-per-segment-road-width-design.md`

---

## Task 1: OSM Width Tag Parsing on OsmLaneInfo

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs`

- [ ] **Step 1: Add WidthMeters and EstWidthMeters properties**

In `OsmLaneInfo.cs`, add after the existing "Stored for future use" fields (after line 18):

```csharp
// Parsed from width=* and est_width=* tags
public float? WidthMeters { get; set; }
public float? EstWidthMeters { get; set; }
```

- [ ] **Step 2: Add TryParseWidth static helper**

Add this static method to `OsmLaneInfo` (before `TryParse`):

```csharp
/// <summary>
/// Parses an OSM width value string with unit support.
/// Supports: bare number (meters), "m", "km", "mi", "ft", feet'inches" formats.
/// Normalizes Unicode quote characters (U+2019, U+2032, U+2033) to ASCII.
/// </summary>
public static bool TryParseWidth(string? value, out float meters)
{
    meters = 0f;
    if (string.IsNullOrWhiteSpace(value)) return false;

    // Normalize Unicode quotes to ASCII
    var s = value.Trim()
        .Replace('\u2019', '\'')  // right single quotation mark
        .Replace('\u2032', '\'')  // prime
        .Replace('\u2033', '"')   // double prime
        .Replace("''", "\"");     // two single quotes → double quote

    // Try feet+inches: 25'6" or 25'
    var feetMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*'(?:\s*(\d+(?:\.\d+)?)\s*""?\s*)?$");
    if (feetMatch.Success)
    {
        var feet = float.Parse(feetMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var inches = feetMatch.Groups[2].Success
            ? float.Parse(feetMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0f;
        meters = (feet + inches / 12f) * 0.3048f;
        return meters > 0;
    }

    // Try number + unit suffix
    var unitMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(m|km|mi|ft)?$");
    if (unitMatch.Success)
    {
        var num = float.Parse(unitMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = unitMatch.Groups[2].Success ? unitMatch.Groups[2].Value : "m";
        meters = unit switch
        {
            "km" => num * 1000f,
            "mi" => num * 1609.344f,
            "ft" => num * 0.3048f,
            _ => num  // "m" or bare number
        };
        return meters > 0;
    }

    return false;
}
```

- [ ] **Step 3: Update TryParse to parse width tags and return non-null for width-only ways**

In `TryParse()` (line 36), after the lane parsing block that currently returns `null` at line 101 when no lane tags exist, restructure:

Replace the `// Priority 7: no lane tags` block and the code after it. The method should:
1. Still parse lanes as before (priorities 1-6)
2. After lane parsing, parse width tags regardless of whether lanes were found
3. If no lane tags AND no width tags: return `null` (unchanged behavior)
4. If no lane tags BUT width tags exist: return `OsmLaneInfo` with `TotalLanes = 0` and width fields set

At the end of `TryParse()`, before the final `return info;`, add:

```csharp
if (tags.TryGetValue("width", out var widthStr) && TryParseWidth(widthStr, out var widthM))
    info.WidthMeters = widthM;

if (tags.TryGetValue("est_width", out var estWidthStr) && TryParseWidth(estWidthStr, out var estWidthM))
    info.EstWidthMeters = estWidthM;
```

And change the `// Priority 7: no lane tags` else block: instead of returning `null`, check if width tags are present. If width tags parsed successfully, create the `OsmLaneInfo` with `TotalLanes = 0` and continue to the width parsing. If neither lane nor width tags exist, return `null`.

- [ ] **Step 4: Update Reversed() to carry width fields**

In `Reversed()` (line 20), add to the object initializer:

```csharp
WidthMeters = WidthMeters,
EstWidthMeters = EstWidthMeters,
```

- [ ] **Step 5: Build and verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/OsmLaneInfo.cs
git commit -m "feat: add OSM width/est_width tag parsing to OsmLaneInfo"
```

---

## Task 2: Update AreLaneConfigsEqual for Width Fields

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs:86`

- [ ] **Step 1: Read current AreLaneConfigsEqual method**

Read `LaneSegmentOps.cs` around line 86 to understand the current comparison logic.

- [ ] **Step 2: Add width field comparisons**

In `AreLaneConfigsEqual` (line 86), add comparisons for `WidthMeters` and `EstWidthMeters` to the equality check. These are `float?` so use `==` which handles null comparison correctly for nullable value types.

Add to the boolean expression:
```csharp
&& a.WidthMeters == b.WidthMeters
&& a.EstWidthMeters == b.EstWidthMeters
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/LaneSegmentOps.cs
git commit -m "feat: include width fields in AreLaneConfigsEqual to prevent consolidation loss"
```

---

## Task 3: New Data Models (WidthSegment, WidthSource, RoadWidthProfile)

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/WidthSegment.cs`
- Create: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadWidthProfile.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs:63`

- [ ] **Step 1: Create WidthSegment.cs and WidthSource enum**

Create `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/WidthSegment.cs`:

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

public class WidthSegment
{
    public float StartDistance { get; set; }
    public float RoadSurfaceWidth { get; set; }
    public float SmoothingCorridorWidth { get; set; }
    public float MasterSplineWidth { get; set; }
    public int LaneCount { get; set; }
    public WidthSource Source { get; set; }
}

public enum WidthSource
{
    OsmWidthTagExact,
    OsmWidthTagEstimated,
    LaneCalculation,
    LayerSetDefault,
    ParameterFallback
}
```

- [ ] **Step 2: Create RoadWidthProfile.cs**

Create `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadWidthProfile.cs`:

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

public class RoadWidthProfile
{
    public List<WidthSegment> Segments { get; }
    public float TransitionLengthMeters { get; set; } = 15f;

    public RoadWidthProfile(List<WidthSegment> segments)
    {
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        if (segments.Count == 0)
            throw new ArgumentException("At least one segment required.", nameof(segments));
    }

    /// <summary>
    /// Returns interpolated (surface, corridor, masterSpline) widths at the given distance.
    /// Uses binary search + linear interpolation in transition zones.
    /// </summary>
    public (float surface, float corridor, float masterSpline) GetWidthsAtDistance(float distance)
    {
        if (Segments.Count == 1)
            return (Segments[0].RoadSurfaceWidth, Segments[0].SmoothingCorridorWidth, Segments[0].MasterSplineWidth);

        // Binary search for the segment containing this distance
        int idx = 0;
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            if (distance >= Segments[i].StartDistance)
            {
                idx = i;
                break;
            }
        }

        var current = Segments[idx];

        // Check if we're in a transition zone to the next segment
        if (TransitionLengthMeters > 0 && idx < Segments.Count - 1)
        {
            var next = Segments[idx + 1];
            var halfTransition = TransitionLengthMeters / 2f;
            var boundary = next.StartDistance;

            if (distance >= boundary - halfTransition)
            {
                // In transition zone: interpolate
                var t = (distance - (boundary - halfTransition)) / TransitionLengthMeters;
                t = Math.Clamp(t, 0f, 1f);
                return (
                    surface: Lerp(current.RoadSurfaceWidth, next.RoadSurfaceWidth, t),
                    corridor: Lerp(current.SmoothingCorridorWidth, next.SmoothingCorridorWidth, t),
                    masterSpline: Lerp(current.MasterSplineWidth, next.MasterSplineWidth, t)
                );
            }
        }

        // Check if we're in a transition zone from the previous segment
        if (TransitionLengthMeters > 0 && idx > 0)
        {
            var prev = Segments[idx - 1];
            var halfTransition = TransitionLengthMeters / 2f;
            var boundary = current.StartDistance;

            if (distance < boundary + halfTransition)
            {
                var t = (distance - (boundary - halfTransition)) / TransitionLengthMeters;
                t = Math.Clamp(t, 0f, 1f);
                return (
                    surface: Lerp(prev.RoadSurfaceWidth, current.RoadSurfaceWidth, t),
                    corridor: Lerp(prev.SmoothingCorridorWidth, current.SmoothingCorridorWidth, t),
                    masterSpline: Lerp(prev.MasterSplineWidth, current.MasterSplineWidth, t)
                );
            }
        }

        return (current.RoadSurfaceWidth, current.SmoothingCorridorWidth, current.MasterSplineWidth);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
```

- [ ] **Step 3: Add WidthProfile property to ParameterizedRoadSpline**

In `ParameterizedRoadSpline.cs`, add near the `LaneSegments` property (line 63):

```csharp
public RoadWidthProfile? WidthProfile { get; set; }
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/WidthSegment.cs BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadWidthProfile.cs BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs
git commit -m "feat: add WidthSegment, RoadWidthProfile, and WidthProfile on ParameterizedRoadSpline"
```

---

## Task 4: DecalRoadLayerSet Model + UI + Defaults

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs`

- [ ] **Step 1: Add margin fields to DecalRoadLayerSet model**

In `DecalRoadLayerSet.cs` (10-line file), add after `DefaultLaneWidth`:

```csharp
public bool EnablePerSegmentWidth { get; set; } = true;
public float SmoothingCorridorMargin { get; set; } = 2.0f;
public float MasterSplineMargin { get; set; } = 0.0f;
```

- [ ] **Step 2: Add UI fields in DecalRoadLayerSetEditor.razor**

In `DecalRoadLayerSetEditor.razor`, find the `DefaultLaneWidth` field (around line 31). After it, add two new fields in the same row style:

The existing fields use `@bind-Value` with `Variant.Outlined` and `Disabled="@ReadOnly"` (no `ValueChanged`). Follow the same pattern exactly. Add a toggle and two new `<MudItem>` blocks inside the existing `<MudGrid>` (lines 22-38), after the `DefaultLaneWidth` item (line 37):

```razor
    <MudItem xs="12">
        <MudCheckBox T="bool" @bind-Value="LayerSet.EnablePerSegmentWidth"
                     Label="Enable per-segment width from OSM lane/width data"
                     Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="LayerSet.SmoothingCorridorMargin"
                         Label="Smoothing Margin (m)"
                         Variant="Variant.Outlined"
                         Min="0.0f" Max="10.0f" Step="0.5f"
                         Format="F1"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="6" sm="3">
        <MudNumericField T="float" @bind-Value="LayerSet.MasterSplineMargin"
                         Label="Spline Margin (m)"
                         Variant="Variant.Outlined"
                         Min="-5.0f" Max="10.0f" Step="0.5f"
                         Format="F1"
                         Disabled="@ReadOnly" />
    </MudItem>
```

Note: Do NOT use both `@bind-Value` and `ValueChanged` together — that causes a Blazor compile error. The existing fields use only `@bind-Value`.

- [ ] **Step 3: Update DecalRoadDefaultLayerSets with road-type-specific defaults**

In `DecalRoadDefaultLayerSets.cs`, update `CreateAsphaltRoadSet` (line 213) to accept and set the new margin fields. Update each road type's `DefaultLaneWidth` and margins per the spec table:

| Road Type | DefaultLaneCount | DefaultLaneWidth | SmoothingCorridorMargin | MasterSplineMargin |
|-----------|-----------------|------------------|------------------------|--------------------|
| Motorway | 4 | 3.5f | 2.0f | 0.0f |
| Trunk | 4 | 3.5f | 2.0f | 0.0f |
| Primary | 2 | 3.5f | 2.0f | 0.0f |
| Secondary | 2 | 3.5f | 2.0f | 0.0f |
| Tertiary | 2 | 3.0f | 2.0f | 0.0f |
| Residential | 2 | 3.0f | 2.0f | 0.0f |
| Service | 2 | 2.75f | 1.5f | 0.0f |
| Unclassified | 2 | 3.0f | 2.0f | 0.0f |
| Track | 1 | 2.5f | 1.0f | 0.0f |
| Roundabout | 1 | 3.5f | 2.0f | 0.0f |

**Method signature changes required:**

- `CreateAsphaltRoadSet(string name, int lanes)` at line 213 currently returns `new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers }`. The `DefaultLaneWidth` is never set (uses model default 3.5f). Change the signature to `CreateAsphaltRoadSet(string name, int lanes, float laneWidth = 3.5f, float smoothingMargin = 2.0f, float splineMargin = 0.0f)` and set all four properties in the return statement.
- `CreateRoundaboutSet(string name, int lanes)` at line 216 — same pattern: add `laneWidth`, `smoothingMargin`, `splineMargin` parameters.
- `CreateTrackSet(string name, int lanes)` — same pattern. **Note:** Track currently uses `CreateTrackSet("Track", 2)` but the spec requires `DefaultLaneCount = 1`. Change the call to pass `lanes: 1, laneWidth: 2.5f, smoothingMargin: 1.0f`.
- In `GetDefaults()` (line 29), update each road type's `Create*Set` call to pass the per-type values from the table above. For example: `{"tertiary", CreateAsphaltRoadSet("Tertiary", 2, laneWidth: 3.0f)}`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs
git commit -m "feat: add SmoothingCorridorMargin/MasterSplineMargin to layerset model, UI, and defaults"
```

---

## Task 5: Width Profile Construction in UnifiedRoadNetworkBuilder

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (caller)
- Modify: `BeamNgTerrainPoc/Terrain/Services/TerrainAnalyzer.cs` (caller)

- [ ] **Step 1: Add DecalRoad parameters to BuildNetwork signature**

`BuildNetwork()` (line 46) currently takes `(List<MaterialDefinition> materials, float[,] heightMap, float metersPerPixel, int terrainSize, bool flipMaterialProcessingOrder)`. It does NOT have access to `DecalRoadSettings` or the layerset defaults dictionary.

Add two new optional parameters:

```csharp
public UnifiedRoadNetwork BuildNetwork(
    List<MaterialDefinition> materials,
    float[,] heightMap,
    float metersPerPixel,
    int terrainSize,
    bool flipMaterialProcessingOrder = true,
    DecalRoadSettings? decalRoadSettings = null,
    IReadOnlyDictionary<string, DecalRoadLayerSet>? appDataDefaults = null)
```

Add required `using` directives for `DecalRoadSettings`, `DecalRoadLayerSet`, `DecalRoadLayerSetResolver`, `WidthSegment`, `WidthSource`, `RoadWidthProfile`.

- [ ] **Step 1b: Update callers to pass the new parameters**

There are two callers:
- `UnifiedRoadSmoother.cs` line 127: `_networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size, flipMaterialProcessingOrder)` — pass `decalRoadSettings` and `appDataDefaults` if available in that class. Read the class to find how to thread them through.
- `TerrainAnalyzer.cs` line 140: `_networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size)` — same approach.

If these callers don't have `DecalRoadSettings`/`appDataDefaults`, the new params are optional (default `null`), so existing calls still compile. Width profiles won't be built in those paths (falls back to `RoadSmoothingParameters`), which is the correct fallback behavior.

- [ ] **Step 2: Read current spline creation code**

Read `UnifiedRoadNetworkBuilder.cs` around lines 90-120 where `ParameterizedRoadSpline` is created and `LaneSegments` is assigned (line 104).

- [ ] **Step 2: Add width profile construction method**

Add a private method to `UnifiedRoadNetworkBuilder`:

```csharp
private static RoadWidthProfile? BuildWidthProfile(
    ParameterizedRoadSpline spline,
    DecalRoadLayerSet? layerSet)
{
    if (layerSet == null) return null;

    var segments = new List<WidthSegment>();

    // When per-segment width is disabled, use layerset defaults as a single uniform segment
    if (layerSet.EnablePerSegmentWidth && spline.LaneSegments is { Count: > 0 })
    {
        foreach (var ls in spline.LaneSegments)
        {
            float surfaceWidth;
            WidthSource source;

            if (ls.LaneInfo.WidthMeters.HasValue)
            {
                surfaceWidth = ls.LaneInfo.WidthMeters.Value;
                source = WidthSource.OsmWidthTagExact;
            }
            else if (ls.LaneInfo.EstWidthMeters.HasValue)
            {
                surfaceWidth = ls.LaneInfo.EstWidthMeters.Value;
                source = WidthSource.OsmWidthTagEstimated;
            }
            else if (ls.LaneInfo.TotalLanes > 0)
            {
                surfaceWidth = ls.LaneInfo.TotalLanes * layerSet.DefaultLaneWidth;
                source = WidthSource.LaneCalculation;
            }
            else
            {
                surfaceWidth = layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth;
                source = WidthSource.LayerSetDefault;
            }

            segments.Add(new WidthSegment
            {
                StartDistance = ls.StartDistance,
                RoadSurfaceWidth = surfaceWidth,
                SmoothingCorridorWidth = surfaceWidth + 2 * layerSet.SmoothingCorridorMargin,
                MasterSplineWidth = surfaceWidth + 2 * layerSet.MasterSplineMargin,
                LaneCount = ls.LaneInfo.TotalLanes,
                Source = source
            });
        }
    }
    else
    {
        // No lane segments — single uniform width from layerset defaults
        var surfaceWidth = layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth;
        segments.Add(new WidthSegment
        {
            StartDistance = 0f,
            RoadSurfaceWidth = surfaceWidth,
            SmoothingCorridorWidth = surfaceWidth + 2 * layerSet.SmoothingCorridorMargin,
            MasterSplineWidth = surfaceWidth + 2 * layerSet.MasterSplineMargin,
            LaneCount = layerSet.DefaultLaneCount,
            Source = WidthSource.LayerSetDefault
        });
    }

    return new RoadWidthProfile(segments);
}
```

- [ ] **Step 3: Call BuildWidthProfile after spline creation**

After the line where `LaneSegments` is assigned (line 104), resolve the layerset and attach the width profile:

```csharp
// Resolve layerset for width profile
var layerSet = DecalRoadLayerSetResolver.Resolve(
    paramSpline.OsmRoadType, paramSpline.MaterialName, decalRoadSettings, appDataDefaults);
paramSpline.WidthProfile = BuildWidthProfile(paramSpline, layerSet);
```

The `decalRoadSettings` and `appDataDefaults` are now available as method parameters from Step 1.

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs
git commit -m "feat: build RoadWidthProfile from OSM lane/width data during network construction"
```

---

## Task 6: Update UnifiedCrossSection.FromSplineSample (Key Bottleneck)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs:266`

- [ ] **Step 1: Read FromSplineSample method**

Read `UnifiedCrossSection.cs` around lines 251-280 to see how `EffectiveRoadWidth` is currently set from `ownerSpline.Parameters.RoadWidthMeters`.

- [ ] **Step 2: Update EffectiveRoadWidth to use WidthProfile**

At line 266 where `EffectiveRoadWidth` is set, change from:

```csharp
EffectiveRoadWidth = ownerSpline.Parameters.RoadWidthMeters,
```

to:

```csharp
EffectiveRoadWidth = ownerSpline.WidthProfile?.GetWidthsAtDistance(sample.Distance).corridor
    ?? ownerSpline.Parameters.RoadWidthMeters,
```

`EffectiveBlendRange` stays unchanged (reads `TerrainAffectedRangeMeters`).

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs
git commit -m "feat: use per-segment corridor width in UnifiedCrossSection.FromSplineSample"
```

---

## Task 7: Update MedialAxisRoadExtractor

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/MedialAxisRoadExtractor.cs`

- [ ] **Step 1: Read current width usage**

Read `MedialAxisRoadExtractor.cs` around lines 91-103 and 169-182 where `CrossSection.WidthMeters` is set from `parameters.RoadWidthMeters`.

- [ ] **Step 2: Update cross-section width to use WidthProfile**

At each site where `WidthMeters = parameters.RoadWidthMeters` is set, change to:

```csharp
WidthMeters = paramSpline.WidthProfile?.GetWidthsAtDistance(distance).corridor
    ?? parameters.RoadWidthMeters,
```

The `paramSpline` variable should be available in scope (check the method parameters). The `distance` is the cumulative distance along the spline at the current sample point.

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/MedialAxisRoadExtractor.cs
git commit -m "feat: use per-segment corridor width in MedialAxisRoadExtractor"
```

---

## Task 8: Update Elevation Smoothing Consumers (Blenders + Smoother)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/PostProcessingSmoother.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/DistanceFieldTerrainBlender.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

These consumers already receive cross-sections that now carry per-point `EffectiveRoadWidth` from Task 6. The changes here are for sites that read `s.Parameters.RoadWidthMeters` directly instead of using cross-section width.

- [ ] **Step 1: Update SinglePassBlender.cs (line 90)**

This file has one usage at line 90: `s.Parameters.RoadWidthMeters / 2.0f` in a LINQ projection that builds a lookup dictionary keyed by `SplineId`. This runs BEFORE cross-sections are iterated — it pre-computes `HalfWidth` per spline.

**Strategy:** This is a per-spline lookup used during blending. Since width now varies per cross-section, the `HalfWidth` should come from each cross-section's `EffectiveRoadWidth` instead. Read the blending loop to see how `HalfWidth` is consumed and refactor to use `cs.EffectiveRoadWidth / 2.0f` at the point of use instead of the pre-computed dictionary value. If the dictionary pattern is deeply embedded, use the spline's first segment width as a reasonable approximation: `s.WidthProfile?.Segments[0].SmoothingCorridorWidth / 2.0f ?? s.Parameters.RoadWidthMeters / 2.0f`.

- [ ] **Step 2: Update PostProcessingSmoother.cs (line 94)**

One usage at line 94: `s.Parameters.RoadWidthMeters` passed into a tuple for smoothing mask parameters. This is per-spline, used for mask construction. Same strategy as SinglePassBlender: use cross-section width if available during mask iteration, or use `s.WidthProfile?.Segments[0].SmoothingCorridorWidth ?? s.Parameters.RoadWidthMeters` for the per-spline precomputation.

- [ ] **Step 3: Update DistanceFieldTerrainBlender.cs (4 usages)**

Four usages at lines 56, 70, 81, 547:
- **Line 56:** `BuildRoadCoreMask(..., parameters.RoadWidthMeters)` — this builds a raster mask using cross-sections. The method receives geometry (cross-sections) which now carry per-point `EffectiveRoadWidth`. Read `BuildRoadCoreMask` to see if it uses the passed width or the cross-section widths. If it uses the passed scalar, refactor to use per-cross-section width inside the method. If it's a simple threshold, use the max width across all cross-sections.
- **Line 70:** `BuildElevationMap(..., parameters.RoadWidthMeters, ...)` — same pattern, reads cross-sections.
- **Line 81:** `parameters.RoadWidthMeters / 2.0f` — used for blend function. Use per-cross-section width.
- **Line 547:** `parameters.RoadWidthMeters / 2.0f + parameters.SmoothingMaskExtensionMeters` — smoothing mask distance. Use max cross-section width for the mask extent.

- [ ] **Step 4: Update UnifiedRoadSmoother.cs (5 usages)**

Five usages at lines 866, 1178, 1233, 1333:
- **Line 866:** `GetEffectiveBlendDistance(contributor.Spline.Parameters.RoadWidthMeters)` — junction harmonization. Use `WidthProfile` query: `contributor.Spline.WidthProfile?.GetWidthsAtDistance(distanceAtEndpoint).corridor ?? contributor.Spline.Parameters.RoadWidthMeters`.
- **Lines 1178 and 1233:** `paramSpline.Parameters.RoadWidthMeters / 2.0f` — used for outline rendering in debug visualization. These iterate cross-sections, so use `cs.EffectiveRoadWidth / 2.0f`.
- **Line 1333:** `network.Splines.Max(s => s.Parameters.RoadWidthMeters) / 2.0f` — computes max half-width across all splines for outline bounds. Use `network.Splines.Max(s => s.WidthProfile?.Segments.Max(seg => seg.SmoothingCorridorWidth) ?? s.Parameters.RoadWidthMeters) / 2.0f`.

- [ ] **Step 6: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs BeamNgTerrainPoc/Terrain/Algorithms/Blending/PostProcessingSmoother.cs BeamNgTerrainPoc/Terrain/Algorithms/DistanceFieldTerrainBlender.cs BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat: use per-segment width in elevation smoothing consumers"
```

---

## Task 9: Update Junction and Banking Consumers

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Banking/PriorityAwareJunctionBankingCalculator.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs`

- [ ] **Step 1: Read each file and find all RoadWidthMeters usages**

Grep for `RoadWidthMeters` in each file. Note the context for each usage — junction endpoint distance, cross-section iteration, etc.

- [ ] **Step 2: Update UnifiedJunctionProfileBlender.cs (~8 sites)**

For each `contributor.Spline.Parameters.RoadWidthMeters` usage, replace with:

```csharp
contributor.Spline.WidthProfile?.GetWidthsAtDistance(distanceAtJunction).corridor
    ?? contributor.Spline.Parameters.RoadWidthMeters
```

The `distanceAtJunction` should be the distance along the spline at the junction point. Check what distance information is available in context at each call site.

- [ ] **Step 3: Update BankingOrchestrator.cs**

Replace `spline.Parameters.RoadWidthMeters / 2.0f` with per-cross-section width. Since banking iterates over cross-sections, use the cross-section's `EffectiveRoadWidth / 2.0f`.

- [ ] **Step 4: Update PriorityAwareJunctionBankingCalculator.cs**

Replace `s.Parameters.RoadWidthMeters` with `WidthProfile` query at the relevant distance.

- [ ] **Step 5: Update RoundaboutElevationHarmonizer.cs**

Replace `parameters.RoadWidthMeters` with `WidthProfile` query at the junction point distance.

- [ ] **Step 6: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs BeamNgTerrainPoc/Terrain/Algorithms/Banking/PriorityAwareJunctionBankingCalculator.cs BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs
git commit -m "feat: use per-segment width in junction blending and banking"
```

---

## Task 10: Update Material Painting

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/MaterialPainter.cs`

- [ ] **Step 1: Read current width usage**

Read `MaterialPainter.cs` around lines 88 and 323 where `EffectiveRoadSurfaceWidthMeters` is used.

- [ ] **Step 2: Update to use WidthProfile for surface width**

Replace:
```csharp
var surfaceHalfWidth = paramSpline.Parameters.EffectiveRoadSurfaceWidthMeters / 2.0f;
```

With:
```csharp
// Width is now queried per-sample inside the painting loop
```

Inside the painting loop where samples are iterated, query the surface width at each sample's distance:

```csharp
var surfaceWidth = paramSpline.WidthProfile?.GetWidthsAtDistance(sampleDistance).surface
    ?? paramSpline.Parameters.EffectiveRoadSurfaceWidthMeters;
var surfaceHalfWidth = surfaceWidth / 2.0f;
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/MaterialPainter.cs
git commit -m "feat: use per-segment surface width in MaterialPainter"
```

---

## Task 11: Update Master Spline and DecalRoad Export

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`

- [ ] **Step 1: Read MasterSplineExporter width usages**

Read `MasterSplineExporter.cs` and find all `EffectiveMasterSplineWidthMeters` usages (lines ~116, ~203, ~426, ~530). The lines ~426 and ~530 are legacy single-material export paths that read from `RoadSmoothingParameters` directly.

- [ ] **Step 2: Update MasterSplineExporter unified network path**

For the main export path (lines ~116, ~203), replace with per-node width query:

```csharp
var roadWidth = paramSpline.WidthProfile?.GetWidthsAtDistance(nodeDistance).masterSpline
    ?? paramSpline.Parameters.EffectiveMasterSplineWidthMeters;
```

For legacy paths (lines ~426, ~530), pass the `WidthProfile` through if the `ParameterizedRoadSpline` is available, otherwise keep the existing fallback.

- [ ] **Step 3: Update DecalRoadGenerator**

Find `EffectiveMasterSplineWidthMeters` usages and replace with `WidthProfile` query. This aligns with the existing Phase B lane-segment splitting.

- [ ] **Step 4: Update RoadCorridorBuilder**

Replace `EffectiveMasterSplineWidthMeters` with per-sample `WidthProfile` query.

- [ ] **Step 5: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs
git commit -m "feat: use per-segment width in master spline and DecalRoad export"
```

---

## Task 12: Binary Snapshot Round-Trip for Width Data

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs:149`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs`

The binary snapshot system serializes spline data for standalone DecalRoad regeneration. `LaneSegmentSnapshot` (line 149 of `DecalRoadNetworkSnapshot.cs`) currently serializes lane fields but NOT width fields. Width-only ways would lose their width data on round-trip.

- [ ] **Step 1: Add WidthMeters and EstWidthMeters to LaneSegmentSnapshot**

In `DecalRoadNetworkSnapshot.cs`, `LaneSegmentSnapshot` class (line 149), add properties:

```csharp
public float? WidthMeters { get; set; }
public float? EstWidthMeters { get; set; }
```

Update `WriteTo(BinaryWriter w)` (line 159) — after `w.Write(IsOneWay)` add:

```csharp
w.Write(WidthMeters.HasValue);
if (WidthMeters.HasValue) w.Write(WidthMeters.Value);
w.Write(EstWidthMeters.HasValue);
if (EstWidthMeters.HasValue) w.Write(EstWidthMeters.Value);
```

Update `ReadFrom(BinaryReader r)` (line 170) — after `IsOneWay = r.ReadBoolean()` add:

```csharp
WidthMeters = r.ReadBoolean() ? r.ReadSingle() : null,
EstWidthMeters = r.ReadBoolean() ? r.ReadSingle() : null,
```

**Important:** This changes the binary format. Bump `FormatVersion` from 2 to 3 in `DecalRoadNetworkSnapshot` (find the constant). The loader should check the version and handle both v2 (no width fields) and v3 (with width fields).

- [ ] **Step 2: Update DecalRoadNetworkSnapshotBuilder to capture width fields**

In `DecalRoadNetworkSnapshotBuilder.cs`, find where `LaneSegmentSnapshot` objects are created from `LaneSegment` data. Add:

```csharp
WidthMeters = ls.LaneInfo.WidthMeters,
EstWidthMeters = ls.LaneInfo.EstWidthMeters,
```

- [ ] **Step 3: Update DecalRoadNetworkSnapshotLoader to reconstruct RoadWidthProfile**

In `DecalRoadNetworkSnapshotLoader.cs`, after loading spline data and reconstructing `ParameterizedRoadSpline` with `LaneSegments`, call the same `BuildWidthProfile` logic (or a shared static method) to attach `WidthProfile` to each loaded spline. The loader already has access to `DecalRoadSettings` and `appDataDefaults` for layerset resolution.

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs
git commit -m "feat: serialize width fields in binary snapshot and reconstruct RoadWidthProfile on load"
```

---

## Task 13: Update Debug and Logging Consumers (Lower Priority)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/RoadDebugExporter.cs`
- Modify: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

- [ ] **Step 1: Update RoadDebugExporter**

Find all `parameters.RoadWidthMeters` usages in `RoadDebugExporter.cs` and update to use `WidthProfile` query where distance is available, for accurate debug outlines.

- [ ] **Step 2: Update TerrainCreator logging**

In `TerrainCreator.cs`, update the logging line that uses `EffectiveRoadSurfaceWidthMeters` to also log the width profile info if available.

- [ ] **Step 3: Build full solution**

Run: `dotnet build`
Expected: BUILD SUCCEEDED (entire solution)

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/RoadDebugExporter.cs BeamNgTerrainPoc/Terrain/TerrainCreator.cs
git commit -m "feat: update debug exporter and logging for per-segment width"
```

---

## Task 14: Final Build Verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build BeamNG_LevelCleanUp.sln`
Expected: BUILD SUCCEEDED with 0 errors

- [ ] **Step 2: Search for any remaining direct RoadWidthMeters reads that should use WidthProfile**

Run: `grep -rn "\.RoadWidthMeters" --include="*.cs" BeamNgTerrainPoc/` and verify all remaining usages are either:
- Inside `RoadSmoothingParameters` itself (property definition)
- Inside `RoadWidthProfile` or `WidthSegment` (the new model)
- Fallback paths that correctly use `??` with `WidthProfile`

Similarly check for `EffectiveRoadSurfaceWidthMeters` and `EffectiveMasterSplineWidthMeters`.

- [ ] **Step 3: Fix any missed consumers**

If any direct reads remain that should use `WidthProfile`, update them.

- [ ] **Step 4: Final commit if any fixes**

```bash
git add -A
git commit -m "fix: update remaining width parameter consumers to use WidthProfile"
```

---

## Implementation Deviations & Concerns

Recorded after implementation on 2026-03-24.

### Deviations from Plan

1. **Task 5 — Callers not threaded through (FIXED).** The plan suggested threading `DecalRoadSettings` and `appDataDefaults` through `UnifiedRoadSmoother` and `TerrainAnalyzer` callers. Initially the new parameters were left as `null` defaults, making the entire feature a no-op — `WidthProfile` was never constructed because the guard `if (decalRoadSettings != null && appDataDefaults != null)` was always false. **Fix applied:** Added `decalRoadSettings` and `appDataDefaults` optional parameters to `UnifiedRoadSmoother.SmoothAllRoads()`, threaded them to `BuildNetwork()`, and passed them from `TerrainCreator.ApplyRoadSmoothing()` using `terrainParameters.DecalRoadSettings` and `terrainParameters.DecalRoadAppDataDefaults` (falling back to `DecalRoadDefaultLayerSets.GetDefaults()`). Also relaxed the guard in `BuildNetwork` to only require `appDataDefaults != null` (not both), since layerset resolution works with `decalRoadSettings = null`. `TerrainAnalyzer` was not updated (still passes null — width profiles are not needed for terrain analysis).

2. **Task 7 — `WidthProfile` added to `RoadSpline` (not just `ParameterizedRoadSpline`).** The plan only mentioned adding `WidthProfile` to `ParameterizedRoadSpline` (Task 3). The implementer also added it to the base `RoadSpline` class because `MedialAxisRoadExtractor` iterates over `List<RoadSpline>` (from `parameters.PreBuiltSplines`), not `ParameterizedRoadSpline`. This was necessary for the extractor to access per-segment width. The property on `RoadSpline` is currently never populated — it exists only so the fallback path compiles. If pre-built splines need per-segment width in the future, this property is ready.

3. **Task 8 — `DistanceFieldTerrainBlender` partial update.** The plan specified updating 4 usages (lines 56, 70, 81, 547). The implementation updated the internal logic of `BuildRoadCoreMask` and `BuildElevationMap` to use per-cross-section `cs.WidthMeters`, but the method signatures still accept `parameters.RoadWidthMeters` as a parameter. The scalar parameter now serves as a fallback/max value rather than the primary width. Line 557 (`ApplyPostProcessingSmoothing`) was left unchanged because that standalone path has no cross-section data available.

4. **Task 10 — `MaterialPainter` method signatures changed.** The plan described replacing inline width reads. The implementation went further: `PaintSplineDirectly` and `PaintSplineDirectlyAntiAliased` method signatures were changed from accepting `(RoadSpline spline, float halfWidth, ...)` to `(ParameterizedRoadSpline paramSpline, ...)`, moving the width query inside the per-sample loop. This is a more thorough approach but changes internal API surfaces not mentioned in the plan.

5. **Task 11 — `DecalRoadGenerator` and `RoadCorridorBuilder` use distance-0 only.** The plan specified "per-sample query" for both. The implementation queries `WidthProfile` at distance 0 only, using this as a representative width for the entire spline. This is because the DecalRoad generator's Phase B lane-segment splitting already handles per-segment variation through its own mechanism. However, if a spline has width changes mid-span without lane changes, the DecalRoad width won't vary. This could be improved in a follow-up.

6. **Task 11 — `MasterSplineExporter` legacy paths left unchanged.** Lines ~426 and ~530 (legacy single-material export paths) still read `parameters.EffectiveMasterSplineWidthMeters` directly. These paths don't have access to a `ParameterizedRoadSpline`, so no `WidthProfile` is available. The plan noted this possibility ("pass through if available, otherwise keep fallback").

7. **Task 12 — `BuildWidthProfile` duplicated.** The plan suggested calling `UnifiedRoadNetworkBuilder.BuildWidthProfile` from the snapshot loader, or extracting a shared helper. Since `BuildWidthProfile` is `private static`, it was duplicated as a private method in `DecalRoadNetworkSnapshotLoader`. The two copies must be kept in sync manually.

8. **Task 13 — `TerrainCreator.UpdateRoadMaterialLayersAsync` signature changed.** The plan only asked to update a logging line. The implementation added an optional `UnifiedRoadNetwork? network` parameter to the method and updated the call site to pass it. This allows the logging to look up splines and report width profile segment counts per material.

9. **Task 13 — `RoadDebugExporter.ExportRoadMaskVisualization` (line ~495) not updated.** This method still uses `parameters.RoadWidthMeters / 2.0f` directly. It was not mentioned explicitly in the plan's line references for Task 13, and the implementer did not find/update it.

### Concerns

1. **`RoadMaskBuilder.cs:120` not updated.** Uses `.OrderByDescending(id => splineLookup[id].Parameters.RoadWidthMeters)` for sorting splines by width during mask building. For splines with varying width, this sorts by the global parameter rather than the actual maximum segment width. Low impact (affects ordering only, not width computation), but could cause incorrect priority ordering for splines with significant width variation.

2. **`ParameterizedRoadSpline.cs:255` priority calculation uses global width.** `GetWidthBasedPriority(Parameters.RoadWidthMeters)` is called during spline construction, potentially before `WidthProfile` is attached. If width-based priority should reflect per-segment width, this would need to use the max segment width instead. Currently uses the global parameter fallback.

3. **`SpawnPointData.cs` ordering uses global width.** Two sites (lines 142, 263) order by `RoadWidthMeters` for spawn point selection. These prefer wider roads for spawning, but use the global parameter rather than actual segment widths. Low impact for spawn point selection.

4. **Duplicated `BuildWidthProfile` logic.** Exists in both `UnifiedRoadNetworkBuilder` (private static) and `DecalRoadNetworkSnapshotLoader` (private). If the priority chain logic changes, both must be updated. Consider extracting to a shared static helper on `RoadWidthProfile` itself in a future refactor.

5. **`TryParseWidth` regex not compiled.** Called once per OSM way during import — not a hot path, but could use `[GeneratedRegex]` source generators for marginal performance improvement.

6. **`TryParseWidth` is case-sensitive on unit suffixes.** The regex `(m|km|mi|ft)?` only matches lowercase. Some OSM contributors use "M", "Km", "FT". Consider `RegexOptions.IgnoreCase` or lowercasing input before matching.
