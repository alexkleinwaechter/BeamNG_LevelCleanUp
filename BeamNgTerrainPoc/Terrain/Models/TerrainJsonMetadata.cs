using System.Text.Json.Serialization;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Represents the terrain.json metadata file that accompanies a .ter terrain file.
/// This file is required by BeamNG.drive to load the terrain properly.
/// </summary>
public class TerrainJsonMetadata
{
    /// <summary>
    /// Description of the binary format used in the .ter file.
    /// </summary>
    [JsonPropertyName("binaryFormat")]
    public string BinaryFormat { get; set; } = "version(char), size(unsigned int), heightMap(heightMapSize * heightMapItemSize), layerMap(layerMapSize * layerMapItemSize), layerTextureMap(layerMapSize * layerMapItemSize), materialNames";

    /// <summary>
    /// Path to the .ter data file in BeamNG path format (e.g., "/levels/levelname/theTerrain.ter")
    /// </summary>
    [JsonPropertyName("datafile")]
    public string DataFile { get; set; } = string.Empty;

    /// <summary>
    /// Size in bytes of each heightmap item (2 bytes for 16-bit height values)
    /// </summary>
    [JsonPropertyName("heightMapItemSize")]
    public int HeightMapItemSize { get; set; } = 2;

    /// <summary>
    /// Total number of heightmap values (size * size)
    /// </summary>
    [JsonPropertyName("heightMapSize")]
    public long HeightMapSize { get; set; }

    /// <summary>
    /// Path to the heightmap preview image in BeamNG path format
    /// </summary>
    [JsonPropertyName("heightmapImage")]
    public string HeightmapImage { get; set; } = string.Empty;

    /// <summary>
    /// Size in bytes of each layer map item (1 byte per material index)
    /// </summary>
    [JsonPropertyName("layerMapItemSize")]
    public int LayerMapItemSize { get; set; } = 1;

    /// <summary>
    /// Total number of layer map values (size * size)
    /// </summary>
    [JsonPropertyName("layerMapSize")]
    public long LayerMapSize { get; set; }

    /// <summary>
    /// List of material names in order matching their indices in the terrain file
    /// </summary>
    [JsonPropertyName("materials")]
    public List<string> Materials { get; set; } = new();

    /// <summary>
    /// Terrain size (width and height in pixels, must be power of 2)
    /// </summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>
    /// Version of the terrain file format (currently 9)
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 9;

    /// <summary>
    /// Creates a TerrainJsonMetadata instance from terrain creation parameters.
    /// </summary>
    /// <param name="parameters">The terrain creation parameters</param>
    /// <param name="levelName">The name of the level (used for path construction)</param>
    /// <returns>A configured TerrainJsonMetadata instance</returns>
    public static TerrainJsonMetadata FromParameters(TerrainCreationParameters parameters, string levelName)
    {
        var terrainName = parameters.TerrainName;
        long pixelCount = (long)parameters.Size * parameters.Size;

        return new TerrainJsonMetadata
        {
            Size = parameters.Size,
            HeightMapSize = pixelCount,
            LayerMapSize = pixelCount,
            DataFile = $"/levels/{levelName}/{terrainName}.ter",
            HeightmapImage = $"/levels/{levelName}/{terrainName}.terrainheightmap.png",
            Materials = parameters.Materials.Select(m => m.MaterialName).ToList(),
            Version = 9
        };
    }

    /// <summary>
    /// Creates a TerrainJsonMetadata instance with custom paths.
    /// </summary>
    /// <param name="size">Terrain size in pixels</param>
    /// <param name="materialNames">List of material names in order</param>
    /// <param name="dataFilePath">BeamNG path to the .ter file</param>
    /// <param name="heightmapImagePath">BeamNG path to the heightmap preview image</param>
    /// <returns>A configured TerrainJsonMetadata instance</returns>
    public static TerrainJsonMetadata Create(
        int size,
        IEnumerable<string> materialNames,
        string dataFilePath,
        string heightmapImagePath)
    {
        long pixelCount = (long)size * size;

        return new TerrainJsonMetadata
        {
            Size = size,
            HeightMapSize = pixelCount,
            LayerMapSize = pixelCount,
            DataFile = dataFilePath,
            HeightmapImage = heightmapImagePath,
            Materials = materialNames.ToList(),
            Version = 9
        };
    }
}
