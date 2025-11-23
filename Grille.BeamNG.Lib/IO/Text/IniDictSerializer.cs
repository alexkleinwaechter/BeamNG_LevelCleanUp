using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Text;
internal static class IniDictSerializer
{
    public static JsonDict Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Deserialize(stream);
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
        using var reader = new StreamReader(stream, leaveOpen: true);
        Deserialize(reader, dst);
    }

    public static void Deserialize<T>(TextReader reader, T dst) where T : IDictionary<string, object>
    {
        while (true)
        {
            var line = reader.ReadLine();
            if (line == null)
                break;
            var split = line.Split('=', 2);
            if (split.Length != 2)
                continue;
            dst.Add(split[0].Trim(), split[1].Trim());
        }
    }
}
