using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal static class PathResolver
    {
        internal static string LevelPath { get; set; }
        internal static string LevelPathCopyFrom { get; set; }
        internal static string ResolvePath(string levelPath, string resourcePath, bool concatDistinctStrategy)
        {
            string retVal = null;
            char toReplaceDelim = '/';
            char delim = '\\';
            //if (resourcePath.Contains("D:\\Temp\\Test_Cleanup\\_unpacked\\levels\\TSH\\ART\\road\\asphaltroad_laned_centerline_n.dds")) Debugger.Break();
            if (Path.IsPathRooted(resourcePath) && Path.IsPathFullyQualified(resourcePath))
            {
                retVal = resourcePath;
            }
            else if (concatDistinctStrategy)
            {
                retVal = DirectorySanitizer(string.Join(
                    new string(delim, 1),
                    levelPath.Split(delim).Select(x => x.ToUpperInvariant()).Concat(resourcePath.ToUpperInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                    .Replace("\\\\", "\\"));
            }
            else
            {
                retVal = DirectorySanitizer(Path.Join(levelPath, resourcePath.Replace(toReplaceDelim, delim)));
            }
            WriteToLog(retVal);
            return retVal;
        }

        internal static string ResolvePathBasedOnCsFilePath(FileInfo csFile, string resourcePath)
        {
            string retVal = null;
            char toReplaceDelim = '/';
            char delim = '\\';
            //if (resourcePath.Contains("D:\\Temp\\Test_Cleanup\\_unpacked\\levels\\TSH\\ART\\road\\asphaltroad_laned_centerline_n.dds")) Debugger.Break();
            retVal = DirectorySanitizer(string.Join(
                new string(delim, 1),
                csFile.DirectoryName.Split(delim).Select(x => x.ToUpperInvariant()).Concat(resourcePath.ToUpperInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                .Replace("\\\\", "\\"));
            WriteToLog(retVal);
            return retVal;
        }

        internal static string DirectorySanitizer(string path)
        {
            return path
                .Replace(@"levels\levels", "levels")
                .Replace(@"levels\game:levels", "levels");
        }

        private static void WriteToLog(string line) {
#if DEBUG
            using StreamWriter file = new(Path.Join(LevelPath, "PathResolverLog.txt"), append: true);
            file.WriteLine(line);
#endif
        }
    }
}
