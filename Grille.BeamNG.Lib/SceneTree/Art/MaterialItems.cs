using Grille.BeamNG.Collections;
using Grille.BeamNG.IO;
using Grille.BeamNG.IO.Resources;
using Grille.BeamNG.SceneTree.Main;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree.Art;

public class MaterialItems : ArtItemsCollection<ArtItem>
{
    public const string FileName = "main.materials.json";

    public MaterialItems(ArtGroup? owner) : base(owner)
    {

    }

    public string[] GetMaterialNames()
    {
        List<string> names = new List<string>();
        foreach (var material in Enumerate<Material>())
        {
            names.Add(material.Name.Value);
        }
        return names.ToArray();
    }

    public bool TrySaveToDirectory(string dirPath)
    {
        return TrySaveToDirectory(dirPath, FileName);
    }

    public bool TryLoadFromDirectory(string dirPath, ItemClassRegistry registry)
    {
        return TryLoadFromDirectory(dirPath, FileName, registry);
    }

    public override void EnumerateRecursive<T>(ICollection<T> values)
    {
        foreach (var item in Enumerate<T>())
        {
            values.Add(item);
        }

        if (Owner != null)
        {
            foreach (var group in Owner.Children)
            {
                group.MaterialItems.EnumerateRecursive(values);
            }
        }
    }
}
