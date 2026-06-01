namespace FbxToDae;

/// <summary>
/// Converts a filesystem path to a BeamNG-absolute level path of the form
/// "/levels/&lt;level&gt;/art/shapes/...". BeamNG reads texture paths in materials.json
/// relative to the game's virtual root, which starts at "/levels/".
/// </summary>
public static class BeamngPath
{
    /// <summary>
    /// Derives the BeamNG-absolute prefix (with trailing '/') for the given filesystem
    /// directory by locating the "levels" segment in the path.
    ///
    /// Examples:
    ///   "C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\rochester\art\shapes\bungalow"
    ///   -> "/levels/rochester/art/shapes/bungalow/"
    ///
    /// Returns null if no "levels" segment is found — caller should fall back to bare
    /// filenames and warn the user that the output won't be resolvable by BeamNG.
    /// </summary>
    public static string? DeriveLevelRelativePrefix(string targetDir)
    {
        var normalized = targetDir.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("levels", StringComparison.OrdinalIgnoreCase))
                return "/" + string.Join('/', segments[i..]) + "/";
        }
        return null;
    }
}
