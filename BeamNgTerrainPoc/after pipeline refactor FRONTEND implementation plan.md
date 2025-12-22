# Implementation Plan: Post-Refactor Cleanup and Parameter Alignment

## Overview

This plan addresses cleanup and alignment issues discovered after the pipeline refactoring from material-centric to unified network processing. The goal is to remove dead code, align UI with actual functionality, and properly implement the global/per-material junction detection pattern.

---

## 1. Remove DirectMask Approach (UI and Library)

### 1.1 Problem Statement

The `RoadSmoothingApproach` enum (`Spline` vs `DirectMask`) is now cosmetic. The unified pipeline (`UnifiedRoadSmoother`) always uses spline-based processing. DirectMask-specific parameters are unused.

### 1.2 Files to Modify

| File | Action | Description |
|------|--------|-------------|
| `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` | Modify | Remove `Approach` property, `RoadSmoothingApproach` enum, and `DirectMaskParameters` |
| `BeamNgTerrainPoc/Terrain/Models/DirectMaskRoadParameters.cs` | Delete | Entire file is unused |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Modify | Remove "Algorithm" tab's Approach selector and DirectMask tab |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Modify | Remove DirectMask properties from `TerrainMaterialItemExtended` |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` | Modify | Remove DirectMask from export JSON |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` | Modify | Remove DirectMask import handling (graceful ignore for legacy presets) |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs` | Modify | Remove `DirectMaskParametersSettings` class |
| `BeamNgTerrainPoc/Examples/RoadSmoothingPresets.cs` | Modify | Remove DirectMask references from presets |
| `BeamNgTerrainPoc/Terrain/Algorithms/DirectTerrainBlender.cs` | Delete | Unused |
| `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` | Review | Check if still needed or can be removed |

### 1.3 Implementation Steps

#### Step 1.3.1: Remove DirectMask Model Classes

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

**Changes**:
```csharp
// REMOVE these:
public enum RoadSmoothingApproach { DirectMask, Spline }
public RoadSmoothingApproach Approach { get; set; }
public DirectMaskRoadParameters? DirectMaskParameters { get; set; }
public DirectMaskRoadParameters GetDirectMaskParameters() { ... }

// REMOVE validation logic referencing DirectMask approach
```

**File**: `BeamNgTerrainPoc/Terrain/Models/DirectMaskRoadParameters.cs`

**Action**: Delete entire file.

#### Step 1.3.2: Remove DirectMask from UI

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

**Remove**:
- The `Approach` MudSelect in the "Algorithm" tab
- The entire "DirectMask" MudTabPanel
- Any disabled state logic based on `Material.Approach != RoadSmoothingApproach.Spline`

