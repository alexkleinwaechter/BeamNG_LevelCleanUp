namespace BeamNG_LevelCleanUp.Objects;

public class MaterialStage
{
    // Texture Maps - Common
    public string AmbientOcclusionMap { get; set; }
    public string BaseColorMap { get; set; }
    public string BaseColorPaletteMap { get; set; }
    public string BaseColorDetailMap { get; set; }
    public string DetailMap { get; set; }
    public string DetailNormalMap { get; set; }
    public string NormalMap { get; set; }
    public string NormalDetailMap { get; set; }
    public string OverlayMap { get; set; }
    public string RoughnessMap { get; set; }
    public string ColorMap { get; set; }
    public string SpecularMap { get; set; }
    public string ReflectivityMap { get; set; }
    public string MetallicMap { get; set; }
    public string OpacityMap { get; set; }
    public string ColorPaletteMap { get; set; }
    public string EmissiveMap { get; set; }
    public string ClearCoatMap { get; set; }
    public string ClearCoatBottomNormalMap { get; set; }
    public string DiffuseMap { get; set; }
    public string MacroMap { get; set; }
    
    // Color Properties
    public List<double> DiffuseColor { get; set; }
    
    // Legacy (v1.0) Properties
    public List<double> Specular { get; set; }
    public double? SpecularPower { get; set; }
    public double? MinnaertConstant { get; set; }
    public List<double> GlowFactor { get; set; }
    
    // PBR (v1.5) Factor Properties
    public double? MetallicFactor { get; set; }
    public double? RoughnessFactor { get; set; }
    public double? NormalMapStrength { get; set; }
    public double? OpacityFactor { get; set; }
    public List<double> EmissiveFactor { get; set; }
    public double? ClearCoatFactor { get; set; }
    public double? ClearCoatRoughnessFactor { get; set; }
    public List<double> DetailScale { get; set; }
    public double? DetailBaseColorMapStrength { get; set; }
    public double? DetailNormalMapStrength { get; set; }
    public double? ReflectivityMapFactor { get; set; }
    
    // Flags
    public bool? Glow { get; set; }
    public bool? UseAnisotropic { get; set; }
    public bool? InstanceDiffuse { get; set; }
    public bool? InstanceEmissive { get; set; }
    public bool? InstanceOpacity { get; set; }
}