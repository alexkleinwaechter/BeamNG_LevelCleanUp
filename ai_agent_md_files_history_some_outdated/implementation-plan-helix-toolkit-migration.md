# Implementation Plan: Replace Blazor3D with Helix Toolkit

## Overview

This document provides a detailed implementation plan for replacing the current `HomagGroup.Blazor3D` library with **Helix Toolkit** for 3D asset viewing in the BeamNG Tools application.

### Current State

- **Component**: `BlazorUI/Components/AssetViewer.razor`
- **Library**: `HomagGroup.Blazor3D` (Three.js-based, runs in WebView)
- **Issues**: 
  - Poor performance
  - Limited material support
  - JavaScript interop overhead
  - Requires copying files to `wwwroot/temp` for web serving
- **Features**: DAE file preview, material texture display on plane

### Target State

- **Library**: `HelixToolkit.Wpf.SharpDX` + `HelixToolkit.SharpDX.Assimp`
- **Integration**: WPF control hosted in Windows Forms via `ElementHost`
- **Benefits**: 
  - Native .NET rendering (no JS interop)
  - Direct file access via `PathResolver` (no temp folder copying)
  - Robust Collada parsing via Assimp
  - Multi-material support
  - Better performance
  - Reusable across the application (not tied to CopyAssets)

---

## Supported Preview Types

The viewer must support previewing assets from multiple contexts:

| Preview Type | Source | 3D Display | Use Cases |
|--------------|--------|------------|-----------|
| **DAE Model** | `.dae` file + materials | Full 3D model with textures | CopyAssets (Dae type), ForestBrush shapes |
| **Material on Plane** | Material definition | Textured plane (aspect ratio preserved) | Roads, Decals, DecalRoads, Terrain materials |
| **Texture Only** | Single texture file | Textured plane | Quick texture preview |

### Preview Type Detection

The viewer should **NOT** be tied to `CopyAssetType`. Instead, use a generic `ViewerRequest` that describes what to show:

```csharp
public enum Viewer3DMode
{
    DaeModel,           // Load .dae file with materials
    MaterialOnPlane,    // Show material textures on a plane
    TextureOnly         // Show single texture on plane
}

public class Viewer3DRequest
{
    public Viewer3DMode Mode { get; set; }
    
    // For DaeModel mode
    public string DaeFilePath { get; set; }
    
    // For all modes - materials to display
    public List<MaterialJson> Materials { get; set; } = new();
    
    // Display name for the viewer title
    public string DisplayName { get; set; }
    
    // Optional: source level path for PathResolver
    public string LevelPath { get; set; }
}
```

---

## Architecture

### Current Architecture (Blazor3D)

```
???????????????????????????????????????????????????????????????????
?              Windows Forms Application (Main)                    ?
???????????????????????????????????????????????????????????????????
?  ?????????????????????????????????????????????????????????????  ?
?  ?                    BlazorWebView                          ?  ?
?  ?  ???????????????????????????????????????????????????????  ?  ?
?  ?  ?          MudDialog (AssetViewer.razor)              ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ?  ?    HomagGroup.Blazor3D.Viewers.Viewer         ?  ?  ?  ?
?  ?  ?  ?    (Three.js via JavaScript Interop)          ?  ?  ?  ?
?  ?  ?  ?    - JS module loads DAE via ColladaLoader    ?  ?  ?  ?
?  ?  ?  ?    - Textures served from wwwroot/temp ?     ?  ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ?  ?    Texture Gallery (MudGrid of MudCards)      ?  ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ???????????????????????????????????????????????????????  ?  ?
?  ?????????????????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????????????????
```

**Problems with current approach:**
- Files must be copied to `wwwroot/temp` for web serving
- DDS files must be converted to PNG for browser compatibility
- Memory overhead from file duplication
- Cleanup complexity

### Target Architecture (Helix Toolkit)

```
???????????????????????????????????????????????????????????????????
?              Windows Forms Application (Main)                    ?
???????????????????????????????????????????????????????????????????
?  ?????????????????????????????????????????????????????????????  ?
?  ?                    BlazorWebView                          ?  ?
?  ?  ???????????????????????????????????????????????????????  ?  ?
?  ?  ?     Any Page (CopyAssets, CopyTerrains, etc.)       ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ?  ?    Button: "Preview" ???????????????????????????????????
?  ?  ?  ?    (Opens HelixViewerForm via Viewer3DService)?  ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?  ?
?  ?  ???????????????????????????????????????????????????????  ?  ?
?  ?????????????????????????????????????????????????????????????  ?
?                                                                  ?
?  ????????????????????????????????????????????????????????????????
?  ?          HelixViewerForm (Windows Forms Dialog)           ?
?  ?  ???????????????????????????????????????????????????????  ?
?  ?  ?    ElementHost (WPF in WinForms)                    ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?
?  ?  ?  ?    HelixToolkit.Wpf.SharpDX.Viewport3DX      ?  ?  ?
?  ?  ?  ?    - Native .NET DAE loading via Assimp      ?  ?  ?
?  ?  ?  ?    - Direct file access via PathResolver ?   ?  ?  ?
?  ?  ?  ?    - DDS texture loading (no conversion) ?   ?  ?  ?
?  ?  ?  ?    - Multi-material support                   ?  ?  ?
?  ?  ?  ?????????????????????????????????????????????????  ?  ?
?  ?  ???????????????????????????????????????????????????????  ?
?  ?  ???????????????????????????????????????????????????????  ?
?  ?  ?    Texture Gallery Panel (native WinForms)          ?  ?
?  ?  ?    - Direct file loading via PathResolver           ?  ?
?  ?  ?    - DDS rendering via DDSImage utility             ?  ?
?  ?  ???????????????????????????????????????????????????????  ?
?  ?????????????????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????????????????
```

**Benefits of new approach:**
- Direct file access using existing `PathResolver` logic
- No temp folder copying needed
- Helix Toolkit can load DDS textures natively
- Viewer is reusable from any page
- Clean separation: Blazor handles UI, native WinForms handles 3D

---

## Using PathResolver for Direct File Access

### Current PathResolver Capabilities

The existing `PathResolver` class provides robust path resolution:

```csharp
// Logic/PathResolver.cs - Key methods:

// Resolve a BeamNG path to a Windows file system path
public static string ResolvePath(string levelPath, string resourcePath, bool concatDistinctStrategy)

// Resolve relative to a .cs/.json file location
public static string ResolvePathBasedOnCsFilePath(FileInfo csFile, string resourcePath)

// Sanitize paths (handles "levels/levels" duplication, etc.)
public static string DirectorySanitizer(string path)
```

