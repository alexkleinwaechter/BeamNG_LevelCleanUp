using Grille.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Binary.Torque3D;
public class MisDecalsV5Serializer
{
    public static MisDecalsV5Binary Deserialize(Stream stream, bool ignoreVersion = false)
    {
        var binary = new MisDecalsV5Binary();
        Deserialize(stream, binary, ignoreVersion);
        return binary;
    }

    public static void Deserialize(Stream stream, MisDecalsV5Binary binary, bool ignoreVersion = false)
    {
        using var br = new BinaryViewReader(stream);
        br.ReadInt32();
        byte version = br.ReadVersion(5, ignoreVersion);
        binary.DecalNames = br.ReadStringArray(LengthPrefix.UInt32, LengthPrefix.Byte, Encoding.UTF8);
        binary.DecalInstances = br.ReadArray<MisDecalsV5Binary.DecalInstance>(LengthPrefix.UInt32);
        br.AssertEndOfFile();
    }

    public static Dictionary<string, object> ConvertToV2Dict(MisDecalsV5Binary binary)
    {
        var header = new Dictionary<string, object>
        {
            { "name", "DecalData File" },
            { "version", 2 },
        };

        var instances = new Dictionary<string, List<float[]>>();
        int uid = 0;

        foreach (var src in binary.DecalInstances)
        {
            var name = binary.DecalNames[src.DataIndex];

            if (!instances.TryGetValue(name, out var list))
            {
                instances[name] = list = new List<float[]>();
            }

            var array = new float[13] {
                src.RectIdx,
                src.Size,
                src.RenderPriority,
                src.Position.X, src.Position.Y, src.Position.Z,
                src.Normal.X, src.Normal.Y, src.Normal.Z,
                src.Tangent.X, src.Tangent.Y, src.Tangent.Z,
                uid++,
            };

            list.Add(array);
        }

        return new Dictionary<string, object>
        {
            { "header", header },
            { "instances", instances },
        };
    }

}
