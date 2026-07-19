using System.Security.Cryptography;
using System.Text.Json;
using BeamNG_LevelCleanUp.Objects.Biome;
using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNG_LevelCleanUp.LogicBiome;

/// <summary>
/// Loads/saves MT_Biome/manifest.json — the ledger of every item the biome generator placed.
///
/// The manifest file itself is a small header (layers with counts/hashes/stamps). The
/// per-item identity records live in one NDJSON sidecar per layer under MT_Biome/items/,
/// written streamingly at generation time and read back ONLY for the fallback delete path —
/// so neither page load nor generation ever holds hundreds of thousands of records in memory.
/// </summary>
public static class BiomeManifestStore
{
    private const string ManifestFileName = "manifest.json";
    private const string ItemsFolderName = "items";

    public static string GetPath(string levelRoot) =>
        Path.Join(BiomeSettings.GetFolderPath(levelRoot), ManifestFileName);

    public static string GetLayerItemsPath(string levelRoot, string layerId) =>
        Path.Join(BiomeSettings.GetFolderPath(levelRoot), ItemsFolderName, $"{layerId}.jsonl");

    public static BiomeManifest Load(string levelRoot)
    {
        try
        {
            var path = GetPath(levelRoot);
            if (!File.Exists(path))
                return new BiomeManifest();

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<BiomeManifest>(stream, GetSerializerOptions())
                   ?? new BiomeManifest();
        }
        catch
        {
            // A corrupt manifest must not brick the page; deletes fall back to the
            // line-matching path which handles missing records gracefully (0 matches).
            return new BiomeManifest();
        }
    }

    public static void Save(string levelRoot, BiomeManifest manifest)
    {
        Directory.CreateDirectory(BiomeSettings.GetFolderPath(levelRoot));
        using var stream = File.Create(GetPath(levelRoot));
        JsonSerializer.Serialize(stream, manifest, GetSerializerOptions());
    }

    /// <summary>
    /// Identity records of a layer, for the fallback delete: the NDJSON sidecar when it
    /// exists, else records embedded in the manifest (legacy format from before sidecars).
    /// </summary>
    public static List<BiomeManifestItem> LoadLayerItems(string levelRoot, BiomeManifestLayer layer)
    {
        var sidecarPath = GetLayerItemsPath(levelRoot, layer.LayerId);
        if (!File.Exists(sidecarPath))
            return layer.Items ?? new List<BiomeManifestItem>();

        var options = GetSerializerOptions();
        var items = new List<BiomeManifestItem>(Math.Max(layer.ItemCount, 16));
        foreach (var line in File.ReadLines(sidecarPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var item = JsonSerializer.Deserialize<BiomeManifestItem>(line, options);
                if (item != null)
                    items.Add(item);
            }
            catch (JsonException)
            {
                // skip malformed record lines
            }
        }
        return items;
    }

    /// <summary>
    /// Rewrites a layer's identity-record sidecar (used after the negative-list cleanup
    /// removed some of the layer's items; generation streams records directly instead).
    /// </summary>
    public static void SaveLayerItems(string levelRoot, string layerId, IReadOnlyList<BiomeManifestItem> items)
    {
        var path = GetLayerItemsPath(levelRoot, layerId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = GetSerializerOptions();
        using var writer = new StreamWriter(File.Create(path));
        foreach (var item in items)
        {
            writer.WriteLine(JsonSerializer.Serialize(item, options));
        }
    }

    public static void DeleteLayerItemsSidecar(string levelRoot, string layerId)
    {
        var path = GetLayerItemsPath(levelRoot, layerId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static JsonSerializerOptions GetSerializerOptions()
    {
        return BeamJsonOptions.GetJsonSerializerOneLineOptions();
    }
}
