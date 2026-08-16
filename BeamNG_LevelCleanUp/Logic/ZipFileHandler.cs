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
            // Always extract into the working directory. The selected zip stays untouched
            // at its original location and must never be copied or moved into the temp folder.
            retVal = Path.Join(WorkingDirectory, relativeTarget);
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

        // NEVER delete _lastUnpackedZip or _lastCopyFromUnpackedZip here: they point to the
        // user's ORIGINAL zip files at their original location, not to temp copies.
    }

    public static string GetLastUnpackedPath()
    {
        return _lastUnpackedPath;
    }

    /// <summary>
    /// Re-extracts the last selected copy-source zip to its original extraction folder.
    /// The copy pages only store paths into the extracted source level and re-read the files
    /// at copy time - but the extraction under temp can vanish mid-session (app startup wipes
    /// temp, the Create Level wizard cleans it, the other copy pages re-extract their own
    /// source into the same _copyFrom folder). This restores the extraction so an already
    /// scanned selection can still be copied.
    /// </summary>
    /// <returns>True if the source zip is known, still exists and was re-extracted.</returns>
    public static bool TryRestoreCopyFromExtraction()
    {
        try
        {
            if (string.IsNullOrEmpty(_lastCopyFromUnpackedZip) || string.IsNullOrEmpty(_lastCopyFromUnpackedPath))
                return false;

            var zipFile = new FileInfo(_lastCopyFromUnpackedZip);
            if (!zipFile.Exists)
                return false;

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Extracted source level is missing - restoring it from {zipFile.Name}...");

            var encoding = DetectZipEncoding(zipFile.FullName);
            ZipFile.ExtractToDirectory(zipFile.FullName, _lastCopyFromUnpackedPath, encoding, true);
            // Same normalization as the original extraction (moves the level under a levels\ folder if needed)
            GetLevelPath(_lastCopyFromUnpackedPath);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Source level restored to {_lastCopyFromUnpackedPath}");
            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not restore the extracted source level: {ex.Message}");
            return false;
        }
    }

    public static string GetLastUnpackedCopyFromPath()
    {
        return _lastCopyFromUnpackedPath;
    }

    public static void BuildDeploymentFile(string filePath, string levelName, CompressionLevel compressionLevel,
        bool searchLevelParent = false)
    {
        var fileName = $"{levelName}_deploy_{DateTime.Now.ToString("yyMMdd")}.zip";
        var targetDir = GetDeploymentTargetDirectory(filePath);
        var targetPath = Path.Join(targetDir, fileName);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Compressing Deploymentfile at {targetPath}");
        if (File.Exists(targetPath)) File.Delete(targetPath);

        var root = Path.GetFullPath(filePath);
        using (var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create, Encoding.UTF8))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsDeploymentToolArtifact(relative)) continue;
                archive.CreateEntryFromFile(file, relative, compressionLevel);
            }
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Deploymentfile created at {targetPath}");
    }

    /// <summary>
    /// Builds a deployment zip for a level that is edited in place (folder mode).
    /// Zips only the level folder itself (entries "levels/&lt;name&gt;/...") so sibling content
    /// in the user's folder (_copyFrom extraction, source zips, older deployment files,
    /// other levels) is not included. The deployment file is written next to the levels
    /// structure, i.e. into the folder the user selected.
    /// </summary>
    public static void BuildDeploymentFileFromFolder(string levelPath, string levelName, CompressionLevel compressionLevel)
    {
        var levelDir = new DirectoryInfo(levelPath);
        if (!levelDir.Exists)
            throw new Exception($"Level folder not found: {levelPath}");

        var parent = levelDir.Parent;
        var targetDir = parent != null && parent.Name.Equals("levels", StringComparison.OrdinalIgnoreCase)
            ? (parent.Parent ?? parent).FullName
            : (parent ?? levelDir).FullName;

        var fileName = $"{levelName}_deploy_{DateTime.Now.ToString("yyMMdd")}.zip";
        var targetPath = Path.Join(targetDir, fileName);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Compressing Deploymentfile at {targetPath}");
        if (File.Exists(targetPath)) File.Delete(targetPath);

        using (var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create, Encoding.UTF8))
        {
            foreach (var file in Directory.EnumerateFiles(levelDir.FullName, "*", SearchOption.AllDirectories))
            {
                if (file.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(levelDir.FullName, file).Replace('\\', '/');
                var entryName = $"levels/{levelDir.Name}/{relative}";
                if (IsDeploymentToolArtifact(entryName)) continue;
                archive.CreateEntryFromFile(file, entryName, compressionLevel);
            }
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Deploymentfile created at {targetPath}");
    }

    /// <summary>
    ///     Tool-generated reports can contain absolute local paths and may be written directly under
    ///     <c>levels</c>, inside a level folder, or in nested terrain-generation log directories.
    ///     Match path segments so wrapper-folder ZIP layouts are handled without dropping unrelated
    ///     level documentation such as a user-authored README.txt.
    /// </summary>
    internal static bool IsDeploymentToolArtifact(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var levelsIndex = Array.FindIndex(segments,
            segment => segment.Equals("levels", StringComparison.OrdinalIgnoreCase));
        if (levelsIndex < 0 || levelsIndex == segments.Length - 1) return false;

        var fileName = segments[^1];
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;

        if (segments.Skip(levelsIndex + 1).Take(segments.Length - levelsIndex - 2)
            .Any(segment => segment.Equals("logs", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (fileName.StartsWith("Log_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("DeletedAssetFiles", StringComparison.OrdinalIgnoreCase))
            return true;

        return fileName.Equals("DuplicateMaterials.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("MissingFilesFromBeamNgLog.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("MaterialFilesNotFound.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("DeletedTextureLinks.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("DanglingMaterialReferences.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("DegenerateDecalRoadsFixed.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("PathResolverLog.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves where the deployment zip is written. When the folder being zipped is the
    /// unpacked working copy of the last selected level zip, the deployment file goes next
    /// to that original zip - never into the temp folder, which is wiped on startup.
    /// Falls back to the parent of the zipped folder (legacy behavior) when there is no
    /// source zip (e.g. Create Level wizard) or the source lies inside the game installation.
    /// </summary>
    private static string GetDeploymentTargetDirectory(string zippedPath)
    {
        var fallback = new DirectoryInfo(zippedPath).Parent.FullName;
        try
        {
            if (string.IsNullOrEmpty(_lastUnpackedZip) || string.IsNullOrEmpty(_lastUnpackedPath))
                return fallback;

            var zippedFull = Path.GetFullPath(zippedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var unpackedFull = Path.GetFullPath(_lastUnpackedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!zippedFull.Equals(unpackedFull, StringComparison.OrdinalIgnoreCase))
                return fallback;

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(_lastUnpackedZip));
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
                return fallback;

            // Never write deployment files into the game installation (the game would mount them)
            var installDir = Steam.BeamInstallDir;
            if (!string.IsNullOrEmpty(installDir))
            {
                var installFull = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (sourceDir.Equals(installFull, StringComparison.OrdinalIgnoreCase) ||
                    sourceDir.StartsWith(installFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Source zip lies inside the game installation. Writing the deployment file to {fallback} instead.");
                    return fallback;
                }
            }

            return sourceDir;
        }
        catch
        {
            return fallback;
        }
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
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A level folder path is required.", nameof(path));

        // FindLevelRoot stores its result in a static field because this utility predates the
        // current multi-page UI. Never allow a failed scan to reuse the root from a previously
        // selected ZIP/folder.
        _nameLevelPath = null;
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Exists)
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
        else
        {
            throw new DirectoryNotFoundException($"Level folder not found: {dirInfo.FullName}");
        }

        return path;
    }

    public static string GetNamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A level folder path is required.", nameof(path));

        // See GetLevelPath: a missing/invalid new selection must never resolve to a stale level.
        _nameLevelPath = null;
        var dirInfo = new DirectoryInfo(path);
        if (dirInfo.Exists)
        {
            WalkDirectoryTree(dirInfo, "info.json", JobTypeEnum.FindLevelRoot);
            if (string.IsNullOrEmpty(_nameLevelPath))
                throw new Exception($"Can't find level data in {dirInfo.FullName}");
            path = _nameLevelPath;
        }
        else
        {
            throw new DirectoryNotFoundException($"Level folder not found: {dirInfo.FullName}");
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
