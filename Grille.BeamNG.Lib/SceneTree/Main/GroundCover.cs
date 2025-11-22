
namespace Grille.BeamNG.SceneTree.Main;

public class GroundCover : SimItem
{
    public const string ClassName = "GroundCover";

    public JsonDictProperty<string> Material { get; }

    public JsonDictProperty<JsonDict[]> Types { get; }

    public GroundCover(JsonDict dict) : base(dict, ClassName)
    {
        Material = new(this, "material");

        Types = new(this, "Types");
    }

    public void AddInstance(GroundCoverInstance instance)
    {

    }

    public override IEnumerable<JsonDictProperty<string>> EnumerateIdentifiers()
    {
        foreach (var reference in base.EnumerateIdentifiers())
            yield return reference;
        yield return Material;
    }
}
