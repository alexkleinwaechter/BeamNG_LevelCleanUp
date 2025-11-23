
/* Unmerged change from project 'Grille.BeamNG.Lib (net8)'
Before:
using System;
After:
using Grille.BeamNG.Terrain;
using System;
*/
using Grille.BeamNG;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Binary;
public static class TerrainSerializer
{
    public static TerrainVersion PeekVersion(Stream stream)
    {
        var pos = stream.Position;
        var version = stream.ReadByte();
        stream.Position = pos;
        return (TerrainVersion)version;
    }

    public static void Serialize(Stream stream, Terrain terrain, float maxHeight, TerrainVersion version = TerrainVersion.Latest)
    {
        switch (version)
        {
            case TerrainVersion.V9:
            {
                TerrainV9Serializer.Serialize(stream, terrain, maxHeight);
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, $"Version {(int)version} not supported");
            }
        }
    }

    public static void Deserialize(Stream stream, Terrain terrain, float maxHeight)
    {
        var version = PeekVersion(stream);

        switch (version)
        {
            case TerrainVersion.V9:
            {
                TerrainV9Serializer.Deserialize(stream, terrain, maxHeight);
                break;
            }
            default:
            {
                throw new InvalidDataException($"Version {(int)version} not supported");
            }
        }
    }

    public static Terrain Deserialize(Stream stream, float maxHeight)
    {
        var terrain = new Terrain();
        Deserialize(stream, terrain, maxHeight);
        return terrain;
    }
}
