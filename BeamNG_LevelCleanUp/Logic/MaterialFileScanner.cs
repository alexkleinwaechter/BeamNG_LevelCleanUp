using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MaterialFileScanner
    {
        string _levelPath;
        string _matJsonPath;
        List<MaterialStage> _stages { get; set; } = new List<MaterialStage>();

        public MaterialFileScanner(string levelPath, List<MaterialStage> stages, string matJsonPath)
        {
            _levelPath = levelPath;
            _stages = stages;
            _matJsonPath = matJsonPath;
        }
        private string ResolvePath(string materialFilePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return string.Join(
                new string(delim, 1),
                _levelPath.Split(delim).Concat(materialFilePath.Replace(toReplaceDelim, delim).Split(delim)).Distinct().ToArray())
                .Replace("\\\\", "\\");
        }

        public List<MaterialFile> GetMaterialFiles()
        {
            var retVal = new List<MaterialFile>();
            foreach (var stage in _stages)
            {
                foreach (var prop in stage.GetType().GetProperties())
                {
                    var val = (string)prop.GetValue(stage, null);
                    if (val == "/levels/ellern_map/art/shapes/custom/smallhouses_c/Haus_2_Dach_Grau_Wand_Gelb/Haus_2_Dach_Grau_Wand_Gelb")
                    {
                        //Bei zwei gleichen Path Segmenten scheitert Resolve!
                        var x = 1;
                    }
                    if (!string.IsNullOrEmpty(val))
                    {
                        if (val.Count(c => c == '/') == 0)
                        {
                            val = Path.Combine(Path.GetDirectoryName(_matJsonPath), val);
                        }
                        var filePath = ResolvePath(val);
                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".dds");
                            fileInfo = new FileInfo(ddsPath);
                        }
                        if (!fileInfo.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".png");
                            fileInfo = new FileInfo(ddsPath);
                        }
                        if (!fileInfo.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".jpg");
                            fileInfo = new FileInfo(ddsPath);
                        }
                        if (!fileInfo.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".jpeg");
                            fileInfo = new FileInfo(ddsPath);
                        }
                        retVal.Add(new MaterialFile
                        {
                            Missing = !fileInfo.Exists,
                            File = fileInfo,
                            MapType = prop.Name
                        });
                    }
                }
            }
            return retVal;
        }
    }
}
