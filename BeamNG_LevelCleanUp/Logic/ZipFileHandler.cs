using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

public static class ZipFileHandler
{
    public enum JobTypeEnum
    {
        FindLevelRoot = 0
    }

    private static readonly StringCollection log = new();

    static ZipFileHandler()
    {
        // Register code page encoding provider for .NET 9
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static string _nameLevelPath { get; set; }
    private static string _lastUnpackedPath { get; set; }
    private static string _lastCopyFromUnpackedPath { get; set; }
    private static string _lastUnpackedZip { get; set; }
    private static string _lastCopyFromUnpackedZip { get; set; }
    
    private static string _workingDirectory;
    
    /// <summary>
    /// Gets or sets the working directory for extraction operations.
    /// Defaults to AppPaths.TempFolder when not explicitly set.
    /// </summary>
    public static string WorkingDirectory 
    { 
        get => _workingDirectory ?? AppPaths.TempFolder;
        set => _workingDirectory = value;
    }

    /// <summary>
    /// Resets working directory to the default centralized temp folder.
    /// Call this instead of setting WorkingDirectory = null.
    /// </summary>
    public static void ResetToDefaultWorkingDirectory()
    {
        _workingDirectory = AppPaths.TempFolder;
    }

    /// <summary>
    /// Resets all static path references. Call this on application startup
    /// when cleaning up stale temp folders from previous sessions.
    /// Note: This does NOT affect BeamFileReader static state used for "previously loaded level" detection.
    /// </summary>
    public static void ResetStaticPaths()
    {
        _lastUnpackedPath = null;
        _lastCopyFromUnpackedPath = null;
        _lastUnpackedZip = null;
        _lastCopyFromUnpackedZip = null;
        _nameLevelPath = null;
    }

    /// <summary>
    /// Resets only the unpacked path references. Call this when cleaning up the _unpacked folder.
    /// </summary>
    public static void ResetUnpackedPaths()
    {
        _lastUnpackedPath = null;
        _lastUnpackedZip = null;
    }

    /// <summary>
    /// Resets only the copyFrom path references. Call this when cleaning up the _copyFrom folder.
    /// </summary>
    public static void ResetCopyFromPaths()
    {
        _lastCopyFromUnpackedPath = null;
        _lastCopyFromUnpackedZip = null;
    }

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
            if (deleteDir.Exists) Directory.Delete(retVal, true);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Unzipping to {retVal}");

            // Detect the correct encoding for the ZIP file
            var encoding = DetectZipEncoding(fi.FullName);
            ZipFile.ExtractToDirectory(fi.FullName, retVal, encoding, true);

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
            if (deleteDir.Exists) Directory.Delete(_lastUnpackedPath, true);
            _lastUnpackedPath = null; // Reset after cleanup
        }

        if (!string.IsNullOrEmpty(_lastCopyFromUnpackedPath))
        {
            var deleteDir = new DirectoryInfo(_lastCopyFromUnpackedPath);
            if (deleteDir.Exists) Directory.Delete(_lastCopyFromUnpackedPath, true);
            _lastCopyFromUnpackedPath = null; // Reset after cleanup
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

    public static void BuildDeploymentFile(string filePath, string levelName, CompressionLevel compressionLevel,
        bool searchLevelParent = false)
    {
        var fileName = $"{levelName}_deploy_{DateTime.Now.ToString("yyMMdd")}.zip";
        var targetDir = new DirectoryInfo(filePath).Parent.FullName;
        var targetPath = Path.Join(targetDir, fileName);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Compressing Deploymentfile at {targetPath}");
        if (File.Exists(targetPath)) File.Delete(targetPath);
        ZipFile.CreateFromDirectory(filePath, targetPath, compressionLevel, false, Encoding.UTF8);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Deploymentfile created at {targetPath}");
    }

    private static Encoding DetectZipEncoding(string zipPath)
    {
        // Try UTF-8 first and check if entry names contain valid UTF-8 characters
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.UTF8))
            {
                foreach (var entry in archive.Entries)
                    // Check if the entry name contains replacement characters which indicate encoding issues
                    if (entry.FullName.Contains('\uFFFD'))
                        // UTF-8 failed, try common fallback encodings
                        // Try code page 850 (Western European - commonly used by 7-Zip)
                        try
                        {
                            return Encoding.GetEncoding(850);
                        }
                        catch
                        {
                            // Fallback to code page 437 (IBM PC)
                            try
                            {
                                return Encoding.GetEncoding(437);
                            }
                            catch
                            {
                                // Last resort: use Latin1/ISO-8859-1
                                return Encoding.Latin1;
                            }
                        }

                return Encoding.UTF8;
            }
        }
        catch
        {
            // If UTF-8 fails completely, try fallback encodings
            try
            {
                return Encoding.GetEncoding(850);
            }
            catch
            {
                try
                {
                    return Encoding.GetEncoding(437);
                }
                catch
                {
                    return Encoding.Latin1;
                }
            }
        }
    }

    public static void RemoveModInfo(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo != null)
            try
            {
                var miPath = Path.Join(dirInfo.FullName, "mod_info");
                if (Directory.Exists(miPath)) Directory.Delete(miPath, true);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error: {ex.Message}");
            }
    }

    public static string GetLevelPath(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo != null)
        {
            WalkDirectoryTree(dirInfo, "info.json", JobTypeEnum.FindLevelRoot);
            if (string.IsNullOrEmpty(_nameLevelPath))
                throw new Exception($"Can't find level data in {dirInfo.FullName}");
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
                throw new Exception($"Can't find level data in {dirInfo.FullName}");
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
            foreach (var fi in files)
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
                        if (mainDir.FirstOrDefault() != null) _nameLevelPath = fi.Directory.FullName;
                        if (mainDir.Length == 0)
                        {
                            mainDir = fi.Directory.GetDirectories("art");
                            if (mainDir.FirstOrDefault() != null) _nameLevelPath = fi.Directory.FullName;
                        }

                        break;
                }
            }

            // Now find all the subdirectories under this directory.
            subDirs = root.GetDirectories();

            foreach (var dirInfo in subDirs)
                // Resursive call for each subdirectory.
                WalkDirectoryTree(dirInfo, filePattern, jobTypeEnum);
        }
    }

    public static void OpenExplorer()
    {
        Process.Start("explorer.exe", WorkingDirectory);
    }

    public static void OpenExplorerLogs()
    {
        var info = new DirectoryInfo(Path.Join(_lastUnpackedPath, "levels"));
        if (info.Exists)
            Process.Start("explorer.exe", info.FullName);
        else
            Process.Start("explorer.exe", Directory.GetParent(_nameLevelPath).FullName);
    }
}