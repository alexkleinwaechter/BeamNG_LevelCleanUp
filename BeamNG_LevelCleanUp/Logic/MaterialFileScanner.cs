using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Grille.BeamNG.IO.Text;
using System.Windows.Navigation;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MaterialFileScanner
    {
        private string _levelPath;
        private string _matJsonPath;
        List<MaterialStage> _stages { get; set; } = new List<MaterialStage>();

        public MaterialFileScanner(string levelPath, List<MaterialStage> stages, string matJsonPath)
        {
            _levelPath = levelPath;
            _stages = stages;
            _matJsonPath = matJsonPath;
        }
        public List<MaterialFile> GetMaterialFiles(string materialName)
        {
            var retVal = new List<MaterialFile>();
            foreach (var stage in _stages)
            {
                foreach (var prop in stage.GetType().GetProperties())
                {
                    var val = prop.GetValue(stage, null) != null ? prop.GetValue(stage, null).ToString() : string.Empty;

                    if (!string.IsNullOrEmpty(val))
                    {
                        if (val.StartsWith("./"))
                        {
                            val = val.Remove(0, 2);
                        }
                        if (val.Count(c => c == '/') == 0)
                        {
                            val = Path.Join(Path.GetDirectoryName(_matJsonPath), val);
                        }
                        var filePath = PathResolver.ResolvePath(_levelPath, val, false);
                        FileInfo fileInfo = FileUtils.ResolveImageFileName(filePath);
                        retVal.Add(new MaterialFile
                        {
                            MaterialName = materialName,
                            Missing = !fileInfo.Exists,
                            File = fileInfo,
                            MapType = prop.Name
                        });
                    }
                }
            }
            return retVal;
        }

        public List<MaterialFile> GetTerrainMaterialFiles(string materialName)
        {
            var retVal = new List<MaterialFile>();
            var jsonDict = new JsonDict();
            using var stream = new FileStream(_matJsonPath, FileMode.Open, FileAccess.Read);
            jsonDict = JsonDictSerializer.Deserialize(stream);
            var dictMat = new JsonDict();
            foreach (var key in jsonDict.Keys)
            {
                dictMat = (JsonDict)jsonDict[key];
                if (dictMat.TryGetValue("internalName", out var nameObj))
                {
                    var internalName = (string)nameObj;
                    if (internalName == materialName)
                    {
                        foreach (var keyMat in dictMat.Keys)
                        {
                            if (keyMat.EndsWith("Tex", StringComparison.OrdinalIgnoreCase))
                            {
                                var val = dictMat[keyMat].ToString();
                                if (!string.IsNullOrEmpty(val))
                                {
                                    if (val.StartsWith("./"))
                                    {
                                        val = val.Remove(0, 2);
                                    }
                                    if (val.Count(c => c == '/') == 0)
                                    {
                                        val = Path.Join(Path.GetDirectoryName(_matJsonPath), val);
                                    }
                                    var filePath = PathResolver.ResolvePath(_levelPath, val, false);
                                    FileInfo fileInfo = FileUtils.ResolveImageFileName(filePath);
                                    retVal.Add(new MaterialFile
                                    {
                                        MaterialName = materialName,
                                        Missing = !fileInfo.Exists,
                                        File = fileInfo,
                                        MapType = keyMat
                                    });
                                }
                            }
                        }
                        break;
                    }
                }
            }

            return retVal;
        }
    }
}
