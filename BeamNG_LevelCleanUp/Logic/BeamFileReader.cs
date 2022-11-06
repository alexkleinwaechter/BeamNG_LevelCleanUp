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
            ManagedDecalData = 4
        }
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _path { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        public static List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public static List<FileInfo> AllDaeList { get; set; } = new List<FileInfo>();
        internal BeamFileReader(string path)
        {
            _path = path;
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

        internal void ResolveObsoleteFiles()
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                var resolver = new ObsoleteFileResolver(MaterialsJson, Assets, AllDaeList, _path);
                resolver.ResolveUnusedAssets();
            }
        }

        static void WalkDirectoryTree(DirectoryInfo root, string filePattern, ReadTypeEnum readTypeEnum)
        {
            var exclude = new List<string> { "art\\shapes\\groundcover", "art\\shapes\\trees", "art\\shapes\\rocks", "art\\shapes\\driver_training" };
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
                            var missionGroupScanner = new MissionGroupScanner(fi.FullName, _path, Assets);
                            missionGroupScanner.ScanMissionGroupFile();
                            break;
                        case ReadTypeEnum.MaterialsJson:
                            var materialScanner = new MaterialScanner(fi.FullName, _path, MaterialsJson);
                            materialScanner.ScanMaterialsJsonFile();
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
