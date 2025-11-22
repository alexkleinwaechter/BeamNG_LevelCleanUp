using Grille.BeamNG.IO;
using Grille.BeamNG.IO.Text;
using Grille.BeamNG.SceneTree.Art;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree.Main;

public class SimGroup : SimItem, ISceneTreeGroup
{
    public const string ClassName = "SimGroup";

    public bool IsMain { get; set; } = false;

    public bool IsEmpty => Items.Count == 0;

    public SimItemCollection Items { get; }

    public SimGroup(JsonDict dict) : base(dict, ClassName)
    {
        Items = new(this);
    }

    public SimGroup(string name) : this(new JsonDict())
    {
        Class.Value = "SimGroup";
        Name.Value = name;
    }

    public const string FileName = "items.level.json";

    public void SaveTree(string dirPath, bool ignoreEmpty = true)
    {
        Directory.CreateDirectory(dirPath);

        var filePath = Path.Combine(dirPath, FileName);
        Parent.Remove();
        Items.Save(filePath);

        foreach (var item in Items.Enumerate<SimGroup>())
        {
            if (item.IsEmpty && ignoreEmpty) 
                continue;
            var childPath = Path.Combine(dirPath, item.Name.Value);
            Directory.CreateDirectory(childPath);
            item.SaveTree(childPath, ignoreEmpty);
        }
    }

    public void LoadTree(string dirPath)
    {
        LoadTree(dirPath, ItemClassRegistry.Instance);
    }

    public void LoadTree(string dirPath, ItemClassRegistry registry)
    {
        var filePath = Path.Combine(dirPath, FileName);
        if (!File.Exists(filePath))
            return;
        Items.Load(filePath, registry);

        foreach (var group  in Items.Enumerate<SimGroup>())
        {
            var childPath = Path.Combine(dirPath, group.Name.Value);
            if (!Directory.Exists(childPath))
                continue;
            group.LoadTree(childPath, registry);
        }
    }
}
