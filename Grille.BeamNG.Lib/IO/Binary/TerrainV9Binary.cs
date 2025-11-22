using System.Diagnostics.CodeAnalysis;
using System.Drawing;

namespace Grille.BeamNG.IO.Binary;

public class TerrainV9Binary
{
    public byte Version { get; set; }
     
    public uint Size { get; set; }

    public ushort[] HeightData { get; set; }

    public byte[] MaterialData { get; set; }

    public string[] MaterialNames { get; set; }

    public int Length => (int)(Size * Size);

    public TerrainV9Binary()
    {
        Version = 9;
        Size = 0;

        HeightData = Array.Empty<ushort>();
        MaterialData = Array.Empty<byte>();
        MaterialNames = Array.Empty<string>();
    }

    public TerrainV9Binary(int size)  {
        Version = 9;
        Size = (uint)size;
        var length = Length;
        HeightData = new ushort[length];
        MaterialData = new byte[length];
        MaterialNames = Array.Empty<string>();
    }

    public bool Validate([MaybeNullWhen(true)] out Exception e)
    {
        int length = Length;

        if (Version != 9)
        {
            e = new InvalidDataException("Version must equal 9.");
            return false;
        }

        if (HeightData.Length != length)
        {
            e = new InvalidDataException("HeightData.Length must equal Size^2.");
            return false;
        }

        if (MaterialData.Length != length)
        {
            e = new InvalidDataException("MaterialData.Length must equal Size^2.");
            return false;
        }

        e = null;
        return true;
    }
}