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
        }
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _path { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        public static List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public static List<FileInfo> AllDaeList { get; set; } = new List<FileInfo>();
        public static List<string> ExcludeFiles { get; set; } = new List<string>();
        public static List<string> UnusedAssetFiles = new List<string>();
        internal BeamFileReader(string path)
        {
            _path = path;
        }
        internal void ReadInfoJson()
        {
            var dirInfo = new DirectoryInfo(_path);
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
            var dirInfo = new DirectoryInfo(_path);
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
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.MaterialsJson);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ReadTerrainJson()
        {
            var dirInfo = new DirectoryInfo(_path);
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
            var dirInfo = new DirectoryInfo(_path);
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
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.forest4.json", ReadTypeEnum.ForestJsonFiles);
                WalkDirectoryTree(dirInfo, "managedItemData.cs", ReadTypeEnum.ManagedItemData);
                var forestScanner = new ForestScanner(Assets, _forestJsonFiles, _managedItemData, _path);
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
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.AllDae);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal void ResolveUnusedAssetFiles()
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                var resolver = new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _path);
                UnusedAssetFiles = resolver.ReturnUnusedAssetFiles();
                var deleter = new FileDeleter(UnusedAssetFiles, _path, "DeletedAssetFiles");
                deleter.Delete();
            }
        }

        private static List<FileInfo> _allImageFiles { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _imageFilesToRemove { get; set; } = new List<FileInfo>();
        internal void ResolveOrphanedFiles()
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "*.dds", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.png", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.jpg", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.jpeg", ReadTypeEnum.ImageFile);
                WalkDirectoryTree(dirInfo, "*.ter", ReadTypeEnum.ImageFile);
                var materials = MaterialsJson
                    .SelectMany(x => x.MaterialFiles)
                    .Select(x => x.File.FullName.ToLowerInvariant())
                    .ToList();
                _imageFilesToRemove = _allImageFiles.Where(x => !materials.Contains(x.FullName.ToLowerInvariant())).ToList();
                var deleter = new FileDeleter(_imageFilesToRemove.Select(x => x.FullName).ToList(), _path, "DeletedOrphanedFiles");
                deleter.Delete();
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        static void WalkDirectoryTree(DirectoryInfo root, string filePattern, ReadTypeEnum readTypeEnum)
        {
            var exclude = new List<string>();
            //var exclude = new List<string> { "art\\shapes\\groundcover", "art\\shapes\\trees", "art\\shapes\\rocks", "art\\shapes\\driver_training" };
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
                    if (exclude.Any(fi.FullName.ToLowerInvariant().Contains)) continue;

                    // In this example, we only access the existing FileInfo object. If we
                    // want to open, delete or modify the file, then
                    // a try-catch block is required here to handle the case
                    // where the file has been deleted since the call to TraverseTree().
                    //Console.WriteLine(fi.FullName);
                    //von hie Klassen aufrufen, die file inhalt bearbeiten
                    switch (readTypeEnum)
                    {
                        case ReadTypeEnum.MissionGroup:
                            var missionGroupScanner = new MissionGroupScanner(fi.FullName, _path, Assets, ExcludeFiles);
                            missionGroupScanner.ScanMissionGroupFile();
                            break;
                        case ReadTypeEnum.MaterialsJson:
                            var materialScanner = new MaterialScanner(fi.FullName, _path, MaterialsJson, ExcludeFiles);
                            materialScanner.ScanMaterialsJsonFile();
                            break;
                        case ReadTypeEnum.TerrainFile:
                            var terrainScanner = new TerrainScanner(fi.FullName, _path, Assets, MaterialsJson, ExcludeFiles);
                            terrainScanner.ScanTerrain();
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
                            if (!ExcludeFiles.Contains(fi.FullName))
                            {
                                _allImageFiles.Add(fi);
                            }
                            break;
                        case ReadTypeEnum.InfoJson:
                            var infoJsonScanner = new InfoJsonScanner(fi.FullName, fi.Directory.FullName);
                            ExcludeFiles.AddRange(infoJsonScanner.GetExcludeFiles());
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
