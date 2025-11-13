using System.Globalization;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

public class ObsoleteFileResolver
{
    private readonly List<FileInfo> _allDaeList = new();
    private readonly List<string> _excludeFiles = new();
    private readonly string _levelName;
    private readonly string _levelPath;
    private readonly List<MaterialJson> _materials = new();
    private readonly List<Asset> _usedAssets = new();

    public ObsoleteFileResolver(List<MaterialJson> materials, List<Asset> usedAssets, List<FileInfo> allDaeList,
        string levelPath, string levelName, List<string> excludeFiles)
    {
        _materials = materials;
        _usedAssets = usedAssets;
        _allDaeList = allDaeList;
        _levelPath = levelPath;
        _levelName = levelName;
        _excludeFiles = excludeFiles;
    }

    public List<string> ReturnUnusedAssetFiles()
    {
        var usedDaePaths = _usedAssets
            .Where(x => x.DaeExists.HasValue && x.DaeExists.Value)
            .Select(x => x.DaePath.ToUpperInvariant())
            .Distinct()
            .ToList();
        var unusedDaeFiles = _allDaeList
            .Where(x => !usedDaePaths.Contains(x.FullName.ToUpperInvariant()))
            //.Where(x => !_excludeFiles.Select(y => y.ToUpperInvariant()).Contains(x.FullName.ToUpperInvariant()))
            .ToList();
        var materialsInUnusedDae = new List<MaterialsDae>();
        foreach (var file in unusedDaeFiles)
        {
            var daeScanner = new DaeScanner(_levelPath, file.FullName, true);
            materialsInUnusedDae.AddRange(daeScanner.GetMaterials());
        }

        var materialNamesInUnusedDae =
            materialsInUnusedDae.Select(x => x.MaterialName.ToUpperInvariant()).Distinct().ToList();
        var usedAssetsWithMaterials = _usedAssets
            .Where(x => !string.IsNullOrEmpty(x.Material)
                        || !string.IsNullOrEmpty(x.SideMaterial)
                        || !string.IsNullOrEmpty(x.TopMaterial)
                        || !string.IsNullOrEmpty(x.BottomMaterial)
                        || x.MaterialsDae.Count > 0);
        var materialNamesInUsedAssets = new List<string>();
        foreach (var item in usedAssetsWithMaterials) materialNamesInUsedAssets.AddRange(item.GetAllMaterialNames());
        materialNamesInUsedAssets = materialNamesInUsedAssets.Distinct().ToList();
        var materialsToRemoveFromUnusedDae = materialNamesInUnusedDae
            .Where(x => !materialNamesInUsedAssets.Contains(x.ToUpperInvariant())).ToList();

        var allMaterialsNotused = _materials
            .Select(x => x.MapTo.ToUpperInvariant())
            .Distinct()
            .Where(x => !materialNamesInUsedAssets.Contains(x))
            .ToList();
        var materialsToRemove = materialsToRemoveFromUnusedDae.Concat(allMaterialsNotused).Distinct().ToList();
        var filePathsToRemove = new List<string>();
        filePathsToRemove.AddRange(unusedDaeFiles.Select(x => x.FullName));
        filePathsToRemove.AddRange(unusedDaeFiles.Select(x => Path.ChangeExtension(x.FullName, ".cdae")));
        var stopFlag = true;
        var iterationCounter = 0;
        while (stopFlag)
        {
            var before = filePathsToRemove.Count;
            MarkUnusedMaterials(materialNamesInUsedAssets, materialsToRemove, filePathsToRemove);
            iterationCounter++;
            var after = filePathsToRemove.Count;
            if (after == before) stopFlag = false;
        }

        return filePathsToRemove;
    }

    internal void ExcludeUsedAssetFiles()
    {
        //ToDo improve logic for old cs files
        FillDaeMaterialsWithoutDefinition();

        var usedDaePaths = _usedAssets
            .Where(x => x.DaeExists.HasValue && x.DaeExists.Value)
            .Select(x => x.DaePath.ToUpperInvariant())
            .Distinct()
            .ToList();

        var usedCdaePaths = usedDaePaths.Select(x => Path.ChangeExtension(x, ".cdae"));

        var usedMaterials = _usedAssets
            .Where(x => x.GetAllMaterialNames().Count > 0)
            .SelectMany(y => y.GetAllMaterialNames())
            .Distinct()
            .ToList();

        var usedMaterialFiles = _materials
            .Where(x => usedMaterials.Any(y =>
                x.Name.ToUpperInvariant().Equals(y) || x.MapTo.ToUpperInvariant().Equals(y) ||
                x.InternalName.ToUpperInvariant().Equals(y)))
            .SelectMany(f => f.MaterialFiles.Select(x => x.File.FullName))
            .Distinct()
            .ToList();

        _excludeFiles.AddRange(usedDaePaths);
        _excludeFiles.AddRange(usedCdaePaths);
        _excludeFiles.AddRange(usedMaterialFiles);
        WriteMaterialFilesNotExistingLog();
    }

