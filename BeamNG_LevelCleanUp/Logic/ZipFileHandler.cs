using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using Microsoft.VisualBasic.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal static class ZipFileHandler
    {
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        static string _nameLevelPath { get; set; }
        static string _lastUnpackedPath { get; set; }
        internal enum JobTypeEnum
        {
            FindLevelRoot = 0
        }
        internal static string ExtractToDirectory(string filePath)
        {
            var retVal = string.Empty;
            var fi = new FileInfo(filePath);
            if (fi.Exists)
            {
                retVal = Path.Join(fi.Directory.FullName, "_unpacked");
                _lastUnpackedPath = retVal;
                var deleteDir = new DirectoryInfo(retVal);
                if (deleteDir.Exists)
                {
                    Directory.Delete(retVal, true);
                }
                PubSubChannel.SendMessage(false, $"Unzipping to {retVal}");
                ZipFile.ExtractToDirectory(fi.FullName, retVal);
                PubSubChannel.SendMessage(false, $"Finished unzipping to {retVal}");
                retVal = GetLevelPath(retVal);
            }
            else
            {
                throw new Exception($"Error unzipping: no file {filePath}.");
            }
            return retVal;
        }

        internal static string GetLastUnpackedPath()
        {
            return _lastUnpackedPath;
        }

        internal static void BuildDeploymentFile(string filePath, string levelName, bool searchLevelParent = false)
        {
            var fileName = $"{levelName}_deploy_{DateTime.Now.ToString("yyMMdd")}.zip";
            var targetDir = new DirectoryInfo(filePath).Parent.FullName;
            var targetPath = Path.Join(targetDir, fileName);
            PubSubChannel.SendMessage(false, $"Compressing Deploymentfile at {targetPath}");
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            ZipFile.CreateFromDirectory(filePath, targetPath);
            PubSubChannel.SendMessage(false, $"Deploymentfile created at {targetPath}");
        }

        internal static string GetLevelPath(string path)
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "info.json", JobTypeEnum.FindLevelRoot);
                if (string.IsNullOrEmpty(_nameLevelPath))
                {
                    throw new Exception($"Can't find level data in {dirInfo.FullName}");
                }
                var nameDir = new DirectoryInfo(_nameLevelPath);
                var levelsDir = Directory.GetParent(_nameLevelPath);
                if (!levelsDir.Name.Equals("levels", StringComparison.OrdinalIgnoreCase))
                {
                    levelsDir = Directory.CreateDirectory(Path.Join(path, "levels"));
                    Directory.Move(nameDir.FullName, Path.Join(levelsDir.FullName, nameDir.Name));
                }
                path = levelsDir.FullName;
            }
            return path;
        }

        internal static string GetNamePath(string path)
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo, "info.json", JobTypeEnum.FindLevelRoot);
                if (string.IsNullOrEmpty(_nameLevelPath))
                {
                    throw new Exception($"Can't find level data in {dirInfo.FullName}");
                }
                path = _nameLevelPath;
            }
            return path;
        }

        internal static void WalkDirectoryTree(DirectoryInfo root, string filePattern, JobTypeEnum jobTypeEnum)
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
                    if (exclude.Any(fi.FullName.ToUpperInvariant().Contains)) continue;

                    // In this example, we only access the existing FileInfo object. If we
                    // want to open, delete or modify the file, then
                    // a try-catch block is required here to handle the case
                    // where the file has been deleted since the call to TraverseTree().
                    //Console.WriteLine(fi.FullName);
                    //von hie Klassen aufrufen, die file inhalt bearbeiten
                    switch (jobTypeEnum)
                    {
                        case JobTypeEnum.FindLevelRoot:
                            var mainDir = fi.Directory.GetDirectories("main");
                            if (mainDir.FirstOrDefault() != null)
                            {
                                _nameLevelPath = fi.Directory.FullName;
                            }
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
                    WalkDirectoryTree(dirInfo, filePattern, jobTypeEnum);
                }
            }
        }
    }
}
