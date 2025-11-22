using Grille.BeamNG.Collections;
using Grille.BeamNG.SceneTree.Main;

namespace Grille.BeamNG.SceneTree;

public class JsonDictWrapper : IKeyed
{
    public JsonDict Dict { get; }

    public object this[string key]
    {
        get => Dict[key];
        set => Dict[key] = value;
    }

    public string? TypeClassName { get; }
    public JsonDictProperty<string> Class { get; }
    public JsonDictProperty<string> InternalName { get; }
    public JsonDictProperty<string> Name { get; }
    public JsonDictProperty<string> PersistentId { get; }

    string IKeyed.Key => Name.Exists ? Name.Value : string.Empty;

    public JsonDictWrapper(JsonDict? dict) : this(dict, null) { }

    public JsonDictWrapper(JsonDict? dict, string? className)
    {
        TypeClassName = className;

        Dict = dict != null ? dict : new JsonDict();

        Class = new(this, "class");
        Name = new(this, "name");
        InternalName = new(this, "internalName");
        PersistentId = new(this, "persistentId");

        if (TypeClassName != null)
        {
            if (!Class.Exists)
                Class.Value = TypeClassName;

            if (Class.Value != TypeClassName)
            throw new ArgumentException($"Class '{Class.Value}' does not match expected value '{TypeClassName}'.");
        }
    }

    public void Save(string filePath)
    {
        using var stream = File.Create(filePath);
        Serialize(stream);
    }

    public void Load(string filePath) { 
        using var stream = File.OpenRead(filePath);
    }

    public void Serialize(Stream stream)
    {
        JsonDictSerializer.Serialize(stream, Dict, true);
    }

    public void Deserialize(Stream stream)
    {
        JsonDictSerializer.Deserialize(stream, Dict);
        Deserialize(stream);
    }

    public bool TryGetValue<T>(string key, out T value)
    {
        if (Dict.TryGetValue(key, out var obj))
        {
            value = (T)obj;
            return true;
        }
        value = default!;
        return false;
    }

    public bool TryPopValue<T>(string key, out T value)
    {
        if (TryGetValue(key, out value))
        {
            Dict.Remove(key);
            return true;
        }
        return false;
    }

    public void TryPopValue<T>(string key, out T value, T defaultValue)
    {
        if (!TryPopValue(key, out value))
        {
            value = defaultValue;
        }
    }

    public void ApplyNamespace(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
            return;
        foreach (var item in EnumerateIdentifiers())
        {
            item.Value = @namespace + item.Value;
        }
    }

    /// <summary> Enumerates all fields that are used to identify this or other objects.</summary>
    public virtual IEnumerable<JsonDictProperty<string>> EnumerateIdentifiers()
    {
        if (Name.Exists)
            yield return Name;
        if (InternalName.Exists)
            yield return InternalName;
    }

    public IEnumerable<T> EnumerateValues<T>()
    {
        foreach (var item in Dict.Values)
        {
            if (item is T)
            {
                yield return (T)item;
            }
        }
    }
}