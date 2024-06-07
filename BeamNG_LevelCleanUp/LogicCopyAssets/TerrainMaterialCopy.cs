using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using Grille.BeamNG.IO.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    public class TerrainMaterialCopy
    {
        private List<Asset> _copyAssets { get; set; }
        private List<Asset> _overrideAssets { get; set; }
        private string _levelPathCopy { get; set; }
        private string _levelPathOverride { get; set; }
        private bool _override { get; set; }

        private List<string> _fixedKeys = new List<string> { "internalName", "name", "class" };

        public TerrainMaterialCopy(List<Asset> copyAssets, List<Asset> overrideAssets, string levelPathCopy, string levelPathOverride)
        {
            _copyAssets = copyAssets;
            _overrideAssets = overrideAssets;
            _levelPathCopy = levelPathCopy;
            _levelPathOverride = levelPathOverride;
        }

        public void Copy()
        {
            if (!_copyAssets.Any())
            {
                Console.WriteLine("No Terrain Materials to copy");
                return;
            }

            if (_overrideAssets.Any()) {
                _override = true;
                CopyWithOverride(_copyAssets[0], _overrideAssets[0]);
                return;
            } 
            foreach (Asset copyAsset in _copyAssets)
            {
            }
        }

        private void CopyWithOverride(Asset copyAsset, Asset overrideAsset)
        {
            var materialCopy = copyAsset.MaterialsTerrain.FirstOrDefault();
            var materialOverride = overrideAsset.MaterialsTerrain.FirstOrDefault();
            var jsonDictCopy = new JsonDict();
            var jsonDictOverride = new JsonDict();
            if (File.Exists(materialCopy.MatJsonFileLocation))
            {
                using var stream = new FileStream(materialCopy.MatJsonFileLocation, FileMode.Open, FileAccess.Read);
                jsonDictCopy = JsonDictSerializer.Deserialize(stream);
            }

            if (File.Exists(materialOverride.MatJsonFileLocation))
            {
                using var stream = new FileStream(materialOverride.MatJsonFileLocation, FileMode.Open, FileAccess.Read);
                jsonDictOverride = JsonDictSerializer.Deserialize(stream);
            }

            var dictMatOvr = new JsonDict();
            foreach (var key in jsonDictOverride.Keys)
            {
                dictMatOvr = (JsonDict)jsonDictOverride[key];
                if (dictMatOvr.TryGetValue("internalName", out var nameObj))
                {
                    var internalName = (string)nameObj;
                    if (internalName == materialOverride.InternalName)
                    {
                        var clearedDict = ClearOverride(dictMatOvr);
                        break;
                    }
                }
                dictMatOvr.Clear();
            }
        }

        private JsonDict ClearOverride(JsonDict jsonDict)
        {
            var deleteKeys = new List<string>();

            foreach (var key in jsonDict.Keys)
            {
                if (_fixedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {

                }
                else
                {
                    if (key.EndsWith("Tex", StringComparison.OrdinalIgnoreCase))
                    {
                        //Check ob woanders genutzt!
                        //var resolvedPath = PathResolver.ResolvePath(_levelPathOverride, jsonDict[key].ToString(), true);
                        //if (File.Exists(resolvedPath))
                        //{
                        //    File.Delete(resolvedPath);
                        //}
                    }
                    deleteKeys.Add(key);
                }
            }


            foreach (var key in deleteKeys)
            {
                jsonDict.Remove(key);
            }

            return jsonDict;
        }

        //private JsonDict FillOverwrite(JsonDict jsonDict)
        //{
        //    foreach (var key in jsonDict.Keys)
        //    {
        //        if (_fixedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        //        {
        //            jsonDictOverride[key] = jsonDictCopy[key];
        //        }
        //        else
        //        {
        //            if (key.EndsWith("Tex", StringComparison.OrdinalIgnoreCase))
        //            {
        //                var resolvedPath = PathResolver.ResolvePath(_levelPathOverride, jsonDict[key].ToString(), true);
        //                if (File.Exists(resolvedPath))
        //                {
        //                    File.Delete(resolvedPath);
        //                }
        //                var newFileLocation = Path.Combine(Path.GetDirectoryName(materialOverride.MatJsonFileLocation), Path.GetFileName(jsonDictCopy[key].ToString()));
        //                File.Copy(jsonDictCopy[key].ToString(), newFileLocation, true);
        //                jsonDictOverride[key] = newFileLocation;
        //            }
        //            else
        //            {
        //                jsonDictOverride[key] = jsonDictCopy[key];
        //            }
        //        }
        //    }

        //    return jsonDict;
        //}
    }
}
