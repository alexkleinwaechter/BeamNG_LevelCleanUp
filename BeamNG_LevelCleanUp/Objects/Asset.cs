namespace BeamNG_LevelCleanUp.Objects;

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
    
    /// <summary>
    ///     TerrainBlock: Path to the terrain file (.ter)
    /// </summary>
    public string TerrainFile { get; set; }
    
    /// <summary>
    ///     ScatterSky: FlareType resource reference
    /// </summary>
    public string FlareType { get; set; }

    //public List<MaterialJson> MaterialsJson { get; set; } = new List<MaterialJson>();
    public List<MaterialsDae> MaterialsDae { get; set; } = new();
    public bool? DaeExists { get; set; }
    public List<double>? Position { get; set; }
    public List<double>? RotationMatrix { get; set; }
    public List<double>? Scale { get; set; }
    public bool Hidden { get; set; }
    public string DaePath { get; set; }
    public List<AssetType> Types { get; set; } = new();
    public string? MissionGroupPath { get; set; }
    public int? MissionGroupLine { get; set; }
    public string TranslucentBlendOp { get; set; }

    public List<string> GetAllMaterialNames()
    {
        var retVal = new List<string>();
        if (!string.IsNullOrEmpty(Material)) retVal.Add(Material.ToUpperInvariant());
        if (!string.IsNullOrEmpty(SideMaterial)) retVal.Add(SideMaterial.ToUpperInvariant());
        if (!string.IsNullOrEmpty(TopMaterial)) retVal.Add(TopMaterial.ToUpperInvariant());
        if (!string.IsNullOrEmpty(BottomMaterial)) retVal.Add(BottomMaterial.ToUpperInvariant());
        if (!string.IsNullOrEmpty(GlobalEnviromentMap)) retVal.Add(GlobalEnviromentMap.ToUpperInvariant());
        if (!string.IsNullOrEmpty(Cubemap)) retVal.Add(Cubemap.ToUpperInvariant());
        if (!string.IsNullOrEmpty(NightCubemap)) retVal.Add(NightCubemap.ToUpperInvariant());
        if (!string.IsNullOrEmpty(MoonMat)) retVal.Add(MoonMat.ToUpperInvariant());
        if (MaterialsDae.Count > 0)
            retVal.AddRange(MaterialsDae.Where(i => !string.IsNullOrEmpty(i.MaterialName))
                .Select(x => x.MaterialName.ToUpperInvariant()));
        return retVal.Select(x => x.ToUpperInvariant()).ToList();
    }
}