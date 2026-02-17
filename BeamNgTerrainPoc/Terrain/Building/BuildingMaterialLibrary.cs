using System.Numerics;
using BeamNG.Procedural3D.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Maps OSM material tags to BuildingMaterialDefinition entries and handles
/// deploying the bundled CC0 textures to the output level directory.
///
/// Material keys used by BuildingData.WallMaterial / RoofMaterial:
///   Wall: BUILDING_DEFAULT, BRICK, CONCRETE, WOOD_WALL, GLASS_WALL
///   Roof: ROOF_DEFAULT, CONCRETE, ROOF_TILES, ROOF_METAL
/// </summary>
public class BuildingMaterialLibrary
{
    private readonly Dictionary<string, BuildingMaterialDefinition> _materials;

    public BuildingMaterialLibrary()
    {
        _materials = new Dictionary<string, BuildingMaterialDefinition>(StringComparer.OrdinalIgnoreCase);
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
    /// Gets all registered material definitions.
    /// </summary>
    public IReadOnlyCollection<BuildingMaterialDefinition> AllMaterials => _materials.Values;

    /// <summary>
    /// Collects the unique set of materials actually used by the given buildings.
    /// </summary>
    public HashSet<BuildingMaterialDefinition> GetUsedMaterials(IEnumerable<BuildingData> buildings)
    {
        var used = new HashSet<BuildingMaterialDefinition>();
        foreach (var b in buildings)
        {
            used.Add(GetMaterial(b.WallMaterial));
            used.Add(GetMaterial(b.RoofMaterial));
        }
        return used;
    }

    /// <summary>
    /// Deploys texture files for the given materials to the level's output texture directory.
    /// First tries to copy from the bundled Resources/BuildingTextures/ directory.
    /// Falls back to generating solid-color placeholder textures for any missing files.
    /// </summary>
    /// <param name="usedMaterials">The materials whose textures need to be deployed.</param>
    /// <param name="targetTextureDir">Absolute path to the output textures folder (e.g., levels/{name}/art/shapes/MT_buildings/textures/).</param>
    /// <returns>Number of texture files deployed (copied + generated).</returns>
    public int DeployTextures(IEnumerable<BuildingMaterialDefinition> usedMaterials, string targetTextureDir)
    {
        Directory.CreateDirectory(targetTextureDir);

        var sourceDir = GetBundledTexturesDirectory();
        bool hasBundledDir = sourceDir != null && Directory.Exists(sourceDir);

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

                // Try copying from bundled resources
                if (hasBundledDir)
                {
                    var sourcePath = Path.Combine(sourceDir!, textureFile);
                    if (File.Exists(sourcePath))
                    {
                        File.Copy(sourcePath, targetPath, overwrite: false);
                        deployed++;
                        continue;
                    }
                }

                // Fall back to generating a placeholder texture
                BuildingTexturePlaceholderGenerator.GeneratePlaceholder(textureFile, material, targetPath);
                deployed++;
            }
        }

