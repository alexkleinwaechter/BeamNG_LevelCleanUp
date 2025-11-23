using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Grille.IO;

namespace Grille.BeamNG.IO.Binary;

public static class TerrainV9Serializer
{
    public static TerrainV9Binary Deserialize(Stream stream, bool ignoreVersion = false)
    {
        var binary = new TerrainV9Binary();
        Deserialize(stream, binary, ignoreVersion);
        return binary;
    }

    public static void Deserialize(Stream stream, TerrainV9Binary terrain, bool ignoreVersion = false)
    {
        using var br = new BinaryViewReader(stream);

        byte version = br.ReadVersion(9, ignoreVersion);

        uint size = br.ReadUInt32();

        terrain.Version = version;
        terrain.Size = size;

        int length = terrain.Length;

        terrain.HeightData = br.ReadArray<ushort>(length);
        terrain.MaterialData = br.ReadArray<byte>(length);

        terrain.MaterialNames = br.ReadMaterialNames();

        br.AssertEndOfFile();
    }

    public static void Serialize(Stream stream, TerrainV9Binary terrain, bool validate = true)
    {
        if (validate && !terrain.Validate(out var e))
            throw e;

        using var bw = new BinaryViewWriter(stream);

        bw.WriteByte(terrain.Version);
        bw.WriteUInt32(terrain.Size);

        bw.WriteArray(terrain.HeightData, LengthPrefix.None);
        bw.WriteArray(terrain.MaterialData, LengthPrefix.None);

        bw.WriteMaterialNames(terrain.MaterialNames);
    }

    public static void Serialize(Stream stream, TerrainTemplate info)
    {
        long size = info.Resolution * (long)info.Resolution;
        ushort u16height = info.U16Height;

        using var bw = new BinaryViewWriter(stream);

        bw.WriteByte(9);
        bw.WriteUInt32((uint)info.Resolution);

        bw.Fill(u16height, size);
        bw.Fill((byte)info.MaterialIndex, size);

        bw.WriteMaterialNames(info.MaterialNames.ToArray());
    }

    public static void Deserialize(Stream stream, Terrain terrain, float maxHeight, bool ignoreVersion = false)
    {
        using var br = new BinaryViewReader(stream);

        br.ReadVersion(9, ignoreVersion);
        int size = (int)br.ReadUInt32();

        var data = new TerrainDataBuffer(size, size);

        int length = size * size;

        for (int i = 0; i < data.Length; i++)
        {
            var u16height = br.ReadUInt16();
            data[i].Height = GetSingleHeight(u16height, maxHeight);
        }

        for (int i = 0; i < data.Length; i++)
        {
            var material = br.ReadByte();
            if (material == byte.MaxValue)
            {
                data[i].Material = 0;
                data[i].IsHole = true;
            }
            else
            {
                data[i].Material = material;
                data[i].IsHole = false;
            }
        }

        terrain.Data = data;

        terrain.MaterialNames = br.ReadMaterialNames();
    }

    public static void Serialize(Stream stream, Terrain terrain, float maxHeight)
    {
        using var bw = new BinaryViewWriter(stream);

        var data = terrain.Data;

        bw.WriteByte(9);
        bw.WriteUInt32((uint)terrain.Size);

        for (int i = 0; i < data.Length; i++)
        {
            var u16height = GetU16Height(data[i].Height, maxHeight);
            bw.WriteUInt16(u16height);
        }

        for (int i = 0; i < data.Length; i++)
        {
            var material = data[i].IsHole ? byte.MaxValue : (byte)data[i].Material;
            bw.WriteByte(material);
        }

        bw.WriteMaterialNames(terrain.MaterialNames);
    }

    public static ByteSize CalcApproxSize(int resolution)
    {
        long head = sizeof(byte) + sizeof(uint);
        long length = resolution * (long)resolution;
        long content = length * (sizeof(byte) + sizeof(ushort));
        return head + content;
    }

    public static ByteSize CalcApproxSize(int resolution, IReadOnlyCollection<string> names)
    {
        long size = CalcApproxSize(resolution);
        size += sizeof(uint);
        foreach (string name in names) {
            size += name.Length + 1;
        }
        return size;
    }

    public static ushort GetU16Height(float height, float maxHeight)
    {
        float u16max = ushort.MaxValue;

        float u16height = height / maxHeight * u16max;
        if (u16height > u16max)
            u16height = u16max;

        return (ushort)u16height;
    }

    public static float GetSingleHeight(ushort u16height, float maxHeight)
    {
        float height = u16height;
        float u16max = ushort.MaxValue;

        return height / u16max * maxHeight;
    }
}