### Static Level Context

PathResolver uses static properties for the current level context:

```csharp
public static string LevelPath { get; set; }           // Target level path
public static string LevelPathCopyFrom { get; set; }   // Source level path (for copy operations)
public static string LevelName;                         // Target level name
public static string LevelNameCopyFrom;                 // Source level name
```

### Integration with Viewer

The viewer service should use PathResolver to access files directly:

```csharp
// In Viewer3DService:
public class Viewer3DService
{
    /// <summary>
    /// Resolves a BeamNG resource path to an absolute file system path.
    /// Uses the source level path (LevelPathCopyFrom) for asset previews.
    /// </summary>
    private string ResolveTexturePath(string beamNgPath, string levelPath = null)
    {
        levelPath ??= PathResolver.LevelPathCopyFrom ?? PathResolver.LevelPath;
        
        if (string.IsNullOrEmpty(levelPath))
            return beamNgPath; // Already absolute or can't resolve
        
        return PathResolver.ResolvePath(levelPath, beamNgPath, false);
    }
}
```

---

## Material Resolution Flow

The existing codebase has a well-established flow for resolving materials:

### 1. DAE File ? Material Names (DaeScanner)

```csharp
// Logic/DaeScanner.cs extracts material references from DAE files
public class DaeScanner
{
    // Reads material names from <library_materials> in DAE
    // Returns list of material names referenced by the model
}
```

### 2. Material Names ? Material Definitions (MaterialScanner)

```csharp
// Logic/MaterialScanner.cs finds *.materials.json files
public class MaterialScanner
{
    // Scans level for .materials.json files
    // Matches material names to definitions
    // Returns MaterialJson objects with all properties
}
```

### 3. Material Definition ? Texture Files (MaterialFileScanner)

```csharp
// Logic/MaterialFileScanner.cs resolves texture paths
public class MaterialFileScanner
{
    public List<MaterialFile> GetMaterialFiles(string materialName)
    {
        // For each texture property (colorMap, normalMap, etc.)
        // Resolves path using PathResolver
        // Returns list of MaterialFile with resolved FileInfo
    }
}
```

### 4. Key Data Structures

```csharp
// Objects/MaterialsJson.cs
public class MaterialJson
{
    public string Name { get; set; }
    public string InternalName { get; set; }
    public List<MaterialStage> Stages { get; set; }      // Texture properties
    public List<MaterialFile> MaterialFiles { get; set; } // Resolved files
    public string MatJsonFileLocation { get; set; }       // Source .materials.json path
}

// Objects/MaterialFile.cs
public class MaterialFile
{
    public FileInfo File { get; set; }        // Resolved file path
    public string MapType { get; set; }       // "ColorMap", "NormalMap", etc.
    public bool Missing { get; set; }         // True if file not found
    public string OriginalJsonPath { get; set; } // Original BeamNG path
}
```

### 5. Using This Flow in the Viewer

The viewer doesn't need to re-implement material resolution. The `CopyAsset` and `MaterialJson` objects already contain resolved `MaterialFile` entries with valid `FileInfo` references:

```csharp
// In Viewer3DService - materials are already resolved:
public void OpenViewer(Viewer3DRequest request)
{
    foreach (var material in request.Materials)
    {
        foreach (var file in material.MaterialFiles)
        {
            if (file.File.Exists)
            {
                // Direct file access - no copying needed!
                var texturePath = file.File.FullName;
                // Load texture directly into Helix Toolkit
            }
        }
    }
}
```

---

## NuGet Packages

### Packages to Add

```xml
<!-- Add to BeamNG_LevelCleanUp.csproj -->
<PackageReference Include="HelixToolkit.Wpf.SharpDX" Version="2.24.0" />
<PackageReference Include="HelixToolkit.SharpDX.Assimp" Version="2.24.0" />
```

### Packages to Remove (After Migration Complete)

```xml
<!-- Remove from BeamNG_LevelCleanUp.csproj -->
<PackageReference Include="Blazor3D" Version="..." />
```

### DDS Texture Support

Helix Toolkit with SharpDX can load DDS textures directly:

```csharp
// TextureModel.Create() supports DDS files natively
var texture = TextureModel.Create(ddsFilePath);
```

For the texture gallery (2D preview), continue using `DDSImage` utility:

```csharp
// Utils/DDSImage.cs - existing utility for DDS ? Bitmap conversion
var ddsImage = new DDSImage();
var bitmap = ddsImage.Load(ddsFilePath);
```

---

## File Structure

### New Files to Create

| File | Purpose |
|------|---------|
| `Viewer3D/HelixViewerForm.cs` | Windows Forms dialog hosting Helix Toolkit |
| `Viewer3D/HelixViewerForm.Designer.cs` | Designer file for the form |
| `Viewer3D/HelixViewportControl.xaml` | WPF UserControl with Viewport3DX |
| `Viewer3D/HelixViewportControl.xaml.cs` | Code-behind for WPF control |
| `Viewer3D/Viewer3DRequest.cs` | Generic request model (not tied to CopyAssetType) |
| `Viewer3D/ViewerMaterialMapper.cs` | Maps BeamNG materials to Helix materials |
| `BlazorUI/Services/Viewer3DService.cs` | Service to launch viewer from any Blazor page |

### Files to Modify

| File | Changes |
|------|---------|
| `BlazorUI/Components/AssetViewer.razor` | Replace Blazor3D with button to launch Helix viewer |
| `BlazorUI/Pages/CopyAssets.razor.cs` | Update `OpenAssetViewer` to use new service |
| `BeamNG_LevelCleanUp.csproj` | Add NuGet packages |

### Folder Structure

```
BeamNG_LevelCleanUp/
??? Viewer3D/                           # NEW FOLDER
?   ??? HelixViewerForm.cs              # WinForms dialog
?   ??? HelixViewerForm.Designer.cs     # Designer
?   ??? HelixViewportControl.xaml       # WPF UserControl
?   ??? HelixViewportControl.xaml.cs    # WPF code-behind
?   ??? Viewer3DRequest.cs              # Generic request model
?   ??? ViewerMaterialMapper.cs         # Material conversion
??? BlazorUI/
?   ??? Components/
?   ?   ??? AssetViewer.razor           # MODIFIED (optional - may remove)
?   ??? Pages/
?   ?   ??? CopyAssets.razor.cs         # MODIFIED
?   ??? Services/
?       ??? Viewer3DService.cs          # NEW - Bridge service
??? BeamNG_LevelCleanUp.csproj          # MODIFIED
```

