namespace BeamNG_LevelCleanUp.Logic;

internal class CustomChangesChecker
{
    private readonly string _levelName;
    private readonly string _unpackedPath;
    private string _levelFolderPathChanges;

    internal CustomChangesChecker(string levelName, string unpackedPath)
    {
        _levelName = levelName;
        _unpackedPath = unpackedPath;
    }

    /// <summary>
    ///     Gets the user folder path for BeamNG levels, trying the new path structure first, then falling back to versioned
    ///     folders
    /// </summary>
    /// <returns>The base user folder path or null if not found</returns>
    internal string GetUserFolderPath()
    {
        // New path to user folder with "current" subfolder
        var newUserFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG", "BeamNG.drive", "current");

        // Check if the new path exists
        if (Directory.Exists(newUserFolderPath)) return newUserFolderPath;

        // Fallback: Old path for older versions
        var oldUserFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG.drive");

        // Check if the old user folder exists
        if (!Directory.Exists(oldUserFolderPath)) return null;

        // Get all subfolders and find the highest version number
        var versionFolders = Directory.GetDirectories(oldUserFolderPath)
            .Select(Path.GetFileName)
            .Where(folderName => Version.TryParse(folderName, out _)) // Only valid version folders
            .OrderByDescending(folderName => Version.Parse(folderName)) // Sort descending
            .ToList();

        if (!versionFolders.Any()) return null; // No valid version folders found

        // Get highest version number
        return Path.Combine(oldUserFolderPath, versionFolders.First());
    }

    internal bool HasCustomChanges()
    {
        var userFolderPath = GetUserFolderPath();
        if (userFolderPath == null) return false;

        // Check if the folder "/levels/[_levelName]" exists
        _levelFolderPathChanges = Path.Combine(userFolderPath, "levels", _levelName);
        return Directory.Exists(_levelFolderPathChanges);
    }

    internal bool CopyCustomChangesToUnpacked()
    {
        if (HasCustomChanges())
        {
            // Copy files and subdirectories
            CopyDirectory(_levelFolderPathChanges, _unpackedPath);
            return true;
        }

        return false;
    }

    internal void CopyChangesToUnpacked()
    {
        CopyDirectory(_levelFolderPathChanges, Path.Combine(_unpackedPath, _levelName));
    }

    /// <summary>
    ///     Copies content from the unpacked path to the BeamNG user levels folder
    /// </summary>
    /// <returns>The target directory path, or null if user folder path could not be determined</returns>
    internal string CopyUnpackedToUserFolder()
    {
        var userFolderPath = GetUserFolderPath();
        if (userFolderPath == null)
            throw new DirectoryNotFoundException("Could not determine BeamNG user folder path.");

        var sourcePath = Path.Combine(_unpackedPath, _levelName);
        var targetPath = Path.Combine(userFolderPath, "levels", _levelName);

        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"The source directory '{sourcePath}' was not found.");

        CopyDirectory(sourcePath, targetPath);
        return targetPath;
    }

    /// <summary>
    ///     Checks if the target directory exists in the BeamNG user levels folder
    /// </summary>
    /// <returns>True if the target directory exists, false otherwise</returns>
    internal bool TargetDirectoryExists()
    {
        var userFolderPath = GetUserFolderPath();
        if (userFolderPath == null) return false;

        var targetPath = Path.Combine(userFolderPath, "levels", _levelName);
        return Directory.Exists(targetPath);
    }

    /// <summary>
    ///     Deletes the target directory in the BeamNG user levels folder if it exists
    /// </summary>
    internal void DeleteTargetDirectory()
    {
        var userFolderPath = GetUserFolderPath();
        if (userFolderPath == null) return;

        var targetPath = Path.Combine(userFolderPath, "levels", _levelName);
        if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
    }

    internal string GetLevelFolderPathChanges()
    {
        return _levelFolderPathChanges;
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"The source directory '{sourceDir}' was not found.");

        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, true); // Overwrite existing files
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir); // Recursive call
        }
    }
}