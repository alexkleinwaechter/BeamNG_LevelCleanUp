using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class BeamLogReader
    {
        private string _fileName { get; set; }
        private string _levelPath { get; set; }
        private List<string> _excludeFiles = new List<string>();

        internal BeamLogReader(string fileName, string levelPath)
        {
            _fileName = fileName;
            _levelPath = levelPath;
        }

        private string ResolvePath(string resourcePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return string.Join(
                new string(delim, 1),
                _levelPath.Split(delim).Select(x => x.ToLowerInvariant()).Concat(resourcePath.ToLowerInvariant().Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                .Replace("\\\\", "\\");
        }

        internal List<string> ScanForMissingFiles()
        {
            foreach (string line in File.ReadLines(_fileName))
            {
                if (line.Contains("Missing source texture"))
                {
                    var nameParts = line.Split("Missing source texture");
                    var name = nameParts[1].Trim();
                    if (name.StartsWith("."))
                    {
                        name = name.Remove(0, 1);
                    }
                    //if (name.Contains("slabs_huge_d")) Debugger.Break();
                    var toCheck = ResolvePath(name);
                    var checkForFile = new FileInfo(toCheck);
                    if (!checkForFile.Exists)
                    {
                        checkForFile = CheckMissingExtensions(checkForFile);
                    }
                    if (checkForFile.Exists)
                    {
                        _excludeFiles.Add(checkForFile.FullName);
                    }
                }
            }
            return _excludeFiles;
        }

        internal FileInfo CheckMissingExtensions(FileInfo fileInfo)
        {

            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".dds");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".png");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpg");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpeg");
                fileInfo = new FileInfo(ddsPath);
            }
            return fileInfo;
        }
    }
}
