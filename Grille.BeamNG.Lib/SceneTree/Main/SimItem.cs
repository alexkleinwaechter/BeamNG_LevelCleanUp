namespace Grille.BeamNG.SceneTree.Main;

public class SimItem : JsonDictWrapper
{
    public JsonDictProperty<Vector3> Position { get; }

    public JsonDictProperty<string> Parent { get; }

    public SimItem(JsonDict? dict, string className) : base(dict, className)
    {
        if (className == null)
            throw new ArgumentNullException("class");

        Position = new(this, "position");
        Parent = new(this, "__parent");
    }

    /// <summary>Called by <see cref="SimGroup.SaveTree(string, bool)"/> can be safely ignored otherwise.</summary>
    public void SetParent(SimGroup? parent)
    {
        if (parent == null)
        {
            Parent.Remove();
            return;
        }

        Parent.Value = parent.Name.Value;
    }
}
