using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    public class ObsoleteFileResolver
    {
        private List<MaterialJson> _materials = new List<MaterialJson>();
        private List<Asset> _usedAssets = new List<Asset>();
        private List<FileInfo> _allDaeList = new List<FileInfo>();
        private List<string> _excludeFiles = new List<string>();
        string _levelPath;

        public ObsoleteFileResolver(List<MaterialJson> materials, List<Asset> usedAssets, List<FileInfo> allDaeList, string levelPath, List<string> excludeFiles)
        {

            _materials = materials;
            _usedAssets = usedAssets;
            _allDaeList = allDaeList;
            _levelPath = levelPath;
            _excludeFiles = excludeFiles;
        }
        public List<string> ReturnUnusedAssetFiles()
        {
            var usedDaePaths = _usedAssets
                .Where(x => x.DaeExists.HasValue && x.DaeExists.Value == true)
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
            var materialNamesInUnusedDae = materialsInUnusedDae.Select(x => x.MaterialName.ToUpperInvariant()).Distinct().ToList();
            var usedAssetsWithMaterials = _usedAssets
                .Where(x => !string.IsNullOrEmpty(x.Material)
                || !string.IsNullOrEmpty(x.SideMaterial)
                || !string.IsNullOrEmpty(x.TopMaterial)
                || !string.IsNullOrEmpty(x.BottomMaterial)
                || x.MaterialsDae.Count > 0);
            var materialNamesInUsedAssets = new List<string>();
            foreach (var item in usedAssetsWithMaterials)
            {
                materialNamesInUsedAssets.AddRange(item.GetAllMaterialNames());
            }
            materialNamesInUsedAssets = materialNamesInUsedAssets.Distinct().ToList();
            var materialsToRemoveFromUnusedDae = materialNamesInUnusedDae.Where(x => !materialNamesInUsedAssets.Contains(x.ToUpperInvariant())).ToList();

            var allMaterialsNotused = _materials
                .Select(x => x.MapTo.ToUpperInvariant())
                .Distinct()
                .Where(x => !materialNamesInUsedAssets.Contains(x))
                .ToList();
            var materialsToRemove = materialsToRemoveFromUnusedDae.Concat(allMaterialsNotused).Distinct().ToList();
            var filePathsToRemove = new List<string>();
            filePathsToRemove.AddRange(unusedDaeFiles.Select(x => x.FullName));
            filePathsToRemove.AddRange(unusedDaeFiles.Select(x => Path.ChangeExtension(x.FullName, ".cdae")));
            bool stopFlag = true;
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
                .Where(x => x.DaeExists.HasValue && x.DaeExists.Value == true)
                .Select(x => x.DaePath.ToUpperInvariant())
                .Distinct()
                .ToList();

            var usedCdaePaths = usedDaePaths.Select(x => Path.ChangeExtension(x, ".CDAE"));

            var usedMaterials = _usedAssets
                .Where(x => x.GetAllMaterialNames().Count > 0)
                .SelectMany(y => y.GetAllMaterialNames())
                .Distinct()
                .ToList();

            var usedMaterialFiles = _materials
                .Where(x => usedMaterials.Any(y => x.Name.ToUpperInvariant().Equals(y) || x.MapTo.ToUpperInvariant().Equals(y) || x.InternalName.ToUpperInvariant().Equals(y)))
                .SelectMany(f => f.MaterialFiles.Select(x => x.File.FullName))
                .Distinct()
                .ToList();

            _excludeFiles.AddRange(usedDaePaths);
            _excludeFiles.AddRange(usedCdaePaths);
            _excludeFiles.AddRange(usedMaterialFiles);
        }

        private void FillDaeMaterialsWithoutDefinition()
        {
            var usedDaeMaterials = _usedAssets.Where(x => x.DaeExists.HasValue && x.DaeExists.Value == true)
                         .SelectMany(x => x.MaterialsDae)
                         .ToList();

            var filteredFiles = _excludeFiles.Select(x => new FileInfo(x))
            .Where(x => usedDaeMaterials
                .Any(y => y.MaterialName.Equals(Path.GetFileNameWithoutExtension(x.Name), StringComparison.OrdinalIgnoreCase)
                            && Path.GetDirectoryName(y.DaeLocation).Equals(x.DirectoryName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
            foreach (var item in filteredFiles)
            {
                _excludeFiles.Add(item.FullName);
            }
        }

        private void MarkUnusedMaterials(List<string> materialNamesInUsedAssets, List<string> materialsToRemove, List<string> filePathsToRemove)
        {
            foreach (var item in materialsToRemove)
            {
                var mat = _materials
                    .Where(m => !string.IsNullOrEmpty(m.Name) && m.Name.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mat.Count > 0)
                {
                    foreach (var m in mat)
                    {
                        //if (m.Name.Equals("gridmaterial_curbfacedupe", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
                        m.NotUsed = true;
                        foreach (var file in m.MaterialFiles)
                        {
                            var fileInOtherMaterial = _materials
                                .Where(x => materialNamesInUsedAssets.Contains(x.Name.ToUpperInvariant()))
                                .Where(x => !string.IsNullOrEmpty(x.Name) && x.Name.ToUpperInvariant() != m.Name.ToUpperInvariant() && x.MaterialFiles != null)
                                .Where(x => x.NotUsed == false)
                                .SelectMany(x => x.MaterialFiles).Any(y => y.File.FullName.Equals(file.File.FullName, StringComparison.OrdinalIgnoreCase));
                            if (!fileInOtherMaterial)
                            {
                                if (!filePathsToRemove.Contains(file.File.FullName))
                                {
                                    filePathsToRemove.Add(file.File.FullName);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
