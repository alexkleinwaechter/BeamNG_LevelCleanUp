using System.Collections.Concurrent;
using System.Drawing;
using System.Numerics;
using BeamNG.Procedural3D.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Maps OSM material tags to BuildingMaterialDefinition entries and handles
/// deploying the CC0 textures to the output level directory.
///
/// Texture source priority:
///   1. OSM2World-default-style directory (%LocalAppData%\BeamNG_LevelCleanUp\OSM2World-default-style\textures\cc0textures\)
///   2. Bundled Resources/BuildingTextures/ directory
///   3. Generated solid-color placeholder textures
///
/// Material keys used by BuildingData.WallMaterial / RoofMaterial:
///   Wall: BUILDING_DEFAULT, BRICK, CONCRETE, WOOD_WALL, GLASS_WALL,
///         CORRUGATED_STEEL, ADOBE, SANDSTONE, STONE, STEEL, TILES, MARBLE
///   Roof: ROOF_DEFAULT, CONCRETE, ROOF_TILES, ROOF_METAL, SLATE,
///         THATCH_ROOF, COPPER_ROOF, WOOD_ROOF
/// </summary>
public class BuildingMaterialLibrary
{
    private readonly ConcurrentDictionary<string, BuildingMaterialDefinition> _materials;

    public BuildingMaterialLibrary()
    {
        _materials = new ConcurrentDictionary<string, BuildingMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
        RegisterDefaultMaterials();
    }

    /// <summary>
    /// Gets a material definition by its key. Returns the BUILDING_DEFAULT if not found.
    /// </summary>
    public BuildingMaterialDefinition GetMaterial(string materialKey)
    {
        if (_materials.TryGetValue(materialKey, out var mat))
            return mat;

        return _materials["BUILDING_DEFAULT"];
    }

    /// <summary>
    /// Gets or creates a color variant of a base material for clustered building export.
    /// Thread-safe: clusters are exported in parallel and may create variants concurrently.
    /// The variant shares textures with the base material but has the color baked into DefaultColor.
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
    /// Gets all registered material definitions.
    /// </summary>
    public ICollection<BuildingMaterialDefinition> AllMaterials => _materials.Values;

    /// <summary>
    /// Collects the unique set of materials actually used by the given buildings (flat BuildingData).
    /// </summary>
    public HashSet<BuildingMaterialDefinition> GetUsedMaterials(IEnumerable<BuildingData> buildings)
    {
        var used = new HashSet<BuildingMaterialDefinition>();
        bool anyWindows = false;
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
    /// Collects the unique set of materials actually used by multi-part Building objects.
    /// Iterates all parts of each building to find used wall and roof materials.
    /// </summary>
    public HashSet<BuildingMaterialDefinition> GetUsedMaterials(IEnumerable<BeamNG.Procedural3D.Building.Building> buildings)
    {
        var used = new HashSet<BuildingMaterialDefinition>();
        bool anyWindows = false;
        foreach (var building in buildings)
        {
            foreach (var part in building.Parts)
            {
                used.Add(GetMaterial(part.WallMaterial));
                used.Add(GetMaterial(part.RoofMaterial));
                if (part.HasWindows) anyWindows = true;
            }
        }

        if (anyWindows)
            AddFacadeMaterials(used);

        return used;
    }

    /// <summary>
    /// Adds the standard facade materials (windows, doors) used by LOD1/LOD2 meshes.
    /// </summary>
    private void AddFacadeMaterials(HashSet<BuildingMaterialDefinition> used)
    {
        used.Add(GetMaterial("WINDOW_SINGLE"));
        used.Add(GetMaterial("WINDOW_FRAME"));
        used.Add(GetMaterial("WINDOW_GLASS"));
        used.Add(GetMaterial("DOOR_DEFAULT"));
        used.Add(GetMaterial("DOOR_GARAGE"));
    }

    /// <summary>
    /// Deploys texture files for the given materials to the level's output texture directory.
    /// Lookup order:
    ///   1. OSM2World-default-style/textures/cc0textures/{TextureFolder}/ (if available)
    ///   2. Bundled Resources/BuildingTextures/
    ///   3. Generated solid-color placeholder textures
    /// </summary>
    /// <param name="usedMaterials">The materials whose textures need to be deployed.</param>
    /// <param name="targetTextureDir">Absolute path to the output textures folder.</param>
    /// <returns>Number of texture files deployed (copied + generated).</returns>
    public int DeployTextures(IEnumerable<BuildingMaterialDefinition> usedMaterials, string targetTextureDir)
    {
        Directory.CreateDirectory(targetTextureDir);

        var osm2worldDir = GetOsm2WorldTexturesDirectory();
        bool hasOsm2WorldDir = osm2worldDir != null && Directory.Exists(osm2worldDir);

        var bundledDir = GetBundledTexturesDirectory();
        bool hasBundledDir = bundledDir != null && Directory.Exists(bundledDir);

        int deployed = 0;
        var alreadyDeployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in usedMaterials)
        {
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

                // 1. Try OSM2World-default-style texture folder
                if (hasOsm2WorldDir && material.TextureFolder != null)
                {
                    var osm2worldPath = Path.Combine(osm2worldDir!, material.TextureFolder, textureFile);
                    if (File.Exists(osm2worldPath))
                    {
                        File.Copy(osm2worldPath, targetPath, overwrite: false);
                        deployed++;
                        continue;
                    }
                }

                // 2. Try bundled Resources/BuildingTextures/
                if (hasBundledDir)
                {
                    var sourcePath = Path.Combine(bundledDir!, textureFile);
                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, targetPath, overwrite: false);
                        deployed++;
                        continue;
                    }
                }

