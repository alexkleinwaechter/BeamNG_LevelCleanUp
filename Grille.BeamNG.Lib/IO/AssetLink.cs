using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO;
public class AssetLink
{
    public string Path { get; }
    public string Type { get; }

    public AssetLink(string path, string type)
    {
        Path = path;
        Type = type;
    }

    public static AssetLink Deserialize(Stream stream)
    {
        return Deserialize(JsonDictSerializer.Deserialize(stream));
    }

    public static AssetLink Deserialize(ReadOnlySpan<char> text)
    {
        return Deserialize(JsonDictSerializer.Deserialize(text));
    }

    public static AssetLink Deserialize(IDictionary<string, object> dict)
    {
        var path = (string)dict["path"];
        var type = (string)dict["type"];
        return new AssetLink(path, type);
    }
}
