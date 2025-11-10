using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Shared helper class for terrain texture generation and copying
    /// Used by both TerrainMaterialCopier and TerrainMaterialReplacer
    /// </summary>
    public static class TerrainTextureHelper
    {
        /// <summary>
        /// Reads the terrain size from the *.terrain.json file
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
                using JsonDocument jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(terrainFile);

                if (jsonDoc.RootElement.TryGetProperty("size", out JsonElement sizeElement))
                {
                    var size = sizeElement.GetInt32();
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Terrain size from {Path.GetFileName(terrainFile)}: {size}x{size}");
                    return size;
                }
                else
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"No 'size' property found in {terrainFile}.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error reading terrain size: {ex.Message}.");
                return null;
            }
        }

        /// <summary>
        /// Reads the base material texture size from the *.blabla.json file
        /// Extracts the first value from the baseTexSize array in TerrainMaterialTextureSet
        /// </summary>
        public static int? GetBaseMaterialSize(string levelPath)
        {
            try
            {
                levelPath = Path.Join(levelPath, "art", "terrains");

                // Find the blabla.json file in the level directory
                var materialFiles = Directory.GetFiles(levelPath, "*.main.materials.json", SearchOption.AllDirectories);

                if (materialFiles.Length == 0)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                     $"No *.main.materials.json file found in {levelPath}.");
                    return null;
                }

                var materialFile = materialFiles[0];
                using JsonDocument jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(materialFile);

                // Find the key containing "TextureSet"
                foreach (var property in jsonDoc.RootElement.EnumerateObject())
                {
                    if (property.Name.Contains("TextureSet", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.TryGetProperty("baseTexSize", out JsonElement baseTexSizeElement))
                        {
                            if (baseTexSizeElement.ValueKind == JsonValueKind.Array)
                            {
                                var enumerator = baseTexSizeElement.EnumerateArray();
                                if (enumerator.MoveNext())
                                {
                                    var size = enumerator.Current.GetInt32();
                                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                                            $"Base material size from {Path.GetFileName(materialFile)} ({property.Name}): {size}");
                                    return size;
                                }
                            }
                        }
                    }
                }

                PubSubChannel.SendMessage(PubSubMessageType.Warning,
             $"No 'baseTexSize' property found in TerrainMaterialTextureSet in {materialFile}.");
                return null;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
        $"Error reading base material size: {ex.Message}.");
                return null;
            }
        }

        /// <summary>
        /// Loads basetexture size
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
        /// Copies terrain textures with generation of placeholder textures for base/roughness/normal maps
        /// </summary>
        public static void CopyTerrainTextures(
            MaterialJson material,
            JsonNode materialObj,
            string targetTerrainFolder,
            string baseColorHex,
            int roughnessValue,
            int? terrainSize,
            PathConverter pathConverter,
            FileCopyHandler fileCopyHandler, string levelNameCopyFrom)
        {
            TerrainTextureGenerator? textureGenerator = null;

            // Initialize texture generator if we have terrain size
            if (terrainSize.HasValue)
            {
                textureGenerator = new TerrainTextureGenerator(targetTerrainFolder, terrainSize.Value);
            }

            foreach (var matFile in material.MaterialFiles)
            {
                var originalPath = matFile.OriginalJsonPath;

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
                        {
                            colorToUse = baseColorHex;
                        }
                        // Use custom roughness value for roughnessBaseTex
                        else if (matFile.MapType.Equals("roughnessBaseTex", StringComparison.OrdinalIgnoreCase))
                        {
                            customValue = roughnessValue;
                        }

                        // Generate replacement PNG with level name suffix
                        var baseFileName = Path.GetFileNameWithoutExtension(textureProps.FileName);
                        var extension = Path.GetExtension(textureProps.FileName);
                        var suffixedFileName = $"{baseFileName}_{levelNameCopyFrom}{extension}";

                        var generatedPngPath = textureGenerator.GenerateSolidColorPng(
                            colorToUse,
                            suffixedFileName,
                            textureProps.Type,
                            customValue);

                        // Update the path in the material JSON to point to the generated PNG
                        var newPath = pathConverter.GetBeamNgJsonPathOrFileName(generatedPngPath, false);
                        UpdateTexturePathsInMaterial(materialObj, originalPath, newPath);

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                              $"Replaced {matFile.MapType} with generated texture: {Path.GetFileName(generatedPngPath)}");

                        // Also update the corresponding size property if it exists
                        var sizePropertyName = matFile.MapType + "Size";
                        if (materialObj[sizePropertyName] != null)
                        {
                            materialObj[sizePropertyName] = terrainSize.Value;
                        }

                        continue; // Skip normal file copy for this texture
                    }
                }

                // Normal texture copy for all other textures
                var targetFullName = pathConverter.GetTerrainTargetFileName(matFile.File.FullName);
                if (string.IsNullOrEmpty(targetFullName))
                {
                    continue;
                }

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

                // Update texture path in the material JSON with the suffixed filename
                var newNormalPath = pathConverter.GetBeamNgJsonPathOrFileName(suffixedTargetFullName, false);
                UpdateTexturePathsInMaterial(materialObj, originalPath, newNormalPath);
            }
        }

        /// <summary>
        /// Recursively updates texture paths in material JSON
        /// </summary>
        public static void UpdateTexturePathsInMaterial(JsonNode materialNode, string oldPath, string newPath)
        {
            if (materialNode is JsonObject obj)
            {
                foreach (var prop in obj.ToList())
                {
                    if (prop.Value != null)
                    {
                        if (prop.Value is JsonValue jsonValue)
                        {
                            try
                            {
                                var strValue = jsonValue.GetValue<string>();
                                if (!string.IsNullOrEmpty(strValue) && strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    obj[prop.Key] = newPath;
                                }
                            }
                            catch
                            {
                                // Not a string value, skip
                            }
                        }
                        else
                        {
                            UpdateTexturePathsInMaterial(prop.Value, oldPath, newPath);
                        }
                    }
                }
            }
            else if (materialNode is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] != null)
                    {
                        if (arr[i] is JsonValue jsonValue)
                        {
                            try
                            {
                                var strValue = jsonValue.GetValue<string>();
                                if (!string.IsNullOrEmpty(strValue) && strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    arr[i] = JsonValue.Create(newPath);
                                }
                            }
                            catch
                            {
                                // Not a string value, skip
                            }
                        }
                        else
                        {
                            UpdateTexturePathsInMaterial(arr[i], oldPath, newPath);
                        }
                    }
                }
            }
        }
    }
}