---

## Implementation Phases

### Phase 1: Project Setup & Core Models

**Duration**: ~1 hour

#### Task 1.1: Add NuGet Packages

```xml
<!-- BeamNG_LevelCleanUp.csproj -->
<ItemGroup>
  <!-- Helix Toolkit for 3D rendering -->
  <PackageReference Include="HelixToolkit.Wpf.SharpDX" Version="2.24.0" />
  <PackageReference Include="HelixToolkit.SharpDX.Assimp" Version="2.24.0" />
</ItemGroup>
```

#### Task 1.2: Create Viewer3DRequest Model

**File**: `Viewer3D/Viewer3DRequest.cs`

```csharp
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Display mode for the 3D viewer.
/// </summary>
public enum Viewer3DMode
{
    /// <summary>Load a .dae file with materials applied.</summary>
    DaeModel,
    
    /// <summary>Show material textures on a plane (for roads, decals, terrain).</summary>
    MaterialOnPlane,
    
    /// <summary>Show a single texture on a plane.</summary>
    TextureOnly
}

/// <summary>
/// Request to open the 3D viewer. Generic - not tied to CopyAssetType.
/// Can be created from CopyAsset, MaterialJson, or direct file paths.
/// </summary>
public class Viewer3DRequest
{
    /// <summary>Display mode.</summary>
    public Viewer3DMode Mode { get; set; }
    
    /// <summary>Path to DAE file (for DaeModel mode).</summary>
    public string DaeFilePath { get; set; }
    
    /// <summary>Materials to display. Already resolved via MaterialScanner.</summary>
    public List<MaterialJson> Materials { get; set; } = new();
    
    /// <summary>Display name for the viewer title bar.</summary>
    public string DisplayName { get; set; }
    
    /// <summary>
    /// Optional: source level path for PathResolver.
    /// If null, uses PathResolver.LevelPathCopyFrom or PathResolver.LevelPath.
    /// </summary>
    public string LevelPath { get; set; }
    
    /// <summary>
    /// Creates a request from a CopyAsset (for CopyAssets page compatibility).
    /// </summary>
    public static Viewer3DRequest FromCopyAsset(CopyAsset asset)
    {
        var mode = asset.CopyAssetType switch
        {
            CopyAssetType.Dae => Viewer3DMode.DaeModel,
            CopyAssetType.Road => Viewer3DMode.MaterialOnPlane,
            CopyAssetType.Decal => Viewer3DMode.MaterialOnPlane,
            CopyAssetType.Terrain => Viewer3DMode.MaterialOnPlane,
            CopyAssetType.ForestBrush => Viewer3DMode.DaeModel, // Forest brushes reference DAE shapes
            _ => Viewer3DMode.MaterialOnPlane
        };
        
        return new Viewer3DRequest
        {
            Mode = mode,
            DaeFilePath = asset.DaeFilePath,
            Materials = asset.Materials,
            DisplayName = asset.Name,
            LevelPath = PathResolver.LevelPathCopyFrom
        };
    }
    
    /// <summary>
    /// Creates a request from a single MaterialJson.
    /// </summary>
    public static Viewer3DRequest FromMaterial(MaterialJson material, string levelPath = null)
    {
        return new Viewer3DRequest
        {
            Mode = Viewer3DMode.MaterialOnPlane,
            Materials = new List<MaterialJson> { material },
            DisplayName = material.Name ?? material.InternalName,
            LevelPath = levelPath
        };
    }
    
    /// <summary>
    /// Creates a request for a single texture file.
    /// </summary>
    public static Viewer3DRequest FromTexture(string texturePath, string displayName = null)
    {
        return new Viewer3DRequest
        {
            Mode = Viewer3DMode.TextureOnly,
            DaeFilePath = texturePath, // Reuse this field for single texture path
            DisplayName = displayName ?? Path.GetFileName(texturePath)
        };
    }
}
```

---

### Phase 2: Core Viewer Implementation

**Duration**: ~4-6 hours

#### Task 2.1: Create WPF Viewport Control

**File**: `Viewer3D/HelixViewportControl.xaml`

```xml
<UserControl x:Class="BeamNG_LevelCleanUp.Viewer3D.HelixViewportControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:hx="http://helix-toolkit.org/wpf/SharpDX"
             Background="#1E1E2E">
    
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <!-- 3D Viewport -->
        <hx:Viewport3DX 
            x:Name="Viewport"
            Grid.Row="0"
            BackgroundColor="#1E1E2E"
            ShowCoordinateSystem="True"
            CoordinateSystemLabelForeground="White"
            TextBrush="White"
            EnableSwapChainRendering="True"
            FXAALevel="Medium"
            MSAA="Four">
            
            <!-- Camera -->
            <hx:Viewport3DX.Camera>
                <hx:PerspectiveCamera 
                    Position="5,5,5" 
                    LookDirection="-1,-1,-1" 
                    UpDirection="0,0,1"
                    NearPlaneDistance="0.1"
                    FarPlaneDistance="10000"/>
            </hx:Viewport3DX.Camera>
            
            <!-- Default Lighting -->
            <hx:AmbientLight3D Color="#404040"/>
            <hx:DirectionalLight3D Direction="-1,-1,-1" Color="White"/>
            <hx:DirectionalLight3D Direction="1,1,0.5" Color="#808080"/>
            
            <!-- Camera Controller -->
            <hx:Viewport3DX.InputBindings>
                <!-- Default orbit, pan, zoom controls -->
            </hx:Viewport3DX.InputBindings>
            
        </hx:Viewport3DX>
        
        <!-- Status Bar -->
        <Border Grid.Row="1" Background="#2D2D3D" Padding="8">
            <TextBlock x:Name="StatusText" 
                       Foreground="White" 
                       Text="Ready"/>
        </Border>
    </Grid>
</UserControl>
```

**File**: `Viewer3D/HelixViewportControl.xaml.cs`

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Core.Assimp;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// WPF UserControl hosting the Helix Toolkit 3D viewport.
/// Supports DAE model loading and material preview on plane.
/// Uses PathResolver for direct file access - no temp folder needed.
/// </summary>
public partial class HelixViewportControl : UserControl, IDisposable
{
    private readonly List<Element3D> _loadedModels = new();
    private readonly EffectsManager _effectsManager;
    
    public HelixViewportControl()
    {
        InitializeComponent();
        
        _effectsManager = new DefaultEffectsManager();
        Viewport.EffectsManager = _effectsManager;
        
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Viewport.CameraController.CameraRotationMode = CameraRotationMode.Trackball;
    }
    
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
    
