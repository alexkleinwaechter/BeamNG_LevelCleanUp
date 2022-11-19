using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

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
            LevelRename = 13
        }
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _levelPath { get; set; }
        private static string _levelName { get; set; }
        private static string _namePath { get; set; }
        private static string _beamLogPath { get; set; }
        private static bool _dryRun { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        public static List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public static List<FileInfo> AllDaeList { get; set; } = new List<FileInfo>();
        public static List<string> ExcludeFiles { get; set; } = new List<string>();
        public static List<string> UnusedAssetFiles = new List<string>();
        private static string _newName;

        public static List<FileInfo> DeleteList { get; set; } = new List<FileInfo>();
        internal BeamFileReader(string path, string beamLogPath)
        {
            _levelPath = path;
            _beamLogPath = beamLogPath;
            SanitizePath();
        }

        internal BeamFileReader()
        {
        }

        internal void SanitizePath()
        {
            _levelPath = ZipFileHandler.GetLevelPath(_levelPath);
            _namePath = ZipFileHandler.GetNamePath(_levelPath);
            _levelName = new DirectoryInfo(_namePath).Name;
            PathResolver.LevelPath = _levelPath;
        }

        internal string GetLevelName()
        {
            return _levelName;
        }

        internal void Reset()
        {
            DeleteList = new List<FileInfo>();
            Assets = new List<Asset>();
            MaterialsJson = new List<MaterialJson>();
            AllDaeList = new List<FileInfo>();
            ExcludeFiles = new List<string>();
            UnusedAssetFiles = new List<string>();
            _mainDecalsJson = new List<FileInfo>();
            _managedDecalData = new List<FileInfo>();
            _managedItemData = new List<FileInfo>();
            _forestJsonFiles = new List<FileInfo>();
        }

        internal List<FileInfo> GetDeleteList()
        {
            return DeleteList.OrderBy(x => x.FullName).ToList();
        }

        internal void ReadAll()
        {
            this.Reset();
            ReadInfoJson();
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
            PubSubChannel.SendMessage(false, "Analyzing finished");
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
                WalkDirectoryTree(dirInfo, "managedDecalData.cs", ReadTypeEnum.ManagedDecalData);
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
        internal void ReadForest()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.forest4.json", ReadTypeEnum.ForestJsonFiles);
                WalkDirectoryTree(dirInfo, "managedItemData.cs", ReadTypeEnum.ManagedItemData);
                var forestScanner = new ForestScanner(Assets, _forestJsonFiles, _managedItemData, _levelPath);
                forestScanner.ScanForest();
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ReadAllDae()
        {
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(false, $"Read Collada Assets");
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
            var extras = new List<string> { "scenarios", "quickrace", "buslines" };
            foreach (var extra in extras)
            {
                var dirInfo = new DirectoryInfo(Path.Join(_namePath, extra));
                if (dirInfo != null && dirInfo.Exists)
                {
                    PubSubChannel.SendMessage(false, $"Read Level {extra}");
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
                PubSubChannel.SendMessage(false, $"Resolve unused managed asset files");
                var resolver = new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _levelPath, ExcludeFiles);
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
                PubSubChannel.SendMessage(false, $"Resolve orphaned unmanaged files");
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
            PubSubChannel.SendMessage(false, $"Delete files");
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
        internal void RenameLevel(string nameForPath, string nameForTitle)
        {
            _newName = nameForPath;
            _levelRenamer = new LevelRenamer();
            var dirInfo = new DirectoryInfo(_levelPath);
            if (dirInfo != null)
            {
                _levelRenamer.EditInfoJson(_namePath, nameForTitle);
                WalkDirectoryTree(dirInfo, "*.json", ReadTypeEnum.LevelRename);
                WalkDirectoryTree(dirInfo, "*.prefab", ReadTypeEnum.LevelRename);
                WalkDirectoryTree(dirInfo, "*.cs", ReadTypeEnum.LevelRename);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }

            var dirInfoOld = new DirectoryInfo(_namePath);
            var targetDir = Path.Join(dirInfoOld.Parent.FullName, nameForPath);
            Directory.Move(dirInfoOld.FullName, targetDir);

            // checking directory has
            // been renamed or not
            if (Directory.Exists(targetDir))
            {
                Console.WriteLine("The directory was renamed to " + targetDir);
            }
            PubSubChannel.SendMessage(false, "Renaming level done!");
        }


        private static void WalkDirectoryTree(DirectoryInfo root, string filePattern, ReadTypeEnum readTypeEnum)
        {
            var exclude = new List<string> { ".depth.", ".imposter" };
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
                    if (exclude.Select(x => Path.Join(_namePath, x).ToUpperInvariant()).Any(fi.FullName.ToUpperInvariant().Contains))
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
                            var materialScanner = new MaterialScanner(fi.FullName, _levelPath, MaterialsJson, Assets, ExcludeFiles);
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
                        case ReadTypeEnum.LevelRename:
                            PubSubChannel.SendMessage(false, $"Renaming in file {fi.FullName}", true);
                            _levelRenamer.ReplaceInFile(fi.FullName, $"/{_levelName}/", $"/{_newName}/");
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
