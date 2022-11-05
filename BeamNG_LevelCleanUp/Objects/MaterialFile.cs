using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialFile
    {
        public FileInfo? File { get; set; }
        public string MapType { get; set; }
        public bool Missing { get; set; }
    }
}
