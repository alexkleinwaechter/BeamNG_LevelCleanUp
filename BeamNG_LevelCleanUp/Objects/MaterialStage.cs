using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialStage
    {
        public string AmbientOcclusionMap { get; set; }
        public List<double> BaseColorFactor { get; set; }
        public string BaseColorMap { get; set; }
        public string DetailMap { get; set; }
        public List<double> DetailScale { get; set; }
        public string NormalMap { get; set; }
        public string OverlayMap { get; set; }
        public string RoughnessMap { get; set; }
        public bool? VertColor { get; set; }
        public string ColorMap { get; set; }
        public List<double> DiffuseColor { get; set; }
        public string SpecularMap { get; set; }
        public long? SpecularPower { get; set; }
        public bool? UseAnisotropic { get; set; }
        public bool? PixelSpecular { get; set; }
        public string ReflectivityMap { get; set; }
        public double? DetailNormalMapStrength { get; set; }
        public List<double> Specular { get; set; }
    }
}