    /// <summary>
    /// Loads content based on the viewer request.
    /// </summary>
    public async Task LoadAsync(Viewer3DRequest request)
    {
        ClearModels();
        
        switch (request.Mode)
        {
            case Viewer3DMode.DaeModel:
                await LoadDaeModelAsync(request);
                break;
                
            case Viewer3DMode.MaterialOnPlane:
                LoadMaterialOnPlane(request);
                break;
                
            case Viewer3DMode.TextureOnly:
                LoadTextureOnPlane(request.DaeFilePath);
                break;
        }
    }
    
    /// <summary>
    /// Loads a DAE model with materials.
    /// Materials are already resolved - uses MaterialFile.File.FullName directly.
    /// </summary>
    private async Task LoadDaeModelAsync(Viewer3DRequest request)
    {
        var daeFilePath = request.DaeFilePath;
        
        // Resolve path if needed
        if (!Path.IsPathRooted(daeFilePath) && !string.IsNullOrEmpty(request.LevelPath))
        {
            daeFilePath = PathResolver.ResolvePath(request.LevelPath, daeFilePath, false);
        }
        
        if (!File.Exists(daeFilePath))
        {
            SetStatus($"DAE file not found: {Path.GetFileName(daeFilePath)}");
            return;
        }
        
        SetStatus("Loading model...");
        
        try
        {
            var loader = new Importer();
            var helixScene = await Task.Run(() => loader.Load(daeFilePath));
            
            if (helixScene == null || !helixScene.Root.Items.Any())
            {
                SetStatus("Failed to load model or model is empty");
                return;
            }
            
            // Build texture lookup from resolved materials
            var texturePaths = BuildTextureLookup(request.Materials);
            
            ProcessSceneNode(helixScene, texturePaths);
            
            Viewport.ZoomExtents(500);
            SetStatus($"Loaded: {Path.GetFileName(daeFilePath)} ({_loadedModels.Count} meshes)");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Builds a texture lookup dictionary from resolved MaterialJson objects.
    /// Key: material name or "MaterialName_MapType"
    /// Value: absolute file path (already resolved by MaterialFileScanner)
    /// </summary>
    private Dictionary<string, string> BuildTextureLookup(List<MaterialJson> materials)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var material in materials)
        {
            foreach (var file in material.MaterialFiles)
            {
                // MaterialFile.File is already resolved by MaterialFileScanner
                if (file.File?.Exists != true) continue;
                
                var fullPath = file.File.FullName;
                
                // Add with multiple keys for flexible matching
                lookup[$"{material.Name}_{file.MapType}"] = fullPath;
                lookup[$"{material.InternalName}_{file.MapType}"] = fullPath;
                
                // For color maps, also add just the material name
                if (IsColorMap(file.MapType))
                {
                    lookup[material.Name] = fullPath;
                    lookup[material.InternalName] = fullPath;
                }
            }
        }
        
        return lookup;
    }
    
    private bool IsColorMap(string mapType)
    {
        return mapType?.ToLowerInvariant() switch
        {
            "colormap" => true,
            "basecolormap" => true,
            "diffusemap" => true,
            "basecolortex" => true,
            "basecolordetailtex" => true,
            _ => false
        };
    }
    
    /// <summary>
    /// Loads material textures on a preview plane.
    /// For Roads, Decals, DecalRoads, Terrain materials.
    /// </summary>
    private void LoadMaterialOnPlane(Viewer3DRequest request)
    {
        SetStatus("Loading material preview...");
        
        try
        {
            // Find the color map texture
            string colorMapPath = null;
            float aspectRatio = 1.0f;
            
            foreach (var material in request.Materials)
            {
                foreach (var file in material.MaterialFiles)
                {
                    if (file.File?.Exists != true) continue;
                    
                    if (IsColorMap(file.MapType))
                    {
                        colorMapPath = file.File.FullName;
                        
                        // Get aspect ratio from image
                        try
                        {
                            using var img = System.Drawing.Image.FromFile(colorMapPath);
                            aspectRatio = (float)img.Width / img.Height;
                        }
                        catch { }
                        
                        break;
                    }
                }
                if (colorMapPath != null) break;
            }
            
            // Fallback to first available texture
            if (colorMapPath == null)
            {
                var firstFile = request.Materials
                    .SelectMany(m => m.MaterialFiles)
                    .FirstOrDefault(f => f.File?.Exists == true);
                    
                if (firstFile != null)
                    colorMapPath = firstFile.File.FullName;
            }
            
            if (colorMapPath == null)
            {
                SetStatus("No textures available for preview");
                return;
            }
            
            LoadTextureOnPlane(colorMapPath, aspectRatio);
            SetStatus($"Material preview: {request.DisplayName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads a single texture on a plane.
    /// </summary>
    private void LoadTextureOnPlane(string texturePath, float aspectRatio = 1.0f)
    {
        if (!File.Exists(texturePath))
        {
            SetStatus($"Texture not found: {Path.GetFileName(texturePath)}");
            return;
        }
        
        try
        {
            // Create plane geometry
            var meshBuilder = new MeshBuilder();
            meshBuilder.AddBox(Vector3.Zero, 4 * aspectRatio, 4, 0.1f);
            var geometry = meshBuilder.ToMeshGeometry3D();
            
            // Create material with texture
            // Helix Toolkit can load DDS directly!
            var material = new PhongMaterial
            {
                DiffuseColor = new Color4(0.9f, 0.9f, 0.9f, 1.0f),
                SpecularColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f),
                SpecularShininess = 10,
                DiffuseMap = TextureModel.Create(texturePath)
            };
            
            var meshElement = new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material
            };
            
            _loadedModels.Add(meshElement);
            Viewport.Items.Add(meshElement);
            
            Viewport.ZoomExtents(500);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading texture: {ex.Message}");
        }
    }
    
    private void ProcessSceneNode(HelixToolkitScene scene, Dictionary<string, string> texturePaths)
    {
        foreach (var node in scene.Root.Traverse())
        {
            if (node is MeshNode meshNode && meshNode.Geometry != null)
            {
                var geometry = meshNode.Geometry as MeshGeometry3D;
                if (geometry == null) continue;
                
                var material = CreateMaterialFromNode(meshNode, texturePaths);
                
                var meshElement = new MeshGeometryModel3D
                {
                    Geometry = geometry,
                    Material = material,
                    Transform = new System.Windows.Media.Media3D.MatrixTransform3D(
                        meshNode.ModelMatrix.ToMatrix3D())
                };
                
                _loadedModels.Add(meshElement);
                Viewport.Items.Add(meshElement);
            }
        }
    }
    
    private PhongMaterial CreateMaterialFromNode(MeshNode node, Dictionary<string, string> texturePaths)
    {
        var material = new PhongMaterial
        {
            DiffuseColor = new Color4(0.8f, 0.8f, 0.8f, 1.0f),
            SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1.0f),
            SpecularShininess = 20
        };
        
        var nodeName = node.Name ?? "";
        
        // Try to find matching texture
        foreach (var (key, texturePath) in texturePaths)
        {
            if (nodeName.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(nodeName, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(texturePath))
                {
                    material.DiffuseMap = TextureModel.Create(texturePath);
                    break;
                }
            }
        }
        
        // Fallback to first available color map
        if (material.DiffuseMap == null)
        {
            var firstTexture = texturePaths.Values.FirstOrDefault(File.Exists);
            if (firstTexture != null)
            {
                material.DiffuseMap = TextureModel.Create(firstTexture);
            }
        }
        
        return material;
    }
    
