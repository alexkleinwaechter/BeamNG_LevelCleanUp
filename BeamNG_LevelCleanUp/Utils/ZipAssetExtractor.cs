using System.IO.Compression;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Extracts assets from ZIP archives in BeamNG's content folder.
/// Handles traversing path segments to find the appropriate ZIP file.
/// </summary>
public static class ZipAssetExtractor
{
    /// <summary>
    /// Supported image extensions for fallback searching.
    /// BeamNG materials may reference .png but actual file could be .dds (or vice versa).
    /// </summary>
    private static readonly string[] ImageExtensions = { ".dds", ".png", ".jpg", ".jpeg", ".tga" };

    /// <summary>
    /// Attempts to extract a file from BeamNG's content ZIP archives.
    /// Traverses the path to find the appropriate ZIP file and extracts the content.
    /// </summary>
    /// <param name="assetPath">Asset path (e.g., "assets/materials/tileable/stone/...")</param>
    /// <param name="basePath">Base path to search for ZIPs (defaults to game content folder)</param>
    /// <returns>MemoryStream containing the file content, or null if not found</returns>
    public static MemoryStream? ExtractAsset(string assetPath, string? basePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(basePath))
            {
                var installDir = GameDirectoryService.GetInstallDirectory();
                if (string.IsNullOrEmpty(installDir))
                    return null;
                basePath = Path.Combine(installDir, "content");
            }

            // Normalize the path - use forward slashes for ZIP entry matching
            var normalizedPath = assetPath.TrimStart('/', '\\').Replace('\\', '/');

            // Split path into segments for finding the ZIP file
            var segments = normalizedPath.Split('/');
            var currentPath = basePath;

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var potentialZipPath = Path.Combine(currentPath, segment + ".zip");
                var potentialDirPath = Path.Combine(currentPath, segment);

                // Check if this segment is a ZIP file
                if (File.Exists(potentialZipPath))
                {
                    // Found the ZIP! The full normalized path is the entry path in the ZIP
                    return ExtractFromZip(potentialZipPath, normalizedPath);
                }

                // Continue traversing directories
                if (Directory.Exists(potentialDirPath))
                {
                    currentPath = potentialDirPath;
                }
                else
                {
                    // Directory doesn't exist, can't continue
                    break;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a file from BeamNG's content ZIP archives and write it to a target file.
    /// </summary>
    /// <param name="assetPath">Asset path (e.g., "assets/materials/tileable/stone/...")</param>
    /// <param name="targetFilePath">Target file path to write the extracted content</param>
    /// <param name="basePath">Base path to search for ZIPs (defaults to game content folder)</param>
    /// <returns>True if extraction was successful, false otherwise</returns>
    public static bool ExtractAssetToFile(string assetPath, string targetFilePath, string? basePath = null)
    {
        using var stream = ExtractAsset(assetPath, basePath);
        if (stream == null)
            return false;

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        using var fileStream = File.Create(targetFilePath);
        stream.CopyTo(fileStream);
        return true;
    }

    /// <summary>
    /// Checks if a path has an image extension.
    /// </summary>
    public static bool HasImageExtension(string path)
    {
        return ImageExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Generates alternative paths with different image extensions.
    /// BeamNG materials may reference .png but actual file could be .dds (or vice versa).
    /// </summary>
    private static IEnumerable<string> GetPathVariations(string path)
    {
        // First, try the original path as-is
        yield return path;

        // If this is an image file, try other extensions
        if (HasImageExtension(path))
        {
            var pathWithoutExt = path.Substring(0, path.LastIndexOf('.'));
            foreach (var ext in ImageExtensions)
            {
                var variation = pathWithoutExt + ext;
                if (!variation.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    yield return variation;
                }
            }
        }
    }

    /// <summary>
    /// Extracts a file from a ZIP archive and returns it as a MemoryStream.
    /// Uses normalized path comparison to handle different path separator styles.
    /// Also tries different image extensions (BeamNG may have .dds when material references .png).
    /// </summary>
    public static MemoryStream? ExtractFromZip(string zipPath, string fullPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            // Normalize the search path - convert to forward slashes for comparison
            var normalizedSearchPath = fullPath.Replace('\\', '/');

            // Try all path variations (original + alternate extensions)
            foreach (var pathVariation in GetPathVariations(normalizedSearchPath))
            {
                // Find entry by comparing normalized paths
                var entry = archive.GetEntry(pathVariation);

                // Try without the first segment (e.g., without "assets/")
                if (entry == null)
                {
                    var firstSlash = pathVariation.IndexOf('/');
                    if (firstSlash > 0)
                    {
                        var withoutFirstSegment = pathVariation.Substring(firstSlash + 1);
                        entry = archive.Entries.FirstOrDefault(e =>
                        {
                            var normalizedEntryName = e.FullName.Replace('\\', '/');
                            return normalizedEntryName.Equals(withoutFirstSegment, StringComparison.OrdinalIgnoreCase);
                        });
                    }
                }

                if (entry != null)
                {
                    // Found it! Extract to memory stream
                    var memoryStream = new MemoryStream();
                    using (var entryStream = entry.Open())
                    {
                        entryStream.CopyTo(memoryStream);
                    }
                    memoryStream.Position = 0;
                    return memoryStream;
                }
            }

            // Last resort: try matching just the filename with any extension
            var fileName = Path.GetFileNameWithoutExtension(normalizedSearchPath);
            if (!string.IsNullOrEmpty(fileName))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                {
                    var entryFileName = Path.GetFileNameWithoutExtension(e.FullName);
                    return entryFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                           HasImageExtension(e.FullName);
                });

                if (entry != null)
                {
                    var memoryStream = new MemoryStream();
                    using (var entryStream = entry.Open())
                    {
                        entryStream.CopyTo(memoryStream);
                    }
                    memoryStream.Position = 0;
                    return memoryStream;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
