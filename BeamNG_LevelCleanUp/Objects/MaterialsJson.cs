using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialJson
    {
        public string Name { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        public string MapTo { get; set; } = string.Empty;
        public List<MaterialStage> Stages { get; set; }
        public List<string> CubeFace { get; set; }
        public string Cubemap { get; set; } = string.Empty;
        public List<MaterialFile> MaterialFiles { get; set; } = new List<MaterialFile>();
        public bool NotUsed { get; set; }
    }
}
