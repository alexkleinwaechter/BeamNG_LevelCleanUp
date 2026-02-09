using Grille.BeamNG.SceneTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Text;

/// <summary>
/// Used for Json files in the level’s art directory.
/// <code>
/// FileNames:
/// main.materials.json
/// managedItemData.json
/// </code>
/// </summary>
public class ArtItemsJsonSerializer
{
    public static IReadOnlyList<JsonDict> Load(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open);
        return Deserialize(stream);
    }

    public static void Save(string filePath, IEnumerable<JsonDict> items)
    {
        using var stream = new FileStream(filePath, FileMode.Create);
        Serialize(stream, items);
    }

    public static void Serialize(Stream stream, IEnumerable<JsonDictWrapper> items)
    {
        var dict = new JsonDict();

        foreach (var item in items)
        {
            dict[item.Name.Value] = item.Dict;
        }

        JsonDictSerializer.Serialize(stream, dict, true);
    }

    public static void Serialize(Stream stream, IEnumerable<JsonDict> items)
    {
        var dict = new JsonDict();

        foreach (var item in items)
        {
            var name = (string)item["name"];
            dict[name] = item;
        }

        JsonDictSerializer.Serialize(stream, dict, true);
    }

    public static IReadOnlyList<JsonDict> Deserialize(Stream stream)
    {
        var dict = JsonDictSerializer.Deserialize(stream);

        var list = new List<JsonDict>();

        foreach (var item in dict)
        {
            if (item.Value is not JsonDict)
                continue;

            var obj = (JsonDict)item.Value;
            list.Add(obj);
        }

        return list;
    }

    public static JsonDict[] DeserializeToArray(Stream stream)
    {
        var list = new List<JsonDict>();

        foreach (var item in Deserialize(stream))
        {
            list.Add(item);
        }

        return list.ToArray();
    }
}
