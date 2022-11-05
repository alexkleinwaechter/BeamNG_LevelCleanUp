using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class Asset
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string __parent { get; set; }
        public string ShapeFullName { get; set; }
        public string ShapeName { get; set; }
        public string ShapePath { get; set; }
        public FileInfo File { get; set; }
        public string Material { get; set; }
        public List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public List<MaterialsDae> MaterialsDae { get; set; } = new List<MaterialsDae>();
        public List<decimal>? Position { get; set; }
        public List<decimal>? RotationMatrix { get; set; }
        public bool Hidden { get; set; }   
    }
}
