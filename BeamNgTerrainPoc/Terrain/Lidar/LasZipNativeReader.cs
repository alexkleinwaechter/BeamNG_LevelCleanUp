using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BeamNgTerrainPoc.Terrain.Lidar;

/// <summary>
///     Small streaming wrapper over LASzip's native reader. We read the native point structure
///     directly so LAS 1.4 extended classifications (point formats 6-10) are handled correctly.
/// </summary>
internal sealed unsafe class LasZipNativeReader : IDisposable
{
    private const string LasZipLibrary = "laszip64";

    private IntPtr _reader;
    private LasZipPointNative* _point;
    private bool _isOpen;

    public LasZipHeader Header { get; private set; }

    public ulong PointCount => Math.Max(Header.NumberOfPointRecords, Header.ExtendedNumberOfPointRecords);

    public byte PointFormat => (byte)(Header.PointDataFormat & 0x3f);

    public void Open(string path)
    {
        if (_reader != IntPtr.Zero)
            throw new InvalidOperationException("LASzip reader is already open.");

        if (laszip_create(ref _reader) != 0 || _reader == IntPtr.Zero)
            throw new InvalidOperationException("LASzip could not create a reader.");

        var compressed = false;
        if (laszip_open_reader(_reader, path, ref compressed) != 0)
        {
            Dispose();
            throw new InvalidDataException($"LASzip could not open '{path}'.");
        }

        _isOpen = true;

        var headerPointer = IntPtr.Zero;
        if (laszip_get_header_pointer(_reader, ref headerPointer) != 0 || headerPointer == IntPtr.Zero)
            throw new InvalidDataException($"LASzip could not read the header from '{path}'.");

        Header = Marshal.PtrToStructure<LasZipHeader>(headerPointer);

        var pointPointer = IntPtr.Zero;
        if (laszip_get_point_pointer(_reader, ref pointPointer) != 0 || pointPointer == IntPtr.Zero)
            throw new InvalidDataException($"LASzip could not access point records in '{path}'.");

        _point = (LasZipPointNative*)pointPointer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadPoint(out double x, out double y, out double z, out byte classification)
    {
        if (!_isOpen || _point == null)
            throw new InvalidOperationException("LASzip reader is not open.");

        if (laszip_read_point(_reader) != 0)
        {
            x = y = z = 0;
            classification = 0;
            return false;
        }

        x = _point->X * Header.ScaleFactorX + Header.OffsetX;
        y = _point->Y * Header.ScaleFactorY + Header.OffsetY;
        z = _point->Z * Header.ScaleFactorZ + Header.OffsetZ;

        // LAS 1.4 point formats 6-10 moved classification to the extended byte.
        classification = SelectClassification(
            PointFormat, _point->Classification, _point->ExtendedClassification);
        return true;
    }

    internal static byte SelectClassification(byte pointFormat, byte legacyClassification, byte extendedClassification) =>
        pointFormat >= 6 ? extendedClassification : legacyClassification;

    public void Dispose()
    {
        if (_reader == IntPtr.Zero)
            return;

        if (_isOpen)
            laszip_close_reader(_reader);
        laszip_destroy(_reader);

        _reader = IntPtr.Zero;
        _point = null;
        _isOpen = false;
    }

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_create(ref IntPtr pointer);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int laszip_open_reader(IntPtr pointer, string fileName, ref bool isCompressed);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_get_header_pointer(IntPtr pointer, ref IntPtr headerPointer);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_get_point_pointer(IntPtr pointer, ref IntPtr pointPointer);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_read_point(IntPtr pointer);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_close_reader(IntPtr pointer);

    [DllImport(LasZipLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int laszip_destroy(IntPtr pointer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LasZipHeader
    {
        public ushort FileSourceId;
        public ushort GlobalEncoding;
        public uint ProjectId1;
        public ushort ProjectId2;
        public ushort ProjectId3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] ProjectId4;
        public byte VersionMajor;
        public byte VersionMinor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] SystemIdentifier;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] GeneratingSoftware;
        public ushort CreationDayOfYear;
        public ushort CreationYear;
        public ushort HeaderSize;
        public uint OffsetToPointData;
        public uint NumberOfVariableLengthRecords;
        public byte PointDataFormat;
        public ushort PointDataRecordLength;
        public uint NumberOfPointRecords;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public uint[] NumberOfPointsByReturn;
        public double ScaleFactorX;
        public double ScaleFactorY;
        public double ScaleFactorZ;
        public double OffsetX;
        public double OffsetY;
        public double OffsetZ;
        public double MaxX;
        public double MinX;
        public double MaxY;
        public double MinY;
        public double MaxZ;
        public double MinZ;
        public ulong StartOfWaveformDataPacketRecord;
        public ulong StartOfFirstExtendedVariableLengthRecord;
        public uint NumberOfExtendedVariableLengthRecords;
        public ulong ExtendedNumberOfPointRecords;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)] public ulong[] ExtendedNumberOfPointsByReturn;
        public uint UserDataInHeaderSize;
        public IntPtr UserDataInHeader;
        public IntPtr Vlrs;
        public uint UserDataAfterHeaderSize;
        public IntPtr UserDataAfterHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LasZipPointNative
    {
        public int X;
        public int Y;
        public int Z;
        public ushort Intensity;
        private byte _returnBits;
        private byte _classificationBits;
        public sbyte ScanAngleRank;
        public byte UserData;
        public ushort PointSourceId;
        public short ExtendedScanAngle;
        private byte _extendedFlags;
        public byte ExtendedClassification;
        private byte _extendedReturnBits;
        private fixed byte _dummy[7];
        public double GpsTime;
        private fixed ushort _rgb[4];
        private fixed byte _wavePacket[29];
        public int NumExtraBytes;
        public IntPtr ExtraBytes;

        public byte Classification => (byte)(_classificationBits & 0x1f);
    }
}