    private void ClearModels()
    {
        foreach (var model in _loadedModels)
        {
            Viewport.Items.Remove(model);
            if (model is IDisposable disposable)
                disposable.Dispose();
        }
        _loadedModels.Clear();
    }
    
    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }
    
    public void Dispose()
    {
        ClearModels();
        _effectsManager?.Dispose();
    }
}
```

#### Task 2.2: Create Windows Forms Host Dialog

**File**: `Viewer3D/HelixViewerForm.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Windows Forms dialog hosting the Helix Toolkit WPF viewport.
/// Provides 3D preview for DAE models and material textures.
/// </summary>
public class HelixViewerForm : Form
{
    private readonly ElementHost _elementHost;
    private readonly HelixViewportControl _viewportControl;
    private readonly Panel _texturePanel;
    private readonly FlowLayoutPanel _textureGallery;
    private Viewer3DRequest _currentRequest;
    
    public HelixViewerForm()
    {
        InitializeForm();
        
        _viewportControl = new HelixViewportControl();
        
        _elementHost = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = _viewportControl
        };
        
        _texturePanel = CreateTexturePanel();
        
        var mainPanel = new Panel { Dock = DockStyle.Fill };
        mainPanel.Controls.Add(_elementHost);
        
        var buttonPanel = CreateButtonPanel();
        
        Controls.Add(mainPanel);
        Controls.Add(_texturePanel);
        Controls.Add(buttonPanel);
    }
    
    private void InitializeForm()
    {
        Text = "3D Asset Viewer";
        Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 46);
        MinimumSize = new Size(800, 600);
    }
    
    private Panel CreateTexturePanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 200,
            BackColor = Color.FromArgb(30, 30, 46),
            AutoScroll = true
        };
        
        _textureGallery = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.FromArgb(30, 30, 46)
        };
        
        panel.Controls.Add(_textureGallery);
        return panel;
    }
    
    private Panel CreateButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = Color.FromArgb(45, 45, 61)
        };
        
        var closeButton = new Button
        {
            Text = "Close",
            Size = new Size(100, 35),
            Location = new Point(panel.Width - 120, 8),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 80, 100),
            ForeColor = Color.White
        };
        closeButton.Click += (s, e) => Close();
        
        var resetCameraButton = new Button
        {
            Text = "Reset Camera",
            Size = new Size(120, 35),
            Location = new Point(10, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(80, 80, 100),
            ForeColor = Color.White
        };
        resetCameraButton.Click += (s, e) => _viewportControl?.Viewport?.ZoomExtents(500);
        
        panel.Controls.Add(closeButton);
        panel.Controls.Add(resetCameraButton);
        
        return panel;
    }
    
    /// <summary>
    /// Loads and displays the viewer request.
    /// </summary>
    public async Task LoadAsync(Viewer3DRequest request)
    {
        _currentRequest = request;
        Text = $"3D Asset Viewer - {request.DisplayName}";
        
        await _viewportControl.LoadAsync(request);
        PopulateTextureGallery(request.Materials);
    }
    
    /// <summary>
    /// Populates the texture gallery with material textures.
    /// Uses DDSImage for DDS conversion (2D preview only).
    /// </summary>
    private void PopulateTextureGallery(List<MaterialJson> materials)
    {
        _textureGallery.Controls.Clear();
        
        foreach (var material in materials)
        {
            foreach (var file in material.MaterialFiles)
            {
                // Direct file access - no copying needed!
                if (file.File?.Exists != true) continue;
                
                try
                {
                    var card = CreateTextureCard(file, material.Name);
                    _textureGallery.Controls.Add(card);
                }
                catch
                {
                    // Skip files that can't be loaded
                }
            }
        }
    }
    
    private Panel CreateTextureCard(MaterialFile file, string materialName)
    {
        var card = new Panel
        {
            Size = new Size(150, 180),
            BackColor = Color.FromArgb(45, 45, 61),
            Margin = new Padding(5)
        };
        
        var pictureBox = new PictureBox
        {
            Size = new Size(140, 120),
            Location = new Point(5, 5),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(60, 60, 80)
        };
        
        try
        {
            var filePath = file.File!.FullName;
            var actualExtension = LinkFileResolver.GetActualExtension(filePath);
            
            using var stream = LinkFileResolver.GetFileStream(filePath);
            if (stream != null)
            {
                if (actualExtension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    pictureBox.Image = LoadDdsAsBitmap(stream);
                }
                else
                {
                    pictureBox.Image = System.Drawing.Image.FromStream(stream);
                }
            }
            else
            {
                pictureBox.BackColor = System.Drawing.Color.DarkGray;
            }
        }
        catch
        {
            pictureBox.BackColor = System.Drawing.Color.DarkGray;
        }

        var label = new Label
        {
            Text = $"{file.MapType}\n{file.File.Name}",
            Location = new Point(5, 130),
            Size = new Size(140, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8),
            TextAlign = ContentAlignment.TopCenter
        };
        
        card.Controls.Add(pictureBox);
        card.Controls.Add(label);
        
        return card;
    }
    
    private Bitmap? LoadDdsAsBitmap(Stream stream)
    {
        try
        {
            using var image = Pfimage.FromStream(stream);
            
            // Convert Pfim image to Bitmap
            var bitmap = new Bitmap(image.Width, image.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            
            // Lock bitmap bits for writing
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                bitmap.PixelFormat);
            
            // Copy pixel data from Pfim image to bitmap
            System.Runtime.InteropServices.Marshal.Copy(image.Data, 0, bitmapData.Scan0, image.Data.Length);
            
            // Unlock bitmap bits
            bitmap.UnlockBits(bitmapData);
            
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
    
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _viewportControl?.Dispose();
        base.OnFormClosing(e);
    }
}
```

#### Task 2.3: Create Bridge Service

**File**: `BlazorUI/Services/Viewer3DService.cs`

```csharp
using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Viewer3D;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
/// Service to launch the 3D viewer from Blazor pages.
/// No file copying needed - uses PathResolver for direct access.
/// </summary>
public class Viewer3DService
{
    /// <summary>
    /// Opens the 3D viewer for a CopyAsset.
    /// Convenience method for CopyAssets page compatibility.
    /// </summary>
    public Task OpenViewerAsync(CopyAsset asset)
    {
        var request = Viewer3DRequest.FromCopyAsset(asset);
        return OpenViewerAsync(request);
    }
    
