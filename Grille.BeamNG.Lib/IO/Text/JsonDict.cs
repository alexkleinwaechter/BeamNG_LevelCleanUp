using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Text;
public class JsonDict : IDictionary<string, object>
{
    SortedList<string, object> _dict;

    public JsonDict()
    {
        _dict = new SortedList<string, object>();
    }

    public JsonDict(IDictionary<string, object> dict)
    {
        _dict = new SortedList<string, object>(dict);
    }

    public JsonDict(JsonDict dict)
    {
        _dict = new SortedList<string, object>(dict._dict);
    }

    public object this[string key] { get => _dict[key]; set => _dict[key] = value; }

    public ICollection<string> Keys => _dict.Keys;

    public ICollection<object> Values => _dict.Values;

    public int Count => _dict.Count;

    public bool IsReadOnly => ((ICollection<KeyValuePair<string, object>>)_dict).IsReadOnly;

    public void Add(string key, object value)
    {
        _dict.Add(key, value);
    }

    public void Add(KeyValuePair<string, object> item)
    {
        ((ICollection<KeyValuePair<string, object>>)_dict).Add(item);
    }

    public void Clear()
    {
        _dict.Clear();
    }

    public bool Contains(KeyValuePair<string, object> item)
    {
        return _dict.Contains(item);
    }

    public bool ContainsKey(string key)
    {
        return _dict.ContainsKey(key);
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<string, object>>)_dict).CopyTo(array, arrayIndex);
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return _dict.GetEnumerator();
    }

    public bool Remove(string key)
    {
        return _dict.Remove(key);
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        return ((ICollection<KeyValuePair<string, object>>)_dict).Remove(item);
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value)
    {
        return _dict.TryGetValue(key, out value);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)_dict).GetEnumerator();
    }
}
