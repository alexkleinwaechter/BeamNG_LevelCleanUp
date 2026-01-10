# 3D Viewer Texture Investigation

## Status: Textures Loading Successfully, Rendering Issue Suspected

**Date:** January 2025  
**Branch:** feature/3d_viewer

---

## Summary

Debug logging confirmed that the texture pipeline is working correctly:
- ? Materials are found via `FindMaterialByKey` (Strategy 1)
- ? `MaterialFiles` are populated with texture paths
- ? Textures resolve correctly (files exist or `.link` files can be resolved)
- ? `TextureLoader.LoadTexture` returns non-null `TextureModel`
- ? Textures are assigned to `material.DiffuseMap`

**If textures are still not visible on the 3D model, the issue is in Helix Toolkit rendering, not the material/texture lookup.**

---

## Areas to Investigate

### 1. UV Coordinate Conversion

**File:** `HelixViewportControl.xaml.cs` - `ConvertToWpfGeometry`

Check if UV coordinates are being correctly transferred from the Assimp-loaded geometry to the WPF geometry:

```csharp
// Current implementation
if (coreGeometry.TextureCoordinates != null)
{
    var wpfTexCoords = new Vector2Collection();
    foreach (var texCoord in coreGeometry.TextureCoordinates) 
        wpfTexCoords.Add(texCoord);
    wpfGeometry.TextureCoordinates = wpfTexCoords;
}
```

**Questions:**
- Are `TextureCoordinates` null for some meshes?
- Are UV values in expected range (0-1)?
- Does BeamNG use flipped V coordinates (1-V)?

### 2. Lighting Configuration

**File:** `HelixViewportControl.xaml`

Verify the viewport has proper lighting:
- Is there an `AmbientLight3D`?
- Are there `DirectionalLight3D` or `PointLight3D` elements?
- Is light intensity sufficient?

**Questions:**
- Does PhongMaterial require specific lighting to show diffuse textures?
- Is the scene too dark?

### 3. Material Application

**File:** `HelixViewportControl.xaml.cs` - `ProcessSceneNode`

Verify material is correctly assigned:

```csharp
var meshElement = new MeshGeometryModel3D
{
    Geometry = wpfGeometry,
    Material = material,  // <-- Is this the textured material?
    ...
};
```

**Questions:**
- Is `CreateMaterialForNode` returning the textured material or falling back to default?
- Is the material being replaced/overwritten somewhere?

### 4. Texture Format Compatibility

**File:** `TextureLoader.cs`

DDS textures are converted via Pfim:

```csharp
// Current flow: DDS -> Pfim -> BitmapSource -> PNG -> TextureModel
```

**Questions:**
- Are all DDS compression formats supported by Pfim?
- Is the PNG conversion losing data?
- Should we use `TextureModel.Create(stream)` directly with DDS bytes?

### 5. Helix Toolkit SharpDX vs WPF Material Types

We're using `HelixToolkit.Wpf.SharpDX` with `PhongMaterial` and `PBRMaterial`.

**Questions:**
- Are we using the correct material types for SharpDX?
- Do SharpDX materials have different texture requirements than WPF materials?
- Is `TextureModel` the correct texture type for SharpDX materials?

---

## Debug Steps

### Step 0: Use TextureMapConfig (NEW)
A central configuration class has been added to control texture loading:
```csharp
// Enable debug logging to see texture loading details
TextureMapConfig.EnableDebugLogging = true;

// Test with only base color (simplest case)
TextureMapConfig.EnableBaseColorOnly();

// Or disable all textures to test geometry/lighting
TextureMapConfig.DisableAll();

// Or enable specific maps for testing
TextureMapConfig.EnableBaseColorMap = true;
TextureMapConfig.EnableNormalMap = false;
TextureMapConfig.EnableOpacityMap = false;
```

Configuration options:
- `EnableBaseColorMap` - Primary color texture (default: true)
- `EnableOpacityMap` - Transparency texture (default: true)
- `EnableNormalMap` - Surface detail bumps (default: true)
- `EnableRoughnessMap` - PBR roughness (default: false, may cause issues)
- `EnableMetallicMap` - PBR metallic (default: false, may cause issues)
- `EnableAmbientOcclusionMap` - Shadowing in crevices (default: true)
- `EnableEmissiveMap` - Glowing surfaces (default: true)
- `EnableSpecularMap` - Phong highlights (default: true)
- `EnableDebugLogging` - Output window logging (default: false)

### Step 1: Verify UV Coordinates
Add logging to check if UVs exist:
```csharp
System.Diagnostics.Debug.WriteLine($"Mesh {meshNode.Name}: UVs = {coreGeometry.TextureCoordinates?.Count ?? 0}");
```

### Step 2: Test with Simple Texture
Try loading a known-good PNG texture directly to rule out DDS conversion issues.

### Step 3: Check Viewport Lighting
Add explicit lighting to XAML:
```xml
<hx:AmbientLight3D Color="White" />
<hx:DirectionalLight3D Color="White" Direction="-1,-1,-1" />
```

### Step 4: Verify Material Properties
Log the material properties after creation:
```csharp
if (material is PhongMaterial phong)
    Debug.WriteLine($"DiffuseMap set: {phong.DiffuseMap != null}");
```

### Step 5: Test Material on Simple Geometry
Create a test cube with the loaded texture to verify the texture itself renders.

---

## Related Files

| File | Purpose |
|------|---------|
| `HelixViewportControl.xaml` | Viewport XAML with lighting |
| `HelixViewportControl.xaml.cs` | Scene processing, geometry conversion |
| `MaterialFactory.cs` | Material creation from BeamNG materials |
| `TextureMapConfig.cs` | **Central config for enabling/disabling texture types** |
| `TextureLookup.cs` | Material name to texture path mapping |
| `TextureLoader.cs` | DDS/PNG texture loading |
| `LinkFileResolver.cs` | .link file resolution from ZIP |

---

## Debug Log Evidence

From `CreatePhongMaterial.log`:
```
[18:01:26] Material 'eca_bld_decking': DiffuseMapPath='...\eca_bld_decking_d.dds'
[18:01:26] Material 'eca_bld_decking': Texture loaded = YES
```

From `MaterialFactory_Lookups.json`:
```json
{
  "NodeName": "eca_bld_decking",
  "FoundVia": "Strategy 1 - FindMaterialByKey",
  "FoundMaterialName": "eca_bld_decking"
}
```

---

## Hypothesis

The most likely issues are:
1. **Missing/incorrect UV coordinates** - Meshes may not have UVs or they need flipping
2. **Lighting configuration** - Scene may be too dark or missing lights
3. **DDS format compatibility** - Some DDS compression formats may not convert correctly

---

## Next Actions

1. [ ] Add UV coordinate logging to `ProcessSceneNode`
2. [ ] Verify lighting in `HelixViewportControl.xaml`
3. [ ] Test with a simple PNG texture
4. [ ] Check if BeamNG DAE files have UV coordinates
5. [ ] Try rendering the same model in a standalone Helix sample app
