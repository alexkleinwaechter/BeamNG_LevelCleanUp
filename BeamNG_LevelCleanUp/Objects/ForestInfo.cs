using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class ForestInfo
    {
        public string DaePath { get; set; }
        public string ForestTypeName { get; set; }
        public string FileOrigin { get; set; }
        public List<string> UsedInFiles { get; set; }
    }
}
