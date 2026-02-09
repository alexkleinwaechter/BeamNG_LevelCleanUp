# Wizard Terrain Size Integration - Implementation Summary

## Overview

Integrated the terrain size selection from the Create Level wizard into the terrain material copying process. This ensures that generated terrain textures use the correct size based on the user's intended terrain dimensions.

## Problem Statement

When copying terrain materials in wizard mode, the system needs to know the planned terrain size to:
1. Generate placeholder textures at the correct resolution
2. Set the correct `baseTexSize` in `TerrainMaterialTextureSet`
3. Ensure consistent texture resolution across the new level

Previously, the terrain size was read from existing `*.terrain.json` files, which don't exist yet in a newly created level.

## Solution Architecture

### Data Flow

```
CreateLevelWizardState.TerrainSize (user selection: 256-16384)
    ?
PathResolver.WizardTerrainSize (static property for cross-class access)
    ?
AssetCopy.CopyTerrainMaterialsBatch() (uses for PBR upgrade)
    ?
TerrainMaterialCopier.Copy() (uses for texture generation)
```

## Implementation Details

### 1. Added Terrain Size to WizardState

**File**: `BeamNG_LevelCleanUp/Objects/CreateLevelWizardState.cs`

```csharp
/// <summary>
///     Planned terrain size (power of 2, e.g., 1024, 2048, 4096)
/// </summary>
public int TerrainSize { get; set; } = 2048; // Default to 2048
```

**Available values**: 256, 512, 1024, 2048 (default), 4096, 8192, 16384

### 2. Added UI Dropdown in CreateLevel

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`

```razor
<MudSelect T="int" 
          @bind-Value="_wizardState.TerrainSize" 
          Label="Terrain Size" 
          HelperText="Size of the terrain in meters (power of 2)"
          Variant="Variant.Outlined"
          Class="mb-4">
    <MudSelectItem Value="256">256 x 256</MudSelectItem>
    <MudSelectItem Value="512">512 x 512</MudSelectItem>
    <MudSelectItem Value="1024">1024 x 1024</MudSelectItem>
    <MudSelectItem Value="2048">2048 x 2048 (Default)</MudSelectItem>
    <MudSelectItem Value="4096">4096 x 4096</MudSelectItem>
    <MudSelectItem Value="8192">8192 x 8192</MudSelectItem>
    <MudSelectItem Value="16384">16384 x 16384</MudSelectItem>
</MudSelect>
```

### 3. Added Static Property to PathResolver

**File**: `BeamNG_LevelCleanUp/Logic/PathResolver.cs`

```csharp
/// <summary>
///     Target terrain size for wizard mode level creation (power of 2, e.g., 2048)
/// </summary>
public static int? WizardTerrainSize { get; set; }
```

**Purpose**: Provides cross-class access to wizard terrain size without changing method signatures

### 4. Set Wizard Size Before Copying

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor`

```csharp
private async Task CopyDialogWizardMode()
{
    // ... dialog code ...
    
    if (!result.Canceled)
    {
        // Set wizard terrain size in PathResolver before copying
        if (WizardState != null)
        {
            PathResolver.WizardTerrainSize = WizardState.TerrainSize;
        }
        
        await Task.Run(() =>
        {
            var selected = _selectedItems.Select(y => y.Identifier).ToList();
            Reader.DoCopyAssets(selected);
        });
        
        // Clear wizard terrain size after copying
        PathResolver.WizardTerrainSize = null;
    }
}
```

### 5. Updated AssetCopy to Use Wizard Size

**File**: `BeamNG_LevelCleanUp/LogicCopyAssets/AssetCopy.cs`

```csharp
// Get terrain size - use wizard size if available, otherwise read from JSON
var terrainSize = PathResolver.WizardTerrainSize 
    ?? TerrainTextureHelper.GetTerrainSizeFromJson(PathResolver.LevelNamePath) 
    ?? 1024;
```

**Fallback chain**:
1. `PathResolver.WizardTerrainSize` (from wizard)
2. `GetTerrainSizeFromJson()` (from existing terrain.json)
3. `1024` (hardcoded fallback)

