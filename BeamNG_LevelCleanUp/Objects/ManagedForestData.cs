using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class ManagedForestData
    {
        public double branchAmp { get; set; }
        public double detailAmp { get; set; }
        public double detailFreq { get; set; }
        public double mass { get; set; }
        public double radius { get; set; }
        public double trunkBendScale { get; set; }
        public double windScale { get; set; }
        public double dampingCoefficient { get; set; }
        public double rigidity { get; set; }
        public int shadowNullDetailSize { get; set; }
        public double tightnessCoefficient { get; set; }
        public string @class { get; set; }
        public string annotation { get; set; }
        public string dynamicCubemap { get; set; }
        public string internalName { get; set; }
        public string name { get; set; }
        public string order_simset { get; set; }
        public string persistentId { get; set; }
        public string planarReflection { get; set; }
        public string shapeFile { get; set; }
        public string translucentBlendOp { get; set; }

    }
}
