using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp.Objects.Biome;

public enum BiomeLayerKind
{
    TerrainMaterial,
    Osm
}

/// <summary>
/// All Generate Biome UI selections, persisted as MT_Biome/settings.json in the level root
/// (same pattern as MtSettings: explicit property names, null-tolerant Load, defaults hydration).
/// </summary>
public class BiomeSettings
{
    public const string FolderName = "MT_Biome";
    private const string SettingsFileName = "settings.json";

    /// <summary>Brush density used when a brush has no stored value yet (10 % ≈ light forest).</summary>
    public const double DefaultBrushDensityPercent = 10.0;

    /// <summary>
    /// Upper bound of the brush-density slider — denser than 50 % fill is never useful.
    /// Item mix weights are relative shares and run 0–100 %, unaffected by this cap.
    /// </summary>
    public const double MaxSliderPercent = 50.0;

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Base seed; per-layer/zone seeds derive from this — same seed = same forest.</summary>
    [JsonPropertyName("GlobalSeed")]
    public int GlobalSeed { get; set; } = 1337;

    /// <summary>Scales the min-distance rule between placed items (0 disables spacing).</summary>
    [JsonPropertyName("SpacingFactor")]
    public double SpacingFactor { get; set; } = 1.0;

    /// <summary>
    /// Hard pre-flight cap: a layer whose estimated item count exceeds this is not generated.
    /// Guards against runaway configurations (a 2 GB forest4.json has happened).
    /// </summary>
    [JsonPropertyName("MaxItemsPerLayer")]
    public long MaxItemsPerLayer { get; set; } = 1_000_000;

    [JsonPropertyName("MaterialLayers")]
    public List<BiomeLayerSettings> MaterialLayers { get; set; } = new();

    [JsonPropertyName("OsmLayers")]
    public List<BiomeLayerSettings> OsmLayers { get; set; } = new();

    [JsonPropertyName("NegativeList")]
    public BiomeNegativeList NegativeList { get; set; } = new();

    public static string GetFolderPath(string levelRoot) => Path.Join(levelRoot, FolderName);

    public static string GetSettingsPath(string levelRoot) => Path.Join(GetFolderPath(levelRoot), SettingsFileName);

    public static BiomeSettings? Load(string levelRoot)
    {
        try
        {
            var path = GetSettingsPath(levelRoot);
            if (!File.Exists(path))
                return null;

            var settings = JsonSerializer.Deserialize<BiomeSettings>(File.ReadAllText(path), GetSerializerOptions());
            settings?.EnsureDefaults();
            return settings;
        }
        catch
        {
            // Corrupt settings are treated like missing settings — caller rebuilds defaults.
            return null;
        }
    }

    public void Save(string levelRoot)
    {
        EnsureDefaults();
        Directory.CreateDirectory(GetFolderPath(levelRoot));
        File.WriteAllText(GetSettingsPath(levelRoot), JsonSerializer.Serialize(this, GetSerializerOptions()));
    }

    /// <summary>Backfills nulls from older schema versions (MtSettings hydration pattern).</summary>
    public void EnsureDefaults()
    {
        MaterialLayers ??= new List<BiomeLayerSettings>();
        OsmLayers ??= new List<BiomeLayerSettings>();
        NegativeList ??= new BiomeNegativeList();
        NegativeList.MaterialInternalNames ??= new List<string>();
        NegativeList.OsmLayerKeys ??= new List<string>();
        if (NegativeList.BufferMeters < 0)
            NegativeList.BufferMeters = 0;
        if (SpacingFactor < 0)
            SpacingFactor = 0;
        if (MaxItemsPerLayer < 10_000)
            MaxItemsPerLayer = 1_000_000;

        foreach (var layer in MaterialLayers.Concat(OsmLayers))
        {
            layer.Zones ??= new List<BiomeZoneSettings>();
            if (string.IsNullOrWhiteSpace(layer.LayerId))
                layer.LayerId = Guid.NewGuid().ToString("N");
            foreach (var zone in layer.Zones)
            {
                zone.Items ??= new List<BiomeItemSelection>();
                zone.BrushDensityDefaults ??= new Dictionary<string, double>();

                // Brush densities saved before the 50 % cap may carry higher values —
                // clamp them so the slider shows what the generator will actually use.
                // (Mix weights are relative 0–100 shares and stay untouched.)
                foreach (var key in zone.BrushDensityDefaults
                             .Where(kv => kv.Value > MaxSliderPercent)
                             .Select(kv => kv.Key)
                             .ToList())
                    zone.BrushDensityDefaults[key] = MaxSliderPercent;
            }
        }
    }

