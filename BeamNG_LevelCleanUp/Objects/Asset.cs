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
        public string Filename { get; set; }
        public string Material { get; set; }
        public string SideMaterial { get; set; }
        public string TopMaterial { get; set; }
        public string BottomMaterial { get; set; }
        public string GlobalEnviromentMap { get; set; }
        public string Texture { get; set; }
        public string Cubemap { get; set; }
        public string FoamTex { get; set; }
        public string RippleTex { get; set; }
        public string DepthGradientTex { get; set; }

        //public List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public List<MaterialsDae> MaterialsDae { get; set; } = new List<MaterialsDae>();
        public bool? DaeExists { get; set; }
        //public List<decimal>? Position { get; set; }
        //public List<decimal>? RotationMatrix { get; set; }
        public bool Hidden { get; set; }
        public string DaePath { get; set; }

        public List<string> GetAllMaterialNames() {
            var retVal = new List<string>();
            if (!string.IsNullOrEmpty(this.Material))
            {
                retVal.Add(this.Material.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.SideMaterial))
            {
                retVal.Add(this.SideMaterial.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.TopMaterial))
            {
                retVal.Add(this.TopMaterial.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.BottomMaterial))
            {
                retVal.Add(this.BottomMaterial.ToUpperInvariant());
            }
            if (this.MaterialsDae.Count > 0)
            {
                retVal.AddRange(this.MaterialsDae.Where(i => !string.IsNullOrEmpty(i.MaterialName)).Select(x => x.MaterialName.ToUpperInvariant()));
            }
            return retVal.Select(x => x.ToUpperInvariant()).ToList();
        }
    }
}