    private void FillDaeMaterialsWithoutDefinition()
    {
        var usedDaeMaterials = _usedAssets.Where(x => x.DaeExists.HasValue && x.DaeExists.Value)
            .SelectMany(x => x.MaterialsDae)
            .ToList();

        var filteredFiles = _excludeFiles.Select(x => new FileInfo(x))
            .Where(x => usedDaeMaterials
                .Any(y => y.MaterialName.Equals(Path.GetFileNameWithoutExtension(x.Name),
                              StringComparison.OrdinalIgnoreCase)
                          && Path.GetDirectoryName(y.DaeLocation)
                              .Equals(x.DirectoryName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var item in filteredFiles) _excludeFiles.Add(item.FullName);
        WriteMaterialFilesConventionExcludedLog(filteredFiles.Select(x => x.FullName).ToList());
    }

    private void WriteMaterialFilesNotExistingLog()
    {
        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var listSeparator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;

        var files = _materials
            .SelectMany(x => x.MaterialFiles).Where(x => x.Missing)
            .Select(x => $"{x.MaterialName}{listSeparator}{x.File}")
            .ToList();
        var checkedAgainstVanillaFiles = new List<string>();
        foreach (var f in files)
            if (!FileExistsInVanilla(f))
                checkedAgainstVanillaFiles.Add(f);

        if (checkedAgainstVanillaFiles.Any())
        {
            checkedAgainstVanillaFiles.Insert(0, $"Materialname{listSeparator}File");
            File.WriteAllLines(Path.Join(_levelPath, "MaterialFilesNotFound.txt"), checkedAgainstVanillaFiles);
        }
    }

    private bool FileExistsInVanilla(string sourceFile)
    {
        var retVal = false;
        var fileParts = sourceFile.Split(@"\levels\");
        if (fileParts.Count() == 2)
        {
            var thisLevelName = fileParts[1].Split(@"\").FirstOrDefault() ?? string.Empty;
            var beamDir = Path.Join(Steam.GetBeamInstallDir(), Constants.BeamMapPath, thisLevelName);
            var beamZip = beamDir + ".zip";
            if (new FileInfo(beamZip).Exists &&
                !thisLevelName.Equals(_levelName, StringComparison.InvariantCultureIgnoreCase))
            {
                var filePathEnd = fileParts[1];
                //to Do: check if filepath has image extension, if not attach png
                var imageextensions = new List<string> { ".dds", ".png", ".jpg", ".jpeg" };
                if (!imageextensions.Any(x => filePathEnd.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    filePathEnd = filePathEnd + ".png";
                retVal = ZipReader.FileExists(beamZip, filePathEnd);
                if (!retVal)
                {
                    filePathEnd = Path.ChangeExtension(filePathEnd, ".dds");
                    retVal = ZipReader.FileExists(beamZip, filePathEnd);
                }

                if (!retVal)
                {
                    filePathEnd = Path.ChangeExtension(filePathEnd, ".png");
                    retVal = ZipReader.FileExists(beamZip, filePathEnd);
                }

                if (!retVal)
                {
                    filePathEnd = Path.ChangeExtension(filePathEnd, ".jpg");
                    retVal = ZipReader.FileExists(beamZip, filePathEnd);
                }

                if (!retVal)
                {
                    filePathEnd = Path.ChangeExtension(filePathEnd, ".jpeg");
                    retVal = ZipReader.FileExists(beamZip, filePathEnd);
                }
            }
        }

        return retVal;
    }

    private void WriteMaterialFilesConventionExcludedLog(List<string> files)
    {
        if (files.Any())
        {
            files.Insert(0,
                "Materialfiles not defined in materials.json or materials.cs, but have the same name as materials in dae file.");
            File.WriteAllLines(Path.Join(_levelPath, "MaterialFilesNotDeletedByConvention.txt"), files);
        }
    }

    private void MarkUnusedMaterials(List<string> materialNamesInUsedAssets, List<string> materialsToRemove,
        List<string> filePathsToRemove)
    {
        foreach (var item in materialsToRemove)
        {
            var mat = _materials
                .Where(m => !string.IsNullOrEmpty(m.Name) && m.Name.Equals(item, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mat.Count > 0)
                foreach (var m in mat)
                {
                    //if (m.Name.Equals("gridmaterial_curbfacedupe", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
                    m.NotUsed = true;
                    foreach (var file in m.MaterialFiles)
                    {
                        var fileInOtherMaterial = _materials
                            .Where(x => materialNamesInUsedAssets.Contains(x.Name.ToUpperInvariant()))
                            .Where(x => !string.IsNullOrEmpty(x.Name) &&
                                        x.Name.ToUpperInvariant() != m.Name.ToUpperInvariant() &&
                                        x.MaterialFiles != null)
                            .Where(x => !x.NotUsed)
                            .SelectMany(x => x.MaterialFiles).Any(y =>
                                y.File.FullName.Equals(file.File.FullName, StringComparison.OrdinalIgnoreCase));
                        if (!fileInOtherMaterial)
                            if (!filePathsToRemove.Contains(file.File.FullName))
                                filePathsToRemove.Add(file.File.FullName);
                    }
                }
        }
    }
}