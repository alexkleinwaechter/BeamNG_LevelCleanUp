using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles path conversion and resolution for asset copying operations
/// </summary>
public class PathConverter
{
    private readonly string _levelName;
    private readonly string _levelNameCopyFrom;
    private readonly string _namePath;

    public PathConverter(string namePath, string levelName, string levelNameCopyFrom)
    {
        _namePath = namePath;
        _levelName = levelName;
        _levelNameCopyFrom = levelNameCopyFrom;
    }

    public string GetTargetFileName(string sourceName)
    {
        var fileName = Path.GetFileName(sourceName);
        var dir = Path.GetDirectoryName(sourceName);
        var targetParts = dir.ToLowerInvariant().Split($@"\levels\{_levelNameCopyFrom}\".ToLowerInvariant());
        if (targetParts.Count() < 2)
        {
            //PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error in {sourceName}. Exception:no levels folder in path.");
            targetParts = dir.ToLowerInvariant().Split(@"\levels\".ToLowerInvariant());
            if (targetParts.Count() == 2)
            {
                var pos = targetParts[1].IndexOf(@"\");
                if (pos >= 0) targetParts[1] = targetParts[1].Remove(0, pos);
            }
        }

        // Insert MT_ prefix right after the "art" directory to avoid duplicate materials
        // e.g., art/shapes/building/texture.png -> art/MT_source/shapes/building/texture.png
        var relativePath = targetParts.Last();
        var mtPrefix = $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}";
        relativePath = InsertMtPrefixAfterArt(relativePath, mtPrefix);

        return Path.Join(_namePath, relativePath, fileName);
    }

    /// <summary>
    ///     Inserts the MT_ prefix directory right after the "art" segment in a relative path.
    ///     If the path starts with "art\something", it becomes "art\MT_source\something".
    ///     If no "art" segment is found, the MT_ prefix is prepended to the path.
    /// </summary>
    private static string InsertMtPrefixAfterArt(string relativePath, string mtPrefix)
    {
        if (string.IsNullOrEmpty(relativePath))
            return mtPrefix;

        // Normalize to backslashes for splitting
        var normalized = relativePath.Replace("/", "\\");
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > 0 && segments[0].Equals("art", StringComparison.OrdinalIgnoreCase))
        {
            // Insert MT_ right after "art": art\MT_source\rest\of\path
            var result = new List<string> { segments[0], mtPrefix };
            result.AddRange(segments.Skip(1));
            return Path.Join(result.ToArray());
        }

        // No "art" prefix found - put MT_ at the beginning
        return Path.Join(mtPrefix, normalized);
    }

    public string GetTerrainTargetFileName(string sourceName)
    {
        var fileName = Path.GetFileName(sourceName);
        // All terrain textures go directly to art/terrains folder
        return Path.Join(_namePath, Constants.Terrains, fileName);
    }

    /// <summary>
    ///     Converts a Windows file path to a BeamNG JSON path format.
    ///     Strips .link extension since materials.json should reference actual texture paths without .link.
    /// </summary>
    public string GetBeamNgJsonPathOrFileName(string windowsFileName, bool removeExtension = true)
    {
        // Strip .link extension - BeamNG uses these as virtual redirects
        // but materials.json should reference the actual texture path without .link
        windowsFileName = FileUtils.StripLinkExtension(windowsFileName);
        
        // Normalize path separators to forward slashes for comparison
        var normalizedPath = windowsFileName.Replace(@"\", "/").ToLowerInvariant();

        var targetParts = normalizedPath.Split("/levels/");
        if (targetParts.Count() < 2)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Filepath error in {windowsFileName}. Exception:no levels folder in path.");
            return string.Empty;
        }

        // Build the BeamNG path format (always starts without leading slash)
        var beamNgPath = "levels/" + targetParts.Last();

        // Remove extension
        if (removeExtension) return Path.ChangeExtension(beamNgPath, null);

        return beamNgPath;
    }
}