    private static JsonSerializerOptions GetSerializerOptions()
    {
        var options = BeamJsonOptions.GetJsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public class BiomeLayerSettings
{
    /// <summary>Stable id created when the layer row is added; also names the owned forest file.</summary>
    [JsonPropertyName("LayerId")]
    public string LayerId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("Kind")]
    public BiomeLayerKind Kind { get; set; } = BiomeLayerKind.TerrainMaterial;

    /// <summary>Terrain material internalName (or OSM mask key in Phase 2).</summary>
    [JsonPropertyName("SourceKey")]
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Ordered border → interior.</summary>
    [JsonPropertyName("Zones")]
    public List<BiomeZoneSettings> Zones { get; set; } = new();

    [JsonPropertyName("SeedOverride")]
    public int? SeedOverride { get; set; }
}

public class BiomeZoneSettings
{
    /// <summary>Band thickness in meters from the previous band inward; ignored when IsInterior.</summary>
    [JsonPropertyName("DepthMeters")]
    public double DepthMeters { get; set; } = 5.0;

    /// <summary>Takes all remaining area; only valid as the last zone.</summary>
    [JsonPropertyName("IsInterior")]
    public bool IsInterior { get; set; }

    /// <summary>Empty list is valid — a keep-clear border strip.</summary>
    [JsonPropertyName("Items")]
    public List<BiomeItemSelection> Items { get; set; } = new();

    /// <summary>
    /// Per-brush density (%) — how full the zone gets for this brush's checked items.
    /// The items' <see cref="BiomeItemSelection.MixWeight"/> values split this density as a
    /// normalized species mix, so checking more items never multiplies the total.
    /// Key = brush name ("" for the unbrushed pseudo-group).
    /// </summary>
    [JsonPropertyName("BrushDensityDefaults")]
    public Dictionary<string, double> BrushDensityDefaults { get; set; } = new();

    /// <summary>Optional zone-level slope limits (degrees) applied on top of brush-element limits.</summary>
    [JsonPropertyName("SlopeMinDeg")]
    public double? SlopeMinDeg { get; set; }

    [JsonPropertyName("SlopeMaxDeg")]
    public double? SlopeMaxDeg { get; set; }
}

public class BiomeItemSelection
{
    /// <summary>ForestBrush.name; empty for items not referenced by any brush.</summary>
    [JsonPropertyName("BrushName")]
    public string BrushName { get; set; } = string.Empty;

    /// <summary>managedItemData key — the forest4.json "type".</summary>
    [JsonPropertyName("ItemDataName")]
    public string ItemDataName { get; set; } = string.Empty;

    /// <summary>
    /// Relative species-mix weight 0–100 (0.05 steps); 0 = excluded. Weights are normalized
    /// within the brush, then split the brush's density — they are NOT additive densities.
    /// (JSON name kept as "DensityPercent" for settings written before the semantics change.)
    /// </summary>
    [JsonPropertyName("DensityPercent")]
    public double MixWeight { get; set; } = 50;
}

public class BiomeNegativeList
{
    [JsonPropertyName("MaterialInternalNames")]
    public List<string> MaterialInternalNames { get; set; } = new();

    [JsonPropertyName("OsmLayerKeys")]
    public List<string> OsmLayerKeys { get; set; } = new();

    /// <summary>
    /// Grows the negative mask by this many meters (biome EDT) so items leaning over a
    /// road/parking edge are removed although their trunk pixel is just off the mask.
    /// </summary>
    [JsonPropertyName("BufferMeters")]
    public double BufferMeters { get; set; }

    /// <summary>Dangerous opt-in: cleanup also removes forest items the biome tool did not place.</summary>
    [JsonPropertyName("IncludeForeignItems")]
    public bool IncludeForeignItems { get; set; }

    [JsonIgnore]
    public bool HasEntries => MaterialInternalNames.Count > 0 || OsmLayerKeys.Count > 0;
}
