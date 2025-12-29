using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

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
    ///     Finds the terrain materials.json file dynamically by searching for *.materials.json files
    ///     that contain TerrainMaterial entries. This avoids hardcoding paths like "art/terrains/main.materials.json".
    /// </summary>
    /// <param name="levelPath">The level root path (the folder containing info.json)</param>
    /// <returns>The full path to the terrain materials.json file, or null if not found</returns>
    public static string? FindTerrainMaterialsJsonPath(string levelPath)
    {
        try
        {
            // Search for materials.json files in art/terrains and subdirectories
            var terrainPath = Path.Join(levelPath, "art", "terrains");

            if (!Directory.Exists(terrainPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No terrains folder found at {terrainPath}.");
                return null;
            }

            // Search for materials.json files that contain TerrainMaterial
            var materialFiles = Directory.GetFiles(terrainPath, "*.materials.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(terrainPath, "materials.json", SearchOption.AllDirectories))
                .Distinct()
                .ToList();

            foreach (var materialFile in materialFiles)
            {
                if (!File.Exists(materialFile))
                    continue;

                var jsonContent = File.ReadAllText(materialFile);
                if (jsonContent.Contains("\"class\": \"TerrainMaterial\"") ||
                    jsonContent.Contains("\"class\":\"TerrainMaterial\""))
                    return materialFile;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"No terrain materials.json file found in {terrainPath}.");
            return null;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error finding terrain materials.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Gets the base texture paths from TerrainMaterial entries in the materials.json file.
    ///     Reads the actual texture paths from these properties:
    ///     baseColorBaseTex, aoBaseTex, heightBaseTex, normalBaseTex, roughnessBaseTex
    /// </summary>
    /// <param name="materialsJsonPath">Path to the materials.json file</param>
    /// <param name="levelPath">The level root path for resolving relative paths</param>
    /// <returns>List of full paths to base texture files</returns>
    public static List<string> GetBaseTexturePathsFromMaterialsJson(string materialsJsonPath, string levelPath)
    {
        var texturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Properties that define base textures (must match terrain size)
        // These are the properties that contain terrain-sized textures in TerrainMaterial entries
        var baseTextureProperties = new[]
        {
            "baseColorBaseTex",
            "aoBaseTex",
            "heightBaseTex",
            "normalBaseTex",
            "roughnessBaseTex"
        };

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Scanning {Path.GetFileName(materialsJsonPath)} for base texture properties...");

        try
        {
            using var jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(materialsJsonPath);
            var materialsFound = 0;
            var propertiesScanned = 0;

            foreach (var materialEntry in jsonDoc.RootElement.EnumerateObject())
            {
                // Only process TerrainMaterial entries
                if (!materialEntry.Value.TryGetProperty("class", out var classElement) ||
                    classElement.GetString() != "TerrainMaterial")
                    continue;

                materialsFound++;
                var materialName = materialEntry.Name;

                foreach (var propName in baseTextureProperties)
                {
                    propertiesScanned++;
                    
                    if (!materialEntry.Value.TryGetProperty(propName, out var propValue))
                        continue;

                    var texturePath = propValue.GetString();
                    if (string.IsNullOrEmpty(texturePath))
                        continue;

                    // Resolve the BeamNG path to a filesystem path
                    // BeamNG paths look like: /levels/levelname/art/terrains/texture.png
                    // We need to convert to: C:\...\levels\levelname\art\terrains\texture.png
                    var fullPath = ResolveBeamNgPathToFilesystem(texturePath, levelPath);
                    
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    {
                        texturePaths.Add(fullPath);
                        // Note: Individual "Found" messages removed to reduce UI spam.
                        // Summary is logged at the end of the scan.
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"  {propName} in '{materialName}' points to missing file: {texturePath} (resolved: {fullPath ?? "null"})");
                    }
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Scanned {materialsFound} TerrainMaterial entries, checked {propertiesScanned} properties, found {texturePaths.Count} existing texture files");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error reading base texture paths from materials.json: {ex.Message}");
        }

        return texturePaths.ToList();
    }

    /// <summary>
    ///     Resolves a BeamNG-style path (e.g., /levels/levelname/art/terrains/texture.png)
    ///     to an actual filesystem path.
    /// </summary>
    /// <param name="beamNgPath">The BeamNG path from materials.json</param>
    /// <param name="levelPath">The level folder path on the filesystem</param>
    /// <returns>Full filesystem path, or null if resolution failed</returns>
    private static string? ResolveBeamNgPathToFilesystem(string beamNgPath, string levelPath)
    {
        if (string.IsNullOrEmpty(beamNgPath))
            return null;

        // Normalize path separators
        var normalizedPath = beamNgPath.Replace('/', Path.DirectorySeparatorChar);
        
        // If the path starts with /levels/ or levels/, extract the relative part
        // BeamNG paths: /levels/levelname/art/terrains/texture.png
        // We need the part after the level name: art/terrains/texture.png
        var levelsPrefix = Path.DirectorySeparatorChar + "levels" + Path.DirectorySeparatorChar;
        var levelsPrefixNoSlash = "levels" + Path.DirectorySeparatorChar;
        
        int startIndex = -1;
        
        if (normalizedPath.StartsWith(levelsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Find the second separator after /levels/
            var afterLevels = normalizedPath.Substring(levelsPrefix.Length);
            var nextSep = afterLevels.IndexOf(Path.DirectorySeparatorChar);
            if (nextSep > 0)
            {
                // Skip the level name folder
                startIndex = levelsPrefix.Length + nextSep + 1;
            }
        }
        else if (normalizedPath.StartsWith(levelsPrefixNoSlash, StringComparison.OrdinalIgnoreCase))
        {
            // Find the second separator after levels/
            var afterLevels = normalizedPath.Substring(levelsPrefixNoSlash.Length);
            var nextSep = afterLevels.IndexOf(Path.DirectorySeparatorChar);
            if (nextSep > 0)
            {
                // Skip the level name folder
                startIndex = levelsPrefixNoSlash.Length + nextSep + 1;
            }
        }
        
        string relativePath;
        if (startIndex > 0 && startIndex < normalizedPath.Length)
        {
            // Extract the part after /levels/levelname/
            relativePath = normalizedPath.Substring(startIndex);
        }
        else
        {
            // Fallback: just strip leading separator if present
            relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar);
        }
        
        // Combine with the level path
        var fullPath = Path.Combine(levelPath, relativePath);
        
        return fullPath;
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

            // Handle .link files: strip .link for filename manipulation, then re-add it
            var isLinkFile = FileUtils.IsLinkFile(targetFullName);
            var workingTargetName = isLinkFile ? FileUtils.StripLinkExtension(targetFullName) : targetFullName;
            
            // Add suffix to the filename (before the image extension)
            var targetDirectory = Path.GetDirectoryName(workingTargetName);
            var targetFileName = Path.GetFileNameWithoutExtension(workingTargetName);
            var targetExtension = Path.GetExtension(workingTargetName);
            var suffixedTargetFileName = $"{targetFileName}_{levelNameCopyFrom}{targetExtension}";
            
            // Re-add .link extension if the source was a .link file
            if (isLinkFile)
                suffixedTargetFileName += FileUtils.LinkExtension;
                
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
    ///     Resizes all base textures referenced in the terrain materials.json file
    ///     to match the target terrain size. Base textures must match terrain dimensions for proper rendering.
    ///     This method reads the actual texture paths from materials.json properties:
    ///     baseColorBaseTex, aoBaseTex, heightBaseTex, normalBaseTex, roughnessBaseTex
    ///     This is more accurate than pattern-based matching (e.g., *base*.png) as it only
    ///     resizes textures that are actually used by the terrain materials.
    /// </summary>
    /// <param name="levelPath">Path to the level folder</param>
    /// <param name="targetTerrainSize">Target terrain size (power of 2, e.g., 1024, 2048, 4096)</param>
    /// <returns>Number of textures resized</returns>
    public static int ResizeBaseTexturesToTerrainSize(string levelPath, int targetTerrainSize)
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"=== Resizing base textures to {targetTerrainSize}x{targetTerrainSize} ===");
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Level path: {levelPath}");
        
        // Find the terrain materials.json file dynamically
        var materialsJsonPath = FindTerrainMaterialsJsonPath(levelPath);
        if (string.IsNullOrEmpty(materialsJsonPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"No terrain materials.json found in {levelPath}. Cannot resize base textures.");
            return 0;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found terrain materials.json: {Path.GetFileName(materialsJsonPath)}");

        // Get the actual texture paths from materials.json
        var textureFiles = GetBaseTexturePathsFromMaterialsJson(materialsJsonPath, levelPath);
        if (!textureFiles.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No base textures found in terrain materials.json. " +
                "This may happen if TerrainMaterial entries don't have baseColorBaseTex, aoBaseTex, " +
                "heightBaseTex, normalBaseTex, or roughnessBaseTex properties set.");
            return 0;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {textureFiles.Count} base texture file(s) to check for resizing");

        var resizedCount = 0;
        var skippedCorrectSize = 0;
        var skippedNotPowerOfTwo = 0;

        foreach (var textureFile in textureFiles)
            try
            {
                // Check current dimensions
                var imageInfo = Image.Identify(textureFile);
                if (imageInfo == null)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Could not identify image: {Path.GetFileName(textureFile)}");
                    continue;
                }

                var currentWidth = imageInfo.Width;
                var currentHeight = imageInfo.Height;

                // Skip if already the correct size
                if (currentWidth == targetTerrainSize && currentHeight == targetTerrainSize)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"  {Path.GetFileName(textureFile)}: already {currentWidth}x{currentHeight} ?");
                    skippedCorrectSize++;
                    continue;
                }

                // Skip if not a power of 2 (might not be a terrain base texture)
                if (!IsPowerOfTwo(currentWidth) || !IsPowerOfTwo(currentHeight))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"  {Path.GetFileName(textureFile)}: non-power-of-2 ({currentWidth}x{currentHeight}), skipping");
                    skippedNotPowerOfTwo++;
                    continue;
                }

                // Resize the image
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"  {Path.GetFileName(textureFile)}: resizing {currentWidth}x{currentHeight} ? {targetTerrainSize}x{targetTerrainSize}...");
                
                using var image = Image.Load(textureFile);
                image.Mutate(x => x.Resize(targetTerrainSize, targetTerrainSize));
                image.SaveAsPng(textureFile);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"  {Path.GetFileName(textureFile)}: resized successfully ?");

                resizedCount++;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Failed to resize texture {Path.GetFileName(textureFile)}: {ex.Message}");
            }

        // Summary
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"=== Resize summary: {resizedCount} resized, {skippedCorrectSize} already correct, {skippedNotPowerOfTwo} skipped (non-power-of-2) ===");

        return resizedCount;
    }

    /// <summary>
    ///     Checks if a number is a power of 2
    /// </summary>
    private static bool IsPowerOfTwo(int n)
    {
        return n > 0 && (n & (n - 1)) == 0;
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