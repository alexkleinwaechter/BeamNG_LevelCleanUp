using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Main orchestrator for copying assets between levels
/// </summary>
public class AssetCopy
{
    private readonly List<CopyAsset> _assetsToCopy = new();
    private DaeCopier _daeCopier;
    private FileCopyHandler _fileCopyHandler;
    private GroundCoverCopier _groundCoverCopier;
    private GroundCoverDependencyHelper _groundCoverDependencyHelper;
    private GroundCoverReplacer _groundCoverReplacer;
    private ManagedDecalCopier _managedDecalCopier;
    private MaterialCopier _materialCopier;

    // Specialized copiers
    private PathConverter _pathConverter;
    private TerrainMaterialCopier _terrainMaterialCopier;
    private TerrainMaterialReplacer _terrainMaterialReplacer;
    private bool stopFaultyFile;

    /// <summary>
    ///     Creates an AssetCopy instance for copying assets between levels
    /// </summary>
    /// <param name="identifier">List of asset identifiers to copy</param>
    /// <param name="copyAssetList">Full list of available copy assets</param>
    /// <param name="namePath">Target level path</param>
    /// <param name="levelName">Target level name</param>
    /// <param name="levelNameCopyFrom">Source level name</param>
    public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList)
    {
        _identifier = identifier;
        _assetsToCopy = copyAssetList.Where(x => identifier.Contains(x.Identifier)).ToList();

        InitializeCopiers();
        LoadGroundCoverData();
    }

    private List<Guid> _identifier { get; set; }

    private void InitializeCopiers()
    {
        _pathConverter = new PathConverter(PathResolver.LevelNamePath, PathResolver.LevelName,
            PathResolver.LevelNameCopyFrom);
        _pathConverter = new PathConverter(PathResolver.LevelNamePath, PathResolver.LevelName,
            PathResolver.LevelNameCopyFrom);
        _fileCopyHandler = new FileCopyHandler(PathResolver.LevelNameCopyFrom);
        _materialCopier = new MaterialCopier(_pathConverter, _fileCopyHandler);
        _managedDecalCopier = new ManagedDecalCopier();
        _daeCopier = new DaeCopier(_pathConverter, _fileCopyHandler, _materialCopier);

        // Create shared dependency helper
        _groundCoverDependencyHelper = new GroundCoverDependencyHelper(
            _materialCopier,
            _daeCopier,
            PathResolver.LevelNameCopyFrom,
            PathResolver.LevelNamePath);

        // Initialize copiers using the shared helper
        _groundCoverCopier = new GroundCoverCopier(
            _pathConverter,
            _fileCopyHandler,
            _materialCopier,
            _daeCopier,
            PathResolver.LevelNameCopyFrom,
            PathResolver.LevelNamePath);

        // Initialize replacer using the shared helper
        _groundCoverReplacer = new GroundCoverReplacer(
            _groundCoverDependencyHelper,
            PathResolver.LevelNamePath,
            PathResolver.LevelNameCopyFrom);

        // Pass level paths to TerrainMaterialCopier
        _terrainMaterialCopier = new TerrainMaterialCopier(
            _pathConverter,
            _fileCopyHandler,
            PathResolver.LevelNameCopyFrom,
            _groundCoverCopier,
            PathResolver.LevelPathCopyFrom,
            PathResolver.LevelNamePath);

        // Initialize TerrainMaterialReplacer
        _terrainMaterialReplacer = new TerrainMaterialReplacer(
            _pathConverter,
            _fileCopyHandler,
            _groundCoverReplacer,
            PathResolver.LevelPathCopyFrom,
            PathResolver.LevelNamePath);
    }

    private void LoadGroundCoverData()
    {
        // Load scanned groundcover JSON lines into the copier
        if (BeamFileReader.GroundCoverJsonLines != null && BeamFileReader.GroundCoverJsonLines.Any())
        {
            _groundCoverCopier.LoadGroundCoverJsonLines(BeamFileReader.GroundCoverJsonLines);
            _groundCoverReplacer.LoadGroundCoverJsonLines(BeamFileReader.GroundCoverJsonLines);
        }

        // Load scanned materials for groundcover material lookup
        if (BeamFileReader.MaterialsJsonCopy != null && BeamFileReader.MaterialsJsonCopy.Any())
        {
            _groundCoverCopier.LoadMaterialsJsonCopy(BeamFileReader.MaterialsJsonCopy);
            _groundCoverReplacer.LoadMaterialsJsonCopy(BeamFileReader.MaterialsJsonCopy);
            _groundCoverDependencyHelper.LoadMaterialsJsonCopy(BeamFileReader.MaterialsJsonCopy);
        }
    }

    public void Copy()
    {
        // Collect all terrain materials first for batch processing
        var terrainMaterials = _assetsToCopy.Where(x => x.CopyAssetType == CopyAssetType.Terrain).ToList();
        var otherAssets = _assetsToCopy.Where(x => x.CopyAssetType != CopyAssetType.Terrain).ToList();

        // Group other assets by type for potential batch processing
        var roads = otherAssets.Where(x => x.CopyAssetType == CopyAssetType.Road).ToList();
        var decals = otherAssets.Where(x => x.CopyAssetType == CopyAssetType.Decal).ToList();
        var daeFiles = otherAssets.Where(x => x.CopyAssetType == CopyAssetType.Dae).ToList();

        // Copy roads in batch mode
        if (roads.Any())
        {
            _materialCopier.BeginBatch();
            foreach (var item in roads)
            {
                stopFaultyFile = !CopyRoad(item);
                if (stopFaultyFile) break;
            }

            _materialCopier.EndBatch();

            if (stopFaultyFile) return;
        }

        // Copy decals in batch mode
        if (decals.Any())
        {
            _materialCopier.BeginBatch();
            foreach (var item in decals)
            {
                CopyManagedDecal(item);
                stopFaultyFile = !CopyDecal(item);
                if (stopFaultyFile) break;
            }

            _materialCopier.EndBatch();

            if (stopFaultyFile) return;
        }

        // Copy DAE files (non-batch for now as they use MaterialCopier internally)
        foreach (var item in daeFiles)
        {
            stopFaultyFile = !CopyDae(item);
            if (stopFaultyFile) break;
        }

        if (stopFaultyFile) return;

        // Now process all terrain materials in batch (with groundcover collection)
        if (terrainMaterials.Any()) stopFaultyFile = !CopyTerrainMaterialsBatch(terrainMaterials);

        if (!stopFaultyFile)
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Done! Assets copied. Build your deployment file now.");

        stopFaultyFile = false;
    }

    private bool CopyRoad(CopyAsset item)
    {
        return _materialCopier.Copy(item);
    }

    private bool CopyDecal(CopyAsset item)
    {
        return _materialCopier.Copy(item);
    }

    private void CopyManagedDecal(CopyAsset item)
    {
        _managedDecalCopier.Copy(item);
    }

    private bool CopyDae(CopyAsset item)
    {
        return _daeCopier.Copy(item);
    }

    /// <summary>
    ///     Processes terrain materials - routes to copier or replacer based on ReplaceTargetMaterialNames
    /// </summary>
    private bool CopyTerrainMaterialsBatch(List<CopyAsset> terrainMaterials)
    {
        // Get the target materials.json file path from the first terrain material
        // All terrain materials should have the same target path
        var targetMaterialsPath = Path.Join(terrainMaterials.First().TargetPath, "main.materials.json");
        var targetMaterialsFile = new FileInfo(targetMaterialsPath);
        
        // Check if target JSON is empty or doesn't exist
        bool isTargetJsonEmpty = !targetMaterialsFile.Exists || 
                                  targetMaterialsFile.Length == 0 ||
                                  IsJsonEmptyOrOnlyWhitespace(targetMaterialsFile.FullName);

        // If target JSON is empty, we need to add TerrainMaterialTextureSet regardless of user preference
        // to ensure proper terrain material setup
        if (isTargetJsonEmpty || (BeamFileReader.UpgradeTerrainMaterialsToPbr && terrainMaterials.Any()))
        {
            var pbrUpgradeHandler = new PbrUpgradeHandler(
                targetMaterialsPath,
                PathResolver.LevelName,
                PathResolver.LevelNamePath); // Pass the target level path

            // Get texture sizes from SOURCE level's TerrainMaterialTextureSet
            var sourceSizes = TerrainTextureHelper.GetAllTextureSizes(PathResolver.LevelNamePathCopyFrom);
            var terrainSize = TerrainTextureHelper.GetTerrainSizeFromJson(PathResolver.LevelNamePath) ?? 1024;
            
            if (sourceSizes != null)
            {
                // Use source sizes
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Using texture sizes from source level: base={sourceSizes.BaseTexSize}, detail={sourceSizes.DetailTexSize}, macro={sourceSizes.MacroTexSize}");
                pbrUpgradeHandler.AddTerrainMaterialTextureSet(
                    terrainSize,
                    sourceSizes.DetailTexSize,
                    sourceSizes.MacroTexSize);
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No TerrainMaterialTextureSet found in source level. Using fallback size: {terrainSize}");
                pbrUpgradeHandler.AddTerrainMaterialTextureSet(terrainSize, terrainSize, terrainSize);
            }
        }

        // Separate into copy and replace operations
        var materialsToAdd = terrainMaterials.Where(m => !m.IsReplaceMode).ToList();
        var materialsToReplace = terrainMaterials.Where(m => m.IsReplaceMode).ToList();

        // Process replacements first
        foreach (var item in materialsToReplace)
            // Loop through each target material to replace
        foreach (var targetMaterialName in item.ReplaceTargetMaterialNames)
        {
            if (string.IsNullOrEmpty(targetMaterialName))
                continue; // Skip null/empty (shouldn't happen, but safety check)

            // Create a copy of the item with single replacement target
            var singleReplaceItem = new CopyAsset
            {
                BaseColorHex = item.BaseColorHex,
                RoughnessPreset = item.RoughnessPreset,
                RoughnessValue = item.RoughnessValue,
                Materials = item.Materials,
                TargetPath = item.TargetPath,
                CopyAssetType = item.CopyAssetType,
                ReplaceTargetMaterialName = targetMaterialName // Set single target for backward compatibility
            };

            if (!_terrainMaterialReplacer.Replace(singleReplaceItem))
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Failed to replace material '{targetMaterialName}'. Skipping.");
            // Continue with other replacements, don't fail entire batch
        }

        // Write all groundcover replacements ONCE after all terrain replacements
        if (materialsToReplace.Any()) _groundCoverReplacer.WriteAllGroundCoverReplacements();

        // Process additions (new materials)
        foreach (var item in materialsToAdd)
            if (!_terrainMaterialCopier.Copy(item))
                return false;

        // Write all collected groundcovers ONCE at the end
        if (materialsToAdd.Any()) _groundCoverCopier.WriteAllGroundCovers();

        return true;
    }

    /// <summary>
    ///     Checks if a JSON file is empty, contains only whitespace, or contains only an empty object
    /// </summary>
    private bool IsJsonEmptyOrOnlyWhitespace(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath).Trim();
            
            // Empty or only whitespace
            if (string.IsNullOrWhiteSpace(content))
                return true;
            
            // Only contains empty object
            if (content == "{}" || content == "{ }")
                return true;
            
            return false;
        }
        catch
        {
            // If we can't read the file, treat it as empty
            return true;
        }
    }
}