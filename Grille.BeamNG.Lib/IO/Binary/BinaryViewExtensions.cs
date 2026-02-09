using Grille.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Binary;
internal static class BinaryViewExtensions
{
    public static byte ReadVersion(this BinaryViewReader br, byte expected, bool ignoreVersion)
    {
        byte version = br.ReadByte();
        if (version != expected && !ignoreVersion)
        {
            throw new InvalidDataException($"Unsupported terrain version '{version}'.");
        }
        return version;
    }

    public static void Fill<T>(this BinaryViewWriter bw, T value, long count) where T : unmanaged 
    {
        for (int i = 0; i < count; i++)
        {
            bw.Write(value);
        }
    }

    public static void WriteMaterialNames(this BinaryViewWriter bw, string[] names)
    {
        bw.WriteUInt32((uint)names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            bw.WriteString(names[i], LengthPrefix.Byte, Encoding.UTF8);
        }
    }

    public static string[] ReadMaterialNames(this BinaryViewReader br)
    {
        return br.ReadStringArray(LengthPrefix.UInt32, LengthPrefix.Byte, Encoding.UTF8);
    }

    public static string[] ReadStringArray(this BinaryViewReader br, LengthPrefix arrayLength, LengthPrefix stringLength, Encoding encoding)
    {
        long count = br.ReadLengthPrefix(arrayLength);
        var array = new string[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = br.ReadString(stringLength, encoding);
        }
        return array;
    }

    public static void AssertEndOfFile(this BinaryViewReader br)
    {
        if (br.Position != br.Length)
        {
            throw new InvalidDataException("Unexpected data remaining.");
        }
    }
}
