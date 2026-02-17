# Plan: Move Enable Buildings to TerrainMaterialSettings + OSM Feature Selection

## Summary

Move the global "Enable Buildings" toggle from `GenerateTerrain.razor` into the per-material `TerrainMaterialSettings.razor` component, following the same pattern as `IsRoadMaterial`. Building features will be selected via the existing `OsmFeatureSelectorDialog`, pre-filtered to the "building" category.

## Current State

- `GenerateTerrain.razor` has a global `_enableBuildings` checkbox that maps to `TerrainGenerationState.EnableBuildings`
- `OsmBuildingParser.ParseBuildings()` takes the full `OsmQueryResult` and internally does `queryResult.GetFeaturesByCategory("building")` to get ALL buildings
- `OsmFeatureSelectorDialog` already supports "building" as a category, but doesn't pre-filter to it
- `TerrainMaterialItemExtended` has `IsRoadMaterial` + `SelectedOsmFeatures` but no building equivalent

## Changes

### Step 1: Add `IsBuildingMaterial` property to `TerrainMaterialItemExtended`
**File:** `TerrainMaterialSettings.razor.cs` (line ~720, after `IsRoadMaterial`)

Add:
```csharp
// Building generation enabled
public bool IsBuildingMaterial { get; set; }

// Building-specific OSM feature selections (separate from road/landuse OSM features)
public List<OsmFeatureSelection>? SelectedBuildingFeatures { get; set; }
```

**Why separate `SelectedBuildingFeatures`?** A material could have BOTH terrain painting OSM features (landuse polygons in `SelectedOsmFeatures`) AND building features. Keeping them separate avoids conflating terrain material painting with 3D building generation.

### Step 2: Add building toggle + feature selector UI to `TerrainMaterialSettings.razor`
**File:** `TerrainMaterialSettings.razor` (after the `IsRoadMaterial` toggle, line ~176)

Add a new section below the road toggle:
- `MudSwitch` for "Enable Building Generation" bound to `Material.IsBuildingMaterial`
- When enabled AND `HasGeoBoundingBox`: show "Select Building Features" button + selected feature chips
- Follows the same chip display pattern as the existing OSM features section (lines 136-156)

### Step 3: Add building chip in expansion panel header
**File:** `TerrainMaterialSettings.razor` (line ~25, after the Road chip)

Add a "Building" chip (with Color.Warning or Color.Tertiary) when `Material.IsBuildingMaterial` is true, similar to the "Road" chip.

### Step 4: Add `OpenBuildingFeatureSelector()` method to `TerrainMaterialSettings.razor.cs`
**File:** `TerrainMaterialSettings.razor.cs` (after `OpenOsmFeatureSelector`, line ~337)

New method that opens `OsmFeatureSelectorDialog` with `IsBuildingMaterial=true` parameter. This is separate from `OpenOsmFeatureSelector()` to keep the two feature lists independent.

### Step 5: Update `OsmFeatureSelectorDialog` to support building pre-filtering
**File:** `OsmFeatureSelectorDialog.razor` (code section around line 222, and line 408-418)

- Add `[Parameter] public bool IsBuildingMaterial { get; set; }` parameter
- In `OnInitializedAsync()` pre-filtering logic, add a third branch:
  ```csharp
  if (IsBuildingMaterial)
  {
      _showLines = false;
      _showPolygons = true;
      _selectedCategories = new List<string> { "building" };
  }
  else if (IsRoadMaterial) { ... }
  else { ... }
  ```

### Step 6: Derive `EnableBuildings` from materials instead of global toggle
**File:** `TerrainGenerationState.cs`

Keep `EnableBuildings` property but change it to be derived:
```csharp
// Computed: true if any material has IsBuildingMaterial enabled with selected features
public bool HasBuildingMaterials =>
    TerrainMaterials.Any(m => m.IsBuildingMaterial && m.SelectedBuildingFeatures?.Any() == true);
```

Also add a helper to collect all selected building features across materials:
```csharp
public List<OsmFeatureSelection> GetAllSelectedBuildingFeatures() =>
    TerrainMaterials
        .Where(m => m.IsBuildingMaterial && m.SelectedBuildingFeatures?.Any() == true)
        .SelectMany(m => m.SelectedBuildingFeatures!)
        .ToList();
```

### Step 7: Update `OsmBuildingParser.ParseBuildings()` to accept filtered features
**File:** `OsmBuildingParser.cs`

Add an overload that accepts pre-filtered features:
```csharp
public List<BuildingData> ParseBuildings(
    List<OsmFeature> buildingFeatures,  // pre-filtered
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    Func<float, float, float>? heightSampler = null)
```

The existing method delegates to this new one, keeping backward compatibility.

### Step 8: Update `TerrainGenerationOrchestrator.RunBuildingGeneration()`
**File:** `TerrainGenerationOrchestrator.cs` (lines 135-143, 289-340)

- Change the gating condition from `state.EnableBuildings` to `state.HasBuildingMaterials`
- Collect user-selected building features via `state.GetAllSelectedBuildingFeatures()`
- Convert `OsmFeatureSelection` → `OsmFeature` (need to look up from `OsmQueryResult` by FeatureId)
- Pass the filtered features to `OsmBuildingParser` instead of the full query result

### Step 9: Remove global buildings UI from `GenerateTerrain.razor`
**File:** `GenerateTerrain.razor` (lines 571-603) and `GenerateTerrain.razor.cs` (lines 141-145)

- Delete the "Building Generation" section markup (lines 571-603)
- Remove the `_enableBuildings` property accessor
- Keep `TerrainGenerationState.EnableBuildings` for backward compat but mark it `[Obsolete]` or derive it from `HasBuildingMaterials`

### Step 10: Update preset export/import
**Files:** `TerrainPresetExporter.razor`, `TerrainPresetImporter.razor`

Add `isBuildingMaterial` flag and `buildingFeatureSelections` array to the preset JSON format, following the same pattern used for `isRoadMaterial` and `osmFeatureSelections`.

## File Summary

| File | Change |
|------|--------|
| `TerrainMaterialSettings.razor.cs` | Add `IsBuildingMaterial`, `SelectedBuildingFeatures` props + `OpenBuildingFeatureSelector()` |
| `TerrainMaterialSettings.razor` | Add building toggle UI + feature chips + header chip |
| `OsmFeatureSelectorDialog.razor` | Add `IsBuildingMaterial` param + pre-filter to "building" category |
| `TerrainGenerationState.cs` | Add `HasBuildingMaterials` + `GetAllSelectedBuildingFeatures()` |
| `OsmBuildingParser.cs` | Add overload accepting pre-filtered features |
| `TerrainGenerationOrchestrator.cs` | Use filtered features instead of all buildings |
| `GenerateTerrain.razor` + `.cs` | Remove global buildings checkbox |
| `TerrainPresetExporter.razor` | Export `isBuildingMaterial` + `buildingFeatureSelections` |
| `TerrainPresetImporter.razor` | Import `isBuildingMaterial` + `buildingFeatureSelections` |
