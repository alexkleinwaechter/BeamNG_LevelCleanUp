using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal static class PathResolver
    {
        internal static string ResolvePath(string levelPath, string resourcePath, bool concatDistinctStrategy)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            if (Path.IsPathRooted(resourcePath) && Path.IsPathFullyQualified(resourcePath))
            {
                return resourcePath;
            }
            if (concatDistinctStrategy)
            {
                return DirectorySanitizer(string.Join(
                    new string(delim, 1),
                    levelPath.Split(delim).Select(x => x.ToLowerInvariant()).Concat(resourcePath.ToLowerInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                    .Replace("\\\\", "\\"));
            }
            else
            {
                return DirectorySanitizer(Path.Join(levelPath, resourcePath.Replace(toReplaceDelim, delim)));
            }
        }

        internal static string ResolvePathBasedOnCsFilePath(FileInfo csFile, string resourcePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return DirectorySanitizer(string.Join(
                new string(delim, 1),
                csFile.DirectoryName.Split(delim).Select(x => x.ToLowerInvariant()).Concat(resourcePath.ToLowerInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                .Replace("\\\\", "\\"));
        }

        internal static string DirectorySanitizer(string path)
        {
            return path.Replace(@"levels\levels", "levels");
        }
    }
}
