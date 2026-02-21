using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using static BeamNG_LevelCleanUp.BlazorUI.Components.TerrainMaterialSettings;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
/// Service for scanning and managing terrain materials.
/// Encapsulates material loading, ordering, and configuration operations.
/// </summary>
public class TerrainMaterialService
{
    /// <summary>
    /// Result of loading a level's terrain configuration.
    /// </summary>
    public class LevelLoadResult
    {
        public string LevelPath { get; init; } = string.Empty;
        public string LevelName { get; init; } = string.Empty;
        public List<TerrainMaterialItemExtended> Materials { get; init; } = new();
        public int? ExistingTerrainSize { get; init; }
        public string? TerrainName { get; init; }
        public float? MetersPerPixel { get; init; }
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
    }
    
    /// <summary>
    /// Loads level information and terrain materials from a folder.
    /// </summary>
    public LevelLoadResult LoadLevelFromFolder(string folder)
    {
        try
        {
            // Validate the folder contains expected level structure
            var levelPath = ZipFileHandler.GetNamePath(folder);
            if (string.IsNullOrEmpty(levelPath))
            {
                var infoJsonPath = Path.Join(folder, "info.json");
                if (File.Exists(infoJsonPath))
                {
                    levelPath = folder;
                }
                else
                {
                    return new LevelLoadResult
                    {
                        Success = false,
                        ErrorMessage = "Selected folder does not appear to be a valid BeamNG level. " +
                                       "Please select a folder containing info.json."
                    };
                }
            }

            // Get level name
            var reader = new BeamFileReader(levelPath, null);
            var levelName = reader.GetLevelName();

            // Scan for terrain materials
            var materials = ScanTerrainMaterials(levelPath);

            // Load existing terrain settings
            var (terrainSize, terrainName, terrainJsonSquareSize) = LoadTerrainSettings(levelPath);
            // Try TerrainBlock in items.level.json first, then fall back to terrain.json squareSize
            var metersPerPixel = LoadMetersPerPixelFromTerrainBlock(levelPath) ?? terrainJsonSquareSize;

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Loaded {materials.Count} terrain materials from {levelName}");

            return new LevelLoadResult
            {
                Success = true,
                LevelPath = levelPath,
                LevelName = levelName,
                Materials = materials,
                ExistingTerrainSize = terrainSize,
                TerrainName = terrainName,
                MetersPerPixel = metersPerPixel
            };
        }
        catch (Exception ex)
        {
            return new LevelLoadResult
            {
                Success = false,
                ErrorMessage = ex.InnerException != null 
                    ? $"{ex.Message} {ex.InnerException}" 
                    : ex.Message
            };
        }
    }
    
    /// <summary>
    /// Scans for terrain materials in the level folder.
    /// </summary>
    public List<TerrainMaterialItemExtended> ScanTerrainMaterials(string levelPath)
    {
        var materials = new List<TerrainMaterialItemExtended>();
        
        var terrainMaterialsPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(levelPath);

        if (string.IsNullOrEmpty(terrainMaterialsPath) || !File.Exists(terrainMaterialsPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Terrain materials file not found in: {levelPath}");
            return materials;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found terrain materials at: {Path.GetFileName(terrainMaterialsPath)}");

        try
        {
            var jsonContent = File.ReadAllText(terrainMaterialsPath);
            var jsonNode = JsonUtils.GetValidJsonNodeFromString(jsonContent, terrainMaterialsPath);

            if (jsonNode == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    "Failed to parse terrain materials JSON.");
                return materials;
            }

            var order = 0;
            foreach (var property in jsonNode.AsObject())
            {
                var materialClass = property.Value?["class"]?.ToString();

                if (materialClass != "TerrainMaterial")
                    continue;

                var materialName = property.Value?["name"]?.ToString() ?? property.Key;
                var internalName = property.Value?["internalName"]?.ToString() ?? materialName;

                materials.Add(new TerrainMaterialItemExtended
                {
                    Order = order,
                    MaterialName = materialName,
                    InternalName = internalName,
                    JsonKey = property.Key,
                    IsRoadMaterial = false
                });

                order++;
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error reading terrain materials: {ex.Message}");
        }

        return materials;
    }
    
    /// <summary>
    /// Renormalizes material order values to be contiguous starting from 0.
    /// </summary>
    public void RenormalizeMaterialOrder(List<TerrainMaterialItemExtended> materials)
    {
        var sorted = materials.OrderBy(m => m.Order).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].Order = i;
        }

        materials.Clear();
        materials.AddRange(sorted);
    }
    
