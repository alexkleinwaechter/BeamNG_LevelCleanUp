using Grille.BeamNG.SceneTree;
using System.Text;

namespace Grille.BeamNG.IO.Text;

/// <summary>
/// Used for Json files in the level’s main and forest directory.
/// <code>
/// FileNames:
/// items.level.json
/// *.forest4.json
/// </code>
/// </summary>
public static class SimItemsJsonSerializer
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
        using var sw = new StreamWriter(stream);
        foreach (var item in items)
        {
            JsonDictSerializer.Serialize(stream, item.Dict, false);
            stream.WriteByte((byte)'\n');
        }
    }

    public static void Serialize(Stream stream, IEnumerable<JsonDict> items)
    {
        using var sw = new StreamWriter(stream);
        foreach (var item in items)
        {
            JsonDictSerializer.Serialize(stream, item, false);
            stream.WriteByte((byte)'\n');
        }
    }

    public static IReadOnlyList<JsonDict> Deserialize(Stream stream)
    {
        using var sr = new StreamReader(stream);

        var list = new List<JsonDict>();

        while (true)
        {
            var line = sr.ReadLine();
            if (line == null)
                break;

            var dict = new JsonDict();
            JsonDictSerializer.Deserialize(line, dict);

            list.Add(dict);
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
