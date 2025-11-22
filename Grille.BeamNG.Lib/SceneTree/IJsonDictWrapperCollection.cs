using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree;
public interface IJsonDictWrapperCollection<TItem> : ICollection<TItem> where TItem : JsonDictWrapper
{
    public IEnumerable<TItem> Enumerate();

    public IEnumerable<T> Enumerate<T>() where T : TItem;

    public IReadOnlyCollection<TItem> EnumerateRecursive();

    public IReadOnlyCollection<T> EnumerateRecursive<T>() where T : TItem;

    public void EnumerateRecursive<T>(ICollection<T> values) where T : TItem;
}
