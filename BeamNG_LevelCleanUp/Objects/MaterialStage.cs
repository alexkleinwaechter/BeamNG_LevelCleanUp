namespace BeamNG_LevelCleanUp.Objects;

public class MaterialStage
{
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
    public List<double> DiffuseColor { get; set; }
    public bool? Glow { get; set; }
    public bool? UseAnisotropic { get; set; }
    public bool? InstanceDiffuse { get; set; }
}