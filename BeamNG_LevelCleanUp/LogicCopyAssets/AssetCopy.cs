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
        private TerrainMaterialCopier _terrainMaterialCopier;
        private GroundCoverCopier _groundCoverCopier;

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
        }

        private void InitializeCopiers(string namePath, string levelName, string levelNameCopyFrom)
        {
            _pathConverter = new PathConverter(namePath, levelName, levelNameCopyFrom);
            _fileCopyHandler = new FileCopyHandler(levelNameCopyFrom);
            _materialCopier = new MaterialCopier(_pathConverter, _fileCopyHandler);
            _managedDecalCopier = new ManagedDecalCopier();
            _daeCopier = new DaeCopier(_pathConverter, _fileCopyHandler, _materialCopier);
            _terrainMaterialCopier = new TerrainMaterialCopier(_pathConverter, _fileCopyHandler, levelNameCopyFrom);
            _groundCoverCopier = new GroundCoverCopier(_pathConverter, _fileCopyHandler, _materialCopier, _daeCopier, levelNameCopyFrom, namePath);
        }

        public void Copy()
        {
            foreach (var item in _assetsToCopy)
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
                    case CopyAssetType.Terrain:
                        stopFaultyFile = !CopyTerrain(item);
                        break;
                    case CopyAssetType.GroundCover:
                        stopFaultyFile = !CopyGroundCover(item);
                        break;
                    default:
                        break;
                }

                if (stopFaultyFile)
                {
                    break;
                }
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

        private bool CopyTerrain(CopyAsset item)
        {
            return _terrainMaterialCopier.Copy(item);
        }

        private bool CopyGroundCover(CopyAsset item)
        {
            return _groundCoverCopier.Copy(item);
        }
    }
}
