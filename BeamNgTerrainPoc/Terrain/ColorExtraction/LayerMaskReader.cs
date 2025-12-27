using Grille.BeamNG.IO.Binary;

namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Reads BeamNG terrain (.ter) files and extracts layer masks for each material.
/// </summary>
public static class LayerMaskReader
{
    /// <summary>
    /// Material index value indicating a terrain hole.
    /// </summary>
    private const byte HoleMaterialIndex = 255;

    /// <summary>
    /// Reads layer masks from a terrain .ter file.
    /// Each material gets a boolean mask where true = pixel belongs to this material.
    /// </summary>
    /// <param name="terFilePath">Absolute path to the .ter file</param>
    /// <returns>Dictionary mapping material name to its layer mask (bool array, row-major, Size*Size length)</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    /// <exception cref="InvalidDataException">If .ter file is invalid or version is not 9</exception>
    public static Dictionary<string, bool[]> ReadLayerMasks(string terFilePath)
    {
        if (!File.Exists(terFilePath))
        {
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        TerrainV9Binary binary;
        using (var stream = File.OpenRead(terFilePath))
        {
            binary = TerrainV9Serializer.Deserialize(stream);
        }

        var masks = new Dictionary<string, bool[]>();
        var materialData = binary.MaterialData;
        var materialNames = binary.MaterialNames;

        // Create a mask for each material
        for (int matIndex = 0; matIndex < materialNames.Length; matIndex++)
        {
            var materialName = materialNames[matIndex];
            var mask = new bool[materialData.Length];

            for (int i = 0; i < materialData.Length; i++)
            {
                // Skip holes (material index 255)
                if (materialData[i] == HoleMaterialIndex)
                {
                    mask[i] = false;
                    continue;
                }

                mask[i] = materialData[i] == matIndex;
            }

            masks[materialName] = mask;
        }

        return masks;
    }

    /// <summary>
    /// Gets terrain metadata without loading full mask data.
    /// </summary>
    /// <param name="terFilePath">Path to the .ter file</param>
    /// <returns>Tuple of (terrain size, material names array)</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    /// <exception cref="InvalidDataException">If .ter file is invalid or version is not 9</exception>
    public static (uint Size, string[] MaterialNames) ReadTerrainInfo(string terFilePath)
    {
        if (!File.Exists(terFilePath))
        {
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        TerrainV9Binary binary;
        using (var stream = File.OpenRead(terFilePath))
        {
            binary = TerrainV9Serializer.Deserialize(stream);
        }

        return (binary.Size, binary.MaterialNames);
    }

    /// <summary>
    /// Reads the complete terrain binary data from a .ter file.
    /// </summary>
    /// <param name="terFilePath">Path to the .ter file</param>
    /// <returns>The deserialized terrain binary data</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    /// <exception cref="InvalidDataException">If .ter file is invalid or version is not 9</exception>
    public static TerrainV9Binary ReadTerrainBinary(string terFilePath)
    {
        if (!File.Exists(terFilePath))
        {
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        using var stream = File.OpenRead(terFilePath);
        return TerrainV9Serializer.Deserialize(stream);
    }
}
