using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicConvertForest;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class BeamFileReader
    {
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
        }
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _levelPath { get; set; }
        private static string _levelName { get; set; }
        private static string _namePath { get; set; }
        private static string _levelPathCopyFrom { get; set; }
        private static string _levelNameCopyFrom { get; set; }
        private static string _namePathCopyFrom { get; set; }
        private static string _beamLogPath { get; set; }
        private static bool _dryRun { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        public static List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public static List<MaterialJson> MaterialsJsonCopy { get; set; } = new List<MaterialJson>();
        public static List<FileInfo> AllDaeList { get; set; } = new List<FileInfo>();
        public static List<FileInfo> AllDaeCopyList { get; set; } = new List<FileInfo>();
        public static List<string> ExcludeFiles { get; set; } = new List<string>();
        public static List<string> UnusedAssetFiles = new List<string>();
        public static List<CopyAsset> CopyAssets { get; set; } = new List<CopyAsset>();

        private static string _newName;

        public static List<FileInfo> DeleteList { get; set; } = new List<FileInfo>();
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

        internal void SanitizePath()
        {
            _levelPath = ZipFileHandler.GetLevelPath(_levelPath);
            _namePath = ZipFileHandler.GetNamePath(_levelPath);
            _levelName = new DirectoryInfo(_namePath).Name;
            _levelPathCopyFrom = _levelPathCopyFrom != null ? ZipFileHandler.GetLevelPath(_levelPathCopyFrom) : null;
            _namePathCopyFrom = _levelPathCopyFrom != null ? ZipFileHandler.GetNamePath(_levelPathCopyFrom) : null;
            _levelNameCopyFrom = _namePathCopyFrom != null ? new DirectoryInfo(_namePathCopyFrom).Name : null;
            PathResolver.LevelPath = _levelPath;
            PathResolver.LevelPathCopyFrom = _levelPathCopyFrom;
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
            this.Reset();
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
            this.ResolveUnusedAssetFiles();
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
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Fetching Assets finished");
        }

        private void RemoveDuplicateMaterials(bool fromJsonFile)
        {
            var materialScanner = new MaterialScanner(MaterialsJson, _levelPath, _namePath);
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
                .Where(_ => (_.Class == "TSStatic" && _.__parent != null && _.DaeExists == true && areSame(_.Scale) && _.RotationMatrix?.Count == 9) || _.Class == "SimGroup")
                .ToList();

            foreach (var asset in assets)
            {
                if (asset.Class == "TSStatic")
                {
                    var fi = new FileInfo(asset.DaePath);
                    asset.Name = fi.Name;
                }
            }

            return assets;
        }

        static bool areSame(List<double>? nums)
        {
            return nums == null || nums.Distinct().Count() == 1;
        }

        internal void DoCopyAssets(List<Guid> identifiers)
        {
            var assetCopy = new AssetCopy(identifiers, CopyAssets, _namePath, _levelName, _levelNameCopyFrom);
            assetCopy.Copy();
        }

        internal void ReadInfoJson()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "info.json", ReadTypeEnum.InfoJson);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
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
                lines.Add($"Duplicates found in:");
                foreach (var mat in grp)
                {
                    lines.Add(mat.MatJsonFileLocation);
                }
            }
            if (lines.Count > 0)
            {
                var path = Path.Join(_levelPath, $"DuplicateMaterials.txt");
                File.WriteAllLines(path, lines);
                return path;
            }
            else
            {
                return null;
            }
        }

        internal void WriteLogFile(List<string> lines, string logFileName)
        {
            if (lines.Count > 0)
            {
                var path = Path.Join(_levelPath, $"{logFileName}.txt");
                File.WriteAllLines(path, lines);
            }
        }

        internal void ReadTerrainJson()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.terrain.json", ReadTypeEnum.TerrainFile);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        private static List<FileInfo> _mainDecalsJson { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _managedDecalData { get; set; } = new List<FileInfo>();
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        private static List<FileInfo> _forestJsonFiles { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _managedItemData { get; set; } = new List<FileInfo>();
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
                return forestScanner;
            }

            return null;
        }

        internal void ReadAllDae()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Collada Assets");
                WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.AllDae);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ReadLevelExtras()
        {
            var extras = new List<string> { "scenarios", "quickrace", "buslines", "art\\cubemaps" };
            foreach (var extra in extras)
            {
                var dirInfo = new DirectoryInfo(Path.Join(_namePath, extra));
                if (dirInfo != null && dirInfo.Exists)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Level {extra}");
                    WalkDirectoryTree(dirInfo, "*.prefab", ReadTypeEnum.ScanExtraPrefabs);
                    WalkDirectoryTree(dirInfo, "*.*", ReadTypeEnum.ExcludeAllFiles);
                    Console.WriteLine("Files with restricted access:");
                    foreach (string s in log)
                    {
                        Console.WriteLine(s);
                    }
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ResolveUnusedAssetFiles()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Resolve unused managed asset files");
                var resolver = new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _levelPath, _levelName, ExcludeFiles);
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
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Resolve orphaned unmanaged files");
                WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.cdae", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.dds", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.png", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.jpg", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.jpeg", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.ter", ReadTypeEnum.ImageFile);

                DeleteList = DeleteList.Where(x => !ExcludeFiles.Select(x => x.ToUpperInvariant()).Contains(x.FullName.ToUpperInvariant())).ToList();
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void DeleteFilesAndDeploy(List<FileInfo> deleteList, bool dryRun)
        {
            _dryRun = dryRun;
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Delete files");
            var materialScanner = new MaterialScanner(MaterialsJson, _levelPath, _namePath);
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
                {
                    File.WriteAllLines(Path.Join(_levelPath, $"MissingFilesFromBeamNgLog.txt"), missingFiles);
                }
                return missingFiles;
            }
            else
            {
                return new List<string>();
            }
        }
        private static LevelRenamer _levelRenamer { get; set; }
        internal void RenameLevel(string newNameForPath, string newNameForTitle)
        {
            _newName = newNameForPath;
            _levelRenamer = new LevelRenamer();
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                _levelRenamer.EditInfoJson(_namePath, newNameForTitle);
                WalkDirectoryTree(dirInfo, "*.json", ReadTypeEnum.LevelRename);
                WalkDirectoryTree(dirInfo, "*.prefab", ReadTypeEnum.LevelRename);
                WalkDirectoryTree(dirInfo, "*.cs", ReadTypeEnum.LevelRename);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
                _levelName = newNameForPath;
            }

            var dirInfoOld = new DirectoryInfo(_namePath);
            var targetDir = Path.Join(dirInfoOld.Parent.FullName, newNameForPath);
            Directory.Move(dirInfoOld.FullName, targetDir);

            // checking directory has
            // been renamed or not
            if (Directory.Exists(targetDir))
            {
                Console.WriteLine("The directory was renamed to " + targetDir);
            }
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Renaming level done! Build your deployment file now.");
        }

        internal void CopyAssetRoad()
        {
            var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.CopyAssetRoad);
                WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.CopyAssetRoad);
                var materialScanner = new MaterialScanner(MaterialsJsonCopy, _levelPathCopyFrom, _namePath);
                materialScanner.RemoveDuplicates(false);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void CopyDae()
        {
            var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Collada Assets");
                WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.CopyDae);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
            foreach (var item in AllDaeCopyList)
            {
                var daeScanner = new DaeScanner(_levelPath, item.FullName, true);
                var daeMaterials = daeScanner.GetMaterials();
                var materialsJson = MaterialsJsonCopy
                    .Where(m => daeMaterials.Select(x => x.MaterialName.ToUpper()).Contains(m.Name.ToUpper()))
                    .Distinct()
                    .ToList();
                var asset = new CopyAsset
                {
                    CopyAssetType = CopyAssetType.Dae,
                    Name = item.Name,
                    Materials = materialsJson != null ? materialsJson : new List<MaterialJson>(),
                    MaterialsDae = daeMaterials,
                    TargetPath = Path.Join(_namePath, Constants.Dae, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}"),
                    DaeFilePath = item.FullName
                };
                var fileInfo = new FileInfo(item.FullName);
                asset.SizeMb = Math.Round((asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File).Sum(x => x.Exists ? x.Length : 0) / 1024f) / 1024f, 2);
                asset.SizeMb += Math.Round((fileInfo.Exists ? fileInfo.Length : 0) / 1024f) / 1024f;
                //asset.Duplicate = (asset.Materials.FirstOrDefault() != null && asset.Materials.FirstOrDefault().IsDuplicate) ? true : false;
                //asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation) : string.Empty;
                CopyAssets.Add(asset);
            }
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
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ConvertToForest(List<Asset> assets)
        {
            var forestScanner = ReadForest();
            var forestConverter = new ForestConverter(assets,
                forestScanner.GetForestInfo(),
                _namePath);
            forestConverter.Convert();
        }

        internal void DeleteFromMissiongroups(List<Asset> assets)
        {
            //Group assets by MissionGroupPath and return Missiongrouplines as List
            var assetsByMissionGroupPath = assets
                .Where(a => a.MissionGroupPath != null && a.MissionGroupLine != null)
                .GroupBy(a => a.MissionGroupPath)
                .Select(g => new { MissionGroupPath = g.Key, MissionGroupLines = g.Select(x => x.MissionGroupLine.Value).ToList() })
                .ToList();


            foreach (var assetFile in assetsByMissionGroupPath)
            {
                FileUtils.DeleteLinesFromFile(assetFile.MissionGroupPath, assetFile.MissionGroupLines);
            }
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
                foreach (FileInfo fi in files)
                {
                    if (exclude.Any(x => fi.Name.ToUpperInvariant().Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // In this example, we only access the existing FileInfo object. If we
                    // want to open, delete or modify the file, then
                    // a try-catch block is required here to handle the case
                    // where the file has been deleted since the call to TraverseTree().
                    //Console.WriteLine(fi.FullName);
                    //von hie Klassen aufrufen, die file inhalt bearbeiten
                    switch (readTypeEnum)
                    {
                        case ReadTypeEnum.MissionGroup:
                            var missionGroupScanner = new MissionGroupScanner(fi.FullName, _levelPath, Assets, ExcludeFiles);
                            missionGroupScanner.ScanMissionGroupFile();
                            break;
                        case ReadTypeEnum.MaterialsJson:
                            var materialScanner = new MaterialScanner(fi.FullName, _levelPath, _namePath, MaterialsJson, Assets, ExcludeFiles);
                            materialScanner.ScanMaterialsJsonFile();
                            break;
                        case ReadTypeEnum.TerrainFile:
                            var terrainScanner = new TerrainScanner(fi.FullName, _levelPath, Assets, MaterialsJson, ExcludeFiles);
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
                            var materialCopyScanner = new MaterialScanner(fi.FullName, _levelPathCopyFrom, _namePath, MaterialsJsonCopy, new List<Asset>(), new List<string>());
                            materialCopyScanner.ScanMaterialsJsonFile();
                            materialCopyScanner.CheckDuplicates(MaterialsJson);
                            foreach (var item in MaterialsJsonCopy.Where(x => x.IsRoadAndPath))
                            {
                                if (!CopyAssets.Any(x => x.CopyAssetType == CopyAssetType.Road && x.Name == item.Name))
                                {
                                    var asset = new CopyAsset
                                    {
                                        CopyAssetType = CopyAssetType.Road,
                                        Name = item.Name,
                                        Materials = new List<MaterialJson> { item },
                                        SourceMaterialJsonPath = fi.FullName,
                                        TargetPath = Path.Join(_namePath, Constants.RouteRoad, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
                                    };
                                    asset.SizeMb = Math.Round((asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File).Sum(x => x.Exists ? x.Length : 0) / 1024f) / 1024f, 2);
                                    asset.Duplicate = (asset.Materials.FirstOrDefault() != null && asset.Materials.FirstOrDefault().IsDuplicate) ? true : false;
                                    asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation) : string.Empty;
                                    CopyAssets.Add(asset);
                                }
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
                                {
                                    if (!asset.Materials.Any(x => x.Name == material.Name))
                                    {
                                        asset.Materials.Add(material);
                                        asset.SourceMaterialJsonPath = fi.FullName;
                                        asset.TargetPath = Path.Join(_namePath, Constants.Decals, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}");
                                    }
                                }
                                asset.SizeMb = Math.Round((asset.Materials.SelectMany(x => x.MaterialFiles).Select(y => y.File).Sum(x => x.Exists ? x.Length : 0) / 1024f) / 1024f, 2);
                                asset.Duplicate = (asset.Materials.FirstOrDefault() != null && asset.Materials.FirstOrDefault().IsDuplicate) ? true : false;
                                asset.DuplicateFrom = asset.Materials.FirstOrDefault() != null ? string.Join(", ", asset.Materials.FirstOrDefault().DuplicateFoundLocation) : string.Empty;
                            }
                            break;
                        case ReadTypeEnum.CopyDae:
                            AllDaeCopyList.Add(fi);
                            break;
                        default:
                            break;
                    }
                }

                // Now find all the subdirectories under this directory.
                subDirs = root.GetDirectories();

                foreach (DirectoryInfo dirInfo in subDirs)
                {
                    // Resursive call for each subdirectory.
                    WalkDirectoryTree(dirInfo, filePattern, readTypeEnum);
                }
            }
        }
    }
}
