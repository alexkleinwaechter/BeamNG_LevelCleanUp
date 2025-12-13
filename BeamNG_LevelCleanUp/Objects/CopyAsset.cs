using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp.Objects;

public enum CopyAssetType
{
    Road = 0,
    Decal = 1,
    Dae = 2,

    Terrain = 3
    // GroundCover removed - now copied automatically with Terrain
}

/// <summary>
///     Terrain material roughness presets
/// </summary>
public enum TerrainRoughnessPreset
{
    Custom = 0,
    WetAsphalt = 1, // Very shiny
    Asphalt = 2, // Shiny
    Concrete = 3, // Medium-low roughness
    DirtRoad = 4, // Medium roughness
    Grass = 5, // Medium-high roughness
    Mud = 6, // High roughness
    Forest = 7, // High roughness
    WetSurface = 8, // Low roughness (shiny when wet)
    Rock = 9 // Medium-low roughness
}

public class CopyAsset
{
    public List<MaterialJson> Materials = new();
    public Guid Identifier { get; set; } = Guid.NewGuid();
    public CopyAssetType CopyAssetType { get; set; }
    public string Name { get; set; }
    public string TargetPath { get; set; }
    public string SourceMaterialJsonPath { get; set; }
    public ManagedDecalData DecalData { get; set; }
    public bool Duplicate { get; set; }
    public string DuplicateFrom { get; set; }
    public double SizeMb { get; set; }
    public string DaeFilePath { get; set; }
    public List<MaterialsDae> MaterialsDae { get; set; }
    public string TerrainMaterialName { get; set; }
    public string TerrainMaterialInternalName { get; set; }
    public GroundCover GroundCoverData { get; set; }

    /// <summary>
    ///     Base color for terrain texture generation in hex format (e.g., #808080)
    /// </summary>
    public string BaseColorHex { get; set; } = "#808080";

    /// <summary>
    ///     Roughness preset for terrain material
    /// </summary>
    public TerrainRoughnessPreset RoughnessPreset { get; set; } = TerrainRoughnessPreset.DirtRoad;

    /// <summary>
    ///     Custom roughness value (0-255, where 0 is shiny/black and 255 is rough/white)
    /// </summary>
    public int RoughnessValue { get; set; } = 128;

    /// <summary>
    ///     Target terrain material names to replace (empty/null means "Add" new material)
    /// </summary>
    public List<string> ReplaceTargetMaterialNames { get; set; } = new();

    /// <summary>
    ///     Backward compatibility: Gets/sets the first replacement target
    /// </summary>
    [JsonIgnore]
    public string ReplaceTargetMaterialName
    {
        get => ReplaceTargetMaterialNames?.FirstOrDefault();
        set =>
            ReplaceTargetMaterialNames = !string.IsNullOrEmpty(value)
                ? new List<string> { value }
                : new List<string>();
    }

    /// <summary>
    ///     Returns true if this is in "Replace" mode (has target materials to replace)
    /// </summary>
    [JsonIgnore]
    public bool IsReplaceMode => ReplaceTargetMaterialNames != null && ReplaceTargetMaterialNames.Any();

    /// <summary>
    ///     Gets the roughness value based on preset or custom value
    /// </summary>
    public int GetRoughnessValue()
    {
        return RoughnessPreset switch
        {
            TerrainRoughnessPreset.WetAsphalt => 20,
            TerrainRoughnessPreset.Asphalt => 140,
            TerrainRoughnessPreset.WetSurface => 40,
            TerrainRoughnessPreset.Concrete => 170,
            TerrainRoughnessPreset.Rock => 140,
            TerrainRoughnessPreset.DirtRoad => 200,
            TerrainRoughnessPreset.Grass => 220,
            TerrainRoughnessPreset.Mud => 100,
            TerrainRoughnessPreset.Forest => 230,
            TerrainRoughnessPreset.Custom => RoughnessValue,
            _ => 128 // Default medium roughness
        };
    }

    /// <summary>
    ///     Attempts to detect appropriate roughness preset based on material name
    /// </summary>
    public static TerrainRoughnessPreset DetectRoughnessPresetFromName(string materialName)
    {
        if (string.IsNullOrEmpty(materialName))
            return TerrainRoughnessPreset.DirtRoad;

        // Sanitize and normalize the name
        var normalized = materialName.ToLowerInvariant()
            .Replace("-", "")
            .Replace("_", "")
            .Replace(" ", "");

        // Check for keywords in order of specificity
        if (normalized.Contains("wetasphalt") || normalized.Contains("asphaltw"))
            return TerrainRoughnessPreset.WetAsphalt;

        if (normalized.Contains("asphalt") || normalized.Contains("tarmac"))
            return TerrainRoughnessPreset.Asphalt;

        if (normalized.Contains("wet") || normalized.Contains("rain") || normalized.Contains("water") ||
            normalized.Contains("ive") || normalized.Contains("snow"))
            return TerrainRoughnessPreset.WetSurface;

        if (normalized.Contains("concrete") || normalized.Contains("cement") || normalized.Contains("pavement"))
            return TerrainRoughnessPreset.Concrete;

        if (normalized.Contains("rock") || normalized.Contains("stone") || normalized.Contains("boulder") ||
            normalized.Contains("cliff"))
            return TerrainRoughnessPreset.Rock;

        if (normalized.Contains("grass") || normalized.Contains("lawn") || normalized.Contains("field"))
            return TerrainRoughnessPreset.Grass;

        if (normalized.Contains("forest") || normalized.Contains("wood") || normalized.Contains("tree"))
            return TerrainRoughnessPreset.Forest;

        if (normalized.Contains("mud") || normalized.Contains("clay") || normalized.Contains("swamp"))
            return TerrainRoughnessPreset.Mud;

        if (normalized.Contains("dirt") || normalized.Contains("soil") || normalized.Contains("ground") ||
            normalized.Contains("sand"))
            return TerrainRoughnessPreset.DirtRoad;

        // Default to DirtRoad if nothing matches
        return TerrainRoughnessPreset.DirtRoad;
    }
}