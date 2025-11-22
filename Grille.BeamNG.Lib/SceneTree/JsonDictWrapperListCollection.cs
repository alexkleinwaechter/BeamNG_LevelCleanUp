using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Grille.BeamNG.Collections;
using Grille.BeamNG.IO;
using Grille.BeamNG.SceneTree.Main;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree;
public abstract class JsonDictWrapperListCollection<TItem> : IJsonDictWrapperCollection<TItem> where TItem : JsonDictWrapper
{
    readonly List<TItem> _list;

    public JsonDictWrapperListCollection()
    {
        _list = new();
    }

    public int Count => _list.Count;

    public bool IsReadOnly => false;

    public void Add(TItem item) => _list.Add(item);

    public void Add(params TItem[] items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void Clear() => _list.Clear();

    public bool Contains(TItem item) => _list.Contains(item);

    public void CopyTo(TItem[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);

    public IEnumerator<TItem> GetEnumerator() => _list.GetEnumerator();

    public bool Remove(TItem item) => _list.Remove(item);

    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();

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

    /// <summary> Return all objects derived from the given type. </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEnumerable<T> Enumerate<T>() where T : TItem
    {
        foreach (var item in _list)
        {
            if (item is T)
            {
                yield return (T)item;
            }
        }
    }

    public IEnumerable<TItem> Enumerate()
    {
        foreach (var item in _list)
        {
            yield return item;
        }
    }

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

}