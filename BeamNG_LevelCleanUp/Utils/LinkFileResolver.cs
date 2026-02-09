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
        return ZipAssetExtractor.ExtractAsset(assetPath);
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
