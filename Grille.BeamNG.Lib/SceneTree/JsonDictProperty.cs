using Grille.BeamNG.Collections;
using Grille.BeamNG.SceneTree.Registry;
using System.Runtime.CompilerServices;

namespace Grille.BeamNG.SceneTree;

public class JsonDictProperty<T> : IKeyed where T : notnull
{
    public JsonDictWrapper Owner { get; }

    public string Key { get; }

    public T? DefaultValue { get; }

    public bool Exists => Owner.Dict.ContainsKey(Key);

    public void Remove() => Owner.Dict.Remove(Key);

    public JsonDictProperty(JsonDictWrapper owner, string key)
    {
        Owner = owner;
        Key = key;
    }

    public JsonDictProperty(JsonDictWrapper owner, string key, T defaultValue) : this(owner, key)
    {
        DefaultValue = defaultValue;
    }

    public object RawValue { get => Owner[Key]; set => Owner[Key] = value; }

    public T Value { get => Get(); set => Set(value); }

    public static implicit operator T(JsonDictProperty<T> value) => value.Value;

    public void Set(T value)
    {
        if (!JsonDictPropertyTypeRegistry.TryWrite(Owner.Dict, Key, value))
        {
            RawValue = value;
        }
    }

    public T Get()
    {
        if (!Exists)
        {
            if (DefaultValue != null)
            {
                return DefaultValue;
            }
            var msg = $"Failed to acces property '{Key}' of type '{typeof(T).FullName}'.";
            throw new InvalidOperationException(msg);
        }

        if (JsonDictPropertyTypeRegistry.TryRead<T>(Owner.Dict, Key, out var value))
        {
            return value;
        }
        return (T)RawValue;
    }

    public void SetIfEmpty(T value)
    {
        if (!Exists)
        {
            Set(value);
        }
    }

    public override string? ToString() => Value.ToString();
}