# UseOsmWidthTag Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `UseOsmWidthTag` toggle to `DecalRoadLayerSet` that controls whether the OSM `width=` and `est_width=` tags are used in the road width cascade, defaulting to `false`.

**Architecture:** Single boolean property on `DecalRoadLayerSet` gates the first two priority levels of `BuildWidthProfile`'s width cascade. When `false`, the cascade skips `WidthMeters` and `EstWidthMeters` checks and falls through to lane calculation or layer set defaults. The toggle appears in the existing layer set editor UI alongside `EnablePerSegmentWidth`.

**Tech Stack:** C# / .NET 9, Blazor (MudBlazor v8), System.Text.Json serialization

---

### Task 1: Add `UseOsmWidthTag` property to `DecalRoadLayerSet`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs:9`

- [ ] **Step 1: Add the property**

After line 9 (`EnablePerSegmentWidth`), add:

```csharp
    public bool UseOsmWidthTag { get; set; } = false;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerSet.cs
git commit -m "feat: add UseOsmWidthTag property to DecalRoadLayerSet (default false)"
```

---

### Task 2: Wire `UseOsmWidthTag` into `BuildWidthProfile` (UnifiedRoadNetworkBuilder)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs:203-211`

- [ ] **Step 1: Gate the width tag checks**

In `BuildWidthProfile`, wrap the `WidthMeters` and `EstWidthMeters` checks with the flag.
Replace the cascade block (lines 203-222):

```csharp
                if (layerSet.UseOsmWidthTag && ls.LaneInfo.WidthMeters.HasValue)
                {
                    surfaceWidth = ls.LaneInfo.WidthMeters.Value;
                    source = WidthSource.OsmWidthTagExact;
                }
                else if (layerSet.UseOsmWidthTag && ls.LaneInfo.EstWidthMeters.HasValue)
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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs
git commit -m "feat: gate OSM width tag usage behind UseOsmWidthTag in UnifiedRoadNetworkBuilder"
```

---

### Task 3: Wire `UseOsmWidthTag` into `BuildWidthProfile` (DecalRoadNetworkSnapshotLoader)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs:213-232`

- [ ] **Step 1: Gate the width tag checks (mirror of Task 2)**

Apply the identical change to the snapshot loader's `BuildWidthProfile`.
Replace the cascade block (lines 213-232):

```csharp
                if (layerSet.UseOsmWidthTag && ls.LaneInfo.WidthMeters.HasValue)
                {
                    surfaceWidth = ls.LaneInfo.WidthMeters.Value;
                    source = WidthSource.OsmWidthTagExact;
                }
                else if (layerSet.UseOsmWidthTag && ls.LaneInfo.EstWidthMeters.HasValue)
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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs
git commit -m "feat: gate OSM width tag usage behind UseOsmWidthTag in snapshot loader"
```

---

### Task 4: Add UI toggle in `DecalRoadLayerSetEditor.razor`

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor:42`

- [ ] **Step 1: Add checkbox after the EnablePerSegmentWidth checkbox**

After line 42 (closing `</MudItem>` of `EnablePerSegmentWidth`), add:

```razor
    <MudItem xs="12">
        <MudCheckBox T="bool" @bind-Value="LayerSet.UseOsmWidthTag"
                     Label="Use OSM road width tag if available"
                     Disabled="@ReadOnly" />
    </MudItem>
```

- [ ] **Step 2: Build the main project to verify**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor
git commit -m "feat: add UseOsmWidthTag toggle to layer set editor UI"
```
