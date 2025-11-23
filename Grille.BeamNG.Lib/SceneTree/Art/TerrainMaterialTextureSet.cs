namespace Grille.BeamNG.SceneTree.Art;

public class TerrainMaterialTextureSet : ArtItem
{
    public const string ClassName = "TerrainMaterialTextureSet";

    public JsonDictProperty<Vector2> BaseTexSize { get; }
    public JsonDictProperty<Vector2> DetailTexSize { get; }
    public JsonDictProperty<Vector2> MacroTexSize { get; }

    public TerrainMaterialTextureSet(JsonDict dict) : base(dict, ClassName) {
        BaseTexSize = new(this, "baseTexSize");
        DetailTexSize = new(this, "detailTexSize");
        MacroTexSize = new(this, "macroTexSize");
    }

    public TerrainMaterialTextureSet(string name) : this(new JsonDict())
    {
        Name.Value = name;

        BaseTexSize.Value = new Vector2(1024, 1024);
        DetailTexSize.Value = new Vector2(1024, 1024);
        MacroTexSize.Value = new Vector2(1024, 1024);
    }
}
