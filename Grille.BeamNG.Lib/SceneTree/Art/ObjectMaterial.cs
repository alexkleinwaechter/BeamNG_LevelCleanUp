using System.Collections.ObjectModel;

namespace Grille.BeamNG.SceneTree.Art;

public class ObjectMaterial : Material
{
    public const string ClassName = "Material";

    public JsonDictProperty<float> Version { get; }

    public JsonDictProperty<bool> AlphaTest { get; }

    public JsonDictProperty<int> AlphaRef { get; }

    public JsonDictProperty<string> GroundType { get; }

    public JsonDictProperty<string> TranslucentBlendOp { get; }

    public ReadOnlyCollection<ObjectMaterialStage> Stages { get; }

    public ObjectMaterialStage Stage0 { get; }
    public ObjectMaterialStage Stage1 { get; }
    public ObjectMaterialStage Stage2 { get; }
    public ObjectMaterialStage Stage3 { get; }

    public ObjectMaterial(JsonDict dict) : base(dict, ClassName)
    {
        Version = new(this, "version");

        AlphaTest = new(this, "alphaTest");
        AlphaRef = new(this, "alphaRef");
        GroundType = new(this, "groundType");
        TranslucentBlendOp = new(this, "translucentBlendOp");

        var stages = (JsonDict[])this["Stages"];

        Stage0 = new(stages[0]);
        Stage1 = new(stages[1]);
        Stage2 = new(stages[2]);
        Stage3 = new(stages[3]);

        Stages = new([Stage0, Stage1, Stage2, Stage3]);
    }

    public override IEnumerable<JsonDictProperty<string>> EnumerateTexturePaths()
    {
        foreach (var stage in Stages)
        {
            foreach (var map in stage.Maps)
            {
                if (!map.Exists)
                    continue;

                yield return map;
            }
        }
    }
}

public class ObjectMaterialStage : JsonDictWrapper
{
    public JsonDictProperty<string> AmbientOcclusionMap { get; }

    public JsonDictProperty<string> BaseColorMap { get; }

    public JsonDictProperty<string> NormalMap { get; }

    public JsonDictProperty<string> OpacityMap { get; }

    public JsonDictProperty<string> RoughnessMap { get; }

    public JsonDictProperty<string> ColorMap { get; }

    public JsonDictProperty<string> SpecularMap { get; }

    public JsonDictProperty<bool> UseAnisotropic { get; }

    public ReadOnlyCollection<JsonDictProperty<string>> Maps { get; }

    public ObjectMaterialStage(JsonDict dict) : base(dict)
    {
        AmbientOcclusionMap = new(this, "ambientOcclusionMap");
        BaseColorMap = new(this, "baseColorMap");
        NormalMap = new(this, "normalMap");
        OpacityMap = new(this, "opacityMap");
        RoughnessMap = new(this, "roughnessMap");
        ColorMap = new(this, "colorMap");
        SpecularMap = new(this, "specularMap");
        UseAnisotropic = new(this, "useAnisotropic");

        Maps = new([AmbientOcclusionMap, BaseColorMap, NormalMap, OpacityMap, RoughnessMap, ColorMap, SpecularMap]);
    }
}
