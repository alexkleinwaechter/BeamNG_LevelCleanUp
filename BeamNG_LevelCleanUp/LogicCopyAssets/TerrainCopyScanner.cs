using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNgTerrainPoc.Terrain.ColorExtraction;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

public class TerrainCopyScanner
{
    public TerrainCopyScanner(string terrainMaterialsJsonPath, string levelPathCopyFrom, string namePath,
        List<MaterialJson> materialsJsonCopy, List<CopyAsset> copyAssets)
    {
        _terrainMaterialsJsonPath = terrainMaterialsJsonPath;
        _levelPathCopyFrom = levelPathCopyFrom;
        _namePath = namePath;
        _materialsJsonCopy = materialsJsonCopy;
        _copyAssets = copyAssets;
    }

    private string _terrainMaterialsJsonPath { get; }
    private string _levelPathCopyFrom { get; }
    private string _namePath { get; }
    private List<MaterialJson> _materialsJsonCopy { get; }
    private List<CopyAsset> _copyAssets { get; }

    public void ScanTerrainMaterials()
    {
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_terrainMaterialsJsonPath);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                foreach (var child in jsonObject.RootElement.EnumerateObject())
                    try
                    {
                        var material =
                            child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (material != null && material.Class == "TerrainMaterial")
                        {
                            material.MatJsonFileLocation = _terrainMaterialsJsonPath;

                            if (string.IsNullOrEmpty(material.Name) && !string.IsNullOrEmpty(material.InternalName))
                                material.Name = material.InternalName;

                            // Scan for texture files dynamically from all properties
                            material.MaterialFiles = new List<MaterialFile>();
                            ScanTextureFilesFromProperties(child.Value, material);

                            _materialsJsonCopy.Add(material);

                            // Create CopyAsset for terrain material
                            var copyAsset = new CopyAsset
                            {
                                CopyAssetType = CopyAssetType.Terrain,
                                Name = material.Name,
                                TerrainMaterialName = material.Name,
                                TerrainMaterialInternalName = material.InternalName,
                                Materials = new List<MaterialJson> { material },
                                SourceMaterialJsonPath = _terrainMaterialsJsonPath,
                                TargetPath = Path.Join(_namePath, Constants.Terrains)
                            };

                            copyAsset.SizeMb = Math.Round(
                                material.MaterialFiles.Sum(x => x.File.Exists ? x.File.Length : 0) / 1024f / 1024f, 2);

                            _copyAssets.Add(copyAsset);
                        }
                    }
                    catch (Exception ex)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Error reading terrain material {child.Name}: {ex.Message}");
                    }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error scanning terrain materials from {_terrainMaterialsJsonPath}: {ex.Message}");
        }
    }

    private void ScanTextureFilesFromProperties(JsonElement materialElement, MaterialJson material)
    {
        // Dynamically scan all properties that might contain texture paths
        // Properties ending with "Tex" or "Map" are likely texture references
        foreach (var prop in materialElement.EnumerateObject())
            try
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var propName = prop.Name;
                    var propValue = prop.Value.GetString();

                    // Check if this looks like a texture path (contains "levels/" or ends with image extension)
                    if (!string.IsNullOrEmpty(propValue) &&
                        (propName.EndsWith("Tex", StringComparison.OrdinalIgnoreCase) ||
                         propName.EndsWith("Map", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Paths starting with /assets/ reference BeamNG core game assets
                        var isGameAsset = propValue.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);

                        var fi = new FileInfo(PathResolver.ResolvePath(_levelPathCopyFrom, propValue, false));
                        if (!fi.Exists) fi = FileUtils.ResolveImageFileName(fi.FullName);

                        material.MaterialFiles.Add(new MaterialFile
                        {
                            File = fi,
                            MapType = propName,
                            Missing = !fi.Exists,
                            OriginalJsonPath = propValue, // Store the original JSON path
                            IsGameAsset = isGameAsset
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error scanning property {prop.Name} for material {material.Name}: {ex.Message}");
            }
    }

    /// <summary>
    ///     Scans the target level's terrain materials for the replacement dropdown
    ///     Dynamically finds the actual terrain materials file instead of using hardcoded paths
    ///     Returns a list of terrain material names available in the target
    /// </summary>
    public static List<string> GetTargetTerrainMaterials(string namePath)
    {
        var targetMaterials = new List<string>();

        try
        {
            // Dynamically find terrain materials file (same pattern as CopyTerrainMaterials in BeamFileReader)
            var terrainPath = Path.Join(namePath, "art", "terrains");
            if (!Directory.Exists(terrainPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No terrains folder found in target level at {terrainPath}.");
                return targetMaterials;
            }

            // Search for materials.json files that contain TerrainMaterial
            var materialFiles = Directory.GetFiles(terrainPath, "*.materials.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(terrainPath, "materials.json", SearchOption.AllDirectories))
                .Distinct()
                .ToList();

            foreach (var materialFile in materialFiles)
            {
                // Check if this file contains TerrainMaterial entries
                if (!File.Exists(materialFile))
                    continue;

                var jsonContent = File.ReadAllText(materialFile);
                if (!jsonContent.Contains("\"class\": \"TerrainMaterial\"") &&
                    !jsonContent.Contains("\"class\":\"TerrainMaterial\""))
                    continue;

                // This file has terrain materials, scan it
                using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(materialFile);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                        try
                        {
                            var material =
                                child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                            if (material != null && material.Class == "TerrainMaterial")
                            {
                                var materialName = !string.IsNullOrEmpty(material.Name)
                                    ? material.Name
                                    : material.InternalName;
                                if (!string.IsNullOrEmpty(materialName) && !targetMaterials.Contains(materialName))
                                    targetMaterials.Add(materialName);
                            }
                        }
                        catch (Exception ex)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                                $"Error reading target terrain material {child.Name}: {ex.Message}");
                        }
            }

            if (!targetMaterials.Any())
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No terrain materials found in target level.");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not load target terrain materials: {ex.Message}");
        }

        return targetMaterials.OrderBy(x => x).ToList();
    }

    /// <summary>
    ///     Extracts weighted average colors from terrain materials using the terrain .ter file.
    ///     Uses the internal material name from main.materials.json mapped to baseColorBaseTex textures.
    ///     Supports .link files via LinkFileResolver.
    /// </summary>
    /// <param name="levelPath">Path to the level folder (the namePath from extraction)</param>
    /// <param name="copyAssets">List of CopyAssets to update with extracted colors</param>
    /// <returns>Dictionary mapping internal material name to hex color</returns>
    public static Dictionary<string, string> ExtractTerrainMaterialColors(string levelPath, List<CopyAsset> copyAssets)
    {
        var extractedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            // 1. Find the .ter file in the level
            var terFilePath = FindTerrainTerFile(levelPath);
            if (string.IsNullOrEmpty(terFilePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No .ter terrain file found. Colors will use default values.");
                return extractedColors;
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found terrain file: {Path.GetFileName(terFilePath)}");
            
            // 2. Build material textures dictionary from CopyAssets
            // We need to map: internal material name -> baseColorBaseTex path
            var materialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var copyAsset in copyAssets.Where(a => a.CopyAssetType == CopyAssetType.Terrain))
            {
                // Use the internal name as key (this matches what's in the .ter file)
                var internalName = copyAsset.TerrainMaterialInternalName;
                if (string.IsNullOrEmpty(internalName))
                    continue;
                
                // Find the baseColorBaseTex texture from the material files
                var material = copyAsset.Materials.FirstOrDefault();
                if (material?.MaterialFiles == null)
                    continue;
                
                // Look for baseColorBaseTex (the terrain-sized base texture used for color extraction)
                var baseColorTex = material.MaterialFiles.FirstOrDefault(f => 
                    f.MapType?.Equals("baseColorBaseTex", StringComparison.OrdinalIgnoreCase) == true);
                
                // Check if file exists directly or can be resolved via .link
                if (baseColorTex != null && (baseColorTex.File.Exists || LinkFileResolver.CanResolve(baseColorTex.File.FullName)))
                {
                    materialTextures[internalName] = baseColorTex.File.FullName;
                }
            }
            
            if (!materialTextures.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No valid baseColorBaseTex textures found for color extraction.");
                return extractedColors;
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Extracting colors for {materialTextures.Count} terrain materials...");
            
            // 3. Extract colors using TerrainColorExtractor with LinkFileResolver support
            extractedColors = TerrainColorExtractor.ExtractColors(terFilePath, materialTextures, 
                path => LinkFileResolver.GetFileStream(path));
            
            // 4. Update CopyAssets with extracted colors
            foreach (var copyAsset in copyAssets.Where(a => a.CopyAssetType == CopyAssetType.Terrain))
            {
                var internalName = copyAsset.TerrainMaterialInternalName;
                if (!string.IsNullOrEmpty(internalName) && 
                    extractedColors.TryGetValue(internalName, out var hexColor) &&
                    !string.IsNullOrEmpty(hexColor))
                {
                    copyAsset.BaseColorHex = hexColor;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"  {internalName}: extracted color {hexColor}");
                }
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Color extraction complete. Updated {extractedColors.Count(kv => !string.IsNullOrEmpty(kv.Value))} materials.");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error extracting terrain material colors: {ex.Message}. Using default colors.");
        }
        
        return extractedColors;
    }
    
    /// <summary>
    ///     Extracts dominant roughness values from terrain materials using the terrain .ter file.
    ///     Uses the internal material name from main.materials.json mapped to roughnessBaseTex textures.
    ///     Supports .link files via LinkFileResolver.
    /// </summary>
    /// <param name="levelPath">Path to the level folder (the namePath from extraction)</param>
    /// <param name="copyAssets">List of CopyAssets to update with extracted roughness values</param>
    /// <returns>Dictionary mapping internal material name to roughness value (0-255)</returns>
    public static Dictionary<string, int> ExtractTerrainMaterialRoughness(string levelPath, List<CopyAsset> copyAssets)
    {
        var extractedRoughness = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            // 1. Find the .ter file in the level
            var terFilePath = FindTerrainTerFile(levelPath);
            if (string.IsNullOrEmpty(terFilePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No .ter terrain file found. Roughness will use default values.");
                return extractedRoughness;
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found terrain file for roughness extraction: {Path.GetFileName(terFilePath)}");
            
            // 2. Build material textures dictionary from CopyAssets
            // We need to map: internal material name -> roughnessBaseTex path
            var materialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var copyAsset in copyAssets.Where(a => a.CopyAssetType == CopyAssetType.Terrain))
            {
                // Use the internal name as key (this matches what's in the .ter file)
                var internalName = copyAsset.TerrainMaterialInternalName;
                if (string.IsNullOrEmpty(internalName))
                    continue;
                
                // Find the roughnessBaseTex texture from the material files
                var material = copyAsset.Materials.FirstOrDefault();
                if (material?.MaterialFiles == null)
                    continue;
                
                // Look for roughnessBaseTex (the terrain-sized roughness texture)
                var roughnessTex = material.MaterialFiles.FirstOrDefault(f => 
                    f.MapType?.Equals("roughnessBaseTex", StringComparison.OrdinalIgnoreCase) == true);
                
                // Check if file exists directly or can be resolved via .link
                if (roughnessTex != null && (roughnessTex.File.Exists || LinkFileResolver.CanResolve(roughnessTex.File.FullName)))
                {
                    materialTextures[internalName] = roughnessTex.File.FullName;
                }
            }
            
            if (!materialTextures.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No valid roughnessBaseTex textures found for roughness extraction.");
                return extractedRoughness;
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Extracting roughness for {materialTextures.Count} terrain materials...");
            
            // 3. Extract roughness using TerrainRoughnessExtractor with LinkFileResolver support
            extractedRoughness = TerrainRoughnessExtractor.ExtractRoughness(terFilePath, materialTextures,
                path => LinkFileResolver.GetFileStream(path));
            
            // 4. Update CopyAssets with extracted roughness values
            foreach (var copyAsset in copyAssets.Where(a => a.CopyAssetType == CopyAssetType.Terrain))
            {
                var internalName = copyAsset.TerrainMaterialInternalName;
                if (!string.IsNullOrEmpty(internalName) && 
                    extractedRoughness.TryGetValue(internalName, out var roughnessValue) &&
                    roughnessValue >= 0)
                {
                    // Store the calculated value and set to Calculated preset
                    // This allows user to switch between presets and always return to the calculated value
                    copyAsset.CalculatedRoughnessValue = roughnessValue;
                    copyAsset.RoughnessPreset = TerrainRoughnessPreset.Calculated;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"  {internalName}: extracted roughness {roughnessValue}");
                }
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Roughness extraction complete. Updated {extractedRoughness.Count(kv => kv.Value >= 0)} materials.");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error extracting terrain material roughness: {ex.Message}. Using default values.");
        }
        
        return extractedRoughness;
    }

    /// <summary>
    ///     Finds the terrain .ter file in a level directory.
    /// </summary>
    /// <param name="levelPath">Path to the level folder</param>
    /// <returns>Full path to the .ter file, or null if not found</returns>
    private static string? FindTerrainTerFile(string levelPath)
    {
        try
        {
            // Search for .ter files in the level directory
            var terFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.TopDirectoryOnly);
            
            if (terFiles.Length > 0)
            {
                // Prefer "theTerrain.ter" if it exists
                var theTerrain = terFiles.FirstOrDefault(f => 
                    Path.GetFileName(f).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase));
                
                return theTerrain ?? terFiles[0];
            }
            
            // Also check in subdirectories (some levels might have different structures)
            terFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.AllDirectories);
            if (terFiles.Length > 0)
            {
                var theTerrain = terFiles.FirstOrDefault(f => 
                    Path.GetFileName(f).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase));
                
                return theTerrain ?? terFiles[0];
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error searching for .ter file: {ex.Message}");
        }
        
        return null;
    }
}