    /// <summary>
    /// Opens the 3D viewer for a MaterialJson.
    /// Can be called from any page that has material data.
    /// </summary>
    public Task OpenViewerAsync(MaterialJson material, string levelPath = null)
    {
        var request = Viewer3DRequest.FromMaterial(material, levelPath);
        return OpenViewerAsync(request);
    }
    
    /// <summary>
    /// Opens the 3D viewer for a single texture file.
    /// </summary>
    public Task OpenViewerAsync(string texturePath, string displayName = null)
    {
        var request = Viewer3DRequest.FromTexture(texturePath, displayName);
        return OpenViewerAsync(request);
    }
    
    /// <summary>
    /// Opens the 3D viewer with a generic request.
    /// </summary>
    public async Task OpenViewerAsync(Viewer3DRequest request)
    {
        // Must run on STA thread for WPF/WinForms
        await Task.Run(() =>
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var form = new HelixViewerForm();
                    form.LoadAsync(request).Wait();
                    form.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to open 3D viewer: {ex.Message}",
                        "Viewer Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            });
            
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        });
    }
}
```

---

### Phase 3: Integration

**Duration**: ~2 hours

#### Task 3.1: Update CopyAssets.razor.cs

Update the `OpenAssetViewer` method to use the new service:

```csharp
// In CopyAssets.razor.cs

// Add field
[Inject] private Viewer3DService Viewer3DService { get; set; }

// Update method
private async Task OpenAssetViewer(GridFileListItem context)
{
    try
    {
        await Viewer3DService.OpenViewerAsync(context.CopyAsset);
    }
    catch (Exception ex)
    {
        Snackbar.Add($"Failed to open viewer: {ex.Message}", Severity.Error);
    }
}
```

#### Task 3.2: Register Service

In `Program.cs` or service registration:

```csharp
builder.Services.AddSingleton<Viewer3DService>();
```

#### Task 3.3: Remove Old AssetViewer.razor (Optional)

The old `AssetViewer.razor` with Blazor3D can be removed or kept as fallback. The new viewer is launched directly via `Viewer3DService`.

---

### Phase 4: Cleanup

**Duration**: ~1 hour

#### Task 4.1: Remove Blazor3D Package

After testing, remove from `.csproj`:

```xml
<!-- REMOVE -->
<PackageReference Include="Blazor3D" Version="..." />
```

#### Task 4.2: Remove wwwroot/temp Cleanup Code

The temp directory is no longer needed for the 3D viewer. The texture gallery in `HelixViewerForm` loads files directly.

---

## Usage Examples

### From CopyAssets Page

```csharp
// Existing pattern - just works
await Viewer3DService.OpenViewerAsync(copyAsset);
```

### From CopyTerrains Page

```csharp
// Preview terrain material
await Viewer3DService.OpenViewerAsync(terrainMaterial.Materials.First(), PathResolver.LevelPathCopyFrom);
```

### From Any Page with MaterialJson

```csharp
// Generic material preview
await Viewer3DService.OpenViewerAsync(materialJson);
```

### Direct Texture Preview

```csharp
// Quick texture preview
await Viewer3DService.OpenViewerAsync("/path/to/texture.dds", "My Texture");
```

---

## BeamNG Material Mapping

### Material Property Mapping

| BeamNG Property | Helix Toolkit Property |
|-----------------|------------------------|
| `colorMap` / `baseColorMap` / `baseColorDetailTex` | `DiffuseMap` |
| `normalMap` / `normalDetailTex` | `NormalMap` |
| `specularMap` | `SpecularColorMap` |
| `roughnessMap` / `roughnessDetailTex` | (PBR required) |
| `metallicMap` | (PBR required) |
| `aoMap` / `aoDetailTex` | `AmbientOcclusionMap` |
| `emissiveMap` | `EmissiveMap` |

### Texture Property Detection

Properties ending with these suffixes are treated as texture paths:
- `Map` (e.g., colorMap, normalMap)
- `Tex` (e.g., baseColorDetailTex, roughnessDetailTex)

This matches the existing `MaterialFileScanner` logic.

---

## Testing Plan

### Manual Testing Checklist

- [ ] **DAE Model** - Load DAE from CopyAssets (Dae type)
- [ ] **DAE with Materials** - Verify textures are applied
- [ ] **Road Material** - Preview road material on plane
- [ ] **Decal Material** - Preview decal material on plane
- [ ] **Terrain Material** - Preview terrain material on plane
- [ ] **DDS Textures** - Verify DDS files load directly (no conversion)
- [ ] **PNG Textures** - Verify PNG files load correctly
- [ ] **Missing Files** - Graceful handling of missing textures
- [ ] **Camera Controls** - Orbit, pan, zoom work correctly
- [ ] **Texture Gallery** - All textures displayed with names
- [ ] **Memory** - No leaks after opening/closing viewer multiple times

### Test Levels

1. **jungle_rock_island** - Complex DAE models with multiple materials
2. **west_coast_usa** - Road and terrain materials
3. **driver_training** - Simple assets for basic testing

---

## Migration Steps (Summary)

1. **Add packages**: HelixToolkit.Wpf.SharpDX, HelixToolkit.SharpDX.Assimp
2. **Create Viewer3D folder** with all new files
3. **Create Viewer3DService** for Blazor integration
4. **Update CopyAssets.razor.cs** to use new service
5. **Register service** in DI
6. **Test thoroughly**
7. **Remove Blazor3D** package after verification

---

## Dependencies

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| HelixToolkit.Wpf.SharpDX | 2.24.0 | WPF 3D viewport |
| HelixToolkit.SharpDX.Assimp | 2.24.0 | DAE file loading |

### Internal Dependencies

| Component | Usage |
|-----------|-------|
| `PathResolver` | Resolve BeamNG paths to file system |
| `MaterialJson` | Material definitions |
| `MaterialFile` | Resolved texture file references |
| `DDSImage` | DDS to Bitmap conversion (gallery only) |
| `PubSubChannel` | Error messaging |

---

## Estimated Timeline

| Phase | Duration | Tasks |
|-------|----------|-------|
| Phase 1 | 1 hour | Setup, models |
| Phase 2 | 4-6 hours | Core viewer implementation |
| Phase 3 | 2 hours | Integration |
| Phase 4 | 1 hour | Cleanup, testing |
| **Total** | **8-10 hours** | |

---

## Future Enhancements

1. **PBR Material Support**: Use `PhysicallyBasedMaterial` for roughness/metallic
2. **Wireframe Mode**: Toggle wireframe view
3. **Material Editor**: Live material property editing
4. **Screenshot Export**: Save current view as image
5. **Animation Support**: For animated DAE files
6. **Multi-model Loading**: Load multiple DAE files in one scene

---

# Phase 5: Link File Resolution & Asset ZIP Streaming

**Duration**: ~3-4 hours

### Overview

BeamNG uses `.link` files as references to assets stored in ZIP archives within the game installation directory. When a file path ends with `.link`, the actual content must be streamed from the appropriate ZIP file in the game's `/assets/` folder.

### Link File Format

A `.link` file is a JSON file containing:

```json
{
  "path": "/assets/materials/decal/asphalt/AsphaltRoad_damage_large_decal_01/t_asphalt_repair_patch_decal_ao.data.png",
  "type": "normal"
}
```

- **`path`**: The virtual path within the BeamNG asset structure
- **`type`**: The asset type (e.g., "normal", "color", etc.)

### Asset ZIP Structure

The game's `assets/` folder contains ZIP files organized by category:

```
{BeamNG Install Dir}/
??? content/
    ??? assets/
        ??? decal.zip           # Contains /assets/materials/decal/**
        ??? road.zip            # Contains /assets/materials/road/**
        ??? terrain.zip         # Contains /assets/materials/terrain/**
        ??? shapes.zip          # Contains /assets/shapes/**
        ??? ...
