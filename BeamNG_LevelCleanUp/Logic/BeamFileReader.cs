using System.Collections.Specialized;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicConvertForest;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.LogicCopyForest;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class BeamFileReader
{
    private static readonly StringCollection log = new();
    public static List<string> UnusedAssetFiles = new();
    private static string _newName;
    private static decimal _xOffset;
    private static decimal _yOffset;
    private static decimal _zOffset;

    internal BeamFileReader(string levelpath, string beamLogPath, string levelPathCopyFrom = null)
    {
        var beamInstallDir = Steam.GetBeamInstallDir();
        _levelPath = levelpath;
        _beamLogPath = beamLogPath;
        _levelPathCopyFrom = levelPathCopyFrom;
        SanitizePath();
    }

    internal BeamFileReader()
    {
        var beamInstallDir = Steam.GetBeamInstallDir();
    }

    private static string _levelPath { get; set; }
    private static string _levelName { get; set; }
    private static string _levelNamePath { get; set; }
    private static string _levelPathCopyFrom { get; set; }
    private static string _levelNameCopyFrom { get; set; }
    private static string _levelNamePathCopyFrom { get; set; }
    private static string _beamLogPath { get; set; }
    private static bool _dryRun { get; set; }
    public static List<Asset> Assets { get; set; } = new();
    public static List<MaterialJson> MaterialsJson { get; set; } = new();
    public static List<MaterialJson> MaterialsJsonCopy { get; set; } = new();
    public static List<FileInfo> AllDaeList { get; set; } = new();
    public static List<FileInfo> AllDaeCopyList { get; set; } = new();
    public static List<string> ExcludeFiles { get; set; } = new();
    public static List<CopyAsset> CopyAssets { get; set; } = new();
    public static List<string> GroundCoverJsonLines { get; set; } = new();
    public static List<FileInfo> DeleteList { get; set; } = new();
    private static List<FileInfo> _terrainMaterialFiles { get; set; } = new();
    private static List<FileInfo> _groundCoverFiles { get; set; } = new();

    private static List<FileInfo> _mainDecalsJson { get; set; } = new();
    private static List<FileInfo> _managedDecalData { get; set; } = new();

    private static List<FileInfo> _forestJsonFiles { get; set; } = new();
    private static List<FileInfo> _managedItemData { get; set; } = new();
    private static LevelRenamer _levelRenamer { get; set; }

    /// <summary>
    ///     Indicates whether the user wants to upgrade terrain materials to PBR format
    /// </summary>
    public static bool UpgradeTerrainMaterialsToPbr { get; set; }

    internal void SanitizePath()
    {
        _levelNamePath = ZipFileHandler.GetNamePath(_levelPath);
        _levelPath = ZipFileHandler.GetLevelPath(_levelPath);
        _levelName = new DirectoryInfo(_levelNamePath).Name;
        _levelNamePathCopyFrom = _levelPathCopyFrom != null ? ZipFileHandler.GetNamePath(_levelPathCopyFrom) : null;
        _levelPathCopyFrom = _levelPathCopyFrom != null ? ZipFileHandler.GetLevelPath(_levelPathCopyFrom) : null;
        _levelNameCopyFrom = _levelNamePathCopyFrom != null ? new DirectoryInfo(_levelNamePathCopyFrom).Name : null;
        PathResolver.LevelPath = _levelPath;
        PathResolver.LevelNamePath = _levelNamePath;
        PathResolver.LevelName = _levelName;
        PathResolver.LevelPathCopyFrom = _levelPathCopyFrom;
        PathResolver.LevelNamePathCopyFrom = _levelNamePathCopyFrom;
        PathResolver.LevelNameCopyFrom = _levelNameCopyFrom;
    }

    internal string GetLevelName()
    {
        return _levelName;
    }

    internal string GetLevelPath()
    {
        return _levelPath;
    }

    internal void Reset()
    {
        DeleteList = new List<FileInfo>();
        Assets = new List<Asset>();
        MaterialsJson = new List<MaterialJson>();
        MaterialsJsonCopy = new List<MaterialJson>();
        AllDaeList = new List<FileInfo>();
        ExcludeFiles = new List<string>();
        UnusedAssetFiles = new List<string>();
        CopyAssets = new List<CopyAsset>();
        AllDaeCopyList = new List<FileInfo>();
        _mainDecalsJson = new List<FileInfo>();
        _managedDecalData = new List<FileInfo>();
        _managedItemData = new List<FileInfo>();
        _forestJsonFiles = new List<FileInfo>();
        GroundCoverJsonLines = new List<string>();
        _terrainMaterialFiles = new List<FileInfo>();
        _groundCoverFiles = new List<FileInfo>();

        _xOffset = 0;
        _yOffset = 0;
        _zOffset = 0;
        //_levelName = null;
        //_namePath = null;
        //_levelNameCopyFrom = null;
        //_namePathCopyFrom = null;
        //_beamLogPath = null;
        //_newName = null;
    }

    public string GetSteamBeamFolder()
    {
        return Steam.GetBeamInstallDir();
    }

    public void SetSteamBeamFolder(string path)
    {
        Steam.BeamInstallDir = path;
    }

    internal List<FileInfo> GetDeleteList()
    {
        return DeleteList.OrderBy(x => x.FullName).ToList();
    }

    internal List<CopyAsset> GetCopyList()
    {
        return CopyAssets.OrderBy(x => x.CopyAssetType).ThenBy(x => x.Name).ToList();
    }

    internal void ReadAll()
    {
        Reset();
        ReadInfoJson();
        ReadFacilityJson();
        ReadMissionGroup();
        ReadForest();
        ReadDecals();
        ReadTerrainJson();
        ReadMaterialsJson();
        ReadAllDae();
        ReadCsFilesForGenericExclude();
        ReadLevelExtras();
        ResolveUnusedAssetFiles();
        ResolveOrphanedFiles();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Analyzing finished");
    }

    internal void ReadAllForCopy()
    {
        Reset();
        ReadMaterialsJson();
        RemoveDuplicateMaterials(false);
        CopyAssetRoad();
        CopyAssetDecal();
        CopyDae();
        CopyTerrainMaterials();
        CopyGroundCovers();
        CopyForestBrushes();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Assets finished");
    }

    /// <summary>
    ///     Scans roads, decals, and DAE files from the source level for copying.
    ///     Used by CopyAssets.razor page.
    ///     Does not include terrain materials, groundcovers, or forest brushes.
    /// </summary>
    internal void ReadAssetsForCopy()
    {
        Reset();
        ReadMaterialsJson();
        RemoveDuplicateMaterials(false);
        CopyAssetRoad();
        CopyAssetDecal();
        CopyDae();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Assets finished");
    }

    /// <summary>
    ///     Scans terrain materials and groundcovers from the source level for copying.
    ///     Used by CopyTerrains.razor page.
    ///     Does not include roads, decals, DAE files, or forest brushes.
    /// </summary>
    internal void ReadTerrainMaterialsForCopy()
    {
        Reset();
        ReadMaterialsJson();
        RemoveDuplicateMaterials(false);
        ReadSourceMaterialsJson(); // Needed for groundcover material lookup
        CopyTerrainMaterials();
        CopyGroundCovers();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Terrain Materials finished");
    }

    /// <summary>
    ///     Scans only forest brushes from the source level for copying.
    ///     This is a lightweight scan that doesn't include terrain materials, groundcovers,
    ///     roads, decals, or DAE files - only forest brush definitions.
    /// </summary>
    internal void ReadForestBrushesForCopy()
    {
        Reset();
        ReadSourceMaterialsJson(); // Needed for material lookup when copying shape files
        CopyForestBrushes();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Forest Brushes finished");
    }

    /// <summary>
    ///     Scans materials.json files from the source level (levelPathCopyFrom) and populates MaterialsJsonCopy.
    ///     This is needed by groundcover copier to find material definitions for dependencies.
    /// </summary>
    internal void ReadSourceMaterialsJson()
    {
        if (string.IsNullOrEmpty(_levelPathCopyFrom))
            return;

        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.CopySourceMaterials);
            WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.CopySourceMaterials);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    /// <summary>
    ///     Reads MissionGroup data specifically for Create Level wizard.
    ///     Focuses on essential level setup classes and their file references.
    /// </summary>
    internal void ReadMissionGroupsForCreateLevel()
    {
        Reset();
        
        var allowedClasses = new List<string>
        {
            "LevelInfo",
            "TerrainBlock",
            "TimeOfDay",
            "CloudLayer",
            "ScatterSky",
            "ForestWindEmitter",
            "Forest"
        };

        PubSubChannel.SendMessage(PubSubMessageType.Info, "Reading MissionGroup data for new level creation...");
        
        // Read all mission group files
        ReadMissionGroup();
        
        // Filter to only include allowed classes
        var filteredAssets = Assets
            .Where(asset => allowedClasses.Contains(asset.Class))
            .ToList();
        
        Assets = filteredAssets;
        
        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            $"Found {Assets.Count} essential level objects to copy");
        
        // Log what was found
        foreach (var assetClass in Assets.GroupBy(a => a.Class))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                $"  - {assetClass.Key}: {assetClass.Count()} object(s)", true);
        }
    }

    private void RemoveDuplicateMaterials(bool fromJsonFile)
    {
        var materialScanner = new MaterialScanner(MaterialsJson, _levelPath, _levelNamePath);
        materialScanner.RemoveDuplicates(fromJsonFile);
    }

    internal List<Asset> ReadForConvertToForest()
    {
        var allowedClasses = new List<string>
        {
            "SimGroup",
            "TSStatic"
        };
        Reset();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Missiongroups ... Please wait");
        ReadMissionGroup();
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Missiongroups finished");
        var assets = Assets
            .Where(_ => allowedClasses.Contains(_.Class))
            .Where(_ => (_.Class == "TSStatic" && _.__parent != null && _.DaeExists == true && areSame(_.Scale) &&
                         _.RotationMatrix?.Count == 9) || _.Class == "SimGroup")
            .ToList();

        foreach (var asset in assets)
            if (asset.Class == "TSStatic")
            {
                var fi = new FileInfo(asset.DaePath);
                asset.Name = fi.Name;
            }

        return assets;
    }

    internal bool ChangePosition(decimal xOffset, decimal yOffset, decimal zOffset)
    {
        _xOffset = xOffset;
        _yOffset = yOffset;
        _zOffset = zOffset;
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Missiongroups ... Please wait");
            WalkDirectoryTree(dirInfo, "items.level.json", ReadTypeEnum.ChangeHeightMissionGroups);
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Forest Items ... Please wait");
            WalkDirectoryTree(dirInfo, "*.forest4.json", ReadTypeEnum.ChangeHeightMissionGroups);
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Decals ... Please wait");
            WalkDirectoryTree(dirInfo, "main.decals.json", ReadTypeEnum.ChangeHeightDecals);

            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }

        return true;
    }

    private static bool areSame(List<double>? nums)
    {
        return nums == null || nums.Distinct().Count() == 1;
    }

    internal void DoCopyAssets(List<Guid> identifiers)
    {
        var assetCopy = new AssetCopy(identifiers, CopyAssets);
        assetCopy.Copy();
    }

    internal void ReadInfoJson()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "info.json", ReadTypeEnum.InfoJson);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ReadFacilityJson()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "facilities.json", ReadTypeEnum.FacilityJson);
            WalkDirectoryTree(dirInfo, "*.facilities.json", ReadTypeEnum.FacilityJson);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ReadMissionGroup()
    {
        Assets = new List<Asset>();
        MaterialsJson = new List<MaterialJson>();
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "items.level.json", ReadTypeEnum.MissionGroup);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ReadMaterialsJson()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.MaterialsJson);
            WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.MaterialsJson);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal string GetDuplicateMaterialsLogFilePath()
    {
        var lines = new List<string>();
        var duplicateMaterials = MaterialsJson
            .GroupBy(x => x.Name)
            .Where(x => x.Count() > 1)
            .ToList();
        foreach (var grp in duplicateMaterials)
        {
            lines.Add($"{grp.Key} (Duplicates: {grp.Count()})");
            lines.Add("Duplicates found in:");
            foreach (var mat in grp) lines.Add(mat.MatJsonFileLocation);
        }

        if (lines.Count > 0)
        {
            var path = Path.Join(_levelNamePath, "DuplicateMaterials.txt");
            File.WriteAllLines(path, lines);
            return path;
        }

        return null;
    }

    internal void WriteLogFile(List<string> lines, string logFileName)
    {
        if (lines.Count > 0)
        {
            var path = Path.Join(_levelNamePath, $"{logFileName}.txt");
            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    ///     Writes operation logs to files in the level name path (the actual level folder).
    ///     Creates up to three log files: main log, warnings, and errors.
    ///     Files are only created if they have content.
    /// </summary>
    /// <param name="messages">Info messages list (main operation log)</param>
    /// <param name="warnings">Warning messages list</param>
    /// <param name="errors">Error messages list</param>
    /// <param name="featureName">Feature name for log file prefix (e.g., "AssetCopy" creates Log_AssetCopy.txt)</param>
    internal void WriteOperationLogs(List<string> messages, List<string> warnings, List<string> errors, string featureName)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (messages.Count > 0)
        {
            var messagesWithHeader = new List<string>
            {
                $"# {featureName} Log - {timestamp}",
                ""
            };
            messagesWithHeader.AddRange(messages);
            WriteLogFile(messagesWithHeader, $"Log_{featureName}");
        }

        if (warnings.Count > 0)
            WriteLogFile(warnings, $"Log_{featureName}_Warnings");

        if (errors.Count > 0)
            WriteLogFile(errors, $"Log_{featureName}_Errors");
    }

    internal void ReadTerrainJson()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.terrain.json", ReadTypeEnum.TerrainFile);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ReadDecals()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "main.decals.json", ReadTypeEnum.MainDecalsJson);
            WalkDirectoryTree(dirInfo, "managedDecalData.*", ReadTypeEnum.ManagedDecalData);
            var decalScanner = new DecalScanner(Assets, _mainDecalsJson, _managedDecalData);
            decalScanner.ScanDecals();
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal ForestScanner ReadForest()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.forest4.json", ReadTypeEnum.ForestJsonFiles);
            WalkDirectoryTree(dirInfo, "managedItemData.*", ReadTypeEnum.ManagedItemData);
            var forestScanner = new ForestScanner(Assets, _forestJsonFiles, _managedItemData, _levelPath);
            forestScanner.ScanForest();
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
            return forestScanner;
        }

        return null;
    }

    internal void ReadAllDae()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Read Collada Assets");
            WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.AllDae);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ReadLevelExtras()
    {
        var extras = new List<string>
        {
            "scenarios", "quickrace", "buslines", "art\\cubemaps", "crawls", "perfRecordingCampaths", "camPaths",
            "dragstrips", "driftSpots", "gameplay", "stagedBuildings", "trafficCameras"
        };
        foreach (var extra in extras)
        {
            var dirInfo = new DirectoryInfo(Path.Join(_levelNamePath, extra));
            if (dirInfo != null && dirInfo.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Level {extra}");
                WalkDirectoryTree(dirInfo, "*.prefab", ReadTypeEnum.ScanExtraPrefabs);
                WalkDirectoryTree(dirInfo, "*.*", ReadTypeEnum.ExcludeAllFiles);
                Console.WriteLine("Files with restricted access:");
                foreach (var s in log) Console.WriteLine(s);
            }
        }
    }

    internal void ReadCsFilesForGenericExclude()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.cs", ReadTypeEnum.ExcludeCsFiles);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void ResolveUnusedAssetFiles()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Resolve unused managed asset files");
            var resolver =
                new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _levelPath, _levelName, ExcludeFiles);
            resolver.ExcludeUsedAssetFiles();
            //UnusedAssetFiles = UnusedAssetFiles.Where(x => !ExcludeFiles.Select(x => x.ToUpperInvariant()).Contains(x.ToUpperInvariant())).ToList();
            //DeleteList.AddRange(UnusedAssetFiles.Select(x => new FileInfo(x)));
        }
    }

    internal void ResolveOrphanedFiles()
    {
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Resolve orphaned unmanaged files");
            WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.cdae", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.dds", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.png", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.jpg", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.jpeg", ReadTypeEnum.ImageFile);
            WalkDirectoryTree(dirInfo, "*.ter", ReadTypeEnum.ImageFile);

            DeleteList = DeleteList.Where(x =>
                !ExcludeFiles.Select(x => x.ToUpperInvariant()).Contains(x.FullName.ToUpperInvariant())).ToList();
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void DeleteFilesAndDeploy(List<FileInfo> deleteList, bool dryRun)
    {
        _dryRun = dryRun;
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Delete files");
        var materialScanner = new MaterialScanner(MaterialsJson, _levelPath, _levelNamePath);
        materialScanner.RemoveDuplicates(true);
        var deleter = new FileDeleter(deleteList, _levelPath, "DeletedAssetFiles", _dryRun);
        deleter.Delete();
    }

    internal List<string> GetMissingFilesFromBeamLog()
    {
        if (!string.IsNullOrEmpty(_beamLogPath))
        {
            var logReader = new BeamLogReader(_beamLogPath, _levelPath);
            var missingFiles = logReader.ScanForMissingFiles();
            if (missingFiles.Any())
                File.WriteAllLines(Path.Join(_levelNamePath, "MissingFilesFromBeamNgLog.txt"), missingFiles);
            return missingFiles;
        }

        return new List<string>();
    }

    internal void RenameLevel(string newNameForPath, string newNameForTitle, LevelInfoModel levelInfo = null)
    {
        _newName = newNameForPath;
        _levelRenamer = new LevelRenamer();
        var dirInfo = new DirectoryInfo(_levelPath);
        if (dirInfo != null)
        {
            _levelRenamer.EditInfoJson(_levelNamePath, newNameForTitle, levelInfo);
            WalkDirectoryTree(dirInfo, "*.json", ReadTypeEnum.LevelRename);
            WalkDirectoryTree(dirInfo, "*.prefab", ReadTypeEnum.LevelRename);
            WalkDirectoryTree(dirInfo, "*.cs", ReadTypeEnum.LevelRename);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
            _levelName = newNameForPath;
        }

        var dirInfoOld = new DirectoryInfo(_levelNamePath);
        var targetDir = Path.Join(dirInfoOld.Parent.FullName, newNameForPath);
        Directory.Move(dirInfoOld.FullName, targetDir);

        // Update internal paths to reflect the new directory location
        _levelNamePath = targetDir;
        _levelPath = dirInfoOld.Parent.FullName;

        // checking directory has
        // been renamed or not
        if (Directory.Exists(targetDir)) Console.WriteLine("The directory was renamed to " + targetDir);
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Renaming level done! Build your deployment file now.");
    }

    internal void CopyAssetRoad()
    {
        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.CopyAssetRoad);
            WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.CopyAssetRoad);
            var materialScanner = new MaterialScanner(MaterialsJsonCopy, _levelPathCopyFrom, _levelNamePath);
            materialScanner.RemoveDuplicates(false);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void CopyDae()
    {
        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Read Collada Assets");
            WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.CopyDae);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }

        foreach (var item in AllDaeCopyList)
        {
            var daeScanner = new DaeScanner(_levelPath, item.FullName, true);
            var daeMaterials = daeScanner.GetMaterials();
            var materialsJson = MaterialsJsonCopy
                .Where(m => daeMaterials.Select(x => x.MaterialName.ToUpper()).Contains(m.Name.ToUpper()))
                .Distinct()
                .ToList();
            
            // Enhance materials that have empty stages (physics-only materials) by searching for textures by convention
            EnhanceMaterialsWithConventionTextures(materialsJson, item.DirectoryName);
            
            var asset = new CopyAsset
            {
                CopyAssetType = CopyAssetType.Dae,
                Name = item.Name,
                Materials = materialsJson != null ? materialsJson : new List<MaterialJson>(),
                MaterialsDae = daeMaterials,
                TargetPath = Path.Join(_levelNamePath, "art",
                    $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}", "shapes"),
                DaeFilePath = item.FullName
            };
            var fileInfo = new FileInfo(item.FullName);
            asset.SizeMb =
                Math.Round(
                    asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File)
                        .Sum(x => x.Exists ? x.Length : 0) / 1024f / 1024f, 2);
            asset.SizeMb += Math.Round((fileInfo.Exists ? fileInfo.Length : 0) / 1024f) / 1024f;
            //asset.Duplicate = (asset.Materials.FirstOrDefault() != null && asset.Materials.FirstOrDefault().IsDuplicate) ? true : false;
            //asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation) : string.Empty;
            CopyAssets.Add(asset);
        }
    }

    /// <summary>
    ///     Enhances materials that have empty stages (physics-only materials) by searching for textures
    ///     by naming convention in the specified directory. BeamNG often uses materials like "leaves_strong"
    ///     which are physics-only but the DAE expects visual textures. This method searches for texture files
    ///     that match patterns like: materialname_b.png, materialname_nm.png, t_materialname_b.color.png, etc.
    /// </summary>
    /// <param name="materials">List of materials to enhance</param>
    /// <param name="searchDirectory">Directory to search for texture files (typically the DAE's directory)</param>
    private static void EnhanceMaterialsWithConventionTextures(List<MaterialJson> materials, string searchDirectory)
    {
        if (materials == null || string.IsNullOrEmpty(searchDirectory) || !Directory.Exists(searchDirectory))
            return;

        // Common texture suffixes used in BeamNG
        var textureSuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "_b", "BaseColorMap" },
            { "_b.color", "BaseColorMap" },
            { "_d", "ColorMap" },
            { "_nm", "NormalMap" },
            { "_nm.normal", "NormalMap" },
            { "_n", "NormalMap" },
            { "_r", "RoughnessMap" },
            { "_r.data", "RoughnessMap" },
            { "_ao", "AmbientOcclusionMap" },
            { "_ao.data", "AmbientOcclusionMap" },
            { "_o", "OpacityMap" },
            { "_o.data", "OpacityMap" },
            { "_s", "SpecularMap" },
            { "_m", "MetallicMap" },
            { "_e", "EmissiveMap" }
        };

        var imageExtensions = new[] { ".png", ".dds", ".jpg", ".jpeg", ".tga" };

        foreach (var material in materials)
        {
            // Skip materials that already have texture files
            if (material.MaterialFiles != null && material.MaterialFiles.Any(f => f.File?.Exists == true))
                continue;

            // Check if material has empty stages
            var hasEmptyStages = material.Stages == null || 
                                  !material.Stages.Any() ||
                                  material.Stages.All(s => IsStageEmpty(s));

            if (!hasEmptyStages)
                continue;

            // Initialize MaterialFiles if null
            material.MaterialFiles ??= new List<MaterialFile>();

            var materialName = material.Name?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(materialName))
                continue;

            // Search for texture files matching naming conventions
            try
            {
                var allFiles = Directory.GetFiles(searchDirectory);
                
                foreach (var filePath in allFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var fileNameLower = fileName.ToLowerInvariant();
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                    var extension = Path.GetExtension(filePath).ToLowerInvariant();

                    // Skip non-image files
                    if (!imageExtensions.Contains(extension))
                        continue;

                    // Check various naming patterns
                    foreach (var suffix in textureSuffixes)
                    {
                        // Pattern 1: materialname_suffix.ext (e.g., leaves_strong_b.png)
                        // Pattern 2: t_materialname_suffix.ext (e.g., t_leaves_strong_b.color.png)
                        var pattern1 = $"{materialName}{suffix.Key}";
                        var pattern2 = $"t_{materialName}{suffix.Key}";

                        // Handle double extension like .color.png or .data.png
                        var fileNameForComparison = fileNameWithoutExt;
                        if (fileNameWithoutExt.EndsWith(".color") || fileNameWithoutExt.EndsWith(".data") || fileNameWithoutExt.EndsWith(".normal"))
                        {
                            fileNameForComparison = Path.GetFileNameWithoutExtension(fileNameWithoutExt);
                        }

                        if (fileNameForComparison.Equals(pattern1, StringComparison.OrdinalIgnoreCase) ||
                            fileNameForComparison.Equals(pattern2, StringComparison.OrdinalIgnoreCase))
                        {
                            var fileInfo = new FileInfo(filePath);
                            if (fileInfo.Exists && !material.MaterialFiles.Any(f => f.File?.FullName == fileInfo.FullName))
                            {
                                material.MaterialFiles.Add(new MaterialFile
                                {
                                    MaterialName = material.Name,
                                    File = fileInfo,
                                    MapType = suffix.Value,
                                    Missing = false,
                                    OriginalJsonPath = filePath
                                });
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - this is an enhancement, not critical
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    $"Could not search for convention textures for material {material.Name}: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    ///     Checks if a MaterialStage has no texture map properties set (is empty/physics-only).
    /// </summary>
    private static bool IsStageEmpty(MaterialStage stage)
    {
        if (stage == null)
            return true;

        // Check all string properties that represent texture maps
        foreach (var prop in stage.GetType().GetProperties())
        {
            if (prop.PropertyType != typeof(string))
                continue;

            // Skip non-map properties
            var propName = prop.Name.ToLowerInvariant();
            if (!propName.Contains("map") && !propName.Contains("tex"))
                continue;

            var value = prop.GetValue(stage, null) as string;
            if (!string.IsNullOrEmpty(value))
                return false;
        }

        return true;
    }

    internal void CopyAssetDecal()
    {
        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "managedDecalData.*", ReadTypeEnum.CopyManagedDecal);
            WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.CopyAssetDecal);
            WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.CopyAssetDecal);
            Console.WriteLine("Files with restricted access:");
            foreach (var s in log) Console.WriteLine(s);
        }
    }

    internal void CopyTerrainMaterials()
    {
        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            // Sammle alle materials.json Dateien, die TerrainMaterial enthalten
            var terrainMaterialFiles = new List<FileInfo>();
            WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.FindTerrainMaterialFiles);
            WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.FindTerrainMaterialFiles);

            if (_terrainMaterialFiles.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Found {_terrainMaterialFiles.Count} terrain material file(s) in {_levelNameCopyFrom}");

                foreach (var terrainMaterialFile in _terrainMaterialFiles)
                {
                    var terrainScanner = new TerrainCopyScanner(
                        terrainMaterialFile.FullName,
                        _levelPathCopyFrom,
                        _levelNamePath,
                        MaterialsJsonCopy,
                        CopyAssets);
                    terrainScanner.ScanTerrainMaterials();
                }
                
                // After scanning all terrain materials, extract colors and roughness from the .ter file
                // This updates the BaseColorHex and RoughnessValue properties of each CopyAsset
                if (CopyAssets.Any(a => a.CopyAssetType == CopyAssetType.Terrain))
                {
                    TerrainCopyScanner.ExtractTerrainMaterialColors(_levelNamePathCopyFrom, CopyAssets);
                    
                    TerrainCopyScanner.ExtractTerrainMaterialRoughness(_levelNamePathCopyFrom, CopyAssets);
                }
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No terrain materials found in {_levelNameCopyFrom}");
            }

            // Reset für nächsten Durchlauf
            _terrainMaterialFiles.Clear();
        }
    }

    internal void CopyGroundCovers()
    {
        var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
        if (dirInfo != null)
        {
            // Sammle alle items.level.json Dateien, die GroundCover enthalten
            _groundCoverFiles.Clear();
            WalkDirectoryTree(dirInfo, "items.level.json", ReadTypeEnum.FindGroundCoverFiles);

            if (_groundCoverFiles.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Found {_groundCoverFiles.Count} groundcover file(s) in {_levelNameCopyFrom}");

                foreach (var groundCoverFile in _groundCoverFiles)
                {
                    var groundCoverScanner = new GroundCoverCopyScanner(_levelPathCopyFrom);
                    groundCoverScanner.ScanGroundCovers(groundCoverFile);

                    // Füge die gefundenen GroundCover-Zeilen zur Gesamtliste hinzu
                    if (groundCoverScanner.GroundCoverJsonLines?.Any() == true)
                        GroundCoverJsonLines.AddRange(groundCoverScanner.GroundCoverJsonLines);
                }
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No groundcover files found in {_levelNameCopyFrom}");
            }
        }
    }

    /// <summary>
    ///     Scans and adds forest brushes from the source level to the copy list.
    ///     Forest brushes are painting templates used in BeamNG's World Editor Forest tool.
    /// </summary>
    internal void CopyForestBrushes()
    {
        if (string.IsNullOrEmpty(_levelPathCopyFrom))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No source level path set for forest brush copy");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Scanning forest brushes from {_levelNameCopyFrom}...");

        var forestBrushScanner = new ForestBrushCopyScanner(
            _levelNamePathCopyFrom,
            _levelNamePath,
            CopyAssets);

        forestBrushScanner.ScanForestBrushes();
    }

    internal void ConvertToForest(List<Asset> assets)
    {
        var forestScanner = ReadForest();
        var forestConverter = new ForestConverter(assets,
            forestScanner.GetForestInfo(),
            _levelNamePath);
        forestConverter.Convert();
    }

    internal void DeleteFromMissiongroups(List<Asset> assets)
    {
        //Group assetsby MissionGroup and return Missiongrouplines as List
        var assetsByMissionGroupPath = assets
            .Where(a => a.MissionGroupPath != null && a.MissionGroupLine != null)
            .GroupBy(a => a.MissionGroupPath)
            .Select(g => new
                { MissionGroupPath = g.Key, MissionGroupLines = g.Select(x => x.MissionGroupLine.Value).ToList() })
            .ToList();


        foreach (var assetFile in assetsByMissionGroupPath)
            FileUtils.DeleteLinesFromFile(assetFile.MissionGroupPath, assetFile.MissionGroupLines);
    }

    private static void WalkDirectoryTree(DirectoryInfo root, string filePattern, ReadTypeEnum readTypeEnum)
    {
        var exclude = new List<string> { ".depth.", ".imposter", "foam", "ripple", "_heightmap", "_minimap" };
        FileInfo[] files = null;
        DirectoryInfo[] subDirs = null;

        // First, process all the files directly under this folder
        try
        {
            files = root.GetFiles(filePattern);
        }
        // This is thrown if even one of the files requires permissions greater
        // than the application provides.
        catch (UnauthorizedAccessException e)
        {
            // This code just writes out the message and continues to recurse.
            // You may decide to do something different here. For example, you
            // can try to elevate your privileges and access the file again.
            log.Add(e.Message);
        }

        catch (DirectoryNotFoundException e)
        {
            Console.WriteLine(e.Message);
        }

        if (files != null)
        {
            foreach (var fi in files)
            {
                if (exclude.Any(x => fi.Name.ToUpperInvariant().Contains(x, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // In this example, we only access the existing FileInfo object. If we
                // want to open, delete or modify the file, then
                // a try-catch block is required here to handle the case
                // where the file has been deleted since the call to TraverseTree().
                //Console.WriteLine(fi.FullName);
                //von hie Klassen aufrufen, die file inhalt bearbeiten
                switch (readTypeEnum)
                {
                    case ReadTypeEnum.MissionGroup:
                        var missionGroupScanner =
                            new MissionGroupScanner(fi.FullName, _levelPath, Assets, ExcludeFiles);
                        missionGroupScanner.ScanMissionGroupFile();
                        break;
                    case ReadTypeEnum.MaterialsJson:
                        var materialScanner = new MaterialScanner(fi.FullName, _levelPath, _levelNamePath,
                            MaterialsJson,
                            Assets, ExcludeFiles);
                        materialScanner.ScanMaterialsJsonFile();
                        break;
                    case ReadTypeEnum.TerrainFile:
                        var terrainScanner =
                            new TerrainScanner(fi.FullName, _levelPath, Assets, MaterialsJson, ExcludeFiles);
                        terrainScanner.ScanTerrain();
                        break;
                    case ReadTypeEnum.ExcludeCsFiles:
                        var csScanner = new GenericCsFileScanner(fi, _levelPath, ExcludeFiles, Assets);
                        csScanner.ScanForFilesToExclude();
                        break;
                    case ReadTypeEnum.ExcludeAllFiles:
                        ExcludeFiles.Add(fi.FullName);
                        break;
                    case ReadTypeEnum.ScanExtraPrefabs:
                        var prefabScanner = new PrefabScanner(Assets, _levelPath);
                        prefabScanner.AddPrefabDaeFiles(fi);
                        break;
                    case ReadTypeEnum.AllDae:
                        AllDaeList.Add(fi);
                        break;
                    case ReadTypeEnum.MainDecalsJson:
                        _mainDecalsJson.Add(fi);
                        break;
                    case ReadTypeEnum.ManagedDecalData:
                        _managedDecalData.Add(fi);
                        break;
                    case ReadTypeEnum.ForestJsonFiles:
                        _forestJsonFiles.Add(fi);
                        break;
                    case ReadTypeEnum.ManagedItemData:
                        _managedItemData.Add(fi);
                        break;
                    case ReadTypeEnum.ImageFile:
                        DeleteList.Add(fi);
                        break;
                    case ReadTypeEnum.InfoJson:
                        var infoJsonScanner = new InfoJsonScanner(fi.FullName, fi.Directory.FullName);
                        ExcludeFiles.AddRange(infoJsonScanner.GetExcludeFiles());
                        break;
                    case ReadTypeEnum.FacilityJson:
                        var facilityJsonScanner = new FacilityJsonScanner(fi.FullName, fi.Directory.FullName);
                        ExcludeFiles.AddRange(facilityJsonScanner.GetExcludeFiles());
                        break;
                    case ReadTypeEnum.LevelRename:
                        //PubSubChannel.SendMessage(PubSubMessageType.Info, $"Renaming in file {fi.FullName}", true);
                        _levelRenamer.ReplaceInFile(fi.FullName, $"/{_levelName}/", $"/{_newName}/");
                        break;
                    case ReadTypeEnum.CopyAssetRoad:
                        var materialCopyScanner = new MaterialScanner(fi.FullName, _levelPathCopyFrom, _levelNamePath,
                            MaterialsJsonCopy, new List<Asset>(), new List<string>());
                        materialCopyScanner.ScanMaterialsJsonFile();
                        materialCopyScanner.CheckDuplicates(MaterialsJson);
                        foreach (var item in MaterialsJsonCopy.Where(x => x.IsRoadAndPath))
                            if (!CopyAssets.Any(x => x.CopyAssetType == CopyAssetType.Road && x.Name == item.Name))
                            {
                                var asset = new CopyAsset
                                {
                                    CopyAssetType = CopyAssetType.Road,
                                    Name = item.Name,
                                    Materials = new List<MaterialJson> { item },
                                    SourceMaterialJsonPath = fi.FullName,
                                    TargetPath = Path.Join(_levelNamePath, "art",
                                        $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}", "road")
                                };
                                asset.SizeMb =
                                    Math.Round(
                                        asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File)
                                            .Sum(x => x.Exists ? x.Length : 0) / 1024f / 1024f, 2);
                                asset.Duplicate =
                                    asset.Materials.FirstOrDefault() != null &&
                                    asset.Materials.FirstOrDefault().IsDuplicate
                                        ? true
                                        : false;
                                asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null
                                    ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation)
                                    : string.Empty;
                                CopyAssets.Add(asset);
                            }

                        break;
                    case ReadTypeEnum.CopyManagedDecal:
                        var decalCopyScanner = new DecalCopyScanner(fi, MaterialsJsonCopy, CopyAssets);
                        decalCopyScanner.ScanManagedItems();
                        break;
                    case ReadTypeEnum.CopyAssetDecal:
                        foreach (var asset in CopyAssets.Where(x => x.CopyAssetType == CopyAssetType.Decal))
                        {
                            var materials = MaterialsJsonCopy.Where(x => x.Name == asset.DecalData.Material);
                            foreach (var material in materials)
                                if (!asset.Materials.Any(x => x.Name == material.Name))
                                {
                                    asset.Materials.Add(material);
                                    asset.SourceMaterialJsonPath = fi.FullName;
                                    asset.TargetPath = Path.Join(_levelNamePath, "art",
                                        $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}", "decals");
                                }

                            asset.SizeMb =
                                Math.Round(
                                    asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File)
                                        .Sum(x => x.Exists ? x.Length : 0) / 1024f / 1024f, 2);
                            asset.Duplicate =
                                asset.Materials.FirstOrDefault() != null && asset.Materials.FirstOrDefault().IsDuplicate
                                    ? true
                                    : false;
                            asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null
                                ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation)
                                : string.Empty;
                        }

                        break;
                    case ReadTypeEnum.CopyDae:
                        AllDaeCopyList.Add(fi);
                        break;
                    case ReadTypeEnum.ChangeHeightMissionGroups:
                        var positionScanner = new PositionScanner(_xOffset, _yOffset, _zOffset, fi.FullName,
                            new List<string>());
                        positionScanner.ScanMissionGroupFile();
                        break;
                    case ReadTypeEnum.ChangeHeightDecals:
                        var positionScanner2 = new PositionScanner(_xOffset, _yOffset, _zOffset, fi.FullName,
                            new List<string>());
                        positionScanner2.ScanDecals();
                        break;
                    case ReadTypeEnum.FindTerrainMaterialFiles:
                        // Prüfe ob diese materials.json Datei TerrainMaterial Einträge enthält
                        if (File.Exists(fi.FullName))
                        {
                            var jsonContent = File.ReadAllText(fi.FullName);
                            // Einfache Prüfung ob "class": "TerrainMaterial" im JSON vorkommt
                            if (jsonContent.Contains("\"class\": \"TerrainMaterial\"") ||
                                jsonContent.Contains("\"class\":\"TerrainMaterial\""))
                                _terrainMaterialFiles.Add(fi);
                        }

                        break;
                    case ReadTypeEnum.FindGroundCoverFiles:
                        // Prüfe ob diese items.level.json Datei GroundCover Einträge enthält
                        if (File.Exists(fi.FullName))
                        {
                            var jsonContent = File.ReadAllText(fi.FullName);
                            // Einfache Prüfung ob "class": "GroundCover" im JSON vorkommt
                            if (jsonContent.Contains("\"class\": \"GroundCover\"") ||
                                jsonContent.Contains("\"class\":\"GroundCover\""))
                                _groundCoverFiles.Add(fi);
                        }

                        break;
                    case ReadTypeEnum.CopySourceMaterials:
                        // Scan materials.json files from SOURCE level and populate MaterialsJsonCopy
                        // This is needed for groundcover material lookup
                        var sourceMaterialScanner = new MaterialScanner(fi.FullName, _levelPathCopyFrom, _levelNamePath,
                            MaterialsJsonCopy, new List<Asset>(), new List<string>());
                        sourceMaterialScanner.ScanMaterialsJsonFile();
                        break;
                }
            }

            // Now find all the subdirectories under this directory.
            subDirs = root.GetDirectories();

            foreach (var dirInfo in subDirs)
                // Resursive call for each subdirectory.
                WalkDirectoryTree(dirInfo, filePattern, readTypeEnum);
        }
    }

    private enum ReadTypeEnum
    {
        MissionGroup = 0,
        MaterialsJson = 1,
        AllDae = 2,
        MainDecalsJson = 3,
        ManagedDecalData = 4,
        ForestJsonFiles = 5,
        ManagedItemData = 6,
        InfoJson = 7,
        ImageFile = 8,
        TerrainFile = 9,
        ExcludeCsFiles = 10,
        ScanExtraPrefabs = 11,
        ExcludeAllFiles = 12,
        LevelRename = 13,
        CopyAssetRoad = 14,
        CopyAssetDecal = 15,
        CopyManagedDecal = 16,
        CopyDae = 17,
        FacilityJson = 18,
        ChangeHeightMissionGroups = 19,
        ChangeHeightDecals = 20,
        CopyTerrainMaterials = 21,
        FindTerrainMaterialFiles = 22,
        FindGroundCoverFiles = 23,
        CopySourceMaterials = 24
    }

    /// <summary>
    ///     Extracts all file references from MissionGroup assets for Create Level wizard
    /// </summary>
    internal List<string> GetFileReferencesFromMissionGroupAssets()
    {
        var fileReferences = new List<string>();

        foreach (var asset in Assets)
        {
            // TerrainBlock files
            if (!string.IsNullOrEmpty(asset.TerrainFile))
            {
                fileReferences.Add(asset.TerrainFile);
                
                // Also add corresponding .terrain.json file
                var terrainJsonPath = asset.TerrainFile.Replace(".ter", ".terrain.json");
                fileReferences.Add(terrainJsonPath);
            }

            // ScatterSky gradient files
            if (!string.IsNullOrEmpty(asset.AmbientScaleGradientFile))
                fileReferences.Add(asset.AmbientScaleGradientFile);
            
            if (!string.IsNullOrEmpty(asset.ColorizeGradientFile))
                fileReferences.Add(asset.ColorizeGradientFile);
            
            if (!string.IsNullOrEmpty(asset.FogScaleGradientFile))
                fileReferences.Add(asset.FogScaleGradientFile);
            
            if (!string.IsNullOrEmpty(asset.NightFogGradientFile))
                fileReferences.Add(asset.NightFogGradientFile);
            
            if (!string.IsNullOrEmpty(asset.NightGradientFile))
                fileReferences.Add(asset.NightGradientFile);
            
            if (!string.IsNullOrEmpty(asset.SunScaleGradientFile))
                fileReferences.Add(asset.SunScaleGradientFile);

            // CloudLayer textures
            if (!string.IsNullOrEmpty(asset.Texture))
                fileReferences.Add(asset.Texture);

            // Material references (these will be handled by MaterialCopier)
            if (!string.IsNullOrEmpty(asset.FlareType))
                fileReferences.Add(asset.FlareType); // This is actually a material reference
            
            if (!string.IsNullOrEmpty(asset.NightCubemap))
                fileReferences.Add(asset.NightCubemap); // Material reference
            
            if (!string.IsNullOrEmpty(asset.GlobalEnviromentMap))
                fileReferences.Add(asset.GlobalEnviromentMap); // Material reference
            
            if (!string.IsNullOrEmpty(asset.MoonMat))
                fileReferences.Add(asset.MoonMat); // Material reference
        }

        return fileReferences.Distinct().ToList();
    }
}