    /// <summary>
    /// Reorders materials so that those without layer maps (except index 0) are moved to the end.
    /// </summary>
    /// <returns>True if any materials were reordered.</returns>
    public bool ReorderMaterialsWithoutLayerMapsToEnd(List<TerrainMaterialItemExtended> materials)
    {
        RenormalizeMaterialOrder(materials);

        var sorted = materials.OrderBy(m => m.Order).ToList();

        var firstMaterial = sorted.FirstOrDefault();
        var remainingWithLayerMaps = sorted.Skip(1).Where(m => m.HasLayerMap).ToList();
        var remainingWithoutLayerMaps = sorted.Skip(1).Where(m => !m.HasLayerMap).ToList();

        if (!remainingWithoutLayerMaps.Any())
            return false;

        var expectedOrder = new List<TerrainMaterialItemExtended>();
        if (firstMaterial != null)
            expectedOrder.Add(firstMaterial);
        expectedOrder.AddRange(remainingWithLayerMaps);
        expectedOrder.AddRange(remainingWithoutLayerMaps);

        var currentOrder = sorted.ToList();
        var needsReorder = false;
        for (var i = 0; i < expectedOrder.Count; i++)
        {
            if (expectedOrder[i] != currentOrder[i])
            {
                needsReorder = true;
                break;
            }
        }

        if (!needsReorder)
            return false;

        materials.Clear();
        for (var i = 0; i < expectedOrder.Count; i++)
        {
            expectedOrder[i].Order = i;
            materials.Add(expectedOrder[i]);
        }

        return true;
    }
    
    /// <summary>
    /// Moves a material to the top of the list (order 0).
    /// </summary>
    public void MoveToTop(List<TerrainMaterialItemExtended> materials, TerrainMaterialItemExtended material)
    {
        var currentOrder = material.Order;

        if (currentOrder == 0) return;

        foreach (var mat in materials.Where(m => m.Order < currentOrder))
        {
            mat.Order++;
        }

        material.Order = 0;
        RenormalizeMaterialOrder(materials);
    }
    
    /// <summary>
    /// Moves a material to the bottom of the list.
    /// </summary>
    public void MoveToBottom(List<TerrainMaterialItemExtended> materials, TerrainMaterialItemExtended material)
    {
        var currentOrder = material.Order;
        var maxOrder = materials.Max(m => m.Order);

        if (currentOrder == maxOrder) return;

        foreach (var mat in materials.Where(m => m.Order > currentOrder))
        {
            mat.Order--;
        }

        material.Order = maxOrder;
        RenormalizeMaterialOrder(materials);
    }
    
    // ========================================
    // PRIVATE HELPERS
    // ========================================
    
    private (int? TerrainSize, string? TerrainName, float? SquareSize) LoadTerrainSettings(string levelPath)
    {
        try
        {
            var terrainFiles = Directory.GetFiles(levelPath, "*.terrain.json", SearchOption.TopDirectoryOnly);
            if (terrainFiles.Length > 0)
            {
                var terrainJsonPath = terrainFiles[0];
                var jsonContent = File.ReadAllText(terrainJsonPath);
                var jsonNode = JsonUtils.GetValidJsonNodeFromString(jsonContent, terrainJsonPath);

                if (jsonNode != null)
                {
                    int? terrainSize = null;
                    if (jsonNode["size"] != null)
                    {
                        terrainSize = jsonNode["size"]!.GetValue<int>();
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Loaded terrain size {terrainSize} from existing terrain.json");
                    }

                    float? squareSize = null;
                    if (jsonNode["squareSize"] != null)
                    {
                        squareSize = jsonNode["squareSize"]!.GetValue<float>();
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Loaded squareSize {squareSize} from existing terrain.json");
                    }

                    var terrainName = Path.GetFileNameWithoutExtension(terrainJsonPath)
                        .Replace(".terrain", "");
                    
                    return (terrainSize, terrainName, squareSize);
                }
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }

        return (null, null, null);
    }
    
    private float? LoadMetersPerPixelFromTerrainBlock(string levelPath)
    {
        try
        {
            var searchPaths = new[]
            {
                Path.Join(levelPath, "main", "MissionGroup", "Level_object"),
                Path.Join(levelPath, "main", "MissionGroup"),
                Path.Join(levelPath, "main")
            };

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

                var itemsFiles = Directory.GetFiles(searchPath, "items.level.json", SearchOption.AllDirectories);

                foreach (var itemsFile in itemsFiles)
                {
                    var lines = File.ReadAllLines(itemsFile);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("class", out var classProperty) &&
                                classProperty.GetString() == "TerrainBlock")
                            {
                                if (doc.RootElement.TryGetProperty("squareSize", out var squareSize))
                                {
                                    return (float)squareSize.GetDouble();
                                }
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Skip invalid JSON lines
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not load metersPerPixel from TerrainBlock: {ex.Message}");
        }

        return null;
    }
}
