using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

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
        internal List<string> ScanForMissingFiles()
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Analyzing Errors in BeamNG logfile: {_fileName}");
            List<string> baseDivider = new List<string> { "|", "'" };
            List<string> errorDivider = new List<string> { "failed to load texture", "missing source texture", "failed to load" };
            baseDivider = baseDivider.Concat(errorDivider).ToList();
            foreach (string line in File.ReadLines(_fileName))
            {
                if (errorDivider.Select(x => x.ToUpperInvariant()).Any(y => line.Contains(y, StringComparison.OrdinalIgnoreCase)))
                {
                    var nameParts = line.ToUpperInvariant().Split(baseDivider.ToArray(), StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in nameParts)
                    {
                        var name = part.Trim();
                        if (name.StartsWith("."))
                        {
                            name = name.Remove(0, 1);
                        }
                        //if (name.Contains("slabs_huge_d")) Debugger.Break();
                        var toCheck = PathResolver.ResolvePath(_levelPath, name, true);
                        var checkForFile = new FileInfo(toCheck);
                        if (!checkForFile.Exists)
                        {
                            checkForFile = FileUtils.ResolveImageFileName(checkForFile.FullName);
                        }
                        if (checkForFile.Exists)
                        {
                            _excludeFiles.Add(checkForFile.FullName);
                        }
                    }
                }
            }
            return _excludeFiles.Distinct().ToList();
        }
    }
}
