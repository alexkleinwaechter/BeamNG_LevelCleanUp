using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
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
        string _levelPath;

        public ObsoleteFileResolver(List<MaterialJson> materials, List<Asset> usedAssets, List<FileInfo> allDaeList, string levelPath)
        {

            _materials = materials;
            _usedAssets = usedAssets;
            _allDaeList = allDaeList;
            _levelPath = levelPath;
        }

        public void ResolveUnusedAssets()
        {
            var usedPaths = _usedAssets
                .Where(x => x.DaeExists.HasValue && x.DaeExists.Value == true)
                .Select(x => x.DaePath.ToLowerInvariant())
                .Distinct()
                .ToList();
            var unusedDae = _allDaeList.Where(x => !usedPaths.Contains(x.FullName.ToLowerInvariant())).ToList();
            var materialsInUnusedDae = new List<MaterialsDae>();
            foreach (var file in unusedDae)
            {
                var daeScanner = new DaeScanner(_levelPath, file.FullName, true);
                materialsInUnusedDae.AddRange(daeScanner.GetMaterials());
            }
            var materialNamesInUnusedDae = materialsInUnusedDae.Select(x => x.MaterialName).Distinct().ToList();
            var usedAssetsWithMaterials = _usedAssets
                .Where(x => !string.IsNullOrEmpty(x.Material) || x.MaterialsDae.Count > 0);
            var materialNamesInUsedAssets = new List<string>();
            foreach (var item in usedAssetsWithMaterials)
            {
                if (!string.IsNullOrEmpty(item.Material))
                {
                    materialNamesInUsedAssets.Add(item.Material);
                }
                if (item.MaterialsDae.Count > 0)
                {
                    materialNamesInUsedAssets.AddRange(item.MaterialsDae.Select(x => x.MaterialName));
                }
            }
            materialNamesInUsedAssets = materialNamesInUsedAssets.Distinct().ToList();
            var materialsToRemove = materialNamesInUnusedDae.Where(x => !materialNamesInUsedAssets.Contains(x)).ToList();

            var filePathsToRemove = new List<string>();
            filePathsToRemove.AddRange(unusedDae.Select(x => x.FullName));
            filePathsToRemove.AddRange(unusedDae.Select(x => Path.ChangeExtension(x.FullName, ".cdae")));
            foreach (var item in materialsToRemove)
            {
                var mat = _materials
                    .Where(m => !string.IsNullOrEmpty(m.Name) && m.Name.Equals(item, StringComparison.InvariantCultureIgnoreCase)).ToList();
                if (mat.Count > 0)
                {
                    foreach (var m in mat)
                    {
                        foreach (var file in m.MaterialFiles)
                        {
                            var fileInOtherMaterial = _materials
                                .Where(x => materialNamesInUsedAssets.Contains(x.Name))
                                .Where(x => !string.IsNullOrEmpty(x.Name) && x.Name != m.Name && x.MaterialFiles != null)
                                .SelectMany(x => x.MaterialFiles).Any(y => y.File.FullName.Equals(file.File.FullName, StringComparison.InvariantCultureIgnoreCase));
                            if (!fileInOtherMaterial)
                            {
                                filePathsToRemove.Add(file.File.FullName);
                            }
                        }
                    }
                }
            }
            foreach (var file in filePathsToRemove)
            {
                var info = new FileInfo(file);
                if (info.Exists)
                    File.Delete(file);
            }
        }
    }
}
