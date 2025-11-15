using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Shared helper class for terrain texture generation and copying
///     Used by both TerrainMaterialCopier and TerrainMaterialReplacer
/// </summary>
public static class TerrainTextureHelper
{
    /// <summary>
    ///     Reads the terrain size from the *.terrain.json file
    /// </summary>
    public static int? GetTerrainSizeFromJson(string levelPath)
    {
        try
        {
            // Find the terrain.json file in the level directory
            var terrainFiles = Directory.GetFiles(levelPath, "*.terrain.json", SearchOption.AllDirectories);

            if (terrainFiles.Length == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"No *.terrain.json file found in {levelPath}.");
                return null;
            }

            var terrainFile = terrainFiles[0];
            using var jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(terrainFile);

            if (jsonDoc.RootElement.TryGetProperty("size", out var sizeElement))
            {
                var size = sizeElement.GetInt32();
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain size from {Path.GetFileName(terrainFile)}: {size}x{size}");
                return size;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"No 'size' property found in {terrainFile}.");
            return null;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error reading terrain size: {ex.Message}.");
            return null;
        }
    }

    /// <summary>
    ///     Reads the base material texture size from the *.materials.json file
    ///     Extracts the first value from the baseTexSize array in TerrainMaterialTextureSet
    /// </summary>
    public static int? GetBaseMaterialSize(string levelPath)
    {
        var sizes = GetAllTextureSizes(levelPath);
        return sizes?.BaseTexSize;
    }

    /// <summary>
    ///     Reads all texture sizes (base, detail, macro) from TerrainMaterialTextureSet in materials.json
    ///     Returns null if no TerrainMaterialTextureSet is found
    /// </summary>
    public static TerrainTextureSizes? GetAllTextureSizes(string levelPath)
    {
        try
        {
            var terrainPath = Path.Join(levelPath, "art", "terrains");

            if (!Directory.Exists(terrainPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No terrains folder found at {terrainPath}.");
                return null;
            }

            var materialFiles = Directory.GetFiles(terrainPath, "*.materials.json", SearchOption.AllDirectories);

            if (materialFiles.Length == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No *.materials.json file found in {terrainPath}.");
                return null;
            }

            var materialFile = materialFiles[0];
            using var jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(materialFile);

            // Find the object with class "TerrainMaterialTextureSet"
            foreach (var property in jsonDoc.RootElement.EnumerateObject())
                if (property.Value.TryGetProperty("class", out var classElement) &&
                    classElement.GetString() == "TerrainMaterialTextureSet")
                {
                    var sizes = new TerrainTextureSizes();

                    // Extract baseTexSize
                    if (property.Value.TryGetProperty("baseTexSize", out var baseTexSizeElement) &&
                        baseTexSizeElement.ValueKind == JsonValueKind.Array)
                    {
                        var enumerator = baseTexSizeElement.EnumerateArray();
                        if (enumerator.MoveNext())
                            sizes.BaseTexSize = enumerator.Current.GetInt32();
                    }

                    // Extract detailTexSize
                    if (property.Value.TryGetProperty("detailTexSize", out var detailTexSizeElement) &&
                        detailTexSizeElement.ValueKind == JsonValueKind.Array)
                    {
                        var enumerator = detailTexSizeElement.EnumerateArray();
                        if (enumerator.MoveNext())
                            sizes.DetailTexSize = enumerator.Current.GetInt32();
                    }

                    // Extract macroTexSize
                    if (property.Value.TryGetProperty("macroTexSize", out var macroTexSizeElement) &&
                        macroTexSizeElement.ValueKind == JsonValueKind.Array)
                    {
                        var enumerator = macroTexSizeElement.EnumerateArray();
                        if (enumerator.MoveNext())
                            sizes.MacroTexSize = enumerator.Current.GetInt32();
                    }

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Texture sizes from {Path.GetFileName(materialFile)} ({property.Name}): " +
                        $"base={sizes.BaseTexSize}, detail={sizes.DetailTexSize}, macro={sizes.MacroTexSize}");

                    return sizes;
                }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"No TerrainMaterialTextureSet found in {materialFile}.");
            return null;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error reading texture sizes: {ex.Message}.");
            return null;
        }
    }

    /// <summary>
    ///     Loads basetexture size
    /// </summary>
    public static int LoadBaseTextureSize(string targetLevelPath)
    {
        if (!string.IsNullOrEmpty(targetLevelPath))
        {
            var size = GetBaseMaterialSize(targetLevelPath) ?? GetTerrainSizeFromJson(targetLevelPath) ?? 2048;
            return size;
        }

        return 2048;
    }

    /// <summary>
    ///     Copies terrain textures with generation of placeholder textures for base/roughness/normal maps
    /// </summary>
    public static void CopyTerrainTextures(
        MaterialJson material,
        JsonNode materialObj,
        string targetTerrainFolder,
        string baseColorHex,
        int roughnessValue,
        int? terrainSize,
        PathConverter pathConverter,
        FileCopyHandler fileCopyHandler,
        string levelNameCopyFrom)
    {
        TerrainTextureGenerator? textureGenerator = null;

        // Initialize texture generator if we have terrain size
        if (terrainSize.HasValue)
            textureGenerator = new TerrainTextureGenerator(targetTerrainFolder, terrainSize.Value);

        // OPTIMIZATION: Collect all path replacements first, then do a single pass through JSON
        var pathReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var matFile in material.MaterialFiles)
        {
            var originalPath = matFile.OriginalJsonPath;
            string newPath;

            // Check if this is a texture that should be replaced with generated dummy
            if (textureGenerator != null &&
                TerrainTextureGenerator.IsReplaceableTexture(matFile.MapType))
            {
                var textureProps = TerrainTextureGenerator.GetTextureProperties(matFile.MapType);
                if (textureProps != null)
                {
                    // Determine color and custom value based on texture type
                    var colorToUse = textureProps.HexColor;
                    int? customValue = null;

                    // Use custom color for baseColorBaseTex
                    if (matFile.MapType.Equals("baseColorBaseTex", StringComparison.OrdinalIgnoreCase))
                        colorToUse = baseColorHex;
                    // Use custom roughness value for roughnessBaseTex
                    else if (matFile.MapType.Equals("roughnessBaseTex", StringComparison.OrdinalIgnoreCase))
                        customValue = roughnessValue;

                    // Generate replacement PNG with level name suffix
                    var baseFileName = Path.GetFileNameWithoutExtension(textureProps.FileName);

                    var generatedPngPath = textureGenerator.GenerateSolidColorPng(
                        colorToUse,
                        baseFileName,
                        textureProps.Type,
                        $"_{levelNameCopyFrom}",
                        customValue);

                    // Prepare the new path for batch update
                    newPath = pathConverter.GetBeamNgJsonPathOrFileName(generatedPngPath, false);

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Replaced {matFile.MapType} with generated texture: {Path.GetFileName(generatedPngPath)}");

                    // Also update the corresponding size property if it exists
                    var sizePropertyName = matFile.MapType + "Size";
                    if (materialObj[sizePropertyName] != null)
                        materialObj[sizePropertyName] = terrainSize.Value;

                    // Add to batch replacements
                    pathReplacements[originalPath] = newPath;
                    continue; // Skip normal file copy for this texture
                }
            }

            // Normal texture copy for all other textures
            var targetFullName = pathConverter.GetTerrainTargetFileName(matFile.File.FullName);
            if (string.IsNullOrEmpty(targetFullName)) continue;

            // Add suffix to the filename
            var targetDirectory = Path.GetDirectoryName(targetFullName);
            var targetFileName = Path.GetFileNameWithoutExtension(targetFullName);
            var targetExtension = Path.GetExtension(targetFullName);
            var suffixedTargetFileName = $"{targetFileName}_{levelNameCopyFrom}{targetExtension}";
            var suffixedTargetFullName = Path.Join(targetDirectory, suffixedTargetFileName);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(suffixedTargetFullName));
                fileCopyHandler.CopyFile(matFile.File.FullName, suffixedTargetFullName);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Filepath error for terrain texture {material.Name}. Exception:{ex.Message}");
            }

            // Prepare the new path for batch update
            newPath = pathConverter.GetBeamNgJsonPathOrFileName(suffixedTargetFullName, false);

            // Add to batch replacements
            pathReplacements[originalPath] = newPath;
        }

        // OPTIMIZATION: Single pass through the material JSON to update all paths at once
        // This reduces method calls from ~20,000 to ~150 for typical scenarios
        if (pathReplacements.Any()) UpdateTexturePathsInMaterialBatch(materialObj, pathReplacements);
    }

    /// <summary>
    ///     Batch version: Updates multiple texture paths in a single pass through the material JSON
    ///     This is significantly faster than calling UpdateTexturePathsInMaterial once per texture
    /// </summary>
    private static void UpdateTexturePathsInMaterialBatch(JsonNode materialNode,
        Dictionary<string, string> pathReplacements)
    {
        if (materialNode is JsonObject obj)
            foreach (var prop in obj.ToList())
            {
                if (prop.Value == null) continue;

                if (prop.Value is JsonValue jsonValue)
                {
                    // Avoid exception-based control flow by using TryGetValue
                    if (jsonValue.TryGetValue<string>(out var strValue) &&
                        !string.IsNullOrEmpty(strValue) &&
                        pathReplacements.TryGetValue(strValue, out var newPath))
                        obj[prop.Key] = newPath;
                }
                else
                {
                    // Recurse into nested objects/arrays
                    UpdateTexturePathsInMaterialBatch(prop.Value, pathReplacements);
                }
            }
        else if (materialNode is JsonArray arr)
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] == null) continue;

                if (arr[i] is JsonValue jsonValue)
                {
                    // Avoid exception-based control flow by using TryGetValue
                    if (jsonValue.TryGetValue<string>(out var strValue) &&
                        !string.IsNullOrEmpty(strValue) &&
                        pathReplacements.TryGetValue(strValue, out var newPath))
                        arr[i] = JsonValue.Create(newPath);
                }
                else
                {
                    // Recurse into nested objects/arrays
                    UpdateTexturePathsInMaterialBatch(arr[i], pathReplacements);
                }
            }
    }

    /// <summary>
    ///     Recursively updates texture paths in material JSON (single path at a time)
    ///     Note: This is kept for backward compatibility, but CopyTerrainTextures now uses the batch version
    /// </summary>
    public static void UpdateTexturePathsInMaterial(JsonNode materialNode, string oldPath, string newPath)
    {
        if (materialNode is JsonObject obj)
            foreach (var prop in obj.ToList())
            {
                if (prop.Value == null) continue;

                if (prop.Value is JsonValue jsonValue)
                {
                    // Avoid exception-based control flow by using TryGetValue
                    if (jsonValue.TryGetValue<string>(out var strValue) &&
                        !string.IsNullOrEmpty(strValue) &&
                        strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                        obj[prop.Key] = newPath;
                }
                else
                {
                    UpdateTexturePathsInMaterial(prop.Value, oldPath, newPath);
                }
            }
        else if (materialNode is JsonArray arr)
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] == null) continue;

                if (arr[i] is JsonValue jsonValue)
                {
                    // Avoid exception-based control flow by using TryGetValue
                    if (jsonValue.TryGetValue<string>(out var strValue) &&
                        !string.IsNullOrEmpty(strValue) &&
                        strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                        arr[i] = JsonValue.Create(newPath);
                }
                else
                {
                    UpdateTexturePathsInMaterial(arr[i], oldPath, newPath);
                }
            }
    }

    /// <summary>
    ///     Container for terrain texture sizes
    /// </summary>
    public class TerrainTextureSizes
    {
        public int BaseTexSize { get; set; } = 1024;
        public int DetailTexSize { get; set; } = 1024;
        public int MacroTexSize { get; set; } = 1024;
    }
}