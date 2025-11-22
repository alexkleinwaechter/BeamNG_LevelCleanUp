using Grille.BeamNG.Logging;
using Grille.BeamNG.SceneTree.Art;
using Grille.BeamNG.SceneTree.Main;
using System.Collections;
using System.Reflection;

namespace Grille.BeamNG.SceneTree.Registry;

public class ItemClassRegistry : IReadOnlyCollection<KeyValuePair<string, Type>>
{
    public struct Type<T> where T : JsonDictWrapper
    {
        Type _type;
        public Type(Type type)
        {
            _type = type;
        }

        public T Create(JsonDict dict)
        {
            var constructor = _type.GetConstructor([typeof(JsonDict)])!;
            var obj = constructor.Invoke([dict]);

            return (T)obj;
        }

        public static implicit operator Type(Type<T> type) => type._type;
    }

    public static ItemClassRegistry Instance { get; }

    static ItemClassRegistry()
    {
        var registry = new ItemClassRegistry();
        var assembly = Assembly.GetAssembly(typeof(ItemClassRegistry));
        registry.RegisterAssembly(assembly!);
        Instance = registry;
    }

    Dictionary<string, Type> _types;

    public ItemClassRegistry()
    {
        _types = new Dictionary<string, Type>();
    }

    public int Count => _types.Count;

    public void Clear() => _types.Clear();

    public bool ContainsKey(string key) => _types.ContainsKey(key);

    public void Register<T>(string className, bool overwrite = false) where T : JsonDictWrapper
    {
        Register(className, typeof(T), overwrite);
    }

    public void Register(string className, Type type, bool overwrite = false)
    {
        if (!overwrite && _types.ContainsKey(className))
            throw new ArgumentException($"Class of name '{className}' already registered.", nameof(className));

        if (!type.IsSubclassOf(typeof(JsonDictWrapper)))
            throw new ArgumentException("Type must be derived from JsonDictWrapper.", nameof(type));

        if (type.GetConstructor([typeof(JsonDict)]) == null)
            throw new ArgumentException($"Constructor(JsonDict) Missing.");

        _types[className] = type;
    }

    private void TryRegister(Type type, bool overwrite = false)
    {
        if (!type.IsSubclassOf(typeof(JsonDictWrapper)))
            return;

        if (type.GetConstructor([typeof(JsonDict)]) == null)
            return;

        var classNameField = type.GetField("ClassName");
        if (classNameField == null)
            return;

        var classNameObj = classNameField.GetRawConstantValue();
        if (classNameObj is not string)
            return;

        var className = (string)classNameObj;
        if (!overwrite && _types.ContainsKey(className))
            return;

        _types[className] = type;
    }

    public bool TryGet<T>(string className, out Type<T> value) where T : JsonDictWrapper
    {
        var reftype = typeof(T);
        if (_types.TryGetValue(className, out var type))
        {
            if (type.IsSubclassOf(reftype))
            {
                value = new Type<T>(type);
                return true;
            }
        }
        value = new Type<T>();
        return false;
    }

    public bool TryCreate<T>(string className, JsonDict dict, out T value) where T : JsonDictWrapper
    {
        if (TryGet<T>(className, out var type))
        {
            value = type.Create(dict);
            return true;
        }
        value = null!;
        return false;
    }

    /// <summary>
    /// To be picked up by this method, a <see cref="Type"/> must be derived from <see cref="JsonDictWrapper"/>, and must expose both a constructor that takes <see cref="JsonDict"/>, and and a <c>const</c> const <see cref="string"/> named <c>ClassName</c>.
    /// </summary>
    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            TryRegister(type);
        }
    }

    public IEnumerator<KeyValuePair<string, Type>> GetEnumerator() => _types.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _types.GetEnumerator();
}