```

### Resolution Algorithm

1. **Detect `.link` file**: Check if file path ends with `.link`
2. **Read link JSON**: Parse the `path` property
3. **Find ZIP file**: Traverse the path segments to find which directory is actually a ZIP file
4. **Stream content**: Extract the remaining path from within the ZIP

Example:
- Link path: `/assets/materials/decal/asphalt/texture.png`
- ZIP location: `{BeamNG}/content/assets/decal.zip`
- Path in ZIP: `materials/decal/asphalt/texture.png`

---

### Task 5.1: Create LinkFileResolver Utility

**File**: `Utils/LinkFileResolver.cs`

```csharp
using System.IO.Compression;
using System.Text.Json;
using BeamNG_LevelCleanUp.Logic;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Resolves BeamNG .link files to actual content streams.
/// Link files reference assets stored in ZIP archives within the game installation.
/// </summary>
public static class LinkFileResolver
{
    /// <summary>
    /// JSON structure for .link files
    /// </summary>
    private class LinkFileContent
    {
        public string path { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks if a file path is a .link reference file.
    /// </summary>
    public static bool IsLinkFile(string filePath)
    {
        return !string.IsNullOrEmpty(filePath) && 
               filePath.EndsWith(".link", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a .link file and returns the actual content as a stream.
    /// Returns null if resolution fails.
    /// </summary>
    /// <param name="linkFilePath">Path to the .link file</param>
    /// <returns>MemoryStream containing the file content, or null if not found</returns>
    public static MemoryStream? ResolveToStream(string linkFilePath)
    {
        try
        {
            if (!File.Exists(linkFilePath))
                return null;

            // Read and parse the link file
            var linkJson = File.ReadAllText(linkFilePath);
            var linkContent = JsonSerializer.Deserialize<LinkFileContent>(linkJson);

            if (string.IsNullOrEmpty(linkContent?.path))
                return null;

            return ResolveAssetPath(linkContent.path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a BeamNG asset path (from link file) to a content stream.
    /// Traverses the path to find the ZIP file and extracts content from it.
    /// </summary>
    /// <param name="assetPath">Virtual asset path (e.g., "/assets/materials/decal/...")</param>
    /// <returns>MemoryStream containing the file content, or null if not found</returns>
    public static MemoryStream? ResolveAssetPath(string assetPath)
    {
        try
        {
            var installDir = GameDirectoryService.GetInstallDirectory();
            if (string.IsNullOrEmpty(installDir))
                return null;

            // Normalize the path
            var normalizedPath = assetPath
                .TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar);

            // The base path for assets
            var assetsBasePath = Path.Combine(installDir, "content");

            // Split path into segments
            var segments = normalizedPath.Split(Path.DirectorySeparatorChar);
            
            // Try to find a ZIP file by traversing segments
            var currentPath = assetsBasePath;
            
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var potentialZipPath = Path.Combine(currentPath, segment + ".zip");
                var potentialDirPath = Path.Combine(currentPath, segment);

                // Check if this segment is a ZIP file
                if (File.Exists(potentialZipPath))
                {
                    // Found the ZIP! The rest of the segments are the path inside the ZIP
                    var pathInZip = string.Join("/", segments.Skip(i + 1));
                    
                    // Also try with the segment included (some ZIPs include their name in internal paths)
                    var alternatePathInZip = string.Join("/", segments.Skip(i));

                    return ExtractFromZip(potentialZipPath, pathInZip, alternatePathInZip);
                }

                // Continue traversing directories
                if (Directory.Exists(potentialDirPath))
                {
                    currentPath = potentialDirPath;
                }
                else
                {
                    // Directory doesn't exist, can't continue
                    break;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a file from a ZIP archive and returns it as a MemoryStream.
    /// </summary>
    private static MemoryStream? ExtractFromZip(string zipPath, string primaryPath, string alternatePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            // Try primary path first
            var entry = archive.GetEntry(primaryPath);
            
            // Try alternate path if primary not found
            if (entry == null && !string.IsNullOrEmpty(alternatePath))
            {
                entry = archive.GetEntry(alternatePath);
            }

            // Try case-insensitive search
            if (entry == null)
            {
                entry = archive.Entries.FirstOrDefault(e => 
                    e.FullName.Equals(primaryPath, StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.Equals(alternatePath, StringComparison.OrdinalIgnoreCase));
            }

            if (entry == null)
                return null;

            // Extract to memory stream
            var memoryStream = new MemoryStream();
            using (var entryStream = entry.Open())
            {
                entryStream.CopyTo(memoryStream);
            }
            memoryStream.Position = 0;
            
            return memoryStream;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a .link file or regular file and returns content as a stream.
    /// For regular files, reads directly from disk.
    /// For .link files, resolves from ZIP archives.
    /// </summary>
    /// <param name="filePath">Path to file (may or may not be a .link file)</param>
    /// <returns>MemoryStream containing file content, or null if not found</returns>
    public static MemoryStream? GetFileStream(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        if (IsLinkFile(filePath))
        {
            return ResolveToStream(filePath);
        }

        // Regular file - read directly
        if (File.Exists(filePath))
        {
            var ms = new MemoryStream();
            using var fs = File.OpenRead(filePath);
            fs.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        return null;
    }

    /// <summary>
    /// Gets the actual file extension from a path, ignoring .link suffix.
    /// </summary>
    /// <param name="filePath">File path (may end with .link)</param>
    /// <returns>The actual extension (e.g., ".png", ".dds")</returns>
    public static string GetActualExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        // Remove .link suffix if present
        var path = filePath;
        if (IsLinkFile(path))
        {
            path = path[..^5]; // Remove ".link"
        }

        return Path.GetExtension(path);
    }
}
```

---

### Task 5.2: Update HelixViewportControl to Use LinkFileResolver

Update texture loading methods to handle `.link` files:

```csharp
// In HelixViewportControl.xaml.cs

/// <summary>
/// Loads a texture file and returns a TextureModel.
/// Handles DDS files and .link file references.
/// </summary>
private static TextureModel? LoadTextureAsStream(string filePath)
{
    try
    {
        // Get actual extension (handles .link files)
        var actualExtension = LinkFileResolver.GetActualExtension(filePath);
        
        // Get file stream (resolves .link files automatically)
        using var stream = LinkFileResolver.GetFileStream(filePath);
        if (stream == null)
            return null;

        if (actualExtension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            // Convert DDS to a format Helix can read
            using var image = Pfimage.FromStream(stream);
            
            // ... rest of DDS conversion logic ...
        }
        else
        {
            // For non-DDS files, create texture from stream
            return TextureModel.Create(stream);
        }
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// Gets the aspect ratio of an image file, including DDS and .link support.
/// </summary>
private static float GetImageAspectRatio(string filePath)
{
    try
    {
        var actualExtension = LinkFileResolver.GetActualExtension(filePath);
        
        using var stream = LinkFileResolver.GetFileStream(filePath);
        if (stream == null)
            return 1.0f;

        if (actualExtension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            using var image = Pfimage.FromStream(stream);
            if (image.Width > 0 && image.Height > 0)
                return (float)image.Width / image.Height;
        }
        else
        {
            using var img = System.Drawing.Image.FromStream(stream);
            if (img.Width > 0 && img.Height > 0)
                return (float)img.Width / img.Height;
        }
        
        return 1.0f;
    }
    catch
    {
        return 1.0f;
    }
}
```

---

### Task 5.3: Update HelixViewerForm Texture Gallery

Update the texture card loading to handle `.link` files:

```csharp
// In HelixViewerForm.cs

private Panel CreateTextureCard(MaterialFile file, string materialName)
{
    // ... existing card creation code ...

    try
    {
        var filePath = file.File!.FullName;
        var actualExtension = LinkFileResolver.GetActualExtension(filePath);
        
        using var stream = LinkFileResolver.GetFileStream(filePath);
        if (stream != null)
        {
            if (actualExtension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                pictureBox.Image = LoadDdsAsBitmap(stream);
            }
            else
            {
                pictureBox.Image = System.Drawing.Image.FromStream(stream);
            }
        }
        else
        {
            pictureBox.BackColor = System.Drawing.Color.DarkGray;
        }
    }
    catch
    {
        pictureBox.BackColor = System.Drawing.Color.DarkGray;
    }

    // ... rest of method ...
}
```

---

### Task 5.4: Update MaterialFileScanner (Optional Enhancement)

Consider updating `MaterialFileScanner` to recognize `.link` files and set the `File` property appropriately:

```csharp
// In Logic/MaterialFileScanner.cs

public List<MaterialFile> GetMaterialFiles(string materialName)
{
    var retVal = new List<MaterialFile>();
    
    foreach (var stage in _stages)
    {
        foreach (var prop in stage.GetType().GetProperties())
        {
            // ... existing property iteration ...
            
            var filePath = PathResolver.ResolvePath(_levelPath, val, false);
            var fileInfo = FileUtils.ResolveImageFileName(filePath);
            
            // Check if this is a .link file
            var isLinkFile = LinkFileResolver.IsLinkFile(fileInfo.FullName);
            
            retVal.Add(new MaterialFile
            {
                MaterialName = materialName,
                Missing = !fileInfo.Exists && !isLinkFile, // Link files may not exist as regular files
                File = fileInfo,
                MapType = prop.Name,
                OriginalJsonPath = val,
                IsLinkFile = isLinkFile // New property
            });
        }
    }
    
    return retVal;
}
```

---

### Testing Checklist for Phase 5

- [ ] **Link file detection**: `LinkFileResolver.IsLinkFile()` correctly identifies `.link` files
- [ ] **Link file parsing**: JSON content is correctly parsed
- [ ] **ZIP traversal**: Correctly finds ZIP files in asset path hierarchy
- [ ] **Content extraction**: Files are correctly extracted from ZIP archives
- [ ] **DDS from ZIP**: DDS textures from `.link` files render correctly
- [ ] **PNG from ZIP**: PNG textures from `.link` files render correctly
- [ ] **Fallback**: Graceful handling when ZIP/content not found
- [ ] **Texture gallery**: `.link` file textures display in gallery
- [ ] **3D viewport**: `.link` file textures apply to materials correctly

---

### Dependencies for Phase 5

| Component | Purpose |
|-----------|---------|
| `GameDirectoryService` | Get BeamNG installation directory |
| `System.IO.Compression` | ZIP file handling (already in .NET) |
| `System.Text.Json` | Parse `.link` file JSON |
| `Pfim` | DDS texture loading from stream |

---

**Document Version**: 3.0  
**Updated**: 2025  
**Target Framework**: .NET 9  
**Application**: BeamNG Tools for Mapbuilders
