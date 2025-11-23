using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Collections;

public static class IDictionaryExtension
{
    public static bool TryGetValue<T>(this IDictionary<string,object> dict, string key, [MaybeNullWhen(false)] out T value)
    {
        if (dict.TryGetValue(key, out var obj))
        {
            value = (T)obj;
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryPopValue<T>(this IDictionary<string, object> dict, string key, [MaybeNullWhen(false)]  out T value)
    {
        if (dict.TryGetValue(key, out value))
        {
            dict.Remove(key);
            return true;
        }
        return false;
    }

    public static void TryPopValue<T>(this IDictionary<string, object> dict, string key, out T value, T defaultValue)
    {
        if (!dict.TryPopValue(key, out value!))
        {
            value = defaultValue;
        }
    }
}
