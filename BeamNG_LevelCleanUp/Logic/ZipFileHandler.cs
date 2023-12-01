using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using Microsoft.VisualBasic.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    public static class ZipFileHandler
    {
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        static string _nameLevelPath { get; set; }
        static string _lastUnpackedPath { get; set; }
        static string _lastCopyFromUnpackedPath { get; set; }
        static string _lastUnpackedZip { get; set; }
        static string _lastCopyFromUnpackedZip { get; set; }
        public enum JobTypeEnum
        {
            FindLevelRoot = 0
        }
        public static string WorkingDirectory { get; set; }
        public static string ExtractToDirectory(string filePath, string relativeTarget, bool isCopyFrom = false)
        {
            var retVal = string.Empty;
            var fi = new FileInfo(filePath);
            if (fi.Exists)
            {
                retVal = Path.Join(fi.Directory.FullName, relativeTarget);
                if (isCopyFrom)
                {
                    _lastCopyFromUnpackedZip = filePath;
                    _lastCopyFromUnpackedPath = retVal;
                }
                else
                {
                    _lastUnpackedZip = filePath;
                    _lastUnpackedPath = retVal;
                }
                var deleteDir = new DirectoryInfo(retVal);
                if (deleteDir.Exists)
                {
                    Directory.Delete(retVal, true);
                }
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Unzipping to {retVal}");
                ZipFile.ExtractToDirectory(fi.FullName, retVal);
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Finished unzipping to {retVal}");
                retVal = GetLevelPath(retVal);
            }
            else
            {
                throw new Exception($"Error unzipping: no file {filePath}.");
            }
            return retVal;
        }

        public static void CleanUpWorkingDirectory()
        {
            if (!string.IsNullOrEmpty(_lastUnpackedPath))
            {
                var deleteDir = new DirectoryInfo(_lastUnpackedPath);
                if (deleteDir.Exists)
                {
                    Directory.Delete(_lastUnpackedPath, true);
                }
            }

            if (!string.IsNullOrEmpty(_lastCopyFromUnpackedPath))
            {
                var deleteDir = new DirectoryInfo(_lastCopyFromUnpackedPath);
                if (deleteDir.Exists)
                {
                    Directory.Delete(_lastCopyFromUnpackedPath, true);
                }
            }

            //if (!string.IsNullOrEmpty(_lastUnpackedZip))
            //{
            //    var deleteFile = new FileInfo(_lastUnpackedZip);
            //    if (deleteFile.Exists)
            //    {
            //        File.Delete(_lastUnpackedZip);
            //    }
            //}

            //if (!string.IsNullOrEmpty(_lastCopyFromUnpackedZip))
            //{
            //    var deleteFile = new FileInfo(_lastCopyFromUnpackedZip);
            //    if (deleteFile.Exists)
            //    {
            //        File.Delete(_lastCopyFromUnpackedZip);
            //    }
            //}
        }
        public static string GetLastUnpackedPath()
        {
            return _lastUnpackedPath;
        }

        public static string GetLastUnpackedCopyFromPath()
        {
            return _lastCopyFromUnpackedPath;
        }

        public static void BuildDeploymentFile(string filePath, string levelName, CompressionLevel compressionLevel, bool searchLevelParent = false)
        {
            var fileName = $"{levelName}_deploy_{DateTime.Now.ToString("yyMMdd")}.zip";
            var targetDir = new DirectoryInfo(filePath).Parent.FullName;
            var targetPath = Path.Join(targetDir, fileName);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Compressing Deploymentfile at {targetPath}");
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            ZipFile.CreateFromDirectory(filePath, targetPath, compressionLevel, false);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Deploymentfile created at {targetPath}");
        }

        public static void RemoveModInfo(string path)
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo != null)
            {
                try
                {
                    Directory.Delete(Path.Join(dirInfo.FullName, "mod_info"), true);
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error: {ex.Message}");
                }
            }
        }

        public static string GetLevelPath(string path)
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

        public static string GetNamePath(string path)
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

        public static void WalkDirectoryTree(DirectoryInfo root, string filePattern, JobTypeEnum jobTypeEnum)
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
                            if (mainDir.Length == 0)
                            {
                                mainDir = fi.Directory.GetDirectories("art");
                                if (mainDir.FirstOrDefault() != null)
                                {
                                    _nameLevelPath = fi.Directory.FullName;
                                }
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

        public static void OpenExplorer()
        {
            System.Diagnostics.Process.Start("explorer.exe", WorkingDirectory);
        }

        public static void OpenExplorerLogs()
        {
            var info = new DirectoryInfo(Path.Join(_lastUnpackedPath, "levels"));
            if (info.Exists)
            {
                System.Diagnostics.Process.Start("explorer.exe", info.FullName);
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", Directory.GetParent(_nameLevelPath).FullName);
            }
        }
    }
}
