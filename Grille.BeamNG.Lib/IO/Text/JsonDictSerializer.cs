using System.Text.Json;

namespace Grille.BeamNG.IO.Text;

public static class JsonDictSerializer
{
    public const string ArrayClassName = "Template_Array";

    public static void Serialize<T>(Stream stream, T value, bool intended = false) where T : IDictionary<string, object>
    {
        var options = new JsonSerializerOptions()
        {
            WriteIndented = intended,
        };
        JsonSerializer.Serialize(stream, value, options);
    }

    public static JsonDict Deserialize(Stream stream) => Deserialize<JsonDict>(stream);

    public static T Deserialize<T>(Stream stream) where T : IDictionary<string, object>, new()
    {
        var result = new T();
        Deserialize(stream, result);
        return result;
    }

    public static void Deserialize<T>(Stream stream, T dst) where T : IDictionary<string, object>
    {
        var options = new JsonSerializerOptions()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var json = JsonSerializer.Deserialize<JsonElement>(stream, options);

        Deserialize(json, dst);
    }

    public static JsonDict Deserialize(ReadOnlySpan<char> text) => Deserialize<JsonDict>(text);

    public static T Deserialize<T>(ReadOnlySpan<char> text) where T : IDictionary<string, object>, new()
    {
        var result = new T();
        Deserialize(text, result);
        return result;
    }

    public static void Deserialize<T>(ReadOnlySpan<char> text, T dst) where T : IDictionary<string, object>
    {
        var options = new JsonSerializerOptions()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var json = JsonSerializer.Deserialize<JsonElement>(text, options);

        Deserialize(json, dst);
    }

    public static void Deserialize<T>(JsonElement element, T dst) where T : IDictionary<string, object>
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            GetDict(element, dst);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            dst["class"] = ArrayClassName;
            dst["items"] = GetArray(element);
        }
        else
        {
            throw new Exception();
        }
    }

    static object? GetValue(JsonElement json)
    {
        object value = json.ValueKind switch
        {
            JsonValueKind.String => json.ToString(),
            JsonValueKind.Number => json.GetSingle(),
            JsonValueKind.Array => GetArray(json),
            JsonValueKind.Object => GetDict(json),
            JsonValueKind.True => json.GetBoolean(),
            JsonValueKind.False => json.GetBoolean(),
            JsonValueKind.Null => null!,
            _ => throw new JsonException($"Unexpected type {json.ValueKind}.")
        };

        return value;

    }

    static object GetArray(JsonElement json)
    {
        int count = json.GetArrayLength();
        if (count == 0)
            throw new JsonException("Array length is 0.");

        var first = json[0];

        if (first.ValueKind == JsonValueKind.Object)
        {
            var objarray = new JsonDict[count];

            for (int i = 0; i < count; i++)
            {
                objarray[i] = GetDict(json[i]);
            }

            return objarray;
        }

        Array? array = first.ValueKind switch
        {
            JsonValueKind.Number => json.Deserialize<float[]>(),
            _ => throw new JsonException($"Unexpected type in array {json.ValueKind}."),
        };

        if (array == null)
            throw new JsonException("Array is null.");

        return array;
    }

    static JsonDict GetDict(JsonElement json)
    {
        var result = new JsonDict();
        GetDict(json, result);
        return result;
    }

    static void GetDict<T>(JsonElement json, T dst) where T : IDictionary<string, object>
    {
        var jdict = json.Deserialize<Dictionary<string, JsonElement>>();
        if (jdict == null)
            throw new JsonException("Dict is null.");

        var result = dst;

        foreach (var jelement in jdict)
        {
            var key = jelement.Key;
            object? value = GetValue(jelement.Value);

            if (value != null)
            {
                result[key] = value;
            }
        }
    }

}
