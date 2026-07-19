using System.Text.Json.Serialization;

namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// The delete ledger for generated biome placements (persisted as MT_Biome/manifest.json).
/// The manifest itself is a small header; per-item identity records are streamed to one
/// NDJSON sidecar per layer (MT_Biome/items/{layerId}.jsonl) and only read back for the
/// fallback delete path. <see cref="BiomeManifestLayer.Items"/> stays for legacy manifests
/// that embedded the records inline.
/// </summary>
public sealed class BiomeManifest
{
    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>.ter last-write UTC (round-trip "o" format) at the last generation — staleness banner input.</summary>
    [JsonPropertyName("TerFileTimestampUtc")]
    public string? TerFileTimestampUtc { get; set; }

    [JsonPropertyName("Layers")]
    public List<BiomeManifestLayer> Layers { get; set; } = new();
}

public sealed class BiomeManifestLayer
{
    [JsonPropertyName("LayerId")]
    public string LayerId { get; set; } = string.Empty;

    /// <summary>"TerrainMaterial" or "Osm".</summary>
    [JsonPropertyName("Kind")]
    public string Kind { get; set; } = "TerrainMaterial";

    /// <summary>Material internalName or OSM mask key.</summary>
    [JsonPropertyName("SourceKey")]
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Level-relative forward-slash path of the owned forest file (e.g. "forest/MT_biome_{id}.forest4.json").</summary>
    [JsonPropertyName("ForestFile")]
    public string ForestFile { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the owned file as written — fast-path delete check.</summary>
    [JsonPropertyName("FileSha256")]
    public string FileSha256 { get; set; } = string.Empty;

    [JsonPropertyName("GeneratedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("SeedUsed")]
    public ulong SeedUsed { get; set; }

    [JsonPropertyName("ItemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("Items")]
    public List<BiomeManifestItem> Items { get; set; } = new();
}

/// <summary>
/// Identity record of one placed item. Rotation is deliberately absent — type, position
/// (ε-tolerant) and scale are enough to identify an item even after the in-game editor
/// re-serialized forest files with different float formatting.
/// </summary>
public sealed class BiomeManifestItem
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("Pos")]
    public double[] Pos { get; set; } = new double[3];

    [JsonPropertyName("Scale")]
    public double Scale { get; set; } = 1.0;
}