### 6. Updated TerrainMaterialCopier to Use Wizard Size

**File**: `BeamNG_LevelCleanUp/LogicCopyAssets/TerrainMaterialCopier.cs`

```csharp
// Load terrain size with fallback logic:
// 1. Try wizard terrain size (from CreateLevel wizard)
// 2. Fall back to LoadBaseTextureSize (reads from JSON or uses 2048 default)
if (!_baseTextureSize.HasValue)
{
    _baseTextureSize = PathResolver.WizardTerrainSize 
        ?? TerrainTextureHelper.LoadBaseTextureSize(_targetLevelPath);
}
```

**Added using statement**:
```csharp
using BeamNG_LevelCleanUp.Logic;
```

## How It Works

### Standard Mode (Non-Wizard)

1. User selects source and target maps manually
2. `PathResolver.WizardTerrainSize` remains `null`
3. System reads terrain size from existing `*.terrain.json` file
4. Falls back to 2048 if no file exists

### Wizard Mode

1. User selects terrain size in CreateLevel wizard (e.g., 4096)
2. Value stored in `WizardState.TerrainSize`
3. Before copying, `PathResolver.WizardTerrainSize = WizardState.TerrainSize`
4. `AssetCopy` uses wizard size for `TerrainMaterialTextureSet`
5. `TerrainMaterialCopier` uses wizard size for texture generation
6. After copying, `PathResolver.WizardTerrainSize = null` (cleanup)

## Impact on Texture Generation

When generating placeholder textures (baseColor, roughness, normal, etc.):

```csharp
var textureGenerator = new TerrainTextureGenerator(targetTerrainFolder, terrainSize.Value);
```

**Before**: Would use 2048 (default) or size from non-existent terrain.json  
**After**: Uses user-selected size from wizard (e.g., 4096)

This ensures:
- ? Correct texture resolution for the intended terrain size
- ? Proper `baseTexSize` in `TerrainMaterialTextureSet`
- ? Consistent texture quality across the level

## Testing Scenarios

### Wizard Mode

1. Create new level with terrain size 4096
2. Copy terrain materials
3. Verify generated textures are 4096x4096
4. Verify `TerrainMaterialTextureSet.baseTexSize = [4096, 4096]`

### Standard Mode

1. Copy terrain materials between existing maps
2. Verify terrain size is read from target's terrain.json
3. Verify fallback to 2048 if no terrain.json exists

### Edge Cases

1. **Wizard canceled mid-process**: `WizardTerrainSize` is cleared
2. **Multiple sequential copies**: Each copy uses correct wizard size
3. **Mixed wizard/standard mode**: No interference between modes

## Benefits

1. **User Control**: User explicitly chooses terrain size for new levels
2. **Correct Resolution**: Generated textures match intended terrain dimensions
3. **No Breaking Changes**: Standard mode unchanged, wizard mode enhanced
4. **Simple Architecture**: Minimal changes, leverages existing `PathResolver` pattern

## Files Modified

1. `BeamNG_LevelCleanUp/Objects/CreateLevelWizardState.cs` - Added `TerrainSize` property
2. `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor` - Added terrain size dropdown
3. `BeamNG_LevelCleanUp/Logic/PathResolver.cs` - Added `WizardTerrainSize` property
4. `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor` - Set/clear wizard size
5. `BeamNG_LevelCleanUp/LogicCopyAssets/AssetCopy.cs` - Use wizard size in PBR upgrade
6. `BeamNG_LevelCleanUp/LogicCopyAssets/TerrainMaterialCopier.cs` - Use wizard size for textures

## Build Status

? **Build Successful** - No compilation errors  
? **Backward Compatible** - Standard mode unchanged  
? **Wizard Enhanced** - Terrain size flows correctly through the system  

---

**Feature**: Wizard terrain size integration  
**Impact**: Correct texture generation based on user-selected terrain size  
**Status**: ? COMPLETE  
**Date**: December 2024
