using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Main orchestrator for copying assets between levels
    /// </summary>
    public class AssetCopy
    {
        private List<Guid> _identifier { get; set; }
        private List<CopyAsset> _assetsToCopy = new List<CopyAsset>();
        private bool stopFaultyFile = false;

        // Specialized copiers
        private PathConverter _pathConverter;
        private FileCopyHandler _fileCopyHandler;
        private MaterialCopier _materialCopier;
        private ManagedDecalCopier _managedDecalCopier;
        private DaeCopier _daeCopier;
        private GroundCoverCopier _groundCoverCopier;
        private GroundCoverDependencyHelper _groundCoverDependencyHelper;
        private TerrainMaterialCopier _terrainMaterialCopier;
        private TerrainMaterialReplacer _terrainMaterialReplacer;
        private GroundCoverReplacer _groundCoverReplacer;

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList)
        {
            _identifier = identifier;
            _assetsToCopy = copyAssetList.Where(x => identifier.Contains(x.Identifier)).ToList();
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath)
          : this(identifier, copyAssetList)
        {
            InitializeCopiers(namePath, null, null);
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath, string levelName)
        : this(identifier, copyAssetList, namePath)
        {
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath, string levelName, string levelNameCopyFrom)
        : this(identifier, copyAssetList, namePath, levelName)
        {
            InitializeCopiers(namePath, levelName, levelNameCopyFrom);
            // Load scanned groundcover JSON lines into the copier
            if (Logic.BeamFileReader.GroundCoverJsonLines != null && Logic.BeamFileReader.GroundCoverJsonLines.Any())
            {
                _groundCoverCopier.LoadGroundCoverJsonLines(Logic.BeamFileReader.GroundCoverJsonLines);
                _groundCoverReplacer.LoadGroundCoverJsonLines(Logic.BeamFileReader.GroundCoverJsonLines);
            }
            // Load scanned materials for groundcover material lookup
            if (Logic.BeamFileReader.MaterialsJsonCopy != null && Logic.BeamFileReader.MaterialsJsonCopy.Any())
            {
                _groundCoverCopier.LoadMaterialsJsonCopy(Logic.BeamFileReader.MaterialsJsonCopy);
                _groundCoverReplacer.LoadMaterialsJsonCopy(Logic.BeamFileReader.MaterialsJsonCopy);
                _groundCoverDependencyHelper.LoadMaterialsJsonCopy(Logic.BeamFileReader.MaterialsJsonCopy);
            }
        }

        private void InitializeCopiers(string namePath, string levelName, string levelNameCopyFrom)
        {
            _pathConverter = new PathConverter(namePath, levelName, levelNameCopyFrom);
            _fileCopyHandler = new FileCopyHandler(levelNameCopyFrom);
            _materialCopier = new MaterialCopier(_pathConverter, _fileCopyHandler);
            _managedDecalCopier = new ManagedDecalCopier();
            _daeCopier = new DaeCopier(_pathConverter, _fileCopyHandler, _materialCopier);

            // Create shared dependency helper
            _groundCoverDependencyHelper = new GroundCoverDependencyHelper(
                _materialCopier,
                _daeCopier,
                levelNameCopyFrom);

            // Initialize copiers using the shared helper
            _groundCoverCopier = new GroundCoverCopier(
                _pathConverter,
                _fileCopyHandler,
                _materialCopier,
                _daeCopier,
                levelNameCopyFrom,
                namePath);

            // Initialize replacer using the shared helper
            _groundCoverReplacer = new GroundCoverReplacer(
                _groundCoverDependencyHelper,
                namePath,
                levelNameCopyFrom);

            // Pass level paths to TerrainMaterialCopier
            _terrainMaterialCopier = new TerrainMaterialCopier(
                _pathConverter,
                _fileCopyHandler,
                levelNameCopyFrom,
                _groundCoverCopier,
                Logic.PathResolver.LevelPathCopyFrom,
                Logic.PathResolver.LevelPath);

            // Initialize TerrainMaterialReplacer
            _terrainMaterialReplacer = new TerrainMaterialReplacer(
                _pathConverter,
                _fileCopyHandler,
                _groundCoverReplacer,
                Logic.PathResolver.LevelPathCopyFrom,
                Logic.PathResolver.LevelPath);
        }

        public void Copy()
        {
            // Collect all terrain materials first for batch processing
            var terrainMaterials = _assetsToCopy.Where(x => x.CopyAssetType == CopyAssetType.Terrain).ToList();
            var otherAssets = _assetsToCopy.Where(x => x.CopyAssetType != CopyAssetType.Terrain).ToList();

            // Copy non-terrain assets first (roads, decals, DAE files)
            foreach (var item in otherAssets)
            {
                switch (item.CopyAssetType)
                {
                    case CopyAssetType.Road:
                        stopFaultyFile = !CopyRoad(item);
                        break;
                    case CopyAssetType.Decal:
                        CopyManagedDecal(item);
                        stopFaultyFile = !CopyDecal(item);
                        break;
                    case CopyAssetType.Dae:
                        stopFaultyFile = !CopyDae(item);
                        break;
                    default:
                        break;
                }

                if (stopFaultyFile)
                {
                    break;
                }
            }

            // Now process all terrain materials in batch (with groundcover collection)
            if (!stopFaultyFile && terrainMaterials.Any())
            {
                stopFaultyFile = !CopyTerrainMaterialsBatch(terrainMaterials);
            }

            if (!stopFaultyFile)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Done! Assets copied. Build your deployment file now.");
            }

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
        /// Processes terrain materials - routes to copier or replacer based on ReplaceTargetMaterialNames
        /// </summary>
        private bool CopyTerrainMaterialsBatch(List<CopyAsset> terrainMaterials)
        {
            // Separate into copy and replace operations
            var materialsToAdd = terrainMaterials.Where(m => !m.IsReplaceMode).ToList();
            var materialsToReplace = terrainMaterials.Where(m => m.IsReplaceMode).ToList();

            // Process replacements first
            foreach (var item in materialsToReplace)
            {
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
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                           $"Failed to replace material '{targetMaterialName}'. Skipping.");
                        // Continue with other replacements, don't fail entire batch
                    }
                }
            }

            // Write all groundcover replacements ONCE after all terrain replacements
            if (materialsToReplace.Any())
            {
                _groundCoverReplacer.WriteAllGroundCoverReplacements();
            }

            // Process additions (new materials)
            foreach (var item in materialsToAdd)
            {
                if (!_terrainMaterialCopier.Copy(item))
                {
                    return false;
                }
            }

            // Write all collected groundcovers ONCE at the end
            if (materialsToAdd.Any())
            {
                _groundCoverCopier.WriteAllGroundCovers();
            }

            return true;
        }
    }
}
