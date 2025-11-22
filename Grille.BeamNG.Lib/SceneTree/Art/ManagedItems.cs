using Grille.BeamNG.SceneTree.Registry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Art;
public class ManagedItems : ArtItemsCollection<TSForestItemData>
{
    public const string FileName = "managedItemData.json";

    public ManagedItems(ArtGroup owner) : base(owner) { }

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

        foreach (var group in Owner.Children)
        {
            group.ManagedItems.EnumerateRecursive(values);
        }
    }
}
