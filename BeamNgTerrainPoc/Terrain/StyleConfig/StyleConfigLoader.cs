using System.Numerics;
using System.Text.Json;
using BeamNG.Procedural3D.Building;

namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// Loads osm2world-style.json and converts BeamNG-enhanced materials to
/// <see cref="BuildingMaterialDefinition"/> objects for use by BuildingMaterialLibrary.
///
/// Load priority:
///   1. JSON config file (if exists) — user-editable
///   2. Hard-coded defaults in BuildingMaterialLibrary (fallback)
/// </summary>
public static class StyleConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Attempts to load the main style config from the given path.
    /// Returns null if the file doesn't exist or fails to parse.
    /// </summary>
    public static Osm2WorldStyleConfig? LoadMainConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return null;

            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<Osm2WorldStyleConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"StyleConfigLoader: Failed to load {configPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads all BeamNG-enhanced materials from the config and returns them as
    /// <see cref="BuildingMaterialDefinition"/> objects. Only materials that have
    /// a "beamng" extension are returned.
    /// </summary>
    public static List<BuildingMaterialDefinition> LoadBuildingMaterials(string configPath)
    {
        var config = LoadMainConfig(configPath);
        if (config == null)
            return [];

        var result = new List<BuildingMaterialDefinition>();
        foreach (var (key, mat) in config.Materials)
        {
            var def = ToMaterialDefinition(key, mat);
            if (def != null)
                result.Add(def);
        }
        return result;
    }

    /// <summary>
    /// Converts a <see cref="StyleMaterial"/> with BeamNG extension into a
    /// <see cref="BuildingMaterialDefinition"/>.
    /// Returns null if the material has no BeamNG extension.
    /// </summary>
    public static BuildingMaterialDefinition? ToMaterialDefinition(string materialKey, StyleMaterial styleMat)
    {
        if (styleMat.Beamng == null)
            return null;

        var ext = styleMat.Beamng;

        // Derive TextureFolder from first texture layer's dir path
        // "./textures/cc0textures/Bricks029" → "Bricks029"
        // "./textures/custom/Window" → "Window"
        string? textureFolder = null;
        if (styleMat.TextureLayers.Count > 0 && styleMat.TextureLayers[0].Dir != null)
        {
            textureFolder = Path.GetFileName(styleMat.TextureLayers[0].Dir.TrimEnd('/'));
        }
        else if (ext.ColorMapFile != null)
        {
            // Fallback: derive TextureFolder from ColorMapFile naming convention.
            // Materials not in standard.properties (e.g., ROOF_TILES) have empty TextureLayers
            // but still need a TextureFolder for cc0textures/custom texture resolution.
            // Convention: "{FolderName}_Color.color.png" → "{FolderName}"
            textureFolder = DeriveTextureFolderFromColorMap(ext.ColorMapFile);
        }

        // Derive TextureScaleU/V from first texture layer's width/height
        float scaleU = styleMat.TextureLayers.FirstOrDefault()?.Width ?? 3.0f;
        float scaleV = styleMat.TextureLayers.FirstOrDefault()?.Height ?? 3.0f;

        return new BuildingMaterialDefinition
        {
            MaterialKey = materialKey,
            MaterialName = ext.MaterialName,
            TextureFolder = textureFolder,
            ColorMapFile = ext.ColorMapFile,
            NormalMapFile = ext.NormalMapFile,
            OrmMapFile = ext.OrmMapFile,
            AoMapFile = ext.AoMapFile,
            RoughnessMapFile = ext.RoughnessMapFile,
            MetallicMapFile = ext.MetallicMapFile,
            TextureScaleU = scaleU,
            TextureScaleV = scaleV,
            IsRoofMaterial = ext.IsRoofMaterial,
            DefaultColor = new Vector3(
                ext.DefaultColor[0], ext.DefaultColor[1], ext.DefaultColor[2]),
            Opacity = ext.Opacity,
            DoubleSided = ext.DoubleSided,
            InstanceDiffuse = ext.InstanceDiffuse,
        };
    }

    /// <summary>
    /// Derives a TextureFolder from a ColorMapFile following the cc0textures/custom naming convention.
    /// E.g., "RoofingTiles010_Color.color.png" → "RoofingTiles010"
    ///       "Window_Color.color.png" → "Window"
    /// Returns null for single-file textures (e.g., "DE19F1FreisingDoor00005_small.color.png")
    /// and placeholder textures (e.g., "mtb_window_frame_Color.color.png").
    /// </summary>
    private static string? DeriveTextureFolderFromColorMap(string colorMapFile)
    {
        const string suffix = "_Color.color.png";
        if (!colorMapFile.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var folderName = colorMapFile[..^suffix.Length];

        // Skip our solid-color placeholder naming convention (mtb_*)
        if (folderName.StartsWith("mtb_", StringComparison.OrdinalIgnoreCase))
            return null;

        return folderName;
    }
}
