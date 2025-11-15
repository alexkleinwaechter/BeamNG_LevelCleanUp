using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Helper class to find vegetation items.level.json files containing GroundCover objects
/// </summary>
public static class VegetationFileHelper
{
    /// <summary>
    ///     Finds the first items.level.json file that contains GroundCover objects in the target level
    ///     Returns null if no such file is found
    /// </summary>
    public static FileInfo FindTargetVegetationFile(string levelPath)
    {
        if (string.IsNullOrEmpty(levelPath) || !Directory.Exists(levelPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Level path not found: {levelPath}");
            return null;
        }

        var vegetationFiles = FindAllVegetationFiles(levelPath);

        if (!vegetationFiles.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No vegetation files with GroundCover objects found in target level. Will create new file at default location.");
            return null;
        }

        // Return the first one found
        var selectedFile = vegetationFiles.First();
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found target vegetation file: {selectedFile.FullName}");

        return selectedFile;
    }

    /// <summary>
    ///     Finds all items.level.json files that contain GroundCover objects
    /// </summary>
    public static List<FileInfo> FindAllVegetationFiles(string levelPath)
    {
        var vegetationFiles = new List<FileInfo>();
        var dirInfo = new DirectoryInfo(levelPath);

        if (!dirInfo.Exists)
            return vegetationFiles;

        FindVegetationFilesRecursive(dirInfo, vegetationFiles);

        return vegetationFiles;
    }

    /// <summary>
    ///     Recursively searches for items.level.json files containing GroundCover objects
    /// </summary>
    private static void FindVegetationFilesRecursive(DirectoryInfo root, List<FileInfo> results)
    {
        try
        {
            // Search for items.level.json files in current directory
            var files = root.GetFiles("items.level.json", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
                // Check if this file contains GroundCover entries
                if (File.Exists(file.FullName))
                {
                    var fileContent = File.ReadAllText(file.FullName);
                    if (fileContent.Contains("\"class\":\"GroundCover\"") ||
                        fileContent.Contains("\"class\": \"GroundCover\"")
                        || fileContent.Contains("\"class\":\"Forest\"")
                        || fileContent.Contains("\"class\": \"Forest\""))
                    {
                        results.Add(file);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Found vegetation file with GroundCover: {file.FullName}");
                    }
                }

            // Recurse into subdirectories
            foreach (var subDir in root.GetDirectories()) FindVegetationFilesRecursive(subDir, results);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (DirectoryNotFoundException)
        {
            // Skip directories that don't exist
        }
    }

    /// <summary>
    ///     Gets the default vegetation file path for creating a new file if none exists
    /// </summary>
    public static string GetDefaultVegetationFilePath(string levelNamePath)
    {
        // Try common locations in order of preference
        var preferredPaths = new[]
        {
            Path.Join(levelNamePath, "main", "MissionGroup", "vegetation", "items.level.json"),
            Path.Join(levelNamePath, "main", "MissionGroup", "Level_object", "vegetation", "items.level.json"),
            Path.Join(levelNamePath, "main", "vegetation", "items.level.json")
        };

        // Check if any of the parent directories exist
        foreach (var path in preferredPaths)
        {
            var directory = Path.GetDirectoryName(path);
            if (Directory.Exists(directory))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Using existing vegetation directory: {directory}");
                return path;
            }
        }

        // Default to first option and create directory
        var defaultPath = preferredPaths[0];
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"No existing vegetation directory found. Will create at default location: {defaultPath}");

        return defaultPath;
    }
}