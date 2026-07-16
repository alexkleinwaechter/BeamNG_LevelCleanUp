using System.Text;
using OSGeo.OSR;

namespace BeamNgTerrainPoc.Terrain.Lidar;

/// <summary>
///     Reads projection VLRs from the uncompressed LAS/LAZ header area.
/// </summary>
internal static class LasProjectionReader
{
    private const ushort GeographicTypeGeoKey = 2048;
    private const ushort ProjectedCrsTypeGeoKey = 3072;

    public static int? TryReadEpsg(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "LASF")
                return null;

            stream.Position = 94;
            var headerSize = reader.ReadUInt16();
            var pointDataOffset = reader.ReadUInt32();
            var vlrCount = reader.ReadUInt32();
            stream.Position = headerSize;

            int? geoKeyEpsg = null;

            for (uint i = 0; i < vlrCount && stream.Position + 54 <= pointDataOffset; i++)
            {
                reader.ReadUInt16(); // reserved
                var userId = ReadFixedString(reader, 16);
                var recordId = reader.ReadUInt16();
                var length = reader.ReadUInt16();
                reader.ReadBytes(32); // description

                if (stream.Position + length > stream.Length)
                    break;

                var data = reader.ReadBytes(length);

                if (userId.Equals("LASF_Projection", StringComparison.OrdinalIgnoreCase) && recordId == 2112)
                {
                    var wkt = Encoding.UTF8.GetString(data).TrimEnd('\0', ' ', '\r', '\n');
                    var epsg = TryGetEpsgFromWkt(wkt);
                    if (epsg.HasValue)
                        return epsg;
                }

                if (userId.Equals("LASF_Projection", StringComparison.OrdinalIgnoreCase) && recordId == 34735)
                    geoKeyEpsg = TryGetEpsgFromGeoKeys(data) ?? geoKeyEpsg;
            }

            return geoKeyEpsg;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadFixedString(BinaryReader reader, int length) =>
        Encoding.ASCII.GetString(reader.ReadBytes(length)).TrimEnd('\0', ' ');

    private static int? TryGetEpsgFromWkt(string wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
            return null;

        try
        {
            var srs = new SpatialReference(null);
            var copy = wkt;
            if (srs.ImportFromWkt(ref copy) != 0)
                return null;

            srs.AutoIdentifyEPSG();
            var code = srs.GetAuthorityCode(null) ??
                       srs.GetAuthorityCode("PROJCS") ??
                       srs.GetAuthorityCode("GEOGCS");
            return int.TryParse(code, out var epsg) ? epsg : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetEpsgFromGeoKeys(byte[] data)
    {
        if (data.Length < 8 || data.Length % 2 != 0)
            return null;

        var values = new ushort[data.Length / 2];
        Buffer.BlockCopy(data, 0, values, 0, data.Length);
        var keyCount = values[3];

        int? geographic = null;
        for (var i = 0; i < keyCount; i++)
        {
            var offset = 4 + i * 4;
            if (offset + 3 >= values.Length)
                break;

            var keyId = values[offset];
            var tagLocation = values[offset + 1];
            var count = values[offset + 2];
            var value = values[offset + 3];

            if (tagLocation != 0 || count != 1 || value is 0 or 32767)
                continue;

            if (keyId == ProjectedCrsTypeGeoKey)
                return value;
            if (keyId == GeographicTypeGeoKey)
                geographic = value;
        }

        return geographic;
    }
}
