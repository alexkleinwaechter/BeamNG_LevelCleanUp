# Plan: Move Building Generation to Global Terrain Setting

## Context

Building generation was incorrectly implemented as a per-material setting on `TerrainMaterialItemExtended` (similar to `IsRoadMaterial`). Buildings are **not** terrain materials - they're 3D mesh objects placed on top of the terrain. The building toggle and OSM feature selection should be a **global terrain setting** in `GenerateTerrain.razor`, alongside settings like bridge/tunnel handling.

The OSM feature selection via `OsmFeatureSelectorDialog` with `IsBuildingMaterial` pre-filtering was correctly implemented and should be kept - just moved from per-material to global level.

## Files to Modify (9 files)

### 1. `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs`

**Replace** the BUILDING GENERATION section (~lines 89-117) with simple properties:

```csharp
// ========================================
// BUILDING GENERATION
// ========================================
public bool EnableBuildings { get; set; }
public List<OsmFeatureSelection> SelectedBuildingFeatures { get; set; } = new();

public HashSet<long> GetSelectedBuildingFeatureIds() =>
    SelectedBuildingFeatures.Select(f => f.FeatureId).ToHashSet();
```

**Add** to `Reset()` method (after `FlipMaterialProcessingOrder = false;`):
```csharp
EnableBuildings = false;
SelectedBuildingFeatures = new List<OsmFeatureSelection>();
```

**Add** using: `using BeamNgTerrainPoc.Terrain.Osm.Models;`

### 2. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

**Remove** from `TerrainMaterialItemExtended` class (after `IsRoadMaterial`/`SelectedPreset`):
- `public bool IsBuildingMaterial { get; set; }`
- `public List<OsmFeatureSelection>? SelectedBuildingFeatures { get; set; }`

**Remove** methods:
- `OpenBuildingFeatureSelector()` (~line 353-384)
- `RemoveBuildingFeature()` (~line 386-390)
- `ClearBuildingFeatures()` (~line 392-396)

### 3. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

**Remove** building chip in header (~lines 25-30):
```razor
@if (Material.IsBuildingMaterial) { ... }
```

**Remove** building toggle (~lines 178-184):
```razor
<!-- Building Material Toggle -->
<MudItem xs="12" sm="6"> ... </MudItem>
```

**Remove** building feature selection section (~lines 186-248):
```razor
@if (Material.IsBuildingMaterial) { ... }
```

### 4. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

**Add** property accessor (after `_excludeTunnelsFromTerrain` ~line 139):
```csharp
private bool _enableBuildings
{
    get => _state.EnableBuildings;
    set => _state.EnableBuildings = value;
}
```

**Add** building feature management methods (near other helper methods):
```csharp
private async Task OpenBuildingFeatureSelector()
{
    if (EffectiveBoundingBox == null) return;

    var parameters = new DialogParameters<OsmFeatureSelectorDialog>
    {
        { x => x.MaterialName, "Building Generation" },
        { x => x.BoundingBox, EffectiveBoundingBox },
        { x => x.TerrainSize, _terrainSize },
        { x => x.IsRoadMaterial, false },
        { x => x.IsBuildingMaterial, true },
        { x => x.ExistingSelections, _state.SelectedBuildingFeatures }
    };

    var options = new DialogOptions { FullScreen = true, CloseButton = true, CloseOnEscapeKey = true };
    var dialog = await DialogService.ShowAsync<OsmFeatureSelectorDialog>(
        "Select Building Features", parameters, options);
    var result = await dialog.Result;

    if (result != null && !result.Canceled && result.Data is List<OsmFeatureSelection> selections)
    {
        _state.SelectedBuildingFeatures = selections;
        await InvokeAsync(StateHasChanged);
    }
}

private async Task RemoveBuildingFeature(OsmFeatureSelection feature)
{
    _state.SelectedBuildingFeatures.Remove(feature);
    await InvokeAsync(StateHasChanged);
}

private async Task ClearBuildingFeatures()
{
    _state.SelectedBuildingFeatures.Clear();
    await InvokeAsync(StateHasChanged);
}
```

