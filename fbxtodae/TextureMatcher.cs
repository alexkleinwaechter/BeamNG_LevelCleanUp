namespace FbxToDae;

/// <summary>
/// Resolves {fbxBaseName}_d.* (diffuse) and {fbxBaseName}_n.* (normal) textures
/// from the texture source folder. Case-insensitive on filename (Windows FS is
/// case-insensitive anyway, but we compare invariant-lower to be safe).
/// </summary>
public sealed class TextureMatcher
{
    private readonly Dictionary<string, string> _byLowerName;

    public TextureMatcher(string textureDir)
    {
        if (!Directory.Exists(textureDir))
            throw new DirectoryNotFoundException($"Texture directory not found: {textureDir}");

        _byLowerName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Directory.EnumerateFiles(textureDir))
            _byLowerName.TryAdd(Path.GetFileName(p).ToLowerInvariant(), p);
    }

    /// <summary>
    /// Resolves the diffuse and normal textures for the given FBX base name.
    /// Returns (diffusePath, normalPath). Either may be null if not found.
    /// Tries .png first, then .jpg/.jpeg/.tga/.dds as fallbacks.
    ///
    /// If no texture is found AND the base name ends with a lowercase 'm',
    /// retries once with the trailing 'm' stripped. This matches the common
    /// "mirrored variant reuses base textures" convention (e.g., terraced7am.FBX
    /// reuses terraced7a_d.png / terraced7a_n.png). Safe because it only
    /// activates on primary miss — packs where the '*m' variant has its own
    /// textures (e.g., bungalow1bm_d.png) hit the primary lookup first.
    /// </summary>
    public (string? diffuse, string? normal) Resolve(string fbxBaseName)
    {
        return (Find(fbxBaseName, "_d"), Find(fbxBaseName, "_n"));
    }

    private string? Find(string baseName, string suffix)
    {
        return FindExact(baseName, suffix) ?? MirroredFallback(baseName, suffix);
    }

    private string? FindExact(string baseName, string suffix)
    {
        string[] exts = [".png", ".jpg", ".jpeg", ".tga", ".dds"];
        foreach (var ext in exts)
        {
            var candidate = (baseName + suffix + ext).ToLowerInvariant();
            if (_byLowerName.TryGetValue(candidate, out var path))
                return path;
        }
        return null;
    }

    private string? MirroredFallback(string baseName, string suffix)
    {
        if (baseName.Length < 2 || baseName[^1] != 'm') return null;
        return FindExact(baseName[..^1], suffix);
    }
}
