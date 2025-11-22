using Grille.BeamNG.SceneTree.Registry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Forest;
public class ForestItemCollection : JsonDictWrapperListCollection<ForestItem>
{
    public override void Serialize(Stream stream)
    {
        SimItemsJsonSerializer.Serialize(stream, this);
    }

    public override void Deserialize(Stream stream, ItemClassRegistry? registry)
    {
        foreach (var dict in SimItemsJsonSerializer.Deserialize(stream))
        {
            Add(new ForestItem(dict));
        }
    }

    public override void EnumerateRecursive<T>(ICollection<T> values)
    {
        foreach (var item in Enumerate<T>())
        {
            values.Add(item);
        }
    }
}
