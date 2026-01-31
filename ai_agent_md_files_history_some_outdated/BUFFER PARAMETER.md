# Implementation Plan: Configurable Road Edge Protection Buffer

## Overview

This plan adds a configurable **Road Edge Protection Buffer** parameter that controls how far beyond the road edge the terrain elevation is protected from other roads' blend zones. This prevents lower-priority roads from damaging the edges of higher-priority roads.

## Problem Statement

When a lower-priority road (e.g., dirt road) meets a higher-priority road (e.g., asphalt), the blend zone of the lower-priority road can extend into and modify the terrain near the higher-priority road's edge, causing visible damage/artifacts.

The current protection buffer is hardcoded at 1.0m, which is insufficient in many cases.

## Solution

Add a configurable `RoadEdgeProtectionBufferMeters` parameter per material that:
1. Defaults to **2.0 meters** (increased from 1.0m)
2. Can be adjusted per road material
3. Is saved/loaded in presets
4. Controls how far beyond the road core the protection mask extends

---

## Files to Modify

### 1. Library Layer (BeamNgTerrainPoc)

#### `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

Add new property:

```csharp
/// <summary>
/// Buffer distance (in meters) beyond the road edge that is protected
/// from other roads' blend zones. Higher values prevent edge damage
/// from lower-priority roads meeting this road.
/// Default: 2.0m
/// </summary>
public float RoadEdgeProtectionBufferMeters { get; set; } = 2.0f;
```

#### `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs`

**Change 1:** Remove the hardcoded constant:

```csharp
// REMOVE THIS:
// private const float ProtectionMaskBufferMeters = 1.0f;
```

**Change 2:** Modify `BuildRoadCoreProtectionMaskWithOwnership()` to use per-spline buffer:

In the loop where cross-sections are processed, get the buffer from the spline's parameters:

```csharp
// Get protection buffer from spline parameters (or default)
var protectionBuffer = spline.Parameters.RoadEdgeProtectionBufferMeters;

// Add buffer to road width for protection mask
var halfWidth1 = cs1.EffectiveRoadWidth / 2.0f + protectionBuffer;
var halfWidth2 = cs2.EffectiveRoadWidth / 2.0f + protectionBuffer;
```

This requires access to the spline when processing cross-sections. The method already has access to `network.Splines`.

---

### 2. UI Layer (BeamNG_LevelCleanUp)

#### `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs`

Add tooltip constant:

```csharp
public const string RoadEdgeProtectionBuffer = 
    "Distance (in meters) beyond the road edge that is protected from other roads' blend zones. " +
    "Higher values prevent lower-priority roads from damaging this road's edges. " +
    "Increase if you see edge artifacts where roads meet. Default: 2.0m";
```

#### `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

Add the parameter in the Road Smoothing section, near other road geometry parameters (after Road Width or Terrain Affected Range):

```razor
@* Road Edge Protection Buffer *@
<MudItem xs="12" sm="6" md="4">
    <div class="d-flex align-center gap-1">
        <MudNumericField @bind-Value="Material.RoadEdgeProtectionBufferMeters"
                         Label="Edge Protection Buffer (m)"
                         Variant="Variant.Outlined"
                         Min="0.0f" Max="20.0f" Step="0.5f"
                         Immediate="true"
                         ValueChanged="OnRoadParameterChanged" />
        <MudTooltip Text="@RoadParameterTooltips.RoadEdgeProtectionBuffer">
            <MudIcon Icon="@Icons.Material.Filled.Help" Size="Size.Small" Color="Color.Secondary" />
        </MudTooltip>
    </div>
</MudItem>
```

#### `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

Add property mapping in `TerrainMaterialItemExtended` class:

```csharp
/// <summary>
/// Buffer distance beyond road edge protected from other roads' blend zones.
/// </summary>
public float RoadEdgeProtectionBufferMeters { get; set; } = 2.0f;
```

Update `ToRoadSmoothingParameters()` method to include the new property:

```csharp
RoadEdgeProtectionBufferMeters = RoadEdgeProtectionBufferMeters,
```

Update `ApplyRoadSmoothingParameters()` method to load the property:

```csharp
RoadEdgeProtectionBufferMeters = parameters.RoadEdgeProtectionBufferMeters;
```

---

### 3. Preset Save/Load (BeamNG_LevelCleanUp)

#### `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` (or code-behind)

The preset export already serializes `TerrainMaterialItemExtended` objects, so the new property will be automatically included in the JSON serialization.

#### `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` (or code-behind)

The preset import deserializes to `TerrainMaterialItemExtended`, so the property will be automatically loaded. However, we need to handle **backward compatibility** for presets created before this property existed.

Add default value handling in the import logic (if not already handled by JSON deserialization defaults):

```csharp
// After deserializing materials, ensure defaults for new properties
foreach (var material in importedMaterials)
{
    // Handle presets created before RoadEdgeProtectionBufferMeters existed
    if (material.RoadEdgeProtectionBufferMeters <= 0)
    {
        material.RoadEdgeProtectionBufferMeters = 2.0f;
    }
}
```

---

## Implementation Order

### Phase 1: Library Support
1. Add `RoadEdgeProtectionBufferMeters` property to `RoadSmoothingParameters.cs`
2. Modify `UnifiedTerrainBlender.cs` to use the per-spline buffer instead of hardcoded constant

### Phase 2: UI Support
1. Add tooltip in `RoadParameterTooltips.cs`
2. Add property to `TerrainMaterialItemExtended` class
3. Add UI field in `TerrainMaterialSettings.razor`
4. Update `ToRoadSmoothingParameters()` and `ApplyRoadSmoothingParameters()` methods

### Phase 3: Preset Compatibility
1. Verify preset export includes new property (automatic)
2. Add backward compatibility handling in preset import

### Phase 4: Testing
1. Verify default value of 2.0m works correctly
2. Test with various buffer values (0, 2, 5, 10)
3. Verify presets save and load the value correctly
4. Test backward compatibility with older presets

---

## Code Changes Summary

| File | Change Type | Description |
|------|-------------|-------------|
| `RoadSmoothingParameters.cs` | Add property | `RoadEdgeProtectionBufferMeters` with default 2.0f |
| `UnifiedTerrainBlender.cs` | Modify | Use per-spline buffer instead of constant |
| `RoadParameterTooltips.cs` | Add constant | Tooltip text for new parameter |
| `TerrainMaterialSettings.razor` | Add UI | NumericField for buffer |
| `TerrainMaterialSettings.razor.cs` | Add property + mapping | Property and serialization |
| `TerrainPresetImporter.razor` | Add fallback | Backward compatibility for old presets |

---

## UI Placement

The new field should appear in the **Road Smoothing** section, grouped with other road geometry parameters:

```
Road Smoothing Settings
├── Road Width (m)
├── Terrain Affected Range (m)
├── Edge Protection Buffer (m)  ← NEW
├── Blend Function Type
├── ...
```

---

## Expected Behavior

| Buffer Value | Effect |
|--------------|--------|
| 0.0m | No extra protection (only road core is protected) |
| 2.0m (default) | 2m buffer beyond road edge - good for most cases |
| 5.0m+ | Larger protection zone - for wide roads or aggressive terrain |

Higher values = more protection but may cause visible "steps" where protection zone ends.

---

## Backward Compatibility

- Old presets without `RoadEdgeProtectionBufferMeters` will get the default value of 2.0m
- Existing terrain generation with default parameters will behave better (2.0m vs old 1.0m)