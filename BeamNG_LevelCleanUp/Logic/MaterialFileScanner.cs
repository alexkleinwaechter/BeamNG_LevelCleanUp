using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

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
        public List<MaterialFile> GetMaterialFiles(string materialName)
        {
            var retVal = new List<MaterialFile>();
            foreach (var stage in _stages)
            {
                foreach (var prop in stage.GetType().GetProperties())
                {
                    var val = (string)prop.GetValue(stage, null);
                    //if (val == "/levels/LosInjurus/ART/shapes/Buildings/MetroCity/commercial/concrete_008_d.dds") Debugger.Break();
                    //if (val == "containers_01_a_d.dds") Debugger.Break();
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
                        var fileInfo = new FileInfo(filePath);
                        var fileToCheck = new FileInfo(filePath);
                        if (!fileToCheck.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".dds");
                            fileToCheck = new FileInfo(ddsPath);
                        }
                        if (!fileToCheck.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".png");
                            fileToCheck = new FileInfo(ddsPath);
                        }
                        if (!fileToCheck.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".jpg");
                            fileToCheck = new FileInfo(ddsPath);
                        }
                        if (!fileToCheck.Exists)
                        {
                            var ddsPath = Path.ChangeExtension(filePath, ".jpeg");
                            fileToCheck = new FileInfo(ddsPath);
                        }
                        if (fileToCheck.Exists) {
                            fileInfo = fileToCheck;
                        }
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
    }
}
