using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public List<MaterialFile> GetMaterialFiles()
        {
            var retVal = new List<MaterialFile>();
            foreach (var stage in _stages)
            {
                foreach (var prop in stage.GetType().GetProperties())
                {
                    var val = (string)prop.GetValue(stage, null);
                    //if (val == "/levels/east_coast_rework/art/shapes/rails/shawn2.dds") Debugger.Break();
                    //if (val == "containers_01_a_d.dds") Debugger.Break();
                    if (!string.IsNullOrEmpty(val))
                    {
                        if (val.Count(c => c == '/') == 0)
                        {
                            val = Path.Join(Path.GetDirectoryName(_matJsonPath), val);
                        }
                        var filePath = PathResolver.ResolvePath(_levelPath, val, false);
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