**Add** preset import handling in `OnPresetImported()` (after `ExcludeTunnelsFromTerrain` ~line 1224):
```csharp
if (result.EnableBuildings.HasValue)
    _enableBuildings = result.EnableBuildings.Value;
if (result.SelectedBuildingFeatures?.Any() == true)
    _state.SelectedBuildingFeatures = result.SelectedBuildingFeatures
        .Select(r => r.ToSelection()).ToList();
```

### 5. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`

**Add** building UI section after the tunnel checkbox (before `</MudGrid>` at line 571). Gate with `_canFetchOsmData`:

```razor
@* Building Generation *@
<MudItem xs="12">
    <MudDivider Class="my-2" />
    <MudText Typo="Typo.subtitle2" Class="mb-2">
        <MudIcon Icon="@Icons.Material.Filled.Apartment" Size="Size.Small" Class="mr-1" />
        Building Generation
    </MudText>
    <MudText Typo="Typo.caption" Color="Color.Secondary" Class="mb-2">
        Generate 3D buildings from OSM building footprints.
    </MudText>
</MudItem>

<MudItem xs="12" sm="6" md="3">
    <div class="d-flex align-center gap-1">
        <MudCheckBox @bind-Value="_enableBuildings"
                     Label="Enable Buildings"
                     Color="Color.Warning"
                     Disabled="@(!_canFetchOsmData)" />
        <MudTooltip Text="Generate 3D buildings from selected OSM building footprints after terrain creation.">
            <MudIcon Icon="@Icons.Material.Filled.Help" Size="Size.Small" Color="Color.Secondary" />
        </MudTooltip>
    </div>
    <MudText Typo="Typo.caption" Color="Color.Secondary">
        @if (!_canFetchOsmData)
        { <text>Requires GeoTIFF source with OSM data</text> }
        else
        { <text>Generate buildings from OSM footprints</text> }
    </MudText>
</MudItem>

@if (_enableBuildings)
{
    <MudItem xs="12">
        <div class="d-flex align-center gap-2 mb-2">
            <MudChip T="string" Size="Size.Small" Color="Color.Info">
                @(_state.SelectedBuildingFeatures.Count) building@(_state.SelectedBuildingFeatures.Count != 1 ? "s" : "") selected
            </MudChip>
            <MudButton Variant="Variant.Filled" Color="Color.Warning"
                       StartIcon="@Icons.Material.Filled.Apartment"
                       OnClick="OpenBuildingFeatureSelector" Size="Size.Small">
                Select Buildings
            </MudButton>
            @if (_state.SelectedBuildingFeatures.Any())
            {
                <MudIconButton Icon="@Icons.Material.Filled.Clear"
                              Color="Color.Error" Size="Size.Small"
                              OnClick="ClearBuildingFeatures" />
            }
        </div>
        @if (_state.SelectedBuildingFeatures.Any())
        {
            <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
                @foreach (var feature in _state.SelectedBuildingFeatures.Take(10))
                {
                    <MudChip T="string" Size="Size.Small" Color="Color.Warning"
                             Variant="Variant.Outlined"
                             OnClose="@(() => RemoveBuildingFeature(feature))">
                        @feature.DisplayName
                    </MudChip>
                }
                @if (_state.SelectedBuildingFeatures.Count > 10)
                {
                    <MudChip T="string" Size="Size.Small" Color="Color.Default">
                        +@(_state.SelectedBuildingFeatures.Count - 10) more
                    </MudChip>
                }
            </MudStack>
        }
    </MudItem>
}
```

**Add** exporter parameters (after `ExcludeTunnelsFromTerrain` ~line 108):
```razor
EnableBuildings="@_enableBuildings"
SelectedBuildingFeatures="@_state.SelectedBuildingFeatures"
```

### 6. `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`

