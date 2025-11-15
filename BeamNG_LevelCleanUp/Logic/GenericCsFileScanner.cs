using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class GenericCsFileScanner
{
    private readonly List<Asset> _assets = new();
    private readonly List<string> _excludeFiles = new();

    internal GenericCsFileScanner(FileInfo csFile, string levelPath, List<string> excludeFiles, List<Asset> assets)
    {
        _csFile = csFile;
        _levelPath = levelPath;
        _excludeFiles = excludeFiles;
        _assets = assets;
    }

    private FileInfo _csFile { get; }
    private string _levelPath { get; }

    internal void ScanForFilesToExclude()
    {
        foreach (var line in File.ReadLines(_csFile.FullName))
        {
            var nameParts = line.Split('"');
            if (nameParts.Length > 1)
            {
                var name = nameParts[1];
                //if (name.Contains("slabs_huge_d")) Debugger.Break();
                if (name.StartsWith("./")) name = name.Remove(0, 2);
                if (name.Count(c => c == '/') == 0) name = Path.Join(_csFile.Directory.FullName, name);
                var toCheck = PathResolver.ResolvePath(_levelPath, name, false);
                var checkForFile = new FileInfo(toCheck);
                if (!checkForFile.Exists) checkForFile = FileUtils.ResolveImageFileName(checkForFile.FullName);
                if (checkForFile.Exists)
                {
                    _excludeFiles.Add(checkForFile.FullName);
                    // Should run with compatibility switch for quirky projects
                    if (checkForFile.Extension.Equals(".DAE", StringComparison.OrdinalIgnoreCase))
                        AddAsset(new Asset
                        {
                            Class = "TSStatic",
                            ShapeName = checkForFile.FullName
                        });
                }
                else
                {
                    toCheck = PathResolver.ResolvePathBasedOnCsFilePath(_csFile, name);
                    checkForFile = new FileInfo(toCheck);
                    if (!checkForFile.Exists) checkForFile = FileUtils.ResolveImageFileName(checkForFile.FullName);
                    if (checkForFile.Exists) _excludeFiles.Add(checkForFile.FullName);
                }
            }
        }
    }

    private void AddAsset(Asset? asset)
    {
        //if (asset.ShapeName != null && asset.ShapeName.ToUpperInvariant().Contains("FranklinDouglasTower15flr_var2".ToUpperInvariant())) Debugger.Break();
        if (!string.IsNullOrEmpty(asset?.ShapeName))
        {
            var daeScanner = new DaeScanner(_levelPath, asset.ShapeName);
            asset.DaeExists = daeScanner.Exists();
            if (asset.DaeExists.HasValue && asset.DaeExists.Value)
            {
                asset.DaePath = daeScanner.ResolvedPath();
                asset.MaterialsDae = daeScanner.GetMaterials();
            }
        }

        _assets.Add(asset);
    }
}