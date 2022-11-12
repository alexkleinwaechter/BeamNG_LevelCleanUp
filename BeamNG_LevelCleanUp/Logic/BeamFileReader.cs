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
            ExcludeCsFiles = 10
        }
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _path { get; set; }
        private static string _beamLogPath { get; set; }
        private static bool _dryRun { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        public static List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public static List<FileInfo> AllDaeList { get; set; } = new List<FileInfo>();
        public static List<string> ExcludeFiles { get; set; } = new List<string>();
        public static List<string> UnusedAssetFiles = new List<string>();
        public static List<FileInfo> DeleteList { get; set; } = new List<FileInfo>();
        internal BeamFileReader(string path, string beamLogPath)
        {
            _path = path;
            _beamLogPath = beamLogPath;

        }

        internal BeamFileReader()
        {
        }

        internal void Reset()
        {
            _beamLogPath = null;
            Assets = new List<Asset>();
            MaterialsJson = new List<MaterialJson>();
            AllDaeList = new List<FileInfo>();
            ExcludeFiles = new List<string>();
            UnusedAssetFiles = new List<string>();
            _mainDecalsJson = new List<FileInfo>();
            _managedDecalData = new List<FileInfo>();
            _managedItemData = new List<FileInfo>();
            _forestJsonFiles = new List<FileInfo>();
            _allImageFiles = new List<FileInfo>();
            _imageFilesToRemove = new List<FileInfo>();
        }

        internal List<FileInfo> GetDeleteList()
        {
            return DeleteList.OrderBy(x => x.FullName).ToList();
        }

        internal async Task ReadAll(CancellationToken token)
        {
            this.Reset();
            await this.ReadInfoJson(token);
            await this.ReadMissionGroup(token);
            await this.ReadForest(token);
            await this.ReadDecals(token);
            await this.ReadTerrainJson(token);
            await this.ReadMaterialsJson(token);
            await this.ReadAllDae(token);
            await this.ReadCsFilesForGenericExclude(token);
            this.ResolveUnusedAssetFiles();
            await this.ResolveOrphanedFiles(token);
            PubSubChannel.SendMessage(false, "Analyzing finished");
        }

        internal async Task ReadInfoJson(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "info.json", ReadTypeEnum.InfoJson, token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal async Task ReadMissionGroup(CancellationToken token)
        {
            Assets = new List<Asset>();
            MaterialsJson = new List<MaterialJson>();
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "items.level.json", ReadTypeEnum.MissionGroup, token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal async Task ReadMaterialsJson(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "*.materials.json", ReadTypeEnum.MaterialsJson, token);
                await WalkDirectoryTree(dirInfo, "materials.json", ReadTypeEnum.MaterialsJson, token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal async Task ReadTerrainJson(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "*.terrain.json", ReadTypeEnum.TerrainFile, token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        private static List<FileInfo> _mainDecalsJson { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _managedDecalData { get; set; } = new List<FileInfo>();
        internal async Task ReadDecals(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "main.decals.json", ReadTypeEnum.MainDecalsJson, token);
                await WalkDirectoryTree(dirInfo, "managedDecalData.cs", ReadTypeEnum.ManagedDecalData, token);
                var decalScanner = new DecalScanner(Assets, _mainDecalsJson, _managedDecalData);
                await decalScanner.ScanDecals(token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        private static List<FileInfo> _forestJsonFiles { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _managedItemData { get; set; } = new List<FileInfo>();
        internal async Task ReadForest(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "*.forest4.json", ReadTypeEnum.ForestJsonFiles, token);
                await WalkDirectoryTree(dirInfo, "managedItemData.cs", ReadTypeEnum.ManagedItemData, token);
                var forestScanner = new ForestScanner(Assets, _forestJsonFiles, _managedItemData, _path);
                await forestScanner.ScanForest(token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal async Task ReadAllDae(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(false, $"Read Collada Assets");
                await WalkDirectoryTree(dirInfo, "*.dae", ReadTypeEnum.AllDae, token);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        internal async Task ReadCsFilesForGenericExclude(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                await WalkDirectoryTree(dirInfo, "materials.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "managedDatablocks.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "managedParticleData.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "particles.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "sounds.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "lights.cs", ReadTypeEnum.ExcludeCsFiles, token);
                await WalkDirectoryTree(dirInfo, "audioProfiles.cs", ReadTypeEnum.ExcludeCsFiles, token);
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
                PubSubChannel.SendMessage(false, $"Resolve unused managed asset files");
                var resolver = new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _path, ExcludeFiles);
                UnusedAssetFiles = resolver.ReturnUnusedAssetFiles();
                UnusedAssetFiles = UnusedAssetFiles.Where(x => !ExcludeFiles.Select(x => x.ToLowerInvariant()).Contains(x.ToLowerInvariant())).ToList();
                DeleteList.AddRange(UnusedAssetFiles.Select(x => new FileInfo(x)));
                //var deleter = new FileDeleter(UnusedAssetFiles, _path, "DeletedAssetFiles", _dryRun);
                //deleter.Delete();
            }
        }

        private static List<FileInfo> _allImageFiles { get; set; } = new List<FileInfo>();
        private static List<FileInfo> _imageFilesToRemove { get; set; } = new List<FileInfo>();
        internal async Task ResolveOrphanedFiles(CancellationToken token)
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                PubSubChannel.SendMessage(false, $"Resolve orphaned unmanaged files");
                await WalkDirectoryTree(dirInfo, "*.dds", ReadTypeEnum.ImageFile, token);
                await WalkDirectoryTree(dirInfo, "*.png", ReadTypeEnum.ImageFile, token);
                await WalkDirectoryTree(dirInfo, "*.jpg", ReadTypeEnum.ImageFile, token);
                await WalkDirectoryTree(dirInfo, "*.jpeg", ReadTypeEnum.ImageFile, token);
                await WalkDirectoryTree(dirInfo, "*.ter", ReadTypeEnum.ImageFile, token);
                var materials = MaterialsJson
                    .SelectMany(x => x.MaterialFiles)
                    .Select(x => x.File.FullName.ToLowerInvariant())
                    .ToList();
                _imageFilesToRemove = _allImageFiles.Where(x => !materials.Contains(x.FullName.ToLowerInvariant())).ToList();
                _imageFilesToRemove = _imageFilesToRemove.Where(x => !ExcludeFiles.Select(x => x.ToLowerInvariant()).Contains(x.FullName.ToLowerInvariant())).ToList();
                DeleteList.AddRange(_imageFilesToRemove);
                //var deleter = new FileDeleter(_imageFilesToRemove.Select(x => x.FullName).ToList(), _path, "DeletedOrphanedFiles", _dryRun);
                //deleter.Delete();
                //output exclude files alwasy drytrun true!
                //deleter = new FileDeleter(ExcludeFiles, _path, "ExcludedFiles", true);
                //deleter.Delete();
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
            var deleter = new FileDeleter(deleteList, _path, "DeletedAssetFiles", _dryRun);
            deleter.Delete();
        }

        internal List<string> GetMissingFilesFromBeamLog()
        {
            if (!string.IsNullOrEmpty(_beamLogPath))
            {
                var logReader = new BeamLogReader(_beamLogPath, _path);
                return logReader.ScanForMissingFiles();
            }
            else
            {
                return new List<string>();
            }
        }

        static async Task WalkDirectoryTree(DirectoryInfo root, string filePattern, ReadTypeEnum readTypeEnum, CancellationToken token)
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
                            await missionGroupScanner.ScanMissionGroupFile(token);
                            break;
                        case ReadTypeEnum.MaterialsJson:
                            var materialScanner = new MaterialScanner(fi.FullName, _path, MaterialsJson, Assets, ExcludeFiles);
                            await materialScanner.ScanMaterialsJsonFile(token);
                            break;
                        case ReadTypeEnum.TerrainFile:
                            var terrainScanner = new TerrainScanner(fi.FullName, _path, Assets, MaterialsJson, ExcludeFiles);
                            await terrainScanner.ScanTerrain(token);
                            break;
                        case ReadTypeEnum.ExcludeCsFiles:
                            var csScanner = new GenericCsFileScanner(fi, _path, ExcludeFiles);
                            await csScanner.ScanForFilesToExclude(token);
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
                            if (!ExcludeFiles.Select(x => x.ToLowerInvariant()).Contains(fi.FullName.ToLowerInvariant()))
                            {
                                _allImageFiles.Add(fi);
                            }
                            break;
                        case ReadTypeEnum.InfoJson:
                            var infoJsonScanner = new InfoJsonScanner(fi.FullName, fi.Directory.FullName);
                            ExcludeFiles.AddRange(await infoJsonScanner.GetExcludeFiles(token));
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
                    WalkDirectoryTree(dirInfo, filePattern, readTypeEnum, token);
                }
            }
        }
    }
}
