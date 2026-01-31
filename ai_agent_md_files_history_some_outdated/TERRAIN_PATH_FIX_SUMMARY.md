# Terrain Copy Path Fix Summary

## Problem
When copying terrain materials, the target paths in the material JSON files were incorrect due to inconsistent handling of leading slashes in BeamNG path formats.

### Root Cause
BeamNG terrain material JSON files use paths with **leading forward slashes**, such as:
```json
"aoBaseTex": "/levels/driver_training/art/terrains/t_terrain_base_ao.png"
```

The code had two issues:
1. **PathResolver.cs** - Didn't strip the leading `/` before processing paths
2. **AssetCopy.cs** - `GetBeamNgJsonFileName()` only handled Windows backslash format, not forward slash format

## Solution

### 1. Fixed PathResolver.ResolvePath() 
**File:** `BeamNG_LevelCleanUp\Logic\PathResolver.cs`

Added logic to remove leading slashes before processing:
```csharp
// Remove leading slash if present - BeamNG paths like "/levels/..." are relative to game root
if (resourcePath.StartsWith("/"))
{
    resourcePath = resourcePath.Substring(1);
}
```

This ensures paths like `/levels/driver_training/art/terrains/texture.png` are treated as relative paths from the game root.

### 2. Fixed GetBeamNgJsonFileName()
**File:** `BeamNG_LevelCleanUp\LogicCopyAssets\AssetCopy.cs`

Rewrote the method to handle both path formats:
```csharp
private string GetBeamNgJsonFileName(string windowsFileName)
{
    // Normalize the path - replace backslashes with forward slashes first
    var normalizedPath = windowsFileName.Replace(@"\", "/");
    
    // Extract the part after "/levels/"
    var targetParts = normalizedPath.ToLowerInvariant().Split("/levels/");
    if (targetParts.Count() < 2)
    {
        // Try with backslash if forward slash didn't work
        // ... fallback logic ...
    }
  
    // Build the final path: "levels/" + remaining path, without extension
    var finalPath = "levels/" + targetParts.Last();
    
    // Ensure forward slashes and remove extension
    return Path.ChangeExtension(finalPath.Replace(@"\", "/"), null);
}
```

## Testing
The fix handles paths in both formats:
- ✅ `/levels/driver_training/art/terrains/texture.png` (BeamNG JSON format)
- ✅ `D:\Path\levels\driver_training\art\terrains\texture.png` (Windows file system)
- ✅ `levels/driver_training/art/terrains/texture.png` (relative path)

## Result
Terrain material texture paths are now correctly resolved and written to the target `main.materials.json` file, regardless of whether the source uses leading slashes or not.
