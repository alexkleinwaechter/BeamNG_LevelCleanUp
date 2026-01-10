using System.Diagnostics;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Factory for creating Helix Toolkit materials from BeamNG material definitions.
///     Supports both legacy Phong (v1.0) and PBR (v1.5+) materials.
/// </summary>
public class MaterialFactory
{
    /// <summary>
    ///     Map type names that STRONGLY indicate PBR workflow.
    ///     Note: NormalMap/NormalDetailMap are NOT included as they exist in both Phong and PBR materials.
    ///     We only use maps that are exclusive to PBR workflow.
    /// </summary>
    private static readonly HashSet<string> PbrMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Base color maps (PBR terminology - but also check for version)
        "basecolormap", "basecolortex", "basecolordetailtex", "basecolordetailmap",
        // These are exclusive to PBR workflow
        "roughnessmap", "roughnesstex", "roughnessdetailtex",
        "metallicmap", "metallictex",
        "ambientocclusionmap", "aotex", "aomap"
        // Note: NormalMap/NormalDetailMap removed - they exist in both Phong and PBR
    };

    private readonly TextureLookup _textureLookup;

    public MaterialFactory(TextureLookup textureLookup)
    {
        _textureLookup = textureLookup;
    }

    /// <summary>
    ///     Creates a Helix Toolkit material from a BeamNG MaterialJson.
    ///     Uses PBR material for version >= 1.5 or when PBR textures are detected.
    /// </summary>
    public Material CreateMaterial(MaterialJson? materialJson, string? fallbackNodeName = null)
    {
        if (materialJson == null)
            return CreateDefaultPhongMaterial();

        // Determine if this is a PBR material
        // Check explicit version first, then fall back to texture type detection
        if (IsPbrMaterial(materialJson)) return CreatePbrMaterial(materialJson);

        return CreatePhongMaterial(materialJson);
    }

    /// <summary>
    ///     Determines if a material should use PBR rendering.
    ///     Primarily relies on explicit version >= 1.5, with conservative fallback detection.
    /// </summary>
    private bool IsPbrMaterial(MaterialJson materialJson)
    {
        // Explicit version check (most reliable) - version >= 1.5 is PBR
        if (materialJson.IsPbr)
            return true;

        // If version is explicitly set and < 1.5, it's definitely Phong
        if (materialJson.Version.HasValue && materialJson.Version.Value < 1.5)
            return false;

        // Fallback: check Stages for PBR-EXCLUSIVE properties (metallic, roughness factors)
        // Note: We're conservative here - only trigger PBR if we have explicit PBR factors
        if (materialJson.Stages?.Count > 0)
        {
            var stage = materialJson.Stages[0];
            // MetallicFactor and RoughnessFactor are exclusive to PBR
            if (stage.MetallicFactor.HasValue || stage.RoughnessFactor.HasValue)
                return true;
            // ClearCoat is also PBR-only
            if (stage.ClearCoatFactor.HasValue)
                return true;
        }

        // Fallback: detect PBR from texture map types that are EXCLUSIVE to PBR
        // (roughness, metallic, AO maps - but NOT normal maps which exist in both)
        if (materialJson.MaterialFiles != null)
        {
            foreach (var file in materialJson.MaterialFiles)
            {
                if (string.IsNullOrEmpty(file.MapType)) 
                    continue;
                    
                var mapType = file.MapType.ToLowerInvariant();
                // Only check for maps that are exclusive to PBR workflow
                if (mapType.Contains("roughness") || 
                    mapType.Contains("metallic") || 
                    mapType.Contains("ambientocclusion") ||
                    mapType == "aomap" || mapType == "aotex")
                    return true;
            }
        }

        // Default to Phong for legacy materials without explicit PBR indicators
        return false;
    }

    /// <summary>
    ///     Creates a PBR (Physically Based Rendering) material for BeamNG v1.5+ materials.
    ///     Note: HelixToolkit PBR expects RoughnessMetallicMap in specific channel format.
    ///     For BeamNG's separate roughness/metallic maps, we use scalar factors instead
    ///     to avoid incorrect channel interpretation causing gray/desaturated output.
    ///     Texture loading is controlled by TextureMapConfig settings.
    /// </summary>
    private Material CreatePbrMaterial(MaterialJson materialJson)
    {
        var material = new PBRMaterial
        {
            // Bright white albedo to show texture colors accurately
            AlbedoColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f),
            // Default to non-metallic, medium roughness for vegetation/natural materials
            MetallicFactor = 0.0,
            RoughnessFactor = 0.5,
            // Disable AO factor by default - only enable when AO map is actually applied
            AmbientOcclusionFactor = 1.0,
            // Higher reflectance for better light response
            ReflectanceFactor = 0.5,
            ClearCoatStrength = 0.0,
            ClearCoatRoughness = 0.0,
            // Enable alpha blending for vegetation with opacity maps
            EnableAutoTangent = true,
            // Render both sides of faces for vegetation
            RenderShadowMap = false
        };

        // Get texture paths from lookup
        var textures = GetTexturesForMaterial(materialJson);

        LogDebug($"Creating PBR material for '{materialJson.Name ?? materialJson.InternalName}'");
        LogDebug($"Available textures: {string.Join(", ", textures.Keys)}");

        // Apply base color/albedo map (with opacity merged if available)
        if (TextureMapConfig.EnableBaseColorMap)
        {
            var baseColorPath = textures.GetValueOrDefault("BaseColorMap")
                                ?? textures.GetValueOrDefault("ColorMap")
                                ?? textures.GetValueOrDefault("DiffuseMap");

            // Check for opacity map
            var opacityMapPath = TextureMapConfig.EnableOpacityMap
                ? textures.GetValueOrDefault("OpacityMap")
                : null;

            if (!string.IsNullOrEmpty(baseColorPath))
            {
                LogDebug($"  Loading BaseColorMap: {Path.GetFileName(baseColorPath)}");
                TextureModel? texture;

                // If we have an opacity map, merge it into the albedo's alpha channel
                if (!string.IsNullOrEmpty(opacityMapPath) && TextureLoader.CanLoadTexture(opacityMapPath))
                {
                    LogDebug($"  Merging OpacityMap: {Path.GetFileName(opacityMapPath)}");
                    texture = TextureLoader.LoadTextureWithOpacity(baseColorPath, opacityMapPath);
                }
                else
                {
                    texture = TextureLoader.LoadTexture(baseColorPath);
                }

                if (texture != null)
                {
                    material.AlbedoMap = texture;
                    LogDebug($"  BaseColorMap loaded successfully");
                }
                else
                {
                    LogDebug($"  WARNING: BaseColorMap failed to load");
                }
            }
        }
        else
        {
            LogDebug($"  BaseColorMap DISABLED by config");
        }

        // Apply normal map
        if (TextureMapConfig.EnableNormalMap)
        {
            var normalMapPath = textures.GetValueOrDefault("NormalMap")
                                ?? textures.GetValueOrDefault("NormalDetailMap");
            if (!string.IsNullOrEmpty(normalMapPath))
            {
                LogDebug($"  Loading NormalMap: {Path.GetFileName(normalMapPath)}");
                var texture = TextureLoader.LoadTexture(normalMapPath);
                if (texture != null)
                {
                    material.NormalMap = texture;
                    LogDebug($"  NormalMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  NormalMap DISABLED by config");
        }

        // NOTE: BeamNG uses separate roughness and metallic maps, but HelixToolkit's PBR shader
        // expects a combined RoughnessMetallicMap with specific channel layout (typically glTF format:
        // R=unused/AO, G=Roughness, B=Metallic). 
        //
        // Applying BeamNG's single-channel roughness map directly causes the shader to misinterpret
        // the data, resulting in gray/desaturated output.
        //
        // These can be enabled via TextureMapConfig for testing, but may cause visual issues.

        if (TextureMapConfig.EnableRoughnessMap)
        {
            var roughnessMapPath = textures.GetValueOrDefault("RoughnessMap");
            if (!string.IsNullOrEmpty(roughnessMapPath))
            {
                LogDebug($"  Loading RoughnessMap: {Path.GetFileName(roughnessMapPath)} (WARNING: may cause visual issues)");
                var texture = TextureLoader.LoadTexture(roughnessMapPath);
                if (texture != null)
                {
                    material.RoughnessMetallicMap = texture;
                    LogDebug($"  RoughnessMap loaded (applied as RoughnessMetallicMap)");
                }
            }
        }
        else
        {
            LogDebug($"  RoughnessMap DISABLED by config (using factor instead)");
        }

        if (TextureMapConfig.EnableMetallicMap)
        {
            var metallicMapPath = textures.GetValueOrDefault("MetallicMap");
            if (!string.IsNullOrEmpty(metallicMapPath))
            {
                LogDebug($"  MetallicMap found but not applied (Helix requires combined RoughnessMetallicMap)");
            }
        }

        // Apply ambient occlusion map - with moderate factor for subtle darkening in crevices
        if (TextureMapConfig.EnableAmbientOcclusionMap)
        {
            var aoMapPath = textures.GetValueOrDefault("AmbientOcclusionMap");
            if (!string.IsNullOrEmpty(aoMapPath))
            {
                LogDebug($"  Loading AmbientOcclusionMap: {Path.GetFileName(aoMapPath)}");
                var texture = TextureLoader.LoadTexture(aoMapPath);
                if (texture != null)
                {
                    material.AmbientOcculsionMap = texture;
                    // Moderate AO factor - 0.5 gives subtle darkening without over-darkening
                    material.AmbientOcclusionFactor = 0.5;
                    LogDebug($"  AmbientOcclusionMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  AmbientOcclusionMap DISABLED by config");
        }

        // Apply emissive map
        if (TextureMapConfig.EnableEmissiveMap)
        {
            var emissiveMapPath = textures.GetValueOrDefault("EmissiveMap");
            if (!string.IsNullOrEmpty(emissiveMapPath))
            {
                LogDebug($"  Loading EmissiveMap: {Path.GetFileName(emissiveMapPath)}");
                var texture = TextureLoader.LoadTexture(emissiveMapPath);
                if (texture != null)
                {
                    material.EmissiveMap = texture;
                    LogDebug($"  EmissiveMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  EmissiveMap DISABLED by config");
        }

        // Apply factor values from MaterialStage if available
        if (materialJson.Stages?.Count > 0)
        {
            var stage = materialJson.Stages[0];

            if (stage.MetallicFactor.HasValue)
                material.MetallicFactor = stage.MetallicFactor.Value;

            if (stage.RoughnessFactor.HasValue)
                material.RoughnessFactor = stage.RoughnessFactor.Value;

            if (stage.ClearCoatFactor.HasValue)
                material.ClearCoatStrength = stage.ClearCoatFactor.Value;

            if (stage.ClearCoatRoughnessFactor.HasValue)
                material.ClearCoatRoughness = stage.ClearCoatRoughnessFactor.Value;

            // Apply diffuse color if specified
            if (stage.DiffuseColor?.Count >= 3)
                material.AlbedoColor = new Color4(
                    (float)stage.DiffuseColor[0],
                    (float)stage.DiffuseColor[1],
                    (float)stage.DiffuseColor[2],
                    stage.DiffuseColor.Count >= 4 ? (float)stage.DiffuseColor[3] : 1.0f);

            // Apply emissive factor
            if (stage.EmissiveFactor?.Count >= 3)
                material.EmissiveColor = new Color4(
                    (float)stage.EmissiveFactor[0],
                    (float)stage.EmissiveFactor[1],
                    (float)stage.EmissiveFactor[2],
                    stage.EmissiveFactor.Count >= 4 ? (float)stage.EmissiveFactor[3] : 1.0f);
        }

        return material;
    }

    /// <summary>
    ///     Creates a Phong material for legacy BeamNG materials (version < 1.5).
    ///     Texture loading is controlled by TextureMapConfig settings.
    /// </summary>
    private Material CreatePhongMaterial(MaterialJson materialJson)
    {
        // Use configurable intensity values from TextureMapConfig
        var diffuseIntensity = TextureMapConfig.MaterialDiffuseIntensity;
        var ambientIntensity = TextureMapConfig.MaterialAmbientIntensity;

        var material = new PhongMaterial
        {
            // Diffuse color controlled by config (reduce for less washed-out appearance)
            DiffuseColor = new Color4(diffuseIntensity, diffuseIntensity, diffuseIntensity, 1.0f),
            // Minimal specular to avoid shiny spots
            SpecularColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f),
            SpecularShininess = 10,
            // Ambient color controlled by config (reduce for less washed-out appearance)
            AmbientColor = new Color4(ambientIntensity, ambientIntensity, ambientIntensity, 1.0f)
        };

        // Get texture paths from lookup
        var textures = GetTexturesForMaterial(materialJson);

        LogDebug($"Creating Phong material for '{materialJson.Name ?? materialJson.InternalName}'");
        LogDebug($"Available textures: {string.Join(", ", textures.Keys)}");

        // Apply diffuse/color map (with opacity merged if available)
        if (TextureMapConfig.EnableBaseColorMap)
        {
            var diffuseMapPath = textures.GetValueOrDefault("ColorMap")
                                 ?? textures.GetValueOrDefault("DiffuseMap")
                                 ?? textures.GetValueOrDefault("BaseColorMap");

            // Check for opacity map
            var opacityMapPath = TextureMapConfig.EnableOpacityMap
                ? textures.GetValueOrDefault("OpacityMap")
                : null;

            if (!string.IsNullOrEmpty(diffuseMapPath))
            {
                LogDebug($"  Loading DiffuseMap: {Path.GetFileName(diffuseMapPath)}");
                TextureModel? texture;

                // If we have an opacity map, merge it into the diffuse's alpha channel
                if (!string.IsNullOrEmpty(opacityMapPath) && TextureLoader.CanLoadTexture(opacityMapPath))
                {
                    LogDebug($"  Merging OpacityMap: {Path.GetFileName(opacityMapPath)}");
                    texture = TextureLoader.LoadTextureWithOpacity(diffuseMapPath, opacityMapPath);
                }
                else
                {
                    texture = TextureLoader.LoadTexture(diffuseMapPath);
                }

                if (texture != null)
                {
                    material.DiffuseMap = texture;
                    LogDebug($"  DiffuseMap loaded successfully");
                }
                else
                {
                    LogDebug($"  WARNING: DiffuseMap failed to load");
                }
            }
        }
        else
        {
            LogDebug($"  DiffuseMap (BaseColorMap) DISABLED by config");
        }

        // Apply normal map
        if (TextureMapConfig.EnableNormalMap)
        {
            var normalMapPath = textures.GetValueOrDefault("NormalMap")
                                ?? textures.GetValueOrDefault("NormalDetailMap");
            if (!string.IsNullOrEmpty(normalMapPath))
            {
                LogDebug($"  Loading NormalMap: {Path.GetFileName(normalMapPath)}");
                var texture = TextureLoader.LoadTexture(normalMapPath);
                if (texture != null)
                {
                    material.NormalMap = texture;
                    LogDebug($"  NormalMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  NormalMap DISABLED by config");
        }

        // Apply specular map
        if (TextureMapConfig.EnableSpecularMap)
        {
            var specularMapPath = textures.GetValueOrDefault("SpecularMap");
            if (!string.IsNullOrEmpty(specularMapPath))
            {
                LogDebug($"  Loading SpecularMap: {Path.GetFileName(specularMapPath)}");
                var texture = TextureLoader.LoadTexture(specularMapPath);
                if (texture != null)
                {
                    material.SpecularColorMap = texture;
                    LogDebug($"  SpecularMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  SpecularMap DISABLED by config");
        }

        // Apply emissive map
        if (TextureMapConfig.EnableEmissiveMap)
        {
            var emissiveMapPath = textures.GetValueOrDefault("EmissiveMap");
            if (!string.IsNullOrEmpty(emissiveMapPath))
            {
                LogDebug($"  Loading EmissiveMap: {Path.GetFileName(emissiveMapPath)}");
                var texture = TextureLoader.LoadTexture(emissiveMapPath);
                if (texture != null)
                {
                    material.EmissiveMap = texture;
                    LogDebug($"  EmissiveMap loaded successfully");
                }
            }
        }
        else
        {
            LogDebug($"  EmissiveMap DISABLED by config");
        }

        // Apply factor values from MaterialStage if available
        if (materialJson.Stages?.Count > 0)
        {
            var stage = materialJson.Stages[0];

            // Apply diffuse color
            if (stage.DiffuseColor?.Count >= 3)
                material.DiffuseColor = new Color4(
                    (float)stage.DiffuseColor[0],
                    (float)stage.DiffuseColor[1],
                    (float)stage.DiffuseColor[2],
                    stage.DiffuseColor.Count >= 4 ? (float)stage.DiffuseColor[3] : 1.0f);

            // Apply specular color and power (legacy properties)
            if (stage.Specular?.Count >= 3)
                material.SpecularColor = new Color4(
                    (float)stage.Specular[0],
                    (float)stage.Specular[1],
                    (float)stage.Specular[2],
                    stage.Specular.Count >= 4 ? (float)stage.Specular[3] : 1.0f);

            if (stage.SpecularPower.HasValue) material.SpecularShininess = (float)stage.SpecularPower.Value;

            // Handle glow
            if (stage.Glow == true && stage.GlowFactor?.Count >= 3)
                material.EmissiveColor = new Color4(
                    (float)stage.GlowFactor[0],
                    (float)stage.GlowFactor[1],
                    (float)stage.GlowFactor[2],
                    stage.GlowFactor.Count >= 4 ? (float)stage.GlowFactor[3] : 1.0f);
        }

        return material;
    }

    /// <summary>
    ///     Creates a default Phong material when no material definition is available.
    /// </summary>
    public PhongMaterial CreateDefaultPhongMaterial()
    {
        // Use configurable intensity values from TextureMapConfig
        var diffuseIntensity = TextureMapConfig.MaterialDiffuseIntensity;
        var ambientIntensity = TextureMapConfig.MaterialAmbientIntensity;

        return new PhongMaterial
        {
            // Diffuse color controlled by config
            DiffuseColor = new Color4(diffuseIntensity, diffuseIntensity, diffuseIntensity, 1.0f),
            // Minimal specular to avoid shiny spots
            SpecularColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f),
            SpecularShininess = 10,
            // Ambient color controlled by config
            AmbientColor = new Color4(ambientIntensity, ambientIntensity, ambientIntensity, 1.0f)
        };
    }

    /// <summary>
    ///     Creates a material for a mesh node by finding the matching BeamNG material.
    /// </summary>
    public Material CreateMaterialForNode(string nodeName)
    {
        // Try to find matching material in lookup
        var materialJson = FindMaterialForNode(nodeName);

        if (materialJson != null) return CreateMaterial(materialJson, nodeName);

        // No matching material found, create a default with any available texture
        var defaultMaterial = CreateDefaultPhongMaterial();

        // Try to apply any available texture as a fallback
        var fallbackTexturePath = _textureLookup.GetFirstAvailableColorMap();
        if (!string.IsNullOrEmpty(fallbackTexturePath))
        {
            var texture = TextureLoader.LoadTexture(fallbackTexturePath);
            if (texture != null)
                defaultMaterial.DiffuseMap = texture;
        }

        return defaultMaterial;
    }

    /// <summary>
    ///     Finds the MaterialJson that matches the given node name.
    /// </summary>
    private MaterialJson? FindMaterialForNode(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName))
            return null;

        // Strategy 1: Direct match by Name, InternalName, or MapTo
        var material = _textureLookup.FindMaterialByKey(nodeName);
        if (material != null)
            return material;

        // Strategy 2: Check MaterialsDae mappings (high priority - these are explicit DAE-to-material mappings)
        material = _textureLookup.FindMaterialByDaeNodeName(nodeName);
        if (material != null)
            return material;

        // Strategy 3: Try extracting material name from node name patterns
        // Common patterns: "MaterialName-mesh", "geometry-MaterialName", "MaterialName_submesh0", "MaterialName-material"
        var nameParts = nodeName.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in nameParts)
        {
            var lower = part.ToLowerInvariant();
            if (lower == "mesh" || lower == "geometry" || lower == "submesh" || lower == "material")
                continue;

            material = _textureLookup.FindMaterialByKey(part);
            if (material != null)
                return material;
        }

        // Strategy 4: Fuzzy match - check if any material name is contained in node name
        material = _textureLookup.FindMaterialByFuzzyMatch(nodeName);
        return material;
    }

    /// <summary>
    ///     Logs a debug message if debug logging is enabled in TextureMapConfig.
    /// </summary>
    private static void LogDebug(string message)
    {
        if (TextureMapConfig.EnableDebugLogging)
        {
            Debug.WriteLine($"[MaterialFactory] {message}");
        }
    }

    /// <summary>
    ///     Gets all texture paths for a material from the lookup.
    /// </summary>
    private Dictionary<string, string> GetTexturesForMaterial(MaterialJson materialJson)
    {
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Try to get textures from the lookup using various keys
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(materialJson.Name))
            keys.Add(materialJson.Name);
        if (!string.IsNullOrEmpty(materialJson.InternalName))
            keys.Add(materialJson.InternalName);
        if (!string.IsNullOrEmpty(materialJson.MapTo))
            keys.Add(materialJson.MapTo);

        foreach (var key in keys)
            if (_textureLookup.AllTextures.TryGetValue(key, out var materialTextures))
                foreach (var kvp in materialTextures)
                    if (!textures.ContainsKey(kvp.Key))
                        textures[kvp.Key] = kvp.Value;

        // Also get textures directly from MaterialFiles
        if (materialJson.MaterialFiles != null)
            foreach (var file in materialJson.MaterialFiles)
            {
                if (file.File == null || string.IsNullOrEmpty(file.MapType))
                    continue;

                var canResolve = file.File.Exists || LinkFileResolver.CanResolve(file.File.FullName);
                if (!canResolve)
                    continue;

                if (!textures.ContainsKey(file.MapType))
                    textures[file.MapType] = file.File.FullName;
            }

        return textures;
    }
}