        return deployed;
    }

    /// <summary>
    /// Maps an OSM building:material tag value to our internal material key.
    /// </summary>
    public static string MapOsmWallMaterial(string? osmMaterialTag)
    {
        if (string.IsNullOrEmpty(osmMaterialTag))
            return "BUILDING_DEFAULT";

        return osmMaterialTag.ToLowerInvariant() switch
        {
            "brick" => "BRICK",
            "concrete" or "concrete_block" => "CONCRETE",
            "wood" or "timber_framing" => "WOOD_WALL",
            "glass" => "GLASS_WALL",
            "plaster" or "plastered" or "stucco" => "BUILDING_DEFAULT",
            "metal" or "steel" => "CONCRETE",
            "stone" or "sandstone" or "limestone" => "BRICK",
            _ => "BUILDING_DEFAULT"
        };
    }

    /// <summary>
    /// Maps an OSM roof:material tag value to our internal material key.
    /// </summary>
    public static string MapOsmRoofMaterial(string? osmRoofMaterialTag)
    {
        if (string.IsNullOrEmpty(osmRoofMaterialTag))
            return "ROOF_DEFAULT";

        return osmRoofMaterialTag.ToLowerInvariant() switch
        {
            "roof_tiles" or "tiles" or "tile" or "clay" => "ROOF_TILES",
            "metal" or "copper" or "steel" or "tin" => "ROOF_METAL",
            "concrete" or "tar_paper" => "CONCRETE",
            "slate" or "stone" => "ROOF_TILES",
            "thatch" or "grass" => "ROOF_TILES",
            "glass" => "GLASS_WALL",
            _ => "ROOF_DEFAULT"
        };
    }

    /// <summary>
    /// Locates the bundled BuildingTextures directory.
    /// Searches relative to the executing assembly (handles both debug and published layouts).
    /// </summary>
    private static string? GetBundledTexturesDirectory()
    {
        // Try relative to the executing assembly location
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

        return candidates[0]; // Return expected path for error message
    }

    private void RegisterDefaultMaterials()
    {
        // --- Wall Materials ---

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "BUILDING_DEFAULT",
            MaterialName = "mtb_plaster",
            ColorMapFile = "Plaster002_Color.jpg",
            NormalMapFile = "Plaster002_Normal.jpg",
            OrmMapFile = "Plaster002_ORM.jpg",
            TextureScaleU = 3.0f,
            TextureScaleV = 3.0f,
            DefaultColor = new Vector3(220, 210, 190)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "BRICK",
            MaterialName = "mtb_brick",
            ColorMapFile = "Bricks029_Color.jpg",
            NormalMapFile = "Bricks029_Normal.jpg",
            OrmMapFile = "Bricks029_ORM.jpg",
            TextureScaleU = 2.5f,
            TextureScaleV = 2.5f,
            DefaultColor = new Vector3(165, 85, 60)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "CONCRETE",
            MaterialName = "mtb_concrete",
            ColorMapFile = "Concrete034_Color.jpg",
            NormalMapFile = "Concrete034_Normal.jpg",
            OrmMapFile = "Concrete034_ORM.jpg",
            TextureScaleU = 4.0f,
            TextureScaleV = 4.0f,
            DefaultColor = new Vector3(170, 170, 165)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD_WALL",
            MaterialName = "mtb_wood",
            ColorMapFile = "WoodSiding008_Color.jpg",
            NormalMapFile = "WoodSiding008_Normal.jpg",
            OrmMapFile = "WoodSiding008_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            DefaultColor = new Vector3(160, 120, 75)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_WALL",
            MaterialName = "mtb_glass",
            ColorMapFile = "Facade005_Color.jpg",
            NormalMapFile = "Facade005_Normal.jpg",
            OrmMapFile = "Facade005_ORM.jpg",
            TextureScaleU = 3.0f,
            TextureScaleV = 3.0f,
            DefaultColor = new Vector3(140, 170, 200)
        });

        // --- Roof Materials ---

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_DEFAULT",
            MaterialName = "mtb_roof_tiles",
            ColorMapFile = "RoofingTiles010_Color.jpg",
            NormalMapFile = "RoofingTiles010_Normal.jpg",
            OrmMapFile = "RoofingTiles010_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_TILES",
            MaterialName = "mtb_roof_tiles",
            ColorMapFile = "RoofingTiles010_Color.jpg",
            NormalMapFile = "RoofingTiles010_Normal.jpg",
            OrmMapFile = "RoofingTiles010_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 55, 40)
        });

        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "ROOF_METAL",
            MaterialName = "mtb_roof_metal",
            ColorMapFile = "MetalPlates006_Color.jpg",
            NormalMapFile = "MetalPlates006_Normal.jpg",
            OrmMapFile = "MetalPlates006_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(120, 125, 130)
        });

        // GLASS_ROOF reuses GLASS_WALL textures
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "GLASS_ROOF",
            MaterialName = "mtb_glass_roof",
            ColorMapFile = "Facade005_Color.jpg",
            NormalMapFile = "Facade005_Normal.jpg",
            OrmMapFile = "Facade005_ORM.jpg",
            TextureScaleU = 3.0f,
            TextureScaleV = 3.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(140, 170, 200)
        });

        // WOOD roof
        Register(new BuildingMaterialDefinition
        {
            MaterialKey = "WOOD",
            MaterialName = "mtb_wood_roof",
            ColorMapFile = "WoodSiding008_Color.jpg",
            NormalMapFile = "WoodSiding008_Normal.jpg",
            OrmMapFile = "WoodSiding008_ORM.jpg",
            TextureScaleU = 2.0f,
            TextureScaleV = 2.0f,
            IsRoofMaterial = true,
            DefaultColor = new Vector3(160, 120, 75)
        });
    }

    private void Register(BuildingMaterialDefinition definition)
    {
        _materials[definition.MaterialKey] = definition;
    }
}