                // 3. Fall back to generating a placeholder texture
                BuildingTexturePlaceholderGenerator.GeneratePlaceholder(textureFile, material, targetPath);
                deployed++;
            }
        }

        return deployed;
    }

    /// <summary>
    /// Maps an OSM building:material / building:facade:material tag value to our internal material key.
    /// Follows OSM2World's BuildingPart.parseMaterial() mapping.
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
    /// Maps an OSM roof:material tag value to our internal material key.
    /// Follows OSM2World's BuildingPart.parseMaterial() mapping.
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
    /// Locates the OSM2World-default-style textures directory.
    /// Expected at: %LocalAppData%\BeamNG_LevelCleanUp\OSM2World-default-style\textures\cc0textures\
    /// </summary>
    private static string? GetOsm2WorldTexturesDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var osm2worldPath = Path.Combine(localAppData, "BeamNG_LevelCleanUp", "OSM2World-default-style", "textures", "cc0textures");

        if (Directory.Exists(osm2worldPath))
            return osm2worldPath;

        return null;
    }

    /// <summary>
    /// Locates the bundled BuildingTextures directory.
    /// Searches relative to the executing assembly (handles both debug and published layouts).
    /// </summary>
    private static string? GetBundledTexturesDirectory()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(assemblyDir, "Resources", "BuildingTextures"),
            Path.Combine(assemblyDir, "..", "Resources", "BuildingTextures"),
            // Development-time: navigate from bin/Debug/net9.0 up to project root
            Path.Combine(assemblyDir, "..", "..", "..", "Resources", "BuildingTextures"),
            // BeamNgTerrainPoc project
            Path.Combine(assemblyDir, "..", "..", "..", "..", "BeamNgTerrainPoc", "Resources", "BuildingTextures"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (Directory.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    private void RegisterDefaultMaterials()
    {
        // --- Wall Materials ---

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "BUILDING_DEFAULT",
            MaterialName = "mtb_plaster",
            TextureFolder = "Plaster002",
            ColorMapFile = "Plaster002_Color.jpg",
            NormalMapFile = "Plaster002_Normal.jpg",
            OrmMapFile = "Plaster002_ORM.jpg",
            TextureScaleU = 2.5f,
            TextureScaleV = 2.5f,
            DefaultColor = new Vector3(220, 210, 190),
            InstanceDiffuse = true
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "BRICK",
            MaterialName = "mtb_brick",
            TextureFolder = "Bricks029",
            ColorMapFile = "Bricks029_Color.jpg",
            NormalMapFile = "Bricks029_Normal.jpg",
            OrmMapFile = "Bricks029_ORM.jpg",
            TextureScaleU = 1.4f,
            TextureScaleV = 1.4f,
            DefaultColor = new Vector3(165, 85, 60)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "CONCRETE",
            MaterialName = "mtb_concrete",
            TextureFolder = "Concrete034",
            ColorMapFile = "Concrete034_Color.jpg",
            NormalMapFile = "Concrete034_Normal.jpg",
            OrmMapFile = "Concrete034_ORM.jpg",
            TextureScaleU = 1.2f,
            TextureScaleV = 0.6f,
            DefaultColor = new Vector3(170, 170, 165)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD_WALL",
            MaterialName = "mtb_wood_wall",
            TextureFolder = "WoodSiding008",
            ColorMapFile = "WoodSiding008_Color.jpg",
            NormalMapFile = "WoodSiding008_Normal.jpg",
            OrmMapFile = "WoodSiding008_ORM.jpg",
            TextureScaleU = 3.2f,
            TextureScaleV = 1.6f,
            DefaultColor = new Vector3(160, 120, 75)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_WALL",
            MaterialName = "mtb_glass_wall",
            TextureFolder = "Facade005",
            ColorMapFile = "Facade005_Color.jpg",
            NormalMapFile = "Facade005_Normal.jpg",
            OrmMapFile = "Facade005_ORM.jpg",
            TextureScaleU = 72.0f,
            TextureScaleV = 72.0f,
            DefaultColor = new Vector3(230, 230, 230) // OSM2World: 0.9, 0.9, 0.9
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "CORRUGATED_STEEL",
            MaterialName = "mtb_corrugated_steel",
            TextureFolder = "CorrugatedSteel005",
            ColorMapFile = "CorrugatedSteel005_Color.jpg",
            NormalMapFile = "CorrugatedSteel005_Normal.jpg",
            OrmMapFile = "CorrugatedSteel005_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(160, 165, 170)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ADOBE",
            MaterialName = "mtb_adobe",
            TextureFolder = "Ground026",
            ColorMapFile = "Ground026_Color.jpg",
            NormalMapFile = "Ground026_Normal.jpg",
            OrmMapFile = "Ground026_ORM.jpg",
            TextureScaleU = 1.5f,
            TextureScaleV = 1.5f,
            DefaultColor = new Vector3(180, 150, 110)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "SANDSTONE",
            MaterialName = "mtb_sandstone",
            TextureFolder = "Bricks008",
            ColorMapFile = "Bricks008_Color.jpg",
            NormalMapFile = "Bricks008_Normal.jpg",
            OrmMapFile = "Bricks008_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(210, 190, 150)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "STONE",
            MaterialName = "mtb_stone",
            TextureFolder = "Bricks008",
            ColorMapFile = "Bricks008_Color.jpg",
            NormalMapFile = "Bricks008_Normal.jpg",
            OrmMapFile = "Bricks008_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(160, 160, 155)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "STEEL",
            MaterialName = "mtb_steel",
            TextureFolder = "Metal002",
            ColorMapFile = "Metal002_Color.jpg",
            NormalMapFile = "Metal002_Normal.jpg",
            OrmMapFile = "Metal002_ORM.jpg",
            TextureScaleU = 1.5f,
            TextureScaleV = 1.5f,
            DefaultColor = new Vector3(180, 185, 190)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "TILES",
            MaterialName = "mtb_tiles",
            TextureFolder = "Tiles036",
            ColorMapFile = "Tiles036_Color.jpg",
            NormalMapFile = "Tiles036_Normal.jpg",
            OrmMapFile = "Tiles036_ORM.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(200, 200, 195)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "MARBLE",
            MaterialName = "mtb_marble",
            TextureFolder = "Marble001",
            ColorMapFile = "Marble001_Color.jpg",
            NormalMapFile = "Marble001_Normal.jpg",
            OrmMapFile = "Marble001_ORM.jpg",
            TextureScaleU = 0.5f,
            TextureScaleV = 0.5f,
            DefaultColor = new Vector3(230, 230, 225)
        });

        // --- Roof Materials ---

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_DEFAULT",
            MaterialName = "mtb_roof_tiles",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.jpg",
            NormalMapFile = "RoofingTiles010_Normal.jpg",
            OrmMapFile = "RoofingTiles010_ORM.jpg",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_TILES",
            MaterialName = "mtb_roof_tiles",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.jpg",
            NormalMapFile = "RoofingTiles010_Normal.jpg",
            OrmMapFile = "RoofingTiles010_ORM.jpg",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_METAL",
            MaterialName = "mtb_roof_metal",
            TextureFolder = "MetalPlates006",
            ColorMapFile = "MetalPlates006_Color.jpg",
            NormalMapFile = "MetalPlates006_Normal.jpg",
            OrmMapFile = "MetalPlates006_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(120, 125, 130)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "SLATE",
            MaterialName = "mtb_slate",
            TextureFolder = "RoofingTiles003",
            ColorMapFile = "RoofingTiles003_Color.jpg",
            NormalMapFile = "RoofingTiles003_Normal.jpg",
            OrmMapFile = "RoofingTiles003_ORM.jpg",
            TextureScaleU = 1.2f,
            TextureScaleV = 1.2f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(90, 95, 100)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "THATCH_ROOF",
            MaterialName = "mtb_thatch",
            TextureFolder = "ThatchedRoof001A",
            ColorMapFile = "ThatchedRoof001A_Color.jpg",
            NormalMapFile = "ThatchedRoof001A_Normal.jpg",
            OrmMapFile = "ThatchedRoof001A_ORM.jpg",
            TextureScaleU = 2.5f,
            TextureScaleV = 2.5f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(170, 150, 90)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "COPPER_ROOF",
            MaterialName = "mtb_copper_roof",
            TextureFolder = "RoofingTiles010",
            ColorMapFile = "RoofingTiles010_Color.jpg",
            NormalMapFile = "RoofingTiles010_Normal.jpg",
            OrmMapFile = "RoofingTiles010_ORM.jpg",
            TextureScaleU = 2.4f,
            TextureScaleV = 2.4f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(195, 219, 185) // OSM2World: #C3DBB9
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD_ROOF",
            MaterialName = "mtb_wood_roof",
            TextureFolder = "Wood026",
            ColorMapFile = "Wood026_Color.jpg",
            NormalMapFile = "Wood026_Normal.jpg",
            OrmMapFile = "Wood026_ORM.jpg",
            TextureScaleU = 0.5f,
            TextureScaleV = 0.5f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(160, 120, 75)
        });

        // GLASS_ROOF reuses Facade005 textures
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_ROOF",
            MaterialName = "mtb_glass_roof",
            TextureFolder = "Facade005",
            ColorMapFile = "Facade005_Color.jpg",
            NormalMapFile = "Facade005_Normal.jpg",
            OrmMapFile = "Facade005_ORM.jpg",
            TextureScaleU = 72.0f,
            TextureScaleV = 72.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(230, 230, 230) // OSM2World: 0.9, 0.9, 0.9
        });

        // --- Facade Materials (windows, doors) ---
        // Port of OSM2World DefaultMaterials: facade elements use flat colors, no cc0textures.
        // Placeholder generator creates solid-color textures from DefaultColor.

        // SINGLE_WINDOW: LOD1 textured window quad
        // OSM2World: SINGLE_WINDOW = Material(FLAT, WHITE)
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_SINGLE",
            MaterialName = "mtb_window_single",
            TextureFolder = null,
            ColorMapFile = "mtb_window_single_Color.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });

        // WINDOW_FRAME: LOD2 window frame (plastic/wood)
        // OSM2World: PLASTIC = Material(FLAT, WHITE)
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_FRAME",
            MaterialName = "mtb_window_frame",
            TextureFolder = null,
            ColorMapFile = "mtb_window_frame_Color.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });

        // WINDOW_GLASS: LOD2 glass pane
        // OSM2World: GLASS = Material(FLAT, Color(0.9, 0.9, 0.9), Transparency.TRUE)
        // Semi-transparent, double-sided so glass is visible from both directions.
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WINDOW_GLASS",
            MaterialName = "mtb_window_glass",
            TextureFolder = null,
            ColorMapFile = "mtb_window_glass_Color.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(230, 230, 230),
            Opacity = 0.5f,
            DoubleSided = true
        });

        // DOOR_DEFAULT: standard entrance door
        // OSM2World: ENTRANCE_DEFAULT = Material(FLAT, Color(0.2, 0, 0)) — dark red-brown
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "DOOR_DEFAULT",
            MaterialName = "mtb_door_default",
            TextureFolder = null,
            ColorMapFile = "mtb_door_default_Color.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(51, 0, 0)
        });

        // DOOR_GARAGE: overhead/garage door
        // OSM2World: GARAGE_DOOR = Material(FLAT, WHITE)
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "DOOR_GARAGE",
            MaterialName = "mtb_door_garage",
            TextureFolder = null,
            ColorMapFile = "mtb_door_garage_Color.jpg",
            TextureScaleU = 1.0f,
            TextureScaleV = 1.0f,
            DefaultColor = new Vector3(255, 255, 255)
        });
    }

    private void Register(BuildingMaterialDefinition definition)
    {
        _materials[definition.MaterialKey] = definition;
    }
}
