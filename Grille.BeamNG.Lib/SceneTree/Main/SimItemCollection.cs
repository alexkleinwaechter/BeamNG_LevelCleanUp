using Grille.BeamNG.Logging;
using Grille.BeamNG.SceneTree.Registry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Main;
public class SimItemCollection : JsonDictWrapperListCollection<SimItem>
{
    public SimGroup Owner { get; }

    public SimItemCollection(SimGroup owner)
    {
        Owner = owner;
    }

    public override void Serialize(Stream stream)
    {
        foreach (var item in this)
        {
            item.SetParent(Owner.IsMain ? null : Owner);
        }
        SimItemsJsonSerializer.Serialize(stream, this);
    }

    public override void Deserialize(Stream stream, ItemClassRegistry registry)
    {
        foreach (var dict in SimItemsJsonSerializer.Deserialize(stream))
        {
            var className = (string)dict["class"];

            if (registry.TryCreate<SimItem>(className, dict, out var obj))
            {
                Add(obj);
            }
            else
            {
                Add(new SimItem(dict, className));
            }
        }
    }

    public override void EnumerateRecursive<T>(ICollection<T> values)
    {
        foreach (var item in Enumerate<T>())
        {
            values.Add(item);
        }

        foreach (var group in Enumerate<SimGroup>())
        {
            group.Items.EnumerateRecursive(values);
        }
    }
}
