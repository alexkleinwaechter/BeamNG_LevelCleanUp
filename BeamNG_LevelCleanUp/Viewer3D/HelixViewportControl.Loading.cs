using System.Windows.Media.Media3D;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using HelixToolkit.SharpDX.Core.Assimp;
using HelixToolkit.SharpDX.Core.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using SharpDX.Direct3D11;
using MeshGeometry3D = HelixToolkit.Wpf.SharpDX.MeshGeometry3D;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Partial class for model loading functionality.
/// </summary>
public partial class HelixViewportControl
{
    /// <summary>
    ///     Stores mesh visibility state before reload as a simple list of visibility values.
    ///     Index corresponds to mesh order in MeshItems.
    /// </summary>
    private List<bool>? _savedMeshVisibility;

    /// <summary>
    ///     Controls LOD filtering behavior for BeamNG DAE files.
    ///     When true (default): Uses simplified DAE with only highest LOD, filters collision meshes.
    ///     When false: Original behavior - loads all meshes, only simplifies on load failure.
    /// </summary>
    public static bool UseHighestLodOnly { get; set; } = true;

    /// <summary>
    ///     Saves the current mesh visibility state for restoration after reload.
    /// </summary>
    private void SaveMeshVisibilityState()
    {
        if (MeshItems.Count == 0)
        {
            _savedMeshVisibility = null;
            return;
        }

        _savedMeshVisibility = MeshItems
            .Select(m => m.IsVisible)
            .ToList();
    }

    /// <summary>
    ///     Restores mesh visibility state after reload.
    ///     Applies visibility by index position since the same model loads meshes in the same order.
    /// </summary>
    private void RestoreMeshVisibilityState()
    {
        if (_savedMeshVisibility == null || _savedMeshVisibility.Count == 0)
            return;

        // Apply visibility by index - same model loads meshes in same order
        var count = Math.Min(_savedMeshVisibility.Count, MeshItems.Count);
        for (var i = 0; i < count; i++) MeshItems[i].IsVisible = _savedMeshVisibility[i];

        _savedMeshVisibility = null;
    }

    /// <summary>
    ///     Loads content based on the viewer request.
    /// </summary>
    public async Task LoadAsync(Viewer3DRequest request)
    {
        // Store for potential reload
        _lastRequest = request;

        // Save mesh visibility state before clearing
        SaveMeshVisibilityState();

        ClearModels();

        switch (request.Mode)
        {
            case Viewer3DMode.DaeModel:
                await LoadDaeModelAsync(request);
                break;

            case Viewer3DMode.MaterialOnPlane:
                LoadMaterialOnPlane(request);
                break;

            case Viewer3DMode.RoadOnPlane:
                LoadMaterialOnPlane(request, 4.0f); // 4:1 rectangle for roads
                break;

            case Viewer3DMode.DecalOnPlane:
                LoadMaterialOnPlane(request, 1.0f); // Square for decals
                break;

            case Viewer3DMode.TextureOnly:
                LoadTextureOnPlane(request.DaeFilePath);
                break;
        }

        // Restore mesh visibility state after loading
        RestoreMeshVisibilityState();
    }

