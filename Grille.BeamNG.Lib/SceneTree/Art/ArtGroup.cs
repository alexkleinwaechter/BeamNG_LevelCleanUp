using Grille.BeamNG.Collections;
using Grille.BeamNG.IO;
using Grille.BeamNG.IO.Resources;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree.Art;

public class ArtGroup : ISceneTreeGroup
{
    string IKeyed.Key => Name;

    public bool IsEmpty => Children.Count == 0 && Resources.Count == 0 && MaterialItems.Count == 0;

    public string Name { get; }

    public KeyedCollection<ArtGroup> Children { get; }

    public ResourceCollection Resources { get; }

    public MaterialItems MaterialItems { get; }

    public ManagedItems ManagedItems { get; }

    public ArtGroup(string name)
    {
        Name = name;
        Children = new();
        Resources = new(true);
        MaterialItems = new(this);
        ManagedItems = new(this);
    }

    public void SaveTree(string dirPath, bool ignoreEmpty = true)
    {
        Directory.CreateDirectory(dirPath);

        foreach (var item in Children)
        {
            if (item.IsEmpty && ignoreEmpty)
                continue;
            var childpath = Path.Combine(dirPath, item.Name);
            item.SaveTree(childpath, ignoreEmpty);
        }

        MaterialItems.TrySaveToDirectory(dirPath);
        ManagedItems.TrySaveToDirectory(dirPath);

        foreach (var resource in Resources)
        {
            resource.SaveToDirectory(dirPath);
        }
    }
     
    public void LoadTree(string dirPath)
    {
        LoadTree(dirPath, ItemClassRegistry.Instance);
    }

    public void LoadTree(string dirPath, ItemClassRegistry registry)
    {
        foreach (var item in Directory.EnumerateDirectories(dirPath))
        {
            var name = Path.GetFileName(item);
            var group = new ArtGroup(name);
            Children.Add(group);
            var childPath = Path.Combine(dirPath, name);
            group.LoadTree(childPath, registry);
        }

        MaterialItems.TryLoadFromDirectory(dirPath, registry);
        ManagedItems.TryLoadFromDirectory(dirPath, registry);

        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            var ext = Path.GetExtension(file);
            if (ext.ToLower() == ".json")
                continue;

            var name = Path.GetFileName(file);

            var resouce = new FileResource(name, file, false);

            Resources.Add(resouce);
        }
    }
}
