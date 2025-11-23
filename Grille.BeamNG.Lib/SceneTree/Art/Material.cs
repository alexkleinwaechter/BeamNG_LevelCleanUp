namespace Grille.BeamNG.SceneTree.Art;

public abstract class Material : ArtItem
{
    protected Material(JsonDict dict, string className) : base(dict, className) { }

    public abstract IEnumerable<JsonDictProperty<string>> EnumerateTexturePaths();
}
