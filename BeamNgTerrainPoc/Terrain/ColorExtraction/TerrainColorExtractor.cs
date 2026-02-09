using BeamNgTerrainPoc.Terrain.ColorExtraction.Models;
using BeamNgTerrainPoc.Terrain.Logging;

namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
///     Extracts weighted average colors from BeamNG terrain materials.
///     Main entry point for the ColorExtraction feature.
/// </summary>
public static class TerrainColorExtractor
{
    /// <summary>
    ///     Extracts weighted average colors for terrain materials.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to basecolor texture path (PNG)</param>
    /// <returns>
    ///     Dictionary mapping material name to hex color (#RRGGBB). Empty string for materials with no coverage or failed
    ///     extraction.
    /// </returns>
    /// <remarks>
    ///     Materials not found in the terrain file are skipped with a warning.
    ///     Materials with missing texture files are skipped with a warning.
    ///     Materials with 0% coverage return empty string.
    /// </remarks>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static Dictionary<string, string> ExtractColors(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        var summary = ExtractColorsDetailed(terFilePath, materialTextures);
        return summary.Colors;
    }

    /// <summary>
    ///     Extracts weighted average colors for terrain materials using a stream resolver.
    ///     This overload supports .link files and other stream sources by allowing the caller
    ///     to provide a function that resolves texture paths to streams.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTexturePaths">Dictionary mapping material name to texture path (may be .link file)</param>
    /// <param name="streamResolver">Function that resolves a texture path to a Stream, or null if not found</param>
    /// <returns>
    ///     Dictionary mapping material name to hex color (#RRGGBB). Empty string for materials with no coverage or failed
    ///     extraction.
    /// </returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static Dictionary<string, string> ExtractColors(
        string terFilePath,
        Dictionary<string, string> materialTexturePaths,
        Func<string, Stream?> streamResolver)
    {
        var summary = ExtractColorsDetailed(terFilePath, materialTexturePaths, streamResolver);
        return summary.Colors;
    }

    /// <summary>
    ///     Extracts colors with detailed statistics for each material.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to basecolor texture path</param>
    /// <returns>Complete extraction summary with colors and statistics</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static ColorExtractionSummary ExtractColorsDetailed(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        // Use default file-based stream resolver
        return ExtractColorsDetailed(terFilePath, materialTextures, path =>
        {
            if (File.Exists(path))
                return File.OpenRead(path);
            return null;
        });
    }

    /// <summary>
    ///     Extracts colors with detailed statistics for each material using a stream resolver.
    ///     This overload supports .link files and other stream sources.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTexturePaths">Dictionary mapping material name to texture path (may be .link file)</param>
    /// <param name="streamResolver">Function that resolves a texture path to a Stream, or null if not found</param>
    /// <returns>Complete extraction summary with colors and statistics</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    public static ColorExtractionSummary ExtractColorsDetailed(
        string terFilePath,
        Dictionary<string, string> materialTexturePaths,
        Func<string, Stream?> streamResolver)
    {
        if (!File.Exists(terFilePath))
        {
            TerrainLogger.Error($"Terrain file not found: {terFilePath}");
            throw new FileNotFoundException("Terrain file not found", terFilePath);
        }

        TerrainLogger.Info($"Extracting colors from terrain: {Path.GetFileName(terFilePath)}");
        TerrainLogger.Info($"Processing {materialTexturePaths.Count} material textures...");

        // Read terrain data
        var (terrainSize, terrainMaterialNames) = LayerMaskReader.ReadTerrainInfo(terFilePath);
        var masks = LayerMaskReader.ReadLayerMasks(terFilePath);

        var totalPixels = (int)(terrainSize * terrainSize);
        var colors = new Dictionary<string, string>();
        var details = new List<MaterialColorResult>();

        // Create a set of terrain material names for quick lookup
        var terrainMaterialSet = new HashSet<string>(terrainMaterialNames);

        foreach (var (materialName, texturePath) in materialTexturePaths)
        {
            // Check if material exists in terrain
            if (!terrainMaterialSet.Contains(materialName))
            {
                TerrainLogger.Warning($"Material '{materialName}' not found in terrain file");
                continue;
            }

            // Try to get stream for texture
            using var textureStream = streamResolver(texturePath);
            if (textureStream == null)
            {
                TerrainLogger.Warning($"Texture not found for '{materialName}': {texturePath}");
                colors[materialName] = string.Empty;
                details.Add(new MaterialColorResult(materialName, null, 0, 0f));
                continue;
            }

            // Get mask for this material
            if (!masks.TryGetValue(materialName, out var mask))
            {
                TerrainLogger.Warning($"No mask found for material '{materialName}'");
                colors[materialName] = string.Empty;
                details.Add(new MaterialColorResult(materialName, null, 0, 0f));
                continue;
            }

            // Calculate coverage
            var maskedPixels = MaskedColorCalculator.CountMaskedPixels(mask);
            var coveragePercent = (float)maskedPixels / totalPixels * 100f;

            // Calculate dominant color if there's coverage
            string? hexColor = null;
            if (maskedPixels > 0)
                hexColor = MaskedColorCalculator.CalculateDominantColor(textureStream, mask, terrainSize);

            colors[materialName] = hexColor ?? string.Empty;
            details.Add(new MaterialColorResult(materialName, hexColor, maskedPixels, coveragePercent));

            TerrainLogger.Detail($"  {materialName}: {coveragePercent:F1}% coverage -> {hexColor ?? "(no color)"}");
        }

        TerrainLogger.Info($"Color extraction complete. Processed {details.Count} materials.");

        return new ColorExtractionSummary(colors, terrainSize, details);
    }

    /// <summary>
    ///     Extracts colors for all materials found in a terrain file.
    ///     Automatically discovers materials from the .ter file and looks for corresponding
    ///     basecolor textures in the specified texture directory.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="textureDirectory">Directory containing basecolor textures (named {materialName}_b.png)</param>
    /// <param name="textureFilePattern">Pattern for texture file names. Use {0} for material name. Default: "{0}_b.png"</param>
    /// <returns>Dictionary mapping material name to hex color (#RRGGBB)</returns>
    public static Dictionary<string, string> ExtractColorsAutoDiscover(
        string terFilePath,
        string textureDirectory,
        string textureFilePattern = "{0}_b.png")
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
                materialTextures[materialName] = texturePath;
            else
                TerrainLogger.Detail($"Texture not found for material '{materialName}': {texturePath}");
        }

        return ExtractColors(terFilePath, materialTextures);
    }
}