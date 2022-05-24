using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class Asset
    {
        public string Class { get; set; }
        public string ShapeFullName { get; set; }
        public string ShapeName { get; set; }
        public string ShapePath { get; set; }
        public FileInfo File { get; set; }  
        public List<Material> Materials { get; set; }
    }
}