    /// <summary>
    ///     Loads a DAE model with materials.
    ///     Materials are already resolved - uses MaterialFile.File.FullName directly.
    ///     Behavior controlled by UseHighestLodOnly flag:
    ///     - true: Always uses simplified DAE to extract highest LOD and filter collision meshes.
    ///     - false: Original behavior - tries original DAE first, only simplifies on failure.
    /// </summary>
    private async Task LoadDaeModelAsync(Viewer3DRequest request)
    {
        // Note: TextureMapConfig settings are now controlled by the UI checkboxes
        // and applied via BtnReloadTextureSettings_Click before calling LoadAsync
        TextureMapConfig.EnableDebugLogging = true;

        var daeFilePath = request.DaeFilePath;

        // Resolve path if needed
        if (!string.IsNullOrEmpty(daeFilePath) && !Path.IsPathRooted(daeFilePath))
        {
            var levelPath = request.LevelPath ?? PathResolver.LevelPathCopyFrom ?? PathResolver.LevelPath;
            if (!string.IsNullOrEmpty(levelPath)) daeFilePath = PathResolver.ResolvePath(levelPath, daeFilePath, false);
        }

        if (string.IsNullOrEmpty(daeFilePath) || !File.Exists(daeFilePath))
        {
            SetStatus($"DAE file not found: {Path.GetFileName(daeFilePath ?? "unknown")}");
            return;
        }

        SetStatus("Loading model...");

        try
        {
            // Build texture lookup from resolved materials
            var textureLookup = TextureLookup.Build(request.Materials, request.MaterialsDae);

            HelixToolkitScene? helixScene = null;
            var errorCode = ErrorCode.None;
            string? errorDetails = null;
            var actualLoadedPath = daeFilePath;
            var usedSimplified = false;

            if (UseHighestLodOnly)
            {
                // NEW BEHAVIOR: Always create simplified DAE first to extract highest LOD and filter collision meshes
                // BeamNG DAE files often have multiple LOD levels and collision meshes that clutter the view
                SetStatus("Preparing model (extracting highest LOD)...");
                var simplifiedPath = await Task.Run(() =>
                    DaeSimplifier.CreateSimplifiedDae(daeFilePath));

                if (!string.IsNullOrEmpty(simplifiedPath) && File.Exists(simplifiedPath))
                {
                    await Task.Run(() =>
                    {
                        var loader = new Importer();

                        loader.AssimpExceptionOccurred += (sender, ex) =>
                        {
                            errorDetails = ex.Message;
                            if (ex.InnerException != null)
                                errorDetails += $" Inner: {ex.InnerException.Message}";
                        };

                        errorCode = loader.Load(simplifiedPath, out helixScene);
                    });

                    if (!errorCode.HasFlag(ErrorCode.Failed) && helixScene?.Root != null && helixScene.Root.Items.Any())
                    {
                        actualLoadedPath = simplifiedPath;
                        usedSimplified = true;
                    }
                }

                // If simplified version failed, try original DAE as fallback
                if (!usedSimplified)
                {
                    SetStatus("Simplified version failed, trying original DAE...");
                    helixScene = null;
                    errorCode = ErrorCode.None;
                    errorDetails = null;

                    await Task.Run(() =>
                    {
                        var loader = new Importer();

                        loader.AssimpExceptionOccurred += (sender, ex) =>
                        {
                            errorDetails = ex.Message;
                            if (ex.InnerException != null)
                                errorDetails += $" Inner: {ex.InnerException.Message}";
                        };

                        errorCode = loader.Load(daeFilePath, out helixScene);
                    });

                    actualLoadedPath = daeFilePath;
                }
            }
            else
            {
                // ORIGINAL BEHAVIOR: Try loading the original DAE first
                await Task.Run(() =>
                {
                    var loader = new Importer();

                    loader.AssimpExceptionOccurred += (sender, ex) =>
                    {
                        errorDetails = ex.Message;
                        if (ex.InnerException != null)
                            errorDetails += $" Inner: {ex.InnerException.Message}";
                    };

                    errorCode = loader.Load(daeFilePath, out helixScene);
                });

                // If first attempt failed, try with simplified DAE
                if (errorCode.HasFlag(ErrorCode.Failed) || helixScene?.Root == null || !helixScene.Root.Items.Any())
                {
                    SetStatus("Original DAE failed, creating simplified version...");

                    var simplifiedPath = await Task.Run(() =>
                        DaeSimplifier.CreateSimplifiedDae(daeFilePath));

                    if (!string.IsNullOrEmpty(simplifiedPath) && File.Exists(simplifiedPath))
                    {
                        // Reset and try again with simplified version
                        helixScene = null;
                        errorCode = ErrorCode.None;
                        errorDetails = null;

                        await Task.Run(() =>
                        {
                            var loader = new Importer();

                            loader.AssimpExceptionOccurred += (sender, ex) =>
                            {
                                errorDetails = ex.Message;
                                if (ex.InnerException != null)
                                    errorDetails += $" Inner: {ex.InnerException.Message}";
                            };

                            errorCode = loader.Load(simplifiedPath, out helixScene);
                        });

                        if (!errorCode.HasFlag(ErrorCode.Failed))
                        {
                            actualLoadedPath = simplifiedPath;
                            usedSimplified = true;
                        }
                    }
                }
            }

            // Check for errors after both attempts
            if (errorCode.HasFlag(ErrorCode.Failed))
            {
                var errorMsg = $"Failed to load DAE: {errorCode}";
                if (!string.IsNullOrEmpty(errorDetails))
                    errorMsg += $" - {errorDetails}";
                SetStatus(errorMsg);
                return;
            }

            if (errorCode.HasFlag(ErrorCode.FileTypeNotSupported))
            {
                SetStatus($"File type not supported: {Path.GetExtension(daeFilePath)}");
                return;
            }

            if (helixScene?.Root == null || !helixScene.Root.Items.Any())
            {
                SetStatus($"Model loaded but is empty (ErrorCode: {errorCode})");
                return;
            }

            // Create material factory with the texture lookup
            var materialFactory = new MaterialFactory(textureLookup);

            // Store the texture lookup for later use
            _currentTextureLookup = textureLookup;

            ProcessSceneNode(helixScene, materialFactory, textureLookup);

            // All meshes are visible by default (MeshVisibilityItem._isVisible = true)

            Viewport.ZoomExtents(500);

            var statusMsg = $"Loaded: {Path.GetFileName(daeFilePath)} ({_loadedModels.Count} meshes)";
            if (usedSimplified)
                statusMsg += " [simplified]";
            SetStatus(statusMsg);

            // Cleanup old temp files in background
            _ = Task.Run(() => DaeSimplifier.CleanupTempFiles(TimeSpan.FromHours(24)));
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads material textures on a preview plane.
    ///     For Roads, Decals, DecalRoads, Terrain materials.
    ///     Supports .link file resolution.
    /// </summary>
    /// <param name="request">The viewer request containing materials</param>
    /// <param name="preferredAspectRatio">
    ///     Optional preferred aspect ratio for the plane.
    ///     If null, uses the texture's actual aspect ratio. For roads use 4.0f (4:1), for decals use 1.0f (square).
    /// </param>
    private void LoadMaterialOnPlane(Viewer3DRequest request, float? preferredAspectRatio = null)
    {
        SetStatus("Loading material preview...");

        try
        {
            // Check if materials exist
            if (request.Materials == null || request.Materials.Count == 0)
            {
                SetStatus("No materials available for preview");
                return;
            }

            // Find the color map texture
            string? colorMapPath = null;
            var aspectRatio = preferredAspectRatio ?? 1.0f;

            foreach (var material in request.Materials)
            {
                // MaterialFiles could be null
                if (material.MaterialFiles == null)
                    continue;

                foreach (var file in material.MaterialFiles)
                {
                    if (file.File == null) continue;

                    var filePath = file.File.FullName;
                    var canResolve = file.File.Exists || LinkFileResolver.CanResolve(filePath);

                    if (!canResolve) continue;

                    if (TextureLookup.IsColorMap(file.MapType))
                    {
                        colorMapPath = filePath;

                        // Only get aspect ratio from image if no preferred ratio specified
                        if (preferredAspectRatio == null) aspectRatio = TextureLoader.GetImageAspectRatio(colorMapPath);
                        break;
                    }
                }

                if (colorMapPath != null) break;
            }

            // Fallback to first available texture
            if (colorMapPath == null)
            {
                var firstFile = request.Materials
                    .Where(m => m.MaterialFiles != null)
                    .SelectMany(m => m.MaterialFiles)
                    .FirstOrDefault(f => f.File != null &&
                                         (f.File.Exists || LinkFileResolver.CanResolve(f.File.FullName)));

                if (firstFile != null)
                {
                    colorMapPath = firstFile.File!.FullName;
                    if (preferredAspectRatio == null) aspectRatio = TextureLoader.GetImageAspectRatio(colorMapPath);
                }
            }

            if (colorMapPath == null)
            {
                SetStatus("No textures available for preview");
                return;
            }

            // Determine if materials are PBR and if this is a road texture
            var isPbr = request.Materials.Any(m => m.IsPbr);
            var isRoad = request.Mode == Viewer3DMode.RoadOnPlane;

            if (isPbr)
                LoadPbrMaterialOnPlane(request.Materials, aspectRatio, isRoad);
            else
                LoadTextureOnPlane(colorMapPath, aspectRatio, isRoad);

            SetStatus($"Material preview: {request.DisplayName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads PBR material textures on a preview plane.
    /// </summary>
    /// <param name="materials">The materials to display</param>
    /// <param name="aspectRatio">Aspect ratio of the plane</param>
    /// <param name="rotateUV">If true, rotates UV coordinates 90 degrees for road textures</param>
    private void LoadPbrMaterialOnPlane(List<MaterialJson> materials, float aspectRatio, bool rotateUV = false)
    {
        var material = materials.FirstOrDefault();
        if (material == null)
            return;

        try
        {
            // Create plane geometry with optional UV rotation
            var geometry = CreatePlaneGeometry(aspectRatio, rotateUV);

            // Build texture lookup and create PBR material
            var textureLookup = TextureLookup.Build(materials, null);
            var materialFactory = new MaterialFactory(textureLookup);
            var helixMaterial = materialFactory.CreateMaterial(material);

            var meshElement = new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = helixMaterial,
                IsTransparent = true,
                CullMode = CullMode.None
            };

            _loadedModels.Add(meshElement);
            Viewport.Items.Add(meshElement);

            Viewport.ZoomExtents(500);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading PBR material: {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads a single texture on a plane.
    ///     Supports .link file resolution.
    /// </summary>
    /// <param name="texturePath">Path to the texture file</param>
    /// <param name="aspectRatio">Aspect ratio of the plane</param>
    /// <param name="rotateUV">If true, rotates UV coordinates 90 degrees for road textures</param>
    private void LoadTextureOnPlane(string? texturePath, float aspectRatio = 1.0f, bool rotateUV = false)
    {
        if (string.IsNullOrEmpty(texturePath))
        {
            SetStatus("Texture path is empty");
            return;
        }

        // Check if file exists directly or can be resolved via .link
        if (!TextureLoader.CanLoadTexture(texturePath))
        {
            SetStatus($"Texture not found: {Path.GetFileName(texturePath)}");
            return;
        }

        try
        {
            // Create plane geometry with optional UV rotation
            var geometry = CreatePlaneGeometry(aspectRatio, rotateUV);

            // Create material with texture
            var textureModel = TextureLoader.LoadTexture(texturePath);
            var material = new PhongMaterial
            {
                DiffuseColor = new Color4(0.9f, 0.9f, 0.9f, 1.0f),
                SpecularColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f),
                SpecularShininess = 10,
                DiffuseMap = textureModel
            };

            var meshElement = new MeshGeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                IsTransparent = true,
                CullMode = CullMode.None
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

    /// <summary>
    ///     Creates a plane geometry for material preview.
    /// </summary>
    /// <param name="aspectRatio">Aspect ratio (width/height) of the plane</param>
    /// <param name="rotateUV">
    ///     If true, rotates UV coordinates 90 degrees for road textures.
    ///     This makes the texture's vertical axis (V) run along the plane's length.
    /// </param>
    /// <returns>A MeshGeometry3D representing the plane</returns>
    private static MeshGeometry3D CreatePlaneGeometry(float aspectRatio, bool rotateUV)
    {
        // Plane dimensions: long axis along X, short axis along Y
        var halfWidth = 2.0f * aspectRatio; // X dimension (length for roads)
        var halfHeight = 2.0f; // Y dimension (width for roads)
        var thickness = 0.05f; // Z dimension (thin)

        // Create a simple quad (two triangles) lying flat on the XY plane
        var positions = new Vector3Collection
        {
            new Vector3(-halfWidth, -halfHeight, thickness), // 0: bottom-left
            new Vector3(halfWidth, -halfHeight, thickness), // 1: bottom-right
            new Vector3(halfWidth, halfHeight, thickness), // 2: top-right
            new Vector3(-halfWidth, halfHeight, thickness) // 3: top-left
        };

        var indices = new IntCollection
        {
            0, 1, 2, // First triangle
            0, 2, 3 // Second triangle
        };

        var normals = new Vector3Collection
        {
            new Vector3(0, 0, 1),
            new Vector3(0, 0, 1),
            new Vector3(0, 0, 1),
            new Vector3(0, 0, 1)
        };

        // UV coordinates
        // Standard: U runs along X (width), V runs along Y (height)
        // For roads: We need to rotate so V (texture vertical) runs along X (plane length)
        Vector2Collection texCoords;

        if (rotateUV)
            // Rotated 90 degrees counter-clockwise:
            // - Texture V axis (vertical in image) maps to plane X axis (length of road)
            // - Texture U axis (horizontal in image) maps to plane Y axis (width of road)
            // This makes road markings/lines run along the length of the plane
            texCoords = new Vector2Collection
            {
                new Vector2(1, 0), // 0: bottom-left of plane -> top-right of texture
                new Vector2(1, 1), // 1: bottom-right of plane -> bottom-right of texture  
                new Vector2(0, 1), // 2: top-right of plane -> bottom-left of texture
                new Vector2(0, 0) // 3: top-left of plane -> top-left of texture
            };
        else
            // Standard UV mapping
            texCoords = new Vector2Collection
            {
                new Vector2(0, 1), // 0: bottom-left
                new Vector2(1, 1), // 1: bottom-right
                new Vector2(1, 0), // 2: top-right
                new Vector2(0, 0) // 3: top-left
            };

        return new MeshGeometry3D
        {
            Positions = positions,
            Indices = indices,
            Normals = normals,
            TextureCoordinates = texCoords
        };
    }

    /// <summary>
    ///     Recursively traverses scene nodes and processes mesh nodes.
    ///     When UseHighestLodOnly is true: Filters out collision meshes, LOD control nodes, and lower LOD variants.
    ///     When UseHighestLodOnly is false: Processes all mesh nodes without filtering.
    /// </summary>
    private void ProcessSceneNode(HelixToolkitScene scene, MaterialFactory materialFactory, TextureLookup textureLookup)
    {
        // Collect all mesh nodes using a stack-based traversal
        var allMeshNodes = new List<MeshNode>();
        var stack = new Stack<SceneNode>();
        stack.Push(scene.Root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current is MeshNode meshNode && meshNode.Geometry != null) allMeshNodes.Add(meshNode);

            foreach (var child in current.Items)
                stack.Push(child);
        }

        List<MeshNode> nodesToProcess;

        if (UseHighestLodOnly)
        {
            // NEW BEHAVIOR: Filter out excluded nodes and keep only highest LOD
            var filteredNodes = allMeshNodes
                .Where(node => !DaeSimplifier.ShouldExcludeNode(node.Name))
                .ToList();

            // Group by base name to find LOD variants and keep only highest LOD
            var lodGroups =
                new Dictionary<string, List<(MeshNode node, int lodLevel)>>(StringComparer.OrdinalIgnoreCase);
            var nonLodNodes = new List<MeshNode>();

            foreach (var node in filteredNodes)
            {
                var nodeName = node.Name ?? "";
                var lodLevel = DaeSimplifier.ExtractLodLevel(nodeName);

                if (lodLevel.HasValue)
                {
                    var baseName = DaeSimplifier.GetBaseName(nodeName);
                    if (!lodGroups.ContainsKey(baseName))
                        lodGroups[baseName] = [];
                    lodGroups[baseName].Add((node, lodLevel.Value));
                }
                else
                {
                    // No LOD suffix - include as-is
                    nonLodNodes.Add(node);
                }
            }

            // Select highest LOD from each group (highest number = most detail)
            var highestLodNodes = lodGroups.Values
                .Select(group => group.OrderByDescending(g => g.lodLevel).First().node)
                .ToList();

            // Combine non-LOD nodes with highest LOD selections
            nodesToProcess = nonLodNodes.Concat(highestLodNodes).ToList();
        }
        else
        {
            // ORIGINAL BEHAVIOR: Process all mesh nodes without filtering
            nodesToProcess = allMeshNodes;
        }

        foreach (var meshNode in nodesToProcess)
        {
            var coreGeometry = meshNode.Geometry as HelixToolkit.SharpDX.Core.MeshGeometry3D;
            if (coreGeometry == null) continue;

            // Convert Core geometry to WPF geometry
            var wpfGeometry = ConvertToWpfGeometry(coreGeometry);
            if (wpfGeometry == null) continue;

            // Extract material name from the imported scene's MaterialCore
            // This is the actual material name from the DAE file (e.g., "leaves_strong")
            // rather than the geometry node name (e.g., "Plane.16182")
            var materialName = meshNode.Material?.Name
                               ?? meshNode.Name
                               ?? "";

            // Use MaterialFactory to create appropriate material (Phong or PBR)
            var material = materialFactory.CreateMaterialForNode(materialName);

            var meshElement = new MeshGeometryModel3D
            {
                Geometry = wpfGeometry,
                Material = material,
                Transform = new MatrixTransform3D(meshNode.ModelMatrix.ToMatrix3D()),
                IsTransparent = true,
                CullMode = CullMode.None
            };

            _loadedModels.Add(meshElement);
            Viewport.Items.Add(meshElement);

            // Build selection info for this mesh
            var selectionInfo = BuildSelectionInfo(meshNode.Name ?? "", materialName, textureLookup);
            _meshMaterialMap[meshElement] = selectionInfo;

            // Add to visibility control panel
            var visibilityItem = new MeshVisibilityItem
            {
                MeshElement = meshElement,
                SelectionInfo = selectionInfo,
                OriginalMaterial = material
            };
            MeshItems.Add(visibilityItem);
        }
    }

    /// <summary>
    ///     Converts a Core MeshGeometry3D to a WPF-compatible MeshGeometry3D.
    /// </summary>
    private static MeshGeometry3D? ConvertToWpfGeometry(
        HelixToolkit.SharpDX.Core.MeshGeometry3D coreGeometry)
    {
        try
        {
            var wpfGeometry = new MeshGeometry3D();

            // Convert positions
            if (coreGeometry.Positions != null)
            {
                var wpfPositions = new Vector3Collection();
                foreach (var pos in coreGeometry.Positions) wpfPositions.Add(pos);
                wpfGeometry.Positions = wpfPositions;
            }

            // Convert indices
            if (coreGeometry.Indices != null)
            {
                var wpfIndices = new IntCollection();
                foreach (var idx in coreGeometry.Indices) wpfIndices.Add(idx);
                wpfGeometry.Indices = wpfIndices;
            }

            // Convert normals
            if (coreGeometry.Normals != null)
            {
                var wpfNormals = new Vector3Collection();
                foreach (var normal in coreGeometry.Normals) wpfNormals.Add(normal);
                wpfGeometry.Normals = wpfNormals;
            }

            // Convert texture coordinates
            if (coreGeometry.TextureCoordinates != null)
            {
                var wpfTexCoords = new Vector2Collection();
                foreach (var texCoord in coreGeometry.TextureCoordinates) wpfTexCoords.Add(texCoord);
                wpfGeometry.TextureCoordinates = wpfTexCoords;
            }

            return wpfGeometry;
        }
        catch
        {
            return null;
        }
    }
}