**Update**:
- Remove the `Disabled` attribute from Spline tab (it's always active now)
- Update helper text that mentions "Spline vs DirectMask"

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

**Remove from `TerrainMaterialItemExtended`**:
```csharp
// REMOVE:
public RoadSmoothingApproach Approach { get; set; }
public int DirectMaskSmoothingWindowSize { get; set; }
public int RoadPixelSearchRadius { get; set; }
public bool DirectMaskUseButterworthFilter { get; set; }
public int DirectMaskButterworthFilterOrder { get; set; }
```

**Update `BuildRoadSmoothingParameters()`**:
- Remove `Approach` assignment
- Remove `DirectMaskParameters` creation

**Update `ApplyPreset()`**:
- Remove DirectMask parameter application

#### Step 1.3.3: Update Preset Export/Import

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor`

**Remove from `BuildRoadSmoothingSettings()`**:
```csharp
// REMOVE:
["approach"] = mat.Approach.ToString(),
["directMaskParameters"] = new JsonObject { ... }
```

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor`

**Update `ImportRoadSmoothingFromJson()`**:
- Remove DirectMask import logic
- Keep graceful handling (ignore `directMaskParameters` if present in legacy presets)

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`

**Remove**:
```csharp
public class DirectMaskParametersSettings { ... }
```

**Remove from `RoadSmoothingSettings`**:
```csharp
public string Approach { get; set; }
public DirectMaskParametersSettings? DirectMaskParameters { get; set; }
```

#### Step 1.3.4: Update Presets

**File**: `BeamNgTerrainPoc/Examples/RoadSmoothingPresets.cs`

**Remove** from all presets:
```csharp
Approach = RoadSmoothingApproach.Spline, // Remove - no longer needed
DirectMaskParameters = new DirectMaskRoadParameters { ... } // Remove entirely
```

#### Step 1.3.5: Delete Unused Algorithm Files

**Delete**:
- `BeamNgTerrainPoc/Terrain/Algorithms/DirectTerrainBlender.cs`

**Review and potentially delete**:
- `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` (if only used for DirectMask orchestration)

### 1.4 Acceptance Criteria

- [ ] `RoadSmoothingApproach` enum no longer exists
- [ ] No references to `DirectMask` in codebase (search verification)
- [ ] UI shows only spline parameters (no approach selector)
- [ ] Legacy presets with `approach: "DirectMask"` load without error (ignored gracefully)
- [ ] Build succeeds with no warnings about unused code

---

## 2. Update Debug Export UI Text

### 2.1 Problem Statement

Some debug export flags may produce different outputs than before due to the unified pipeline. UI text should reflect what's actually exported.

### 2.2 Files to Modify

| File | Action | Description |
|------|--------|-------------|
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Modify | Update debug checkbox labels and helper text |
| `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs` | Modify | Update tooltip text for debug options |

### 2.3 Current vs. New Debug Exports

| UI Option | Old Behavior | New Behavior | UI Update Needed |
|-----------|--------------|--------------|------------------|
| `ExportSmoothedHeightmapWithOutlines` | Per-material heightmap with outlines | Unified network heightmap with all roads | Update tooltip to mention "unified network" |
| `ExportSplineDebugImage` | Per-material spline debug | Per-material debug in subfolder | Minor text update |
| `ExportSkeletonDebugImage` | Per-material skeleton | Same (PNG source only) | Add note: "Only for PNG layer sources" |
| `ExportJunctionDebugImage` | Per-material junctions | Unified junction debug with cross-material junctions | Update to mention "cross-material junctions" |

### 2.4 Implementation Steps

#### Step 2.4.1: Update Debug Section in UI

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

**Update debug checkbox labels**:
```razor
@* Update ExportSmoothedHeightmapWithOutlines *@
<MudCheckBox @bind-Value="Material.ExportSmoothedHeightmapWithOutlines"
             Label="Export Unified Heightmap with Road Outlines"
             HelperText="Exports the combined smoothed heightmap showing all road networks with outlines"/>

@* Update ExportJunctionDebugImage *@
<MudCheckBox @bind-Value="Material.ExportJunctionDebugImage"
             Label="Export Junction Debug Image"
             HelperText="Shows detected junctions including cross-material connections"/>

@* Update ExportSkeletonDebugImage with note *@
<MudCheckBox @bind-Value="Material.ExportSkeletonDebugImage"
             Label="Export Skeleton Debug Image"
             HelperText="Shows extracted skeleton (PNG layer source only - not used for OSM)"
             Disabled="@IsUsingOsmSplines"/>
```

#### Step 2.4.2: Update Tooltips

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs`

**Update tooltip constants** (if exists) or add inline tooltips in razor file.

### 2.5 Acceptance Criteria

- [ ] Debug option labels accurately describe unified pipeline output
- [ ] Skeleton debug option is disabled/grayed when using OSM source
- [ ] Tooltips mention "unified network" where appropriate

---

## 3. Split Junction Detection Parameters (Global vs Per-Material)

### 3.1 Problem Statement

Per the implementation plan answer A3:
> (d) Configurable globally with per-material override

Currently, `JunctionDetectionRadiusMeters` uses the first material's value as global. This should be:
1. A global setting with default value
2. Per-material override capability
3. Backend logic that respects the override

### 3.2 Architecture Design

```
┌─────────────────────────────────────────────────────────────────┐
│                  Junction Parameter Flow                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  GenerateTerrain.razor                                          │
│  ┌─────────────────────────────────────────────┐                │
│  │ Global Settings Panel:                       │                │
│  │   • EnableCrossMaterialHarmonization ✓      │                │
│  │   • GlobalJunctionDetectionRadiusMeters: 10 │  ← NEW         │
│  │   • GlobalJunctionBlendDistanceMeters: 30   │  ← NEW         │
│  └─────────────────────────────────────────────┘                │
│                      │                                           │
│                      ▼                                           │
│  TerrainMaterialSettings.razor (per material)                    │
│  ┌─────────────────────────────────────────────┐                │
│  │ Junction Harmonization:                      │                │
│  │   ☐ Override Global Junction Settings       │  ← NEW toggle  │
│  │   • JunctionDetectionRadiusMeters: [    ]   │  (disabled if  │
│  │   • JunctionBlendDistanceMeters: [    ]     │   not override)│
│  └─────────────────────────────────────────────┘                │
│                      │                                           │
│                      ▼                                           │
│  TerrainCreationParameters                                       │
│  ┌─────────────────────────────────────────────┐                │
│  │ GlobalJunctionDetectionRadiusMeters: 10.0f  │  ← NEW         │
│  │ GlobalJunctionBlendDistanceMeters: 30.0f    │  ← NEW         │
│  │ Materials: [...]                            │                 │
│  │   └─ RoadParameters.JunctionHarmonization   │                 │
│  │        └─ UseGlobalSettings: true/false     │  ← NEW         │
│  │        └─ JunctionDetectionRadiusMeters     │  (per-material)│
│  └─────────────────────────────────────────────┘                │
│                      │                                           │
│                      ▼                                           │
│  UnifiedRoadSmoother.SmoothAllRoads()                           │
│  ┌─────────────────────────────────────────────┐                │
│  │ For each junction detection:                 │                │
│  │   radius = spline.UseGlobalSettings         │                │
│  │          ? parameters.GlobalRadius          │                │
│  │          : spline.JunctionDetectionRadius   │                │
│  └─────────────────────────────────────────────┘                │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.3 Files to Modify

| File | Action | Description |
|------|--------|-------------|
| `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs` | Modify | Add global junction parameters |
| `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` | Modify | Add `UseGlobalSettings` flag |
| `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` | Modify | Add global junction state properties |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Modify | Add global junction settings UI |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | Modify | Add global junction state accessors |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Modify | Add override toggle for junction params |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Modify | Add `UseGlobalJunctionSettings` property |
| `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` | Modify | Pass global params to TerrainCreationParameters |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | Modify | Use global params with per-material override |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs` | Modify | Accept per-spline detection radius |

### 3.4 Implementation Steps

#### Step 3.4.1: Update Model Classes

**File**: `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

**Add**:
```csharp
/// <summary>
/// Global junction detection radius in meters.
/// Used when material doesn't override with its own value.
/// Default: 10.0
/// </summary>
public float GlobalJunctionDetectionRadiusMeters { get; set; } = 10.0f;

/// <summary>
/// Global junction blend distance in meters.
/// Used when material doesn't override with its own value.
/// Default: 30.0
/// </summary>
public float GlobalJunctionBlendDistanceMeters { get; set; } = 30.0f;
```

**File**: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

**Add**:
```csharp
/// <summary>
/// When true, uses global junction settings from TerrainCreationParameters.
/// When false, uses the values specified in this instance.
/// Default: true (use global settings)
/// </summary>
public bool UseGlobalSettings { get; set; } = true;
```

#### Step 3.4.2: Update State Management

**File**: `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs`

**Add properties**:
```csharp
/// <summary>
/// Global junction detection radius in meters.
/// </summary>
public float GlobalJunctionDetectionRadiusMeters { get; set; } = 10.0f;

/// <summary>
/// Global junction blend distance in meters.
/// </summary>
public float GlobalJunctionBlendDistanceMeters { get; set; } = 30.0f;
```

**Update `Reset()` method** to reset these values.

#### Step 3.4.3: Add Global Settings to GenerateTerrain UI

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`

**Add in the road smoothing options section** (near `EnableCrossMaterialHarmonization`):

```razor
@* After EnableCrossMaterialHarmonization switch *@
@if (_enableCrossMaterialHarmonization)
{
    <MudItem xs="12">
        <MudText Typo="Typo.subtitle2" Class="mb-2">
            <MudIcon Icon="@Icons.Material.Filled.CallSplit" Size="Size.Small" Class="mr-1"/>
            Global Junction Settings
        </MudText>
        <MudText Typo="Typo.caption" Color="Color.Secondary" Class="mb-2">
            Default values for cross-material junction handling. Individual materials can override these.
        </MudText>
    </MudItem>
    
    <MudItem xs="12" sm="6">
        <MudNumericField @bind-Value="_globalJunctionDetectionRadiusMeters"
                        Label="Global Junction Detection Radius (m)"
                        Variant="Variant.Outlined"
                        Min="1.0f" Max="50.0f" Step="1.0f"
                        HelperText="Distance to detect road intersections"/>
    </MudItem>
    
    <MudItem xs="12" sm="6">
        <MudNumericField @bind-Value="_globalJunctionBlendDistanceMeters"
                        Label="Global Junction Blend Distance (m)"
                        Variant="Variant.Outlined"
                        Min="5.0f" Max="100.0f" Step="5.0f"
                        HelperText="Distance to blend elevations at junctions"/>
    </MudItem>
}
```

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

**Add accessor properties**:
```csharp
private float _globalJunctionDetectionRadiusMeters
{
    get => _state.GlobalJunctionDetectionRadiusMeters;
    set => _state.GlobalJunctionDetectionRadiusMeters = value;
}

private float _globalJunctionBlendDistanceMeters
{
    get => _state.GlobalJunctionBlendDistanceMeters;
    set => _state.GlobalJunctionBlendDistanceMeters = value;
}
```

#### Step 3.4.4: Add Override Toggle to Material Settings

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

**Add to `TerrainMaterialItemExtended`**:
```csharp
/// <summary>
/// When true, uses global junction settings. When false, uses per-material values.
/// </summary>
public bool UseGlobalJunctionSettings { get; set; } = true;
```

**Update `BuildRoadSmoothingParameters()`**:
```csharp
result.JunctionHarmonizationParameters = new JunctionHarmonizationParameters
{
    UseGlobalSettings = UseGlobalJunctionSettings,  // NEW
    EnableJunctionHarmonization = EnableJunctionHarmonization,
    JunctionDetectionRadiusMeters = JunctionDetectionRadiusMeters,
    JunctionBlendDistanceMeters = JunctionBlendDistanceMeters,
    // ... rest of properties
};
```

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

**Update Junction Harmonization section**:
```razor
<!-- Junction Harmonization Card -->
<MudPaper Class="pa-3 mb-3" Elevation="1">
    <MudText Typo="Typo.subtitle2" Class="mb-2">
        <MudIcon Icon="@Icons.Material.Filled.CallSplit" Size="Size.Small" Class="mr-1"/>
        Junction Harmonization
    </MudText>
    
    <MudGrid Spacing="2">
        <MudItem xs="12">
            <MudSwitch @bind-Value="Material.EnableJunctionHarmonization"
                      Color="Color.Primary"
                      Label="Enable Junction Harmonization"/>
        </MudItem>
        
        @if (Material.EnableJunctionHarmonization)
        {
            <MudItem xs="12">
                <MudSwitch @bind-Value="Material.UseGlobalJunctionSettings"
                          Color="Color.Secondary"
                          Label="Use Global Junction Settings"/>
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    @(Material.UseGlobalJunctionSettings 
                        ? "Using global junction detection and blend settings" 
                        : "Using custom junction settings for this material")
                </MudText>
            </MudItem>
            
            @* Show per-material settings only when not using global *@
            @if (!Material.UseGlobalJunctionSettings)
            {
                <MudItem xs="12" sm="6">
                    <MudNumericField @bind-Value="Material.JunctionDetectionRadiusMeters"
                                    Label="Junction Detection Radius (m)"
                                    Variant="Variant.Outlined"
                                    Min="1.0f" Max="50.0f" Step="1.0f"/>
                </MudItem>
                
                <MudItem xs="12" sm="6">
                    <MudNumericField @bind-Value="Material.JunctionBlendDistanceMeters"
                                    Label="Junction Blend Distance (m)"
                                    Variant="Variant.Outlined"
                                    Min="5.0f" Max="100.0f" Step="5.0f"/>
                </MudItem>
            }
            
            @* Always show these - they're always per-material *@
            <MudItem xs="12" sm="6">
                <MudSelect T="JunctionBlendFunctionType"
                          @bind-Value="Material.JunctionBlendFunction"
                          Label="Blend Function"
                          Variant="Variant.Outlined">
                    @* options *@
                </MudSelect>
            </MudItem>
            
            @* ... endpoint taper settings ... *@
        }
    </MudGrid>
</MudPaper>
```

#### Step 3.4.5: Update Orchestrator

**File**: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`

**Update `BuildTerrainParameters()`**:
```csharp
private static TerrainCreationParameters BuildTerrainParameters(
    TerrainGenerationState state,
    List<MaterialDefinition> materialDefinitions)
{
    var parameters = new TerrainCreationParameters
    {
        // ... existing properties ...
        
        // NEW: Global junction parameters
        GlobalJunctionDetectionRadiusMeters = state.GlobalJunctionDetectionRadiusMeters,
        GlobalJunctionBlendDistanceMeters = state.GlobalJunctionBlendDistanceMeters,
    };
    
    // ... rest of method
}
```

#### Step 3.4.6: Update UnifiedRoadSmoother

**File**: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

**Update `SmoothAllRoads()`**:
```csharp
public UnifiedSmoothingResult? SmoothAllRoads(
    float[,] heightMap,
    List<MaterialDefinition> materials,
    float metersPerPixel,
    int size,
    bool enableCrossMaterialHarmonization = true,
    float globalJunctionDetectionRadius = 10.0f,    // NEW
    float globalJunctionBlendDistance = 30.0f)      // NEW
{
    // ... existing code ...
    
    // Phase 3: Detect and harmonize junctions
    if (enableCrossMaterialHarmonization && ShouldHarmonize(roadMaterials))
    {
        // Use global params, but harmonizer will check per-material overrides
        var harmonizationResult = _junctionHarmonizer.HarmonizeNetwork(
            network,
            heightMap,
            metersPerPixel,
            globalJunctionDetectionRadius,    // Global default
            globalJunctionBlendDistance);     // Global default
        
        // ... rest of harmonization
    }
}
```

#### Step 3.4.7: Update NetworkJunctionDetector

**File**: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs`

**Update to support per-spline detection radius**:
```csharp
public List<NetworkJunction> DetectJunctions(
    UnifiedRoadNetwork network,
    float globalDetectionRadius)
{
    // For each spline endpoint, use either global or per-material radius
    foreach (var spline in network.Splines)
    {
        var junctionParams = spline.Parameters.JunctionHarmonizationParameters;
        float effectiveRadius = (junctionParams?.UseGlobalSettings ?? true)
            ? globalDetectionRadius
            : junctionParams.JunctionDetectionRadiusMeters;
        
        // Use effectiveRadius for this spline's endpoint detection
        // ...
    }
}
```

#### Step 3.4.8: Update Preset Export/Import

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor`

**Add to `BuildAppSettings()`**:
```csharp
["terrainOptions"] = new JsonObject
{
    // ... existing ...
    ["globalJunctionDetectionRadiusMeters"] = GlobalJunctionDetectionRadiusMeters,  // NEW
    ["globalJunctionBlendDistanceMeters"] = GlobalJunctionBlendDistanceMeters,      // NEW
}
```

**Add to material's junction settings**:
```csharp
["junctionHarmonization"] = new JsonObject
{
    ["useGlobalSettings"] = mat.UseGlobalJunctionSettings,  // NEW
    // ... existing ...
}
```

**File**: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor`

**Update `ImportAppSettings()`** to read the new global values.

### 3.5 Acceptance Criteria

- [ ] Global junction settings visible in GenerateTerrain page (only when cross-material harmonization enabled)
- [ ] Per-material override toggle works (shows/hides per-material inputs)
- [ ] Default is "Use Global Settings" = true
- [ ] Backend correctly uses global values when `UseGlobalSettings = true`
- [ ] Backend correctly uses per-material values when `UseGlobalSettings = false`
- [ ] Preset export includes global settings and per-material `useGlobalSettings` flag
- [ ] Preset import correctly restores global and per-material settings

---

## 4. Summary of All Changes

### Files to Delete

1. `BeamNgTerrainPoc/Terrain/Models/DirectMaskRoadParameters.cs`
2. `BeamNgTerrainPoc/Terrain/Algorithms/DirectTerrainBlender.cs`

### Files to Modify (Major Changes)

1. `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` - Remove DirectMask
2. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` - Remove DirectMask UI, add junction override
3. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` - Remove DirectMask props, add junction override
4. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` - Add global junction settings
5. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` - Add global junction accessors
6. `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` - Add global junction state
7. `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` - Accept global junction params

### Files to Modify (Minor Changes)

1. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` - Export changes
2. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` - Import changes
3. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs` - Model changes
4. `BeamNgTerrainPoc/Examples/RoadSmoothingPresets.cs` - Remove DirectMask from presets
5. `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` - Add UseGlobalSettings
6. `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs` - Add global params
7. `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs` - Per-spline radius
8. `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` - Pass global params

### Files to Review/Potentially Delete

1. `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` - Check if still needed

---

## 5. Implementation Order

1. **Phase A**: Remove DirectMask (Section 1) - Cleanest to do first
   - Delete unused files
   - Update models
   - Update UI
   - Update presets

2. **Phase B**: Update Debug Text (Section 2) - Quick changes
   - Update labels and tooltips

3. **Phase C**: Split Junction Parameters (Section 3) - Most complex
   - Add models
   - Add state
   - Add global UI
   - Add per-material override UI
   - Update backend
   - Update preset export/import

4. **Phase D**: Verification
   - Build and fix any compilation errors
   - Test terrain generation with various configurations
   - Verify preset export/import roundtrip

---

## 6. Testing Checklist

- [ ] Build succeeds with no warnings
- [ ] Search for "DirectMask" returns no results (except comments/docs if intentionally kept)
- [ ] UI shows no Approach selector or DirectMask tab
- [ ] Legacy preset with DirectMask settings imports without error
- [ ] Global junction settings appear when cross-material harmonization enabled
- [ ] Per-material junction override toggle works correctly
- [ ] Terrain generation works with global-only junction settings
- [ ] Terrain generation works with mixed global/per-material junction settings
- [ ] Debug images export correctly with updated names
- [ ] Preset export includes all new fields
- [ ] Preset import restores all new fields correctly