using BeamNgTerrainPoc.Terrain.ColorExtraction.Models;
using BeamNgTerrainPoc.Terrain.Logging;

namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Extracts dominant roughness values from BeamNG terrain materials.
/// Main entry point for roughness extraction from terrain textures.
/// </summary>
public static class TerrainRoughnessExtractor
{
    /// <summary>
    /// Extracts dominant roughness values for terrain materials.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to roughnessBaseTex texture path (PNG)</param>
    /// <returns>Dictionary mapping material name to roughness value (0-255). -1 for materials with no coverage or failed extraction.</returns>
    /// <remarks>
    /// Materials not found in the terrain file are skipped with a warning.
    /// Materials with missing texture files are skipped with a warning.
    /// Materials with 0% coverage return -1.
    /// 
    /// Roughness values: 0 = very shiny/smooth, 255 = very rough/matte
    /// </remarks>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static Dictionary<string, int> ExtractRoughness(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        var summary = ExtractRoughnessDetailed(terFilePath, materialTextures);
        return summary.RoughnessValues;
    }

    /// <summary>
    /// Extracts roughness with detailed statistics for each material.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to roughnessBaseTex texture path</param>
    /// <returns>Complete extraction summary with roughness values and statistics</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static RoughnessExtractionSummary ExtractRoughnessDetailed(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        if (!File.Exists(terFilePath))
        {
            TerrainLogger.Error($"Terrain file not found: {terFilePath}");
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        TerrainLogger.Info($"Extracting roughness from terrain: {Path.GetFileName(terFilePath)}");
        TerrainLogger.Info($"Processing {materialTextures.Count} material textures...");

        // Read terrain data
        var (terrainSize, terrainMaterialNames) = LayerMaskReader.ReadTerrainInfo(terFilePath);
        var masks = LayerMaskReader.ReadLayerMasks(terFilePath);

        int totalPixels = (int)(terrainSize * terrainSize);
        var roughnessValues = new Dictionary<string, int>();
        var details = new List<MaterialRoughnessResult>();

        // Create a set of terrain material names for quick lookup
        var terrainMaterialSet = new HashSet<string>(terrainMaterialNames);

        foreach (var (materialName, texturePath) in materialTextures)
        {
            // Check if material exists in terrain
            if (!terrainMaterialSet.Contains(materialName))
            {
                TerrainLogger.Warning($"Material '{materialName}' not found in terrain file");
                continue;
            }

            // Check if texture file exists
            if (!File.Exists(texturePath))
            {
                TerrainLogger.Warning($"Roughness texture not found for '{materialName}': {texturePath}");
                roughnessValues[materialName] = -1;
                details.Add(new MaterialRoughnessResult(materialName, null, 0, 0f));
                continue;
            }

            // Get mask for this material
            if (!masks.TryGetValue(materialName, out var mask))
            {
                TerrainLogger.Warning($"No mask found for material '{materialName}'");
                roughnessValues[materialName] = -1;
                details.Add(new MaterialRoughnessResult(materialName, null, 0, 0f));
                continue;
            }

            // Calculate coverage
            int maskedPixels = MaskedColorCalculator.CountMaskedPixels(mask);
            float coveragePercent = (float)maskedPixels / totalPixels * 100f;

            // Calculate dominant roughness if there's coverage
            int? roughness = null;
            if (maskedPixels > 0)
            {
                roughness = MaskedRoughnessCalculator.CalculateDominantRoughness(texturePath, mask, terrainSize);
            }

            roughnessValues[materialName] = roughness ?? -1;
            details.Add(new MaterialRoughnessResult(materialName, roughness, maskedPixels, coveragePercent));

            TerrainLogger.Detail($"  {materialName}: {coveragePercent:F1}% coverage -> roughness {roughness?.ToString() ?? "(none)"}");
        }

        TerrainLogger.Info($"Roughness extraction complete. Processed {details.Count} materials.");

        return new RoughnessExtractionSummary(roughnessValues, terrainSize, details);
    }

    /// <summary>
    /// Extracts roughness for all materials found in a terrain file.
    /// Automatically discovers materials from the .ter file and looks for corresponding
    /// roughness textures in the specified texture directory.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="textureDirectory">Directory containing roughness textures (named {materialName}_r.png)</param>
    /// <param name="textureFilePattern">Pattern for texture file names. Use {0} for material name. Default: "{0}_r.png"</param>
    /// <returns>Dictionary mapping material name to roughness value (0-255)</returns>
    public static Dictionary<string, int> ExtractRoughnessAutoDiscover(
        string terFilePath,
        string textureDirectory,
        string textureFilePattern = "{0}_r.png")
    {
        if (!File.Exists(terFilePath))
        {
            TerrainLogger.Error($"Terrain file not found: {terFilePath}");
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        if (!Directory.Exists(textureDirectory))
        {
            TerrainLogger.Error($"Texture directory not found: {textureDirectory}");
            throw new DirectoryNotFoundException($"Texture directory not found: {textureDirectory}");
        }

        // Read material names from terrain
        var (_, materialNames) = LayerMaskReader.ReadTerrainInfo(terFilePath);

        // Build material textures dictionary
        var materialTextures = new Dictionary<string, string>();
        foreach (var materialName in materialNames)
        {
            var textureFileName = string.Format(textureFilePattern, materialName);
            var texturePath = Path.Combine(textureDirectory, textureFileName);
            
            if (File.Exists(texturePath))
            {
                materialTextures[materialName] = texturePath;
            }
            else
            {
                TerrainLogger.Detail($"Roughness texture not found for material '{materialName}': {texturePath}");
            }
        }

        return ExtractRoughness(terFilePath, materialTextures);
    }
}
