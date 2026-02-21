using System.Collections.Concurrent;
using System.Drawing;
using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNgTerrainPoc.Terrain.StyleConfig;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
///     Maps OSM material tags to BuildingMaterialDefinition entries and handles
///     deploying the CC0 textures to the output level directory.
///     Texture source priority:
///     1. OSM2World-default-style directory
///     (%LocalAppData%\BeamNG_LevelCleanUp\OSM2World-default-style\textures\cc0textures\)
///     2. Bundled Resources/BuildingTextures/ directory
///     3. Generated solid-color placeholder textures
///     Material keys used by BuildingData.WallMaterial / RoofMaterial:
///     Wall: BUILDING_DEFAULT, BRICK, CONCRETE, WOOD_WALL, GLASS_WALL,
///     CORRUGATED_STEEL, ADOBE, SANDSTONE, STONE, STEEL, TILES, MARBLE
///     Roof: ROOF_DEFAULT, CONCRETE, ROOF_TILES, ROOF_METAL, SLATE,
///     THATCH_ROOF, COPPER_ROOF, WOOD_ROOF
///     Facade: WINDOW_SINGLE, BUILDING_WINDOWS, WINDOW_FRAME, WINDOW_GLASS,
///     GLASS_TRANSPARENT, DOOR_DEFAULT, DOOR_GARAGE
/// </summary>
public class BuildingMaterialLibrary
{
    /// <summary>
    ///     Static cache of resolved texture source paths (textureFile → absolute DDS path on disk).
    ///     Persists for the app session so textures are never searched/converted twice.
    ///     Key: "textureFolder/textureFile" or just "textureFile" if no folder.
    ///     Value: absolute path to the DDS file, or null if not found in any source.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string?> _resolvedTextureCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Maps OSM2World material names to our internal material keys where they differ.
    ///     Most materials have the same key in both systems; only these facade materials differ.
    /// </summary>
    private static readonly Dictionary<string, string> Osm2WorldToInternalKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SINGLE_WINDOW"] = "WINDOW_SINGLE",
        ["ENTRANCE_DEFAULT"] = "DOOR_DEFAULT",
        ["GARAGE_DOOR"] = "DOOR_GARAGE",
        ["GLASS"] = "WINDOW_GLASS"
    };

    private readonly ConcurrentDictionary<string, BuildingMaterialDefinition> _materials;

    /// <summary>
    ///     Tracks texture file renames from PoT dimension checking.
    ///     Key: original filename with cooker suffix (e.g., "Bricks029_Color.color.png").
    ///     Value: renamed filename without suffix (e.g., "Bricks029_Color.png").
    ///     Only contains entries for non-PoT textures whose cooker suffix was stripped.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _textureFileRenames = new(StringComparer.OrdinalIgnoreCase);

    public BuildingMaterialLibrary()
    {
        _materials = new ConcurrentDictionary<string, BuildingMaterialDefinition>(StringComparer.OrdinalIgnoreCase);

        // Try loading from JSON config first (user-editable)
        var config = StyleConfigLoader.LoadMainConfig(GetStyleConfigPath());
        if (config != null)
            foreach (var (osm2WorldKey, styleMat) in config.Materials)
            {
                // Map OSM2World keys to our internal keys where they differ
                var key = Osm2WorldToInternalKeyMap.TryGetValue(osm2WorldKey, out var internalKey)
                    ? internalKey
                    : osm2WorldKey;

                var def = StyleConfigLoader.ToMaterialDefinition(key, styleMat);
                if (def != null)
                    _materials[key] = def;
            }

        // Fill gaps with hard-coded defaults (JSON-loaded materials take precedence)
        RegisterDefaultMaterials();
    }

    /// <summary>
    ///     Gets all registered material definitions.
    /// </summary>
    public ICollection<BuildingMaterialDefinition> AllMaterials => _materials.Values;

    /// <summary>
    ///     Returns the path to the osm2world-style.json configuration file.
    /// </summary>
    public static string GetStyleConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG_LevelCleanUp",
            "osm2world-style.json");
    }

    /// <summary>
    ///     Gets a material definition by its key. Returns the BUILDING_DEFAULT if not found.
    /// </summary>
    public BuildingMaterialDefinition GetMaterial(string materialKey)
    {
        if (_materials.TryGetValue(materialKey, out var mat))
            return mat;

        return _materials["BUILDING_DEFAULT"];
    }

    /// <summary>
    ///     Gets or creates a color variant of a base material for clustered building export.
    ///     Thread-safe: clusters are exported in parallel and may create variants concurrently.
    ///     The variant shares textures with the base material but has the color baked into DefaultColor.
    /// </summary>
    /// <param name="baseMaterialKey">The base material key (e.g., "BUILDING_DEFAULT").</param>
    /// <param name="color">The color from the OSM building:colour tag.</param>
    /// <returns>The variant material definition (existing or newly created).</returns>
    public BuildingMaterialDefinition GetOrCreateColorVariant(string baseMaterialKey, Color color)
    {
        var hexSuffix = $"{color.R:x2}{color.G:x2}{color.B:x2}";
        var variantKey = $"{baseMaterialKey}_{hexSuffix}";

        return _materials.GetOrAdd(variantKey, _ =>
        {
            var baseMat = GetMaterial(baseMaterialKey);
            return baseMat.CreateColorVariant(color, hexSuffix);
        });
    }

    /// <summary>
    ///     Returns the deployed filename for a texture, accounting for PoT dimension renames.
    ///     Non-PoT textures have their cooker suffix stripped (e.g., ".color.png" → ".png")
    ///     so BeamNG's Texture Cooker won't attempt DDS conversion on them.
    ///     Returns the original filename if no rename was recorded (PoT or not yet deployed).
    /// </summary>
    public string GetDeployedFileName(string originalFileName)
    {
        return _textureFileRenames.TryGetValue(originalFileName, out var renamed) ? renamed : originalFileName;
    }

    /// <summary>
    ///     Collects the unique set of materials actually used by the given buildings (flat BuildingData).
    /// </summary>
    public HashSet<BuildingMaterialDefinition> GetUsedMaterials(IEnumerable<BuildingData> buildings)
    {
        var used = new HashSet<BuildingMaterialDefinition>();
        var anyWindows = false;
        foreach (var b in buildings)
        {
            used.Add(GetMaterial(b.WallMaterial));
            used.Add(GetMaterial(b.RoofMaterial));
            if (b.HasWindows) anyWindows = true;
        }

        if (anyWindows)
            AddFacadeMaterials(used);

        return used;
    }

    /// <summary>
    ///     Collects the unique set of materials actually used by multi-part Building objects.
    ///     Iterates all parts of each building to find used wall and roof materials.
    /// </summary>
    public HashSet<BuildingMaterialDefinition> GetUsedMaterials(
        IEnumerable<BeamNG.Procedural3D.Building.Building> buildings)
    {
        var used = new HashSet<BuildingMaterialDefinition>();
        var anyWindows = false;
        foreach (var building in buildings)
        foreach (var part in building.Parts)
        {
            used.Add(GetMaterial(part.WallMaterial));
            used.Add(GetMaterial(part.RoofMaterial));
            if (part.HasWindows) anyWindows = true;
        }

        if (anyWindows)
            AddFacadeMaterials(used);

        return used;
    }

    /// <summary>
    ///     Adds the standard facade materials (windows, doors) used by LOD1/LOD2 meshes.
    /// </summary>
    private void AddFacadeMaterials(HashSet<BuildingMaterialDefinition> used)
    {
        used.Add(GetMaterial("WINDOW_SINGLE"));
        used.Add(GetMaterial("BUILDING_WINDOWS"));
        used.Add(GetMaterial("WINDOW_FRAME"));
        used.Add(GetMaterial("WINDOW_GLASS"));
        used.Add(GetMaterial("GLASS_TRANSPARENT"));
        used.Add(GetMaterial("DOOR_DEFAULT"));
        used.Add(GetMaterial("DOOR_GARAGE"));
    }

    /// <summary>
    ///     Deploys texture files for the given materials to the level's output texture directory.
    ///     Searches for textures in the OSM2World-default-style package and bundled Resources/BuildingTextures/.
    ///     Falls back to generating solid-color placeholder textures for any missing files.
    ///     Resolved source paths are cached in memory so repeat runs skip filesystem lookups and DDS conversion.
    /// </summary>
    /// <param name="usedMaterials">The materials whose textures need to be deployed.</param>
    /// <param name="targetTextureDir">Absolute path to the output textures folder.</param>
    /// <returns>Number of texture files deployed (copied + generated).</returns>
    public int DeployTextures(IEnumerable<BuildingMaterialDefinition> usedMaterials, string targetTextureDir)
    {
        Directory.CreateDirectory(targetTextureDir);

        var sourceDirs = GetTextureSourceDirectories();

        // Materialize to list — we need two passes (ORM split pre-pass + deployment)
        var materialsList = usedMaterials.ToList();

        // Pre-pass: split combined ORM textures into individual AO/Roughness/Metallic channels.
        // The cc0textures package ships combined ORM files; BeamNG needs separate PBR maps.
        // Skip splitting for materials where the user has set all 3 explicit channel overrides
        // (AoMapFile, RoughnessMapFile, MetallicMapFile) in osm2world-style.json.
        // Split files are cached on disk alongside the source ORM for future runs.
        var splittedOrms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materialsList)
        {
            if (material.OrmMapFile == null) continue;

            // Skip if user provided all 3 explicit channel textures — no ORM splitting needed
            if (material.AoMapFile != null && material.RoughnessMapFile != null && material.MetallicMapFile != null)
                continue;

            if (!splittedOrms.Add(material.OrmMapFile)) continue;

            // Check if split files already exist (cached from a prior run)
            var aoFile = OrmTextureHelper.DeriveAoFileName(material.OrmMapFile);
            var aoSource = ResolveTextureSource(sourceDirs, aoFile, material.TextureFolder);
            if (aoSource != null) continue; // Already split

            // Find the combined ORM source and split it into 3 channel PNGs
            var ormSource = ResolveTextureSource(sourceDirs, material.OrmMapFile, material.TextureFolder);
            if (ormSource != null)
            {
                var outputDir = Path.GetDirectoryName(ormSource)!;
                BuildingTextureConverter.SplitOrmTexture(ormSource, outputDir, material.OrmMapFile);

                // Invalidate cache entries so the freshly-created split files are found
                var cachePrefix = material.TextureFolder != null ? $"{material.TextureFolder}/" : "";
                _resolvedTextureCache.TryRemove(cachePrefix + aoFile, out _);
                _resolvedTextureCache.TryRemove(
                    cachePrefix + OrmTextureHelper.DeriveRoughnessFileName(material.OrmMapFile), out _);
                _resolvedTextureCache.TryRemove(
                    cachePrefix + OrmTextureHelper.DeriveMetallicFileName(material.OrmMapFile), out _);
            }
        }

        // Main deployment loop: copy resolved textures or generate placeholders
        var deployed = 0;
        var alreadyDeployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in materialsList)
        foreach (var textureFile in material.GetTextureFiles())
        {
            if (!alreadyDeployed.Add(textureFile))
                continue;

            var targetPath = Path.Combine(targetTextureDir, textureFile);

            // Skip if already deployed (e.g., from a previous run)
            if (File.Exists(targetPath))
            {
                deployed++;
                continue;
            }

            // Try finding the texture in source directories (cached — first call may convert, subsequent calls are instant)
            var sourcePath = ResolveTextureSource(sourceDirs, textureFile, material.TextureFolder);
            if (sourcePath != null)
            {
                File.Copy(sourcePath, targetPath, false);

                // BeamNG Texture Cooker requires power-of-2 dimensions for DDS conversion.
                // If texture is non-PoT, strip the cooker suffix so BeamNG leaves it as plain PNG.
                RenameIfNonPowerOfTwo(targetTextureDir, textureFile, targetPath);

                deployed++;
                continue;
            }

            // Fall back to generating a placeholder texture (always 256×256 = PoT, no rename needed)
            BuildingTexturePlaceholderGenerator.GeneratePlaceholder(textureFile, material, targetPath);
            deployed++;
        }

        return deployed;
    }

    /// <summary>
    ///     Maps an OSM building:material / building:facade:material tag value to our internal material key.
    ///     Follows OSM2World's BuildingPart.parseMaterial() mapping.
    /// </summary>
    public static string MapOsmWallMaterial(string? osmMaterialTag)
    {
        if (string.IsNullOrEmpty(osmMaterialTag))
            return "BUILDING_DEFAULT";

        return osmMaterialTag.ToLowerInvariant() switch
        {
            "brick" => "BRICK",
            "concrete" or "concrete_block" => "CONCRETE",
            "wood" or "timber_framing" or "bamboo" => "WOOD_WALL",
            "glass" or "mirror" => "GLASS_WALL",
            "plaster" or "plastered" or "stucco" => "BUILDING_DEFAULT",
            "metal" or "steel" => "STEEL",
            "corrugated_metal" or "corrugated_steel" => "CORRUGATED_STEEL",
            "stone" or "limestone" => "STONE",
            "sandstone" => "SANDSTONE",
            "adobe" or "clay" or "mud" or "rammed_earth" => "ADOBE",
            "tiles" or "tile" => "TILES",
            "marble" => "MARBLE",
            _ => "BUILDING_DEFAULT"
        };
    }

    /// <summary>
    ///     Maps an OSM roof:material tag value to our internal material key.
    ///     Follows OSM2World's BuildingPart.parseMaterial() mapping.
    /// </summary>
    public static string MapOsmRoofMaterial(string? osmRoofMaterialTag)
    {
        if (string.IsNullOrEmpty(osmRoofMaterialTag))
            return "ROOF_DEFAULT";

        return osmRoofMaterialTag.ToLowerInvariant() switch
        {
            "roof_tiles" or "tiles" or "tile" or "clay" => "ROOF_TILES",
            "metal" or "steel" or "tin" => "ROOF_METAL",
            "copper" => "COPPER_ROOF",
            "concrete" or "tar_paper" or "asbestos" => "CONCRETE",
            "slate" => "SLATE",
            "stone" => "SLATE",
            "thatch" or "grass" or "reed" => "THATCH_ROOF",
            "glass" => "GLASS_ROOF",
            "wood" or "bamboo" => "WOOD_ROOF",
            _ => "ROOF_DEFAULT"
        };
    }

    /// <summary>
    ///     Returns the path to the OSM2World-default-style folder in AppData.
    /// </summary>
    public static string GetOsm2WorldStyleFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG_LevelCleanUp",
            "OSM2World-default-style");
    }

    /// <summary>
    ///     Collects all existing texture source directories (OSM2World style folder + bundled resources).
    /// </summary>
    private static List<string> GetTextureSourceDirectories()
    {
        var dirs = new List<string>();

        // 1. OSM2World-default-style in AppData (preferred source)
        var osm2WorldDir = GetOsm2WorldStyleFolder();
        if (Directory.Exists(osm2WorldDir))
            dirs.Add(osm2WorldDir);

        // 2. Bundled Resources/BuildingTextures (fallback)
        var assemblyDir = AppContext.BaseDirectory;
        var bundledCandidates = new[]
        {
            Path.Combine(assemblyDir, "Resources", "BuildingTextures"),
            Path.Combine(assemblyDir, "..", "Resources", "BuildingTextures"),
            Path.Combine(assemblyDir, "..", "..", "..", "Resources", "BuildingTextures"),
            Path.Combine(assemblyDir, "..", "..", "..", "..", "BeamNgTerrainPoc", "Resources", "BuildingTextures")
        };

        foreach (var candidate in bundledCandidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (Directory.Exists(fullPath))
            {
                dirs.Add(fullPath);
                break;
            }
        }

        return dirs;
    }

    /// <summary>
    ///     Resolves a texture file to its source DDS path, using the in-memory cache.
    ///     First call per texture does the full filesystem search + optional DDS conversion.
    ///     All subsequent calls (even across multiple generation runs in the same session) return instantly.
    /// </summary>
    private static string? ResolveTextureSource(List<string> sourceDirs, string textureFile, string? textureFolder)
    {
        var cacheKey = textureFolder != null ? $"{textureFolder}/{textureFile}" : textureFile;

        if (_resolvedTextureCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var resolved = FindTextureInSourcesCore(sourceDirs, textureFile, textureFolder);
        _resolvedTextureCache.TryAdd(cacheKey, resolved);
        return resolved;
    }

    /// <summary>
    ///     Core texture search logic (uncached). Search order:
    ///     1. Look for already-converted PNG with BeamNG cooker naming in source dirs
    ///     2. Derive OSM2World source base name, search for .png then .jpg source
    ///     3. If source found, convert to PNG with cooker naming, save alongside source (on-disk cache)
    /// </summary>
    private static string? FindTextureInSourcesCore(List<string> sourceDirs, string textureFile, string? textureFolder)
    {
        // 1. Check for already-converted PNG with BeamNG naming (on-disk cache or user-provided)
        var cachedPng = FindFileInDirs(sourceDirs, textureFile, textureFolder);
        if (cachedPng != null)
            return cachedPng;

        // 2. Derive OSM2World source base name: "Bricks029_Color.color.png" → "Bricks029_Color"
        var sourceBaseName = BuildingTextureConverter.DeriveSourceBaseName(textureFile);
        string[] sourceExtensions = [".png", ".jpg"];

        foreach (var ext in sourceExtensions)
        {
            var sourceFileName = sourceBaseName + ext;
            var sourcePath = FindFileInDirs(sourceDirs, sourceFileName, textureFolder);
            if (sourcePath != null)
            {
                // Convert source to PNG with BeamNG naming, save alongside source (on-disk cache)
                var pngOutputPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, textureFile);
                try
                {
                    BuildingTextureConverter.ConvertToPng(sourcePath, pngOutputPath);
                    return pngOutputPath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"BuildingMaterialLibrary: PNG conversion failed for {sourceFileName}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Searches for a specific file in the source directories using multiple OSM2World layout patterns:
    ///     1. Flat layout: dir/fileName
    ///     2. cc0textures nested: dir/textures/cc0textures/{textureFolder}/{fileName}
    ///     3. Custom textures nested: dir/textures/custom/{textureFolder}/{fileName}
    ///     4. Single-file textures: dir/textures/{fileName} (for files without a textureFolder)
    /// </summary>
    private static string? FindFileInDirs(List<string> sourceDirs, string fileName, string? textureFolder)
    {
        foreach (var dir in sourceDirs)
        {
            // Try flat layout: dir/fileName
            var flatPath = Path.Combine(dir, fileName);
            if (File.Exists(flatPath))
                return flatPath;

            if (textureFolder != null)
            {
                // Try OSM2World cc0textures layout: dir/textures/cc0textures/{textureFolder}/{fileName}
                var cc0Path = Path.Combine(dir, "textures", "cc0textures", textureFolder, fileName);
                if (File.Exists(cc0Path))
                    return cc0Path;

                // Try OSM2World custom textures layout: dir/textures/custom/{textureFolder}/{fileName}
                var customPath = Path.Combine(dir, "textures", "custom", textureFolder, fileName);
                if (File.Exists(customPath))
                    return customPath;
            }
            else
            {
                // No textureFolder — try single-file texture in textures/ subdirectory
                var texturePath = Path.Combine(dir, "textures", fileName);
                if (File.Exists(texturePath))
                    return texturePath;
            }
        }

        return null;
    }

    /// <summary>
    ///     Checks if a deployed texture has non-power-of-2 dimensions.
    ///     If so, renames it to strip the BeamNG cooker suffix (.color.png → .png)
    ///     and records the rename in <see cref="_textureFileRenames" />.
    /// </summary>
    private void RenameIfNonPowerOfTwo(string targetDir, string textureFile, string targetPath)
    {
        if (BuildingTextureConverter.HasPowerOfTwoDimensions(targetPath))
            return;

        var strippedName = BuildingTextureConverter.StripCookerSuffix(textureFile);
        if (strippedName == textureFile)
            return; // No cooker suffix to strip

        var strippedPath = Path.Combine(targetDir, strippedName);
        File.Move(targetPath, strippedPath);
        _textureFileRenames[textureFile] = strippedName;

        Console.WriteLine($"BuildingMaterialLibrary: Non-PoT texture renamed: {textureFile} → {strippedName}");
    }

    private void RegisterDefaultMaterials()
    {
        // --- Wall Materials ---

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "BUILDING_DEFAULT",
            MaterialName = "mtb_plaster",
            TextureFolder = "Plaster002",
            ColorMapFile = "Plaster002_Color.color.png",
            NormalMapFile = "Plaster002_Normal.normal.png",
            OrmMapFile = "Plaster002_ORM.data.png",
            TextureScaleU = 2.5f,
            TextureScaleV = 2.5f,
            DefaultColor = new Vector3(220, 210, 190),
            InstanceDiffuse = true
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "BRICK",
            MaterialName = "mtb_brick",
            TextureFolder = "Bricks029",
            ColorMapFile = "Bricks029_Color.color.png",
            NormalMapFile = "Bricks029_Normal.normal.png",
            OrmMapFile = "Bricks029_ORM.data.png",
            TextureScaleU = 1.4f,
            TextureScaleV = 1.4f,
            DefaultColor = new Vector3(165, 85, 60)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "CONCRETE",
            MaterialName = "mtb_concrete",
            TextureFolder = "Concrete034",
            ColorMapFile = "Concrete034_Color.color.png",
            NormalMapFile = "Concrete034_Normal.normal.png",
            OrmMapFile = "Concrete034_ORM.data.png",
            TextureScaleU = 1.2f,
            TextureScaleV = 0.6f,
            DefaultColor = new Vector3(170, 170, 165)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD_WALL",
            MaterialName = "mtb_wood_wall",
            TextureFolder = "WoodSiding008",
            ColorMapFile = "WoodSiding008_Color.color.png",
            NormalMapFile = "WoodSiding008_Normal.normal.png",
            OrmMapFile = "WoodSiding008_ORM.data.png",
            TextureScaleU = 3.2f,
            TextureScaleV = 1.6f,
            DefaultColor = new Vector3(160, 120, 75)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_WALL",
            MaterialName = "mtb_glass_wall",
            TextureFolder = "Facade005",
            ColorMapFile = "Facade005_Color.color.png",
            NormalMapFile = "Facade005_Normal.normal.png",
            OrmMapFile = "Facade005_ORM.data.png",
            TextureScaleU = 72.0f,
            TextureScaleV = 72.0f,
            DefaultColor = new Vector3(230, 230, 230) // OSM2World: 0.9, 0.9, 0.9
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "CORRUGATED_STEEL",
            MaterialName = "mtb_corrugated_steel",
            TextureFolder = "CorrugatedSteel005",
            ColorMapFile = "CorrugatedSteel005_Color.color.png",
            NormalMapFile = "CorrugatedSteel005_Normal.normal.png",
            OrmMapFile = "CorrugatedSteel005_ORM.data.png",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(160, 165, 170)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "ADOBE",
            MaterialName = "mtb_adobe",
            TextureFolder = "Ground026",
            ColorMapFile = "Ground026_Color.color.png",
            NormalMapFile = "Ground026_Normal.normal.png",
            OrmMapFile = "Ground026_ORM.data.png",
            TextureScaleU = 1.5f,
            TextureScaleV = 1.5f,
            DefaultColor = new Vector3(180, 150, 110)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "SANDSTONE",
            MaterialName = "mtb_sandstone",
            TextureFolder = "Bricks008",
            ColorMapFile = "Bricks008_Color.color.png",
            NormalMapFile = "Bricks008_Normal.normal.png",
            OrmMapFile = "Bricks008_ORM.data.png",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(210, 190, 150)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "STONE",
            MaterialName = "mtb_stone",
            TextureFolder = "Bricks008",
            ColorMapFile = "Bricks008_Color.color.png",
            NormalMapFile = "Bricks008_Normal.normal.png",
            OrmMapFile = "Bricks008_ORM.data.png",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(160, 160, 155)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "STEEL",
            MaterialName = "mtb_steel",
            TextureFolder = "Metal002",
            ColorMapFile = "Metal002_Color.color.png",
            NormalMapFile = "Metal002_Normal.normal.png",
            OrmMapFile = "Metal002_ORM.data.png",
            TextureScaleU = 1.5f,
            TextureScaleV = 1.5f,
            DefaultColor = new Vector3(180, 185, 190)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "TILES",
            MaterialName = "mtb_tiles",
            TextureFolder = "Tiles036",
            ColorMapFile = "Tiles036_Color.color.png",
            NormalMapFile = "Tiles036_Normal.normal.png",
            OrmMapFile = "Tiles036_ORM.data.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(200, 200, 195)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "MARBLE",
            MaterialName = "mtb_marble",
            TextureFolder = "Marble001",
            ColorMapFile = "Marble001_Color.color.png",
            NormalMapFile = "Marble001_Normal.normal.png",
            OrmMapFile = "Marble001_ORM.data.png",
            TextureScaleU = 0.5f,
            TextureScaleV = 0.5f,
            DefaultColor = new Vector3(230, 230, 225)
        });

        // --- Roof Materials ---

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_DEFAULT",
            MaterialName = "mtb_roof_tiles",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.color.png",
            NormalMapFile = "RoofingTiles010_Normal.normal.png",
            OrmMapFile = "RoofingTiles010_ORM.data.png",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_TILES",
            MaterialName = "mtb_roof_tiles",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.color.png",
            NormalMapFile = "RoofingTiles010_Normal.normal.png",
            OrmMapFile = "RoofingTiles010_ORM.data.png",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        // BUG FIX: MetalPlates006 does NOT exist in OSM2World cc0textures package.
        // Using CorrugatedSteel005 which is the closest available metal texture.
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_METAL",
            MaterialName = "mtb_roof_metal",
            TextureFolder = "CorrugatedSteel005",
            ColorMapFile = "CorrugatedSteel005_Color.color.png",
            NormalMapFile = "CorrugatedSteel005_Normal.normal.png",
            OrmMapFile = "CorrugatedSteel005_ORM.data.png",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(120, 125, 130)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "SLATE",
            MaterialName = "mtb_slate",
            TextureFolder = "RoofingTiles003",
            ColorMapFile = "RoofingTiles003_Color.color.png",
            NormalMapFile = "RoofingTiles003_Normal.normal.png",
            OrmMapFile = "RoofingTiles003_ORM.data.png",
            TextureScaleU = 1.2f,
            TextureScaleV = 1.2f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(90, 95, 100)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "THATCH_ROOF",
            MaterialName = "mtb_thatch",
            TextureFolder = "ThatchedRoof001A",
            ColorMapFile = "ThatchedRoof001A_Color.color.png",
            NormalMapFile = "ThatchedRoof001A_Normal.normal.png",
            OrmMapFile = "ThatchedRoof001A_ORM.data.png",
            TextureScaleU = 2.5f,
            TextureScaleV = 2.5f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(170, 150, 90)
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "COPPER_ROOF",
            MaterialName = "mtb_copper_roof",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.color.png",
            NormalMapFile = "RoofingTiles010_Normal.normal.png",
            OrmMapFile = "RoofingTiles010_ORM.data.png",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(195, 219, 185) // OSM2World: #C3DBB9
        });

        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD_ROOF",
            MaterialName = "mtb_wood_roof",
            TextureFolder = "Wood026",
            ColorMapFile = "Wood026_Color.color.png",
            NormalMapFile = "Wood026_Normal.normal.png",
            OrmMapFile = "Wood026_ORM.data.png",
            TextureScaleU = 0.5f,
            TextureScaleV = 0.5f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(160, 120, 75)
        });

        // GLASS_ROOF reuses Facade005 textures
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_ROOF",
            MaterialName = "mtb_glass_roof",
            TextureFolder = "Facade005",
            ColorMapFile = "Facade005_Color.color.png",
            NormalMapFile = "Facade005_Normal.normal.png",
            OrmMapFile = "Facade005_ORM.data.png",
            TextureScaleU = 72.0f,
            TextureScaleV = 72.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(230, 230, 230) // OSM2World: 0.9, 0.9, 0.9
        });

        // --- Facade Materials (windows, doors) ---
        // Port of OSM2World facade elements with real textures from style package.

        // SINGLE_WINDOW: LOD1 textured window quad
        // OSM2World: SINGLE_WINDOW — PBR textures from textures/custom/Window/
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_SINGLE",
            MaterialName = "mtb_window_single",
            TextureFolder = "Window",
            ColorMapFile = "Window_Color.color.png",
            NormalMapFile = "Window_Normal.normal.png",
            OrmMapFile = "Window_ORM.data.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });

        // BUILDING_WINDOWS: facade-wide window texture band (LOD1)
        // OSM2World: BUILDING_WINDOWS — PBR textures from textures/custom/Windows/
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "BUILDING_WINDOWS",
            MaterialName = "mtb_building_windows",
            TextureFolder = "Windows",
            ColorMapFile = "Windows_Color.color.png",
            NormalMapFile = "Windows_Normal.normal.png",
            OrmMapFile = "Windows_ORM.data.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 230, 140)
        });

        // WINDOW_FRAME: LOD2 window frame (plastic/wood)
        // OSM2World: PLASTIC = Material(FLAT, WHITE) — stays solid-color placeholder
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_FRAME",
            MaterialName = "mtb_window_frame",
            TextureFolder = null,
            ColorMapFile = "mtb_window_frame_Color.color.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });

        // WINDOW_GLASS: LOD2 glass pane
        // OSM2World: GLASS — PBR textures from textures/custom/Glass/
        // Semi-transparent, double-sided so glass is visible from both directions.
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_GLASS",
            MaterialName = "mtb_window_glass",
            TextureFolder = "Glass",
            ColorMapFile = "Glass_Color.color.png",
            NormalMapFile = "Glass_Normal.normal.png",
            OrmMapFile = "Glass_ORM.data.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(230, 230, 230),
            Opacity = 0.5f,
            DoubleSided = true
        });

        // GLASS_TRANSPARENT: transparent glass variant
        // OSM2World: GLASS_TRANSPARENT — same textures as GLASS but fully transparent
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_TRANSPARENT",
            MaterialName = "mtb_glass_transparent",
            TextureFolder = "Glass",
            ColorMapFile = "Glass_Color.color.png",
            NormalMapFile = "Glass_Normal.normal.png",
            OrmMapFile = "Glass_ORM.data.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(230, 230, 230),
            Opacity = 0.5f,
            DoubleSided = true
        });

        // DOOR_DEFAULT: standard entrance door
        // OSM2World: ENTRANCE_DEFAULT — single file texture (locale-specific)
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "DOOR_DEFAULT",
            MaterialName = "mtb_door_default",
            TextureFolder = null,
            ColorMapFile = "DE19F1FreisingDoor00005_small.color.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });

        // DOOR_GARAGE: overhead/garage door
        // OSM2World: GARAGE_DOOR — single file texture (locale-specific, colorable)
        RegisterIfMissing(new BuildingMaterialDefinition
        {
            MaterialKey = "DOOR_GARAGE",
            MaterialName = "mtb_door_garage",
            TextureFolder = null,
            ColorMapFile = "DE20F1GarageDoor00001.color.png",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });
    }

    /// <summary>
    ///     Registers a material only if no definition with the same key exists yet.
    ///     JSON-loaded materials take precedence over hard-coded defaults.
    /// </summary>
    private void RegisterIfMissing(BuildingMaterialDefinition definition)
    {
        _materials.TryAdd(definition.MaterialKey, definition);
    }
}