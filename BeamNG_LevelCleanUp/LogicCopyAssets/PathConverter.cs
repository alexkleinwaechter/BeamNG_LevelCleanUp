using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles path conversion and resolution for asset copying operations
    /// </summary>
    public class PathConverter
    {
        private readonly string _namePath;
        private readonly string _levelName;
        private readonly string _levelNameCopyFrom;

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
                targetParts = dir.ToLowerInvariant().Split($@"\levels\".ToLowerInvariant());
                if (targetParts.Count() == 2)
                {
                    int pos = targetParts[1].IndexOf(@"\");
                    if (pos >= 0)
                    {
                        targetParts[1] = targetParts[1].Remove(0, pos);
                    }
                }
            }
            return Path.Join(_namePath, targetParts.Last(), $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}", fileName);
        }

        public string GetTerrainTargetFileName(string sourceName)
        {
            var fileName = Path.GetFileName(sourceName);
            // All terrain textures go directly to art/terrains folder
            return Path.Join(_namePath, Constants.Terrains, fileName);
        }

        public string GetBeamNgJsonPathOrFileName(string windowsFileName, bool removeExtension = true)
        {
            // Normalize path separators to forward slashes for comparison
            var normalizedPath = windowsFileName.Replace(@"\", "/").ToLowerInvariant();

            var targetParts = normalizedPath.Split("/levels/");
            if (targetParts.Count() < 2)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error in {windowsFileName}. Exception:no levels folder in path.");
                return string.Empty;
            }

            // Build the BeamNG path format (always starts without leading slash)
            var beamNgPath = "levels/" + targetParts.Last();

            // Remove extension
            if (removeExtension)
            {
                return Path.ChangeExtension(beamNgPath, null);
            }

            return beamNgPath;
        }
    }
}