**Change** line 137 from `state.HasBuildingMaterials` to:
```csharp
if (generationSuccess && state.EnableBuildings && state.SelectedBuildingFeatures.Any() &&
    osmQueryResult != null && effectiveBoundingBox != null)
```

### 7. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor`

**Add** parameters:
```csharp
[Parameter] public bool EnableBuildings { get; set; }
[Parameter] public List<OsmFeatureSelection>? SelectedBuildingFeatures { get; set; }
```

**Add** `["buildingOptions"]` in `BuildAppSettings()` (after `["terrainOptions"]`):
```csharp
["buildingOptions"] = new JsonObject
{
    ["enableBuildings"] = EnableBuildings
},
```

If `EnableBuildings && SelectedBuildingFeatures?.Any() == true`, also add a `selectedBuildingFeatures` array with `featureId`, `displayName`, `category`, `subCategory`, `geometryType` per feature.

**Remove** from `BuildMaterialSettings()`:
- `["isBuildingMaterial"] = mat.IsBuildingMaterial` from the matSettings object
- The entire `if (mat.IsBuildingMaterial && mat.SelectedBuildingFeatures?.Any() == true)` block

### 8. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor`

**Add** global building import in `ImportAppSettings()` (after `terrainOptions` block):
```csharp
var buildingOptions = appSettings["buildingOptions"];
if (buildingOptions != null)
{
    if (buildingOptions["enableBuildings"] != null)
        result.EnableBuildings = buildingOptions["enableBuildings"]!.GetValue<bool>();
    // Parse selectedBuildingFeatures array into List<OsmFeatureReference>
}
```

**Remove** from per-material loop:
- `isBuildingMaterial` flag parsing (~line 652-654)
- `buildingFeatureSelections` array parsing (~line 657-683)
- `if (layerSettings.IsBuildingMaterial)` application block (~line 746-755)

### 9. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`

**Add** to `TerrainPresetResult` class (global level, after `ExcludeTunnelsFromTerrain`):
```csharp
public bool? EnableBuildings { get; set; }
public List<OsmFeatureReference>? SelectedBuildingFeatures { get; set; }
```

**Remove** from `MaterialLayerSettings` class:
- `public bool IsBuildingMaterial { get; set; }`
- `public List<OsmFeatureReference>? BuildingFeatureSelections { get; set; }`

## Files NOT Modified (keep as-is)

- `OsmFeatureSelectorDialog.razor` - `IsBuildingMaterial` parameter and pre-filtering stays
- `BuildingGenerationOrchestrator.cs` - `selectedFeatureIds` parameter and `ParseFilteredBuildings` helper stays
- `OsmBuildingParser.cs` - `ParseBuildingFeatures` overload stays

## Implementation Order

1. `TerrainGenerationState.cs` - Foundation: replace derived props with simple ones
2. `TerrainMaterialSettings.razor.cs` - Remove building props/methods from material model
3. `TerrainMaterialSettings.razor` - Remove building UI from material panel
4. `TerrainPresetResult.cs` - Move building fields from per-material to global
5. `GenerateTerrain.razor.cs` - Add accessor, methods, preset import handler
6. `GenerateTerrain.razor` - Add building UI section + exporter params
7. `TerrainPresetExporter.razor` - Global building export, remove per-material
8. `TerrainPresetImporter.razor` - Global building import, remove per-material
9. `TerrainGenerationOrchestrator.cs` - Update condition

## Verification

1. `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj` - must succeed with no `error CS` lines
2. Grep for `IsBuildingMaterial` across `*.cs` and `*.razor` - should only appear in `OsmFeatureSelectorDialog.razor`
3. Grep for `SelectedBuildingFeatures` - should only appear in `TerrainGenerationState.cs`, `GenerateTerrain.razor*`, preset files, and `OsmFeatureSelectorDialog.razor` (as parameter) - NOT in `TerrainMaterialSettings*`
4. Grep for `HasBuildingMaterials` - should have zero results
