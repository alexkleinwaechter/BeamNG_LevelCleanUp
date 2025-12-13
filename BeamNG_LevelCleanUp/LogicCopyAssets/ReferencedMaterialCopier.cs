using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of materials referenced by MissionGroup objects
///     (e.g., cubemaps, moon materials, flare types)
/// </summary>
public class ReferencedMaterialCopier
{
    private readonly MaterialCopier _materialCopier;
    private readonly string _targetLevelPath;
    private readonly string _targetLevelName;
    private readonly string _sourceLevelName;
    private readonly List<MaterialJson> _sourceMaterials;
    private readonly PathConverter _pathConverter;
    private readonly FileCopyHandler _fileCopyHandler;

    public ReferencedMaterialCopier(
        string sourceLevelPath,
        string sourceLevelName,
        string targetLevelPath,
        string targetLevelName,
        List<MaterialJson> sourceMaterials)
    {
        _sourceLevelName = sourceLevelName;
        _targetLevelPath = targetLevelPath;
        _targetLevelName = targetLevelName;
        _sourceMaterials = sourceMaterials ?? new List<MaterialJson>();
        
        // Initialize required helpers
        _pathConverter = new PathConverter(targetLevelPath, targetLevelName, sourceLevelName);
        _fileCopyHandler = new FileCopyHandler(sourceLevelName);
        _materialCopier = new MaterialCopier(_pathConverter, _fileCopyHandler);
    }

    /// <summary>
    ///     Copies materials referenced in MissionGroup assets
    ///     Returns list of material names that were successfully copied
    /// </summary>
    public List<string> CopyReferencedMaterials(List<Asset> missionGroupAssets)
    {
        var copiedMaterials = new List<string>();
        var materialsToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all referenced material names from assets
        foreach (var asset in missionGroupAssets)
        {
            // LevelInfo: globalEnviromentMap (cubemap material)
            if (asset.Class == "LevelInfo" && !string.IsNullOrEmpty(asset.GlobalEnviromentMap))
            {
                materialsToCopy.Add(asset.GlobalEnviromentMap);
            }

            // ScatterSky: nightCubemap, moonMat, flareType
            if (asset.Class == "ScatterSky")
            {
                if (!string.IsNullOrEmpty(asset.NightCubemap))
                    materialsToCopy.Add(asset.NightCubemap);
                    
                if (!string.IsNullOrEmpty(asset.MoonMat))
                    materialsToCopy.Add(asset.MoonMat);
                    
                if (!string.IsNullOrEmpty(asset.FlareType))
                    materialsToCopy.Add(asset.FlareType);
            }
        }

        if (!materialsToCopy.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                "No referenced materials found in MissionGroup objects", true);
            return copiedMaterials;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            $"Found {materialsToCopy.Count} referenced material(s) to copy", true);

        // Copy each material
        _materialCopier.BeginBatch();
        
        foreach (var materialName in materialsToCopy)
        {
            if (CopyMaterial(materialName))
            {
                copiedMaterials.Add(materialName);
            }
        }
        
        _materialCopier.EndBatch();

        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            $"Copied {copiedMaterials.Count} referenced material(s)");

        return copiedMaterials;
    }

    /// <summary>
    ///     Copies a single material by name
    /// </summary>
    private bool CopyMaterial(string materialName)
    {
        try
        {
            // Find material in scanned source materials
            var material = _sourceMaterials.FirstOrDefault(m => 
                m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

            if (material == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Referenced material '{materialName}' not found in source level");
                return false;
            }

            // Create CopyAsset for this material
            var copyAsset = new CopyAsset
            {
                CopyAssetType = CopyAssetType.Road, // Use Road type for general materials
                Name = material.Name,
                Materials = new List<MaterialJson> { material },
                SourceMaterialJsonPath = material.MatJsonFileLocation,
                TargetPath = Path.Join(_targetLevelPath, Constants.ReferencedMaterials, 
                    $"{Constants.MappingToolsPrefix}{_sourceLevelName}")
            };

            // Copy material using MaterialCopier
            var success = _materialCopier.Copy(copyAsset);

            if (success)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, 
                    $"Copied material: {materialName}", true);
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Failed to copy material: {materialName}");
            }

            return success;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, 
                $"Error copying material '{materialName}': {ex.Message}");
            return false;
        }
    }
}
