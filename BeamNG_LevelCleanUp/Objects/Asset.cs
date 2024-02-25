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
        public string NightCubemap { get; set; }
        public string MoonMat { get; set; }
        public string FoamTex { get; set; }
        public string RippleTex { get; set; }
        public string DepthGradientTex { get; set; }
        public string ColorizeGradientFile { get; set; }
        public string AmbientScaleGradientFile { get; set; }
        public string FogScaleGradientFile { get; set; }
        public string NightFogGradientFile { get; set; }
        public string NightGradientFile { get; set; }
        public string SunScaleGradientFile { get; set; }

        //public List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
        public List<MaterialsDae> MaterialsDae { get; set; } = new List<MaterialsDae>();
        public bool? DaeExists { get; set; }
        public List<double>? Position { get; set; }
        public List<double>? RotationMatrix { get; set; }
        public List<double>? Scale { get; set; }
        public bool Hidden { get; set; }
        public string DaePath { get; set; }
        public List<AssetType> Types { get; set; } = new List<AssetType>();
        public string? MissionGroupPath { get; set; }
        public int? MissionGroupLine { get; set; }
        public string TranslucentBlendOp { get; set; }

        public List<string> GetAllMaterialNames()
        {
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
            if (!string.IsNullOrEmpty(this.GlobalEnviromentMap))
            {
                retVal.Add(this.GlobalEnviromentMap.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.Cubemap))
            {
                retVal.Add(this.Cubemap.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.NightCubemap))
            {
                retVal.Add(this.NightCubemap.ToUpperInvariant());
            }
            if (!string.IsNullOrEmpty(this.MoonMat))
            {
                retVal.Add(this.MoonMat.ToUpperInvariant());
            }
            if (this.MaterialsDae.Count > 0)
            {
                retVal.AddRange(this.MaterialsDae.Where(i => !string.IsNullOrEmpty(i.MaterialName)).Select(x => x.MaterialName.ToUpperInvariant()));
            }
            return retVal.Select(x => x.ToUpperInvariant()).ToList();
        }
    }
}
