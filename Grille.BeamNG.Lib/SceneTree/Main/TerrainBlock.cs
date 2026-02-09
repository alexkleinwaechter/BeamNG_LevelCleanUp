namespace Grille.BeamNG.SceneTree.Main;

public class TerrainBlock : SimItem
{
    public const string ClassName = "TerrainBlock";

    public JsonDictProperty<string> MaterialTextureSet { get; }
    public JsonDictProperty<string> TerrainFile { get; }
    public JsonDictProperty<int> BaseTexSize { get; }
    public JsonDictProperty<float> MaxHeight { get; }
    public JsonDictProperty<float> SquareSize { get; }

    public TerrainBlock(JsonDict dict) : base(dict, ClassName)
    {
        MaterialTextureSet = new(this, "materialTextureSet");
        TerrainFile = new(this, "terrainFile");
        BaseTexSize = new(this, "baseTexSize");
        MaxHeight = new(this, "maxHeight");
        SquareSize = new(this, "squareSize");

        Name.Value = "theTerrain";
    }

    public TerrainBlock(TerrainTemplate info) : this(new JsonDict())
    {
        int offset = (int)info.WorldSize / 2;
        Position.Value = new Vector3(-offset, -offset, 0);
        MaxHeight.Value = info.MaxHeight;
        SquareSize.Value = info.SquareSize;
    }
}
