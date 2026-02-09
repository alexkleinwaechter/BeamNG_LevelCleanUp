using Grille.BeamNG.Collections;
using Grille.BeamNG.IO.Text;
using Grille.BeamNG.Numerics;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Registry;
public static class JsonDictPropertyTypeRegistry
{
    record Entry<T>(Func<JsonDict, string, T> Read, Action<JsonDict, string, T> Write);

    static Dictionary<Type, object> _types;

    static JsonDictPropertyTypeRegistry()
    {
        _types = new Dictionary<Type, object>();

        Register(Methods.ReadInt32, Methods.WriteInt32);
        Register(Methods.ReadVector2, Methods.WriteVector2);
        Register(Methods.ReadVector3, Methods.WriteVector3);
        Register(Methods.ReadVector4, Methods.WriteVector4);
        Register(Methods.ReadMatrix3, Methods.WriteMatrix3);
    }

    public static bool TryRead<T>(JsonDict dict, string key, [MaybeNullWhen(false)] out T value) where T : notnull
    {
        if (TryGetEntry<T>(out var entry))
        {
            value = entry.Read(dict, key);
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryWrite<T>(JsonDict dict, string key, T value) where T : notnull
    {
        if (TryGetEntry<T>(out var entry))
        {
            entry.Write(dict, key, value);
            return true;
        }
        return false;
    }

    static bool TryGetEntry<T>([MaybeNullWhen(false)] out Entry<T> entry) where T : notnull
    {
        var type = typeof(T);
        if (_types.TryGetValue(type, out var obj)){
            entry = (Entry<T>)obj;
            return true;
        }
        entry = null;
        return false;
    }

    public static bool Remove<T>()
    {
        var type = typeof(T);
        return _types.Remove(type);
    }

    public static void Register<T>(Func<JsonDict, string, T> read, Action<JsonDict, string, T> write) where T : notnull
    {
        var type = typeof(T);
        var entry = new Entry<T>(read, write);
        _types.Add(type, entry);
    }
}

file static class Methods
{
    public static void WriteInt32(JsonDict dict, string key, int value)
    {
        dict[key] = (float)value;
    }

    public static int ReadInt32(JsonDict dict, string key)
    {
        return (int)(float)dict[key];
    }

    static float[] GetWriteArray(JsonDict dict, string key, int length)
    {
        if (dict.TryGetValue<float[]>(key, out var array) && array.Length == length)
        {
            return array;
        }
        array = new float[length];
        dict[key] = array;
        return array;
    }

    static float[] GetReadArray(JsonDict dict, string key, int length)
    {
        var array = (float[])dict[key];
        if (array.Length != length)
        {
            throw new InvalidDataException();
        }
        return array;
    }

    public static void WriteVector2(JsonDict dict, string key, Vector2 vec)
    {
        var array = GetWriteArray(dict, key, 2);
        vec.CopyTo(array);
    }

    public static Vector2 ReadVector2(JsonDict dict, string key)
    {
        var array = GetReadArray(dict, key, 2);
        return new Vector2(array);
    }

    public static void WriteVector3(JsonDict dict, string key, Vector3 vec)
    {
        var array = GetWriteArray(dict, key, 3);
        vec.CopyTo(array);
    }

    public static Vector3 ReadVector3(JsonDict dict, string key)
    {
        var array = GetReadArray(dict, key, 3);
        return new Vector3(array);
    }

    public static void WriteVector4(JsonDict dict, string key, Vector4 vec)
    {
        var array = GetWriteArray(dict, key, 4);
        vec.CopyTo(array);
    }

    public static Vector4 ReadVector4(JsonDict dict, string key)
    {
        var array = GetReadArray(dict, key, 4);
        return new Vector4(array);
    }

    public static void WriteMatrix3(JsonDict dict, string key, RotationMatrix3x3 matrix)
    {
        var array = GetWriteArray(dict, key, 9);
        matrix.CopyTo(array);
    }

    public static RotationMatrix3x3 ReadMatrix3(JsonDict dict, string key)
    {
        var array = GetReadArray(dict, key, 9);
        return new RotationMatrix3x3(array);
    }
}
