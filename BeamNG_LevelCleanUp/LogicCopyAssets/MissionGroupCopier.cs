using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of MissionGroup data and associated files for Create Level wizard
/// </summary>
public class MissionGroupCopier
{
    private readonly List<Asset> _missionGroupAssets;
    private readonly string _sourceLevelPath;
    private readonly string _sourceLevelNamePath;
    private readonly string _targetLevelPath;
    private readonly string _targetLevelNamePath;
    private readonly string _targetLevelName;

    public MissionGroupCopier(
        List<Asset> missionGroupAssets,
        string sourceLevelPath,
        string sourceLevelNamePath,
        string targetLevelPath,
        string targetLevelNamePath,
        string targetLevelName)
    {
        _missionGroupAssets = missionGroupAssets;
        _sourceLevelPath = sourceLevelPath;
        _sourceLevelNamePath = sourceLevelNamePath;
        _targetLevelPath = targetLevelPath;
        _targetLevelNamePath = targetLevelNamePath;
        _targetLevelName = targetLevelName;
    }

    /// <summary>
    ///     Copies all MissionGroup data and associated files to the target level
    /// </summary>
    public void CopyMissionGroupData()
    {
        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Copying MissionGroup data...");

            // 1. Create target directory structure
            CreateDirectoryStructure();

            // 2. Copy referenced files
            CopyReferencedFiles();

            // 3. Write MissionGroup items to target
            WriteMissionGroupItems();

            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                $"Successfully copied {_missionGroupAssets.Count} MissionGroup objects");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error copying MissionGroup data: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Creates the necessary directory structure in the target level
    /// </summary>
    private void CreateDirectoryStructure()
    {
        var directories = new[]
        {
            Path.Join(_targetLevelNamePath, "main", "MissionGroup"),
            Path.Join(_targetLevelNamePath, "art", "skies"),
            Path.Join(_targetLevelNamePath, "art", "terrains")
        };

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Created directory: {dir}", true);
        }
    }

    /// <summary>
    ///     Copies all files referenced by MissionGroup assets
    /// </summary>
    private void CopyReferencedFiles()
    {
        var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in _missionGroupAssets)
        {
            // TerrainBlock: Copy .ter file and .terrain.json
            if (!string.IsNullOrEmpty(asset.TerrainFile))
            {
                CopyTerrainFiles(asset.TerrainFile, copiedFiles);
            }

            // ScatterSky: Copy gradient files
            CopyGradientFile(asset.AmbientScaleGradientFile, copiedFiles);
            CopyGradientFile(asset.ColorizeGradientFile, copiedFiles);
            CopyGradientFile(asset.FogScaleGradientFile, copiedFiles);
            CopyGradientFile(asset.NightFogGradientFile, copiedFiles);
            CopyGradientFile(asset.NightGradientFile, copiedFiles);
            CopyGradientFile(asset.SunScaleGradientFile, copiedFiles);

            // CloudLayer: Copy texture files
            if (!string.IsNullOrEmpty(asset.Texture))
            {
                CopyTextureFile(asset.Texture, copiedFiles);
            }

            // Material references will be handled separately by MaterialCopier
            // (FlareType, NightCubemap, GlobalEnviromentMap, MoonMat)
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            $"Copied {copiedFiles.Count} referenced file(s)");
    }

    /// <summary>
    ///     Copies terrain file (.ter) and its configuration (.terrain.json)
    /// </summary>
    private void CopyTerrainFiles(string terrainFilePath, HashSet<string> copiedFiles)
    {
        try
        {
            // Resolve the source path
            var sourceTerrainPath = PathResolver.ResolvePath(_sourceLevelPath, terrainFilePath, false);
            
            if (File.Exists(sourceTerrainPath))
            {
                // Copy .ter file
                var fileName = Path.GetFileName(terrainFilePath);
                var targetPath = Path.Join(_targetLevelNamePath, fileName);
                File.Copy(sourceTerrainPath, targetPath, true);
                copiedFiles.Add(terrainFilePath);
                
                PubSubChannel.SendMessage(PubSubMessageType.Info, 
                    $"Copied terrain file: {fileName}", true);

                // Copy corresponding .terrain.json file
                var terrainJsonPath = terrainFilePath.Replace(".ter", ".terrain.json");
                var sourceTerrainJsonPath = PathResolver.ResolvePath(_sourceLevelPath, terrainJsonPath, false);
                
                if (File.Exists(sourceTerrainJsonPath))
                {
                    var targetJsonPath = Path.Join(_targetLevelNamePath, Path.GetFileName(terrainJsonPath));
                    
                    // Read, update paths, and write .terrain.json
                    var jsonContent = File.ReadAllText(sourceTerrainJsonPath);
                    var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;
                    jsonContent = jsonContent.Replace($"/levels/{sourceLevelName}/", $"/levels/{_targetLevelName}/");
                    
                    File.WriteAllText(targetJsonPath, jsonContent);
                    copiedFiles.Add(terrainJsonPath);
                    
                    PubSubChannel.SendMessage(PubSubMessageType.Info, 
                        $"Copied terrain config: {Path.GetFileName(terrainJsonPath)}", true);
                }
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Terrain file not found: {terrainFilePath}");
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error copying terrain file {terrainFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Copies a gradient file (PNG)
    /// </summary>
    private void CopyGradientFile(string gradientFilePath, HashSet<string> copiedFiles)
    {
        if (string.IsNullOrEmpty(gradientFilePath) || copiedFiles.Contains(gradientFilePath))
            return;

        try
        {
            var sourcePath = PathResolver.ResolvePath(_sourceLevelPath, gradientFilePath, false);
            
            if (File.Exists(sourcePath))
            {
                var targetPath = Path.Join(_targetLevelNamePath, "art", "skies", Path.GetFileName(gradientFilePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
                copiedFiles.Add(gradientFilePath);
                
                PubSubChannel.SendMessage(PubSubMessageType.Info, 
                    $"Copied gradient: {Path.GetFileName(gradientFilePath)}", true);
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                $"Could not copy gradient file {gradientFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Copies a texture file (DDS, PNG, etc.)
    /// </summary>
    private void CopyTextureFile(string textureFilePath, HashSet<string> copiedFiles)
    {
        if (string.IsNullOrEmpty(textureFilePath) || copiedFiles.Contains(textureFilePath))
            return;

        try
        {
            var sourcePath = PathResolver.ResolvePath(_sourceLevelPath, textureFilePath, false);
            
            if (File.Exists(sourcePath))
            {
                var relativePath = textureFilePath.TrimStart('/');
                var targetPath = Path.Join(_targetLevelPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
                copiedFiles.Add(textureFilePath);
                
                PubSubChannel.SendMessage(PubSubMessageType.Info, 
                    $"Copied texture: {Path.GetFileName(textureFilePath)}", true);
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                $"Could not copy texture file {textureFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Writes MissionGroup items to items.level.json in target level
    /// </summary>
    private void WriteMissionGroupItems()
    {
        try
        {
            var missionGroupPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "items.level.json");
            var lines = new List<string>();

            // Get source level name for path replacement
            var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;

            foreach (var asset in _missionGroupAssets)
            {
                // Serialize asset to JSON
                var json = JsonSerializer.Serialize(asset, BeamJsonOptions.GetJsonSerializerOneLineOptions());
                
                // Replace source level paths with target level paths
                json = json.Replace($"/levels/{sourceLevelName}/", $"/levels/{_targetLevelName}/");
                
                lines.Add(json);
            }

            // Write all lines to file
            File.WriteAllLines(missionGroupPath, lines);
            
            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                $"Wrote {lines.Count} MissionGroup items to items.level.json");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error writing MissionGroup items: {ex.Message}");
            throw;
        }
    }
}
