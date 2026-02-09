using System.Runtime.CompilerServices;

namespace Grille.BeamNG.IO.Binary;
public struct ByteSize
{
    readonly long _size;

    public double Byte => _size;

    public double Kilobyte => Byte / 1024;

    public double Megabyte => Kilobyte / 1024;

    public double Gigabyte => Megabyte / 1024;

    public ByteSize(long size)
    {
        _size = size;
    }

    public static implicit operator long(ByteSize value) => Unsafe.As<ByteSize, long>(ref value);
    public static implicit operator ByteSize(long value) => Unsafe.As<long, ByteSize>(ref value);

    public static string ToString(long size)
    {
        return ((ByteSize)size).ToString();
    }

    public override string ToString()
    {
        if (Gigabyte >= 10)
            return $"{(int)Gigabyte}GB";

        if (Megabyte >= 10)
            return $"{(int)Megabyte}MB";

        if (Kilobyte >= 10)
            return $"{(int)Megabyte}KB";

        return $"{Byte}B";
    }
}
