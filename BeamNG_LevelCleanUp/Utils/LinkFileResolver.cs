using System.IO.Compression;
using System.Text.Json;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Resolves BeamNG .link files to actual content streams.
/// Link files reference assets stored in ZIP archives within the game installation.
/// 
/// A .link file contains JSON with a "path" property pointing to an asset in the
/// game's content/assets/ folder structure, stored in ZIP archives.
/// 
/// Example: A .link file with path "/assets/materials/decal/asphalt/texture.dds"
/// will be resolved from "content/decal.zip" where the ZIP contains the full path
/// "assets/materials/decal/asphalt/texture.dds" as an entry.
/// </summary>
public static class LinkFileResolver
{
    /// <summary>
    /// Supported image extensions for fallback searching.
    /// BeamNG materials may reference .png but actual file could be .dds (or vice versa).
    /// </summary>
    private static readonly string[] ImageExtensions = { ".dds", ".png", ".jpg", ".jpeg", ".tga" };

    /// <summary>
    /// JSON structure for .link files
    /// </summary>
    private class LinkFileContent
    {
        public string path { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks if a file path is a .link reference file.
    /// </summary>
    public static bool IsLinkFile(string filePath)
    {
        return !string.IsNullOrEmpty(filePath) &&
               filePath.EndsWith(".link", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a .link file and returns the actual content as a stream.
    /// Returns null if resolution fails.
    /// </summary>
    /// <param name="linkFilePath">Path to the .link file</param>
    /// <returns>MemoryStream containing the file content, or null if not found</returns>
    public static MemoryStream? ResolveToStream(string linkFilePath)
    {
        try
        {
            if (!File.Exists(linkFilePath))
                return null;

            // Read and parse the link file
            var linkJson = File.ReadAllText(linkFilePath);
            var linkContent = JsonSerializer.Deserialize<LinkFileContent>(linkJson);

            if (string.IsNullOrEmpty(linkContent?.path))
                return null;

            return ResolveAssetPath(linkContent.path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a BeamNG asset path (from link file) to a content stream.
    /// Traverses the path to find the ZIP file and extracts content from it.
    /// 
    /// BeamNG ZIPs contain the full path structure. For example:
    /// - ZIP location: content/decal.zip
    /// - Path in link: /assets/materials/decal/asphalt/texture.dds
    /// - Entry in ZIP: assets/materials/decal/asphalt/texture.dds (full path preserved)
    /// </summary>
    /// <param name="assetPath">Virtual asset path (e.g., "/assets/materials/decal/...")</param>
    /// <returns>MemoryStream containing the file content, or null if not found</returns>
    public static MemoryStream? ResolveAssetPath(string assetPath)
    {
        try
        {
            var installDir = GameDirectoryService.GetInstallDirectory();
            if (string.IsNullOrEmpty(installDir))
                return null;

            // Normalize the path - keep forward slashes for ZIP entry matching
            var normalizedPath = assetPath.TrimStart('/');
            
            // The base path for assets
            var assetsBasePath = Path.Combine(installDir, "content");

            // Split path into segments for finding the ZIP file
            var segments = normalizedPath.Split('/');

            // Try to find a ZIP file by traversing segments
            var currentPath = assetsBasePath;

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var potentialZipPath = Path.Combine(currentPath, segment + ".zip");
                var potentialDirPath = Path.Combine(currentPath, segment);

                // Check if this segment is a ZIP file
                if (File.Exists(potentialZipPath))
                {
                    // Found the ZIP! The full normalized path is the entry path in the ZIP
                    // BeamNG ZIPs contain the complete path structure
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
    /// Checks if a path has an image extension.
    /// </summary>
    private static bool HasImageExtension(string path)
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
    private static MemoryStream? ExtractFromZip(string zipPath, string fullPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            // Normalize the search path - convert to forward slashes for comparison
            var normalizedSearchPath = fullPath.Replace('\\', '/');
            
            // Try all path variations (original + alternate extensions)
            foreach (var pathVariation in GetPathVariations(normalizedSearchPath))
            {
                // Find entry by comparing normalized paths (handles both / and \ in ZIP entries)
                var entry = archive.GetEntry(pathVariation);
                //var entry = archive.Entries.FirstOrDefault(e =>
                //{
                //    var normalizedEntryName = e.FullName.Replace('\\', '/');
                //    return normalizedEntryName.Equals(pathVariation, StringComparison.OrdinalIgnoreCase);
                //});

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

    /// <summary>
    /// Resolves a .link file or regular file and returns content as a stream.
    /// For regular files, reads directly from disk.
    /// For .link files, resolves from ZIP archives.
    /// </summary>
    /// <param name="filePath">Path to file (may or may not be a .link file)</param>
    /// <returns>MemoryStream containing file content, or null if not found</returns>
    public static MemoryStream? GetFileStream(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        if (IsLinkFile(filePath))
        {
            return ResolveToStream(filePath);
        }

        // Regular file - read directly
        if (File.Exists(filePath))
        {
            var ms = new MemoryStream();
            using var fs = File.OpenRead(filePath);
            fs.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        return null;
    }

    /// <summary>
    /// Gets the actual file extension from a path, ignoring .link suffix.
    /// </summary>
    /// <param name="filePath">File path (may end with .link)</param>
    /// <returns>The actual extension (e.g., ".png", ".dds")</returns>
    public static string GetActualExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        // Remove .link suffix if present
        var path = filePath;
        if (IsLinkFile(path))
        {
            path = path[..^5]; // Remove ".link"
        }

        return Path.GetExtension(path);
    }

    /// <summary>
    /// Gets the actual file name from a path, ignoring .link suffix.
    /// </summary>
    /// <param name="filePath">File path (may end with .link)</param>
    /// <returns>The actual file name without .link extension</returns>
    public static string GetActualFileName(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        // Remove .link suffix if present
        var path = filePath;
        if (IsLinkFile(path))
        {
            path = path[..^5]; // Remove ".link"
        }

        return Path.GetFileName(path);
    }

    /// <summary>
    /// Checks if the file content is available (either directly or via link resolution).
    /// This is useful for checking if a .link file can actually be resolved.
    /// </summary>
    /// <param name="filePath">Path to file (may or may not be a .link file)</param>
    /// <returns>True if the file content can be accessed</returns>
    public static bool CanResolve(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // For regular files, just check existence
        if (!IsLinkFile(filePath))
            return File.Exists(filePath);

        // For .link files, try to resolve
        using var stream = ResolveToStream(filePath);
        return stream != null;
    }
}
