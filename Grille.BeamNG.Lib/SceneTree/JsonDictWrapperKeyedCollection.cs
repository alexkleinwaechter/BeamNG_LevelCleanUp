using Grille.BeamNG.Collections;
using Grille.BeamNG.IO;
using Grille.BeamNG.SceneTree.Main;
using Grille.BeamNG.SceneTree.Registry;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree;

public abstract class JsonDictWrapperKeyedCollection<TItem> : KeyedCollection<TItem>, IJsonDictWrapperCollection<TItem> where TItem : JsonDictWrapper
{
    public void Save(string filePath)
    {
        using var stream = File.Create(filePath);
        Serialize(stream);
    }

    public abstract void Serialize(Stream stream);

    public void Load(string filePath) => Load(filePath, ItemClassRegistry.Instance);

    public void Deserialize(Stream stream) => Deserialize(stream, ItemClassRegistry.Instance);

    public void Load(string filePath, ItemClassRegistry registry)
    {
        using var stream = File.OpenRead(filePath);
        Deserialize(stream, registry);
    }

    public abstract void Deserialize(Stream stream, ItemClassRegistry registry);

    public IReadOnlyCollection<TItem> EnumerateRecursive()
    {
        return EnumerateRecursive<TItem>();
    }

    public IReadOnlyCollection<T> EnumerateRecursive<T>() where T : TItem
    {
        var list = new List<T>();
        EnumerateRecursive(list);
        return list;
    }

    public abstract void EnumerateRecursive<T>(ICollection<T> values) where T : TItem;
}
