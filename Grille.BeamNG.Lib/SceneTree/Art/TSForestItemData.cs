namespace Grille.BeamNG.SceneTree.Art;

public sealed class TSForestItemData : ArtItem
{
    public const string ClassName = "TSForestItemData";

    public JsonDictProperty<float> Radius { get; }

    public JsonDictProperty<string> ShapeFile { get; }

    public TSForestItemData(JsonDict dict) : base(dict, ClassName)
    {
        Radius = new(this, "radius");
        ShapeFile = new(this, "shapeFile");
    }
}
