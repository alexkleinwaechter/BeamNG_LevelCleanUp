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
            var usedPaths = _usedAssets
                .Where(x => x.DaeExists.HasValue && x.DaeExists.Value == true)
                .Select(x => x.DaePath.ToLowerInvariant())
                .Distinct()
                .ToList();
            var unusedDae = _allDaeList
                .Where(x => !usedPaths.Contains(x.FullName.ToLowerInvariant()))
                //.Where(x => !_excludeFiles.Select(y => y.ToLowerInvariant()).Contains(x.FullName.ToLowerInvariant()))
                .ToList();
            var materialsInUnusedDae = new List<MaterialsDae>();
            foreach (var file in unusedDae)
            {
                var daeScanner = new DaeScanner(_levelPath, file.FullName, true);
                materialsInUnusedDae.AddRange(daeScanner.GetMaterials());
            }
            var materialNamesInUnusedDae = materialsInUnusedDae.Select(x => x.MaterialName).Distinct().ToList();
            var usedAssetsWithMaterials = _usedAssets
                .Where(x => !string.IsNullOrEmpty(x.Material)
                || !string.IsNullOrEmpty(x.SideMaterial)
                || !string.IsNullOrEmpty(x.TopMaterial)
                || !string.IsNullOrEmpty(x.BottomMaterial)
                || x.MaterialsDae.Count > 0);
            var materialNamesInUsedAssets = new List<string>();
            foreach (var item in usedAssetsWithMaterials)
            {
                if (!string.IsNullOrEmpty(item.Material))
                {
                    materialNamesInUsedAssets.Add(item.Material.ToLowerInvariant());
                }
                if (!string.IsNullOrEmpty(item.SideMaterial))
                {
                    materialNamesInUsedAssets.Add(item.SideMaterial.ToLowerInvariant());
                }
                if (!string.IsNullOrEmpty(item.TopMaterial))
                {
                    materialNamesInUsedAssets.Add(item.TopMaterial.ToLowerInvariant());
                }
                if (!string.IsNullOrEmpty(item.BottomMaterial))
                {
                    materialNamesInUsedAssets.Add(item.BottomMaterial.ToLowerInvariant());
                }
                if (item.MaterialsDae.Count > 0)
                {
                    //if (item.ShapeName != null && item.ShapeName.ToLowerInvariant().Contains("jri_airhangar")) Debugger.Break();
                    materialNamesInUsedAssets.AddRange(item.MaterialsDae.Where(i => !string.IsNullOrEmpty(i.MaterialName)).Select(x => x.MaterialName.ToLowerInvariant()));
                }
            }
            materialNamesInUsedAssets = materialNamesInUsedAssets.Distinct().ToList();
            var materialsToRemove = materialNamesInUnusedDae.Where(x => !materialNamesInUsedAssets.Contains(x.ToLowerInvariant())).ToList();
            
            var allMaterialsNotused = _materials
                .Select(x => x.MapTo.ToLowerInvariant())
                .Distinct()
                .Where(x => !materialNamesInUsedAssets.Contains(x))
                .ToList();
            materialsToRemove = materialsToRemove.Concat(allMaterialsNotused).Distinct().ToList();
            var filePathsToRemove = new List<string>();
            filePathsToRemove.AddRange(unusedDae.Select(x => x.FullName));
            filePathsToRemove.AddRange(unusedDae.Select(x => Path.ChangeExtension(x.FullName, ".cdae")));
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

        private void MarkUnusedMaterials(List<string> materialNamesInUsedAssets, List<string> materialsToRemove, List<string> filePathsToRemove)
        {
            foreach (var item in materialsToRemove)
            {
                var mat = _materials
                    .Where(m => !string.IsNullOrEmpty(m.Name) && m.Name.Equals(item, StringComparison.InvariantCultureIgnoreCase)).ToList();
                if (mat.Count > 0)
                {
                    foreach (var m in mat)
                    {
                        //if (m.Name.Equals("gas", StringComparison.InvariantCultureIgnoreCase)) Debugger.Break();
                        m.NotUsed = true;
                        foreach (var file in m.MaterialFiles)
                        {
                            var fileInOtherMaterial = _materials
                                .Where(x => materialNamesInUsedAssets.Contains(x.Name.ToLowerInvariant()))
                                .Where(x => !string.IsNullOrEmpty(x.Name) && x.Name.ToLowerInvariant() != m.Name.ToLowerInvariant() && x.MaterialFiles != null)
                                .Where(x => x.NotUsed == false)
                                .SelectMany(x => x.MaterialFiles).Any(y => y.File.FullName.Equals(file.File.FullName, StringComparison.InvariantCultureIgnoreCase));
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
