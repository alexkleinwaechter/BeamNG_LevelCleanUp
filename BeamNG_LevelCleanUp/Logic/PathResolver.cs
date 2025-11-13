namespace BeamNG_LevelCleanUp.Logic;

internal static class PathResolver
{
    public static string LevelNameCopyFrom;
    public static string LevelNamePathCopyFrom;
    public static string LevelName;
    public static string LevelNamePath;
    public static string LevelPath { get; set; }
    public static string LevelPathCopyFrom { get; set; }

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
        return path
            .Replace(@"levels\levels", "levels")
            .Replace(@"levels\game:levels", "levels");
    }

    private static void WriteToLog(string line)
    {
#if DEBUG
        using StreamWriter file = new(Path.Join(LevelPath, "PathResolverLog.txt"), true);
        file.WriteLine(line);
#endif
    }
}