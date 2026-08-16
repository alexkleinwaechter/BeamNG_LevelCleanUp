namespace BeamNG_LevelCleanUp.Logic;

internal static class PathResolver
{
    private static readonly System.Text.RegularExpressions.Regex DuplicateLevelsSegmentPattern = new(
        @"(^|[\\/])levels[\\/](?:levels|game:levels)(?=([\\/]|$))",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string LevelNameCopyFrom;
    public static string LevelNamePathCopyFrom;
    public static string LevelName;
    public static string LevelNamePath;
    public static string LevelPath { get; set; }
    public static string LevelPathCopyFrom { get; set; }
    
    /// <summary>
    ///     Target terrain size for wizard mode level creation (power of 2, e.g., 2048)
    /// </summary>
    public static int? WizardTerrainSize { get; set; }

    public static string ResolvePath(string levelPath, string resourcePath, bool concatDistinctStrategy)
    {
        string retVal = null;
        var toReplaceDelim = '/';
        var delim = '\\';
        //if (resourcePath.Contains("D:\\Temp\\Test_Cleanup\\_unpacked\\levels\\TSH\\ART\\road\\asphaltroad_laned_centerline_n.dds")) Debugger.Break();
        resourcePath = resourcePath.Replace("//", "/");
        if (Path.IsPathRooted(resourcePath) && Path.IsPathFullyQualified(resourcePath))
            retVal = resourcePath;
        else if (concatDistinctStrategy)
            retVal = DirectorySanitizer(string.Join(
                    new string(delim, 1),
                    levelPath.Split(delim).Select(x => x.ToUpperInvariant())
                        .Concat(resourcePath.ToUpperInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct()
                        .ToArray())
                .Replace("\\\\", "\\"));
        else
            retVal = DirectorySanitizer(Path.Join(levelPath, resourcePath.Replace(toReplaceDelim, delim)));
        WriteToLog(retVal);
        return retVal;
    }

    public static string ResolvePathBasedOnCsFilePath(FileInfo csFile, string resourcePath)
    {
        string retVal = null;
        var toReplaceDelim = '/';
        var delim = '\\';
        //if (resourcePath.Contains("D:\\Temp\\Test_Cleanup\\_unpacked\\levels\\TSH\\ART\\road\\asphaltroad_laned_centerline_n.dds")) Debugger.Break();
        retVal = DirectorySanitizer(string.Join(
                new string(delim, 1),
                csFile.DirectoryName.Split(delim).Select(x => x.ToUpperInvariant())
                    .Concat(resourcePath.ToUpperInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct()
                    .ToArray())
            .Replace("\\\\", "\\"));
        WriteToLog(retVal);
        return retVal;
    }

    public static string DirectorySanitizer(string path)
    {
        // Collapse only complete path segments. A prefix replacement would corrupt valid level
        // names such as Levels2 or LevelsvilleUSA and could make referenced assets look orphaned.
        return DuplicateLevelsSegmentPattern.Replace(path, "$1levels");
    }

    private static void WriteToLog(string line)
    {
#if DEBUG
        using StreamWriter file = new(Path.Join(LevelPath, "PathResolverLog.txt"), true);
        file.WriteLine(line);
#endif
    }
}
