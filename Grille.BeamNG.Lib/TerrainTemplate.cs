using Grille.BeamNG.IO.Binary;
using Grille.BeamNG.SceneTree.Main;

/* Unmerged change from project 'Grille.BeamNG.Lib (net8)'
Before:
using System.Linq;
After:
using Grille.BeamNG.Terrain;
using System.Linq;
*/
using Grille.BeamNG;
using System.Linq;

namespace Grille.BeamNG;

/// <summary>Abstract lightweight representation of terrain properties, useful to generate blank terrain files.</summary>
public class TerrainTemplate
{
    private int resolution;

    private float worldSize;
    private float squareSize;

    /// <remarks>Updates <see cref="WorldSize"/></remarks>
    public int Resolution
    {
        get => resolution;
        set
        {
            resolution = value;
            worldSize = squareSize * resolution;
        }
    }

    /// <remarks>Updates <see cref="WorldSize"/></remarks>
    public float SquareSize
    {
        get => squareSize;
        set 
        {
            squareSize = value;
            worldSize = squareSize * resolution;
        }
    }

    /// <remarks>Updates <see cref="SquareSize"/></remarks>
    public float WorldSize
    {
        get => worldSize;
        set
        {
            worldSize = value;
            squareSize = worldSize / resolution;
        }
    }

    /// <summary>
    /// <see cref="Resolution"/>^2
    /// </summary>
    public int Length => Resolution * Resolution;

    /// <summary>
    /// Value used for normalizing absolute <see langword="float"/> heights to a ranged <see langword="ushort"/> value.
    /// </summary>
    public float MaxHeight { get; set; }

    public float Height { get; set; }

    public IReadOnlyCollection<string> MaterialNames { get; set; }

    public int MaterialIndex { get; set; }

    public ushort U16Height
    {
        get => TerrainV9Serializer.GetU16Height(Height, MaxHeight);
        set => Height = TerrainV9Serializer.GetSingleHeight(value, MaxHeight);
    }

    public TerrainTemplate()
    {
        resolution = 1024;
        worldSize = 1024;
        squareSize = 1;
        MaxHeight = 512;
        Height = 10;
        MaterialNames = Array.Empty<string>();
    }

    public TerrainData[] ToTerrainData()
    {
        var data = new TerrainData[Length];

        for (int i = 0; i < data.Length; i++)
        {
            data[i].Material = MaterialIndex;
            data[i].Height = Height;
        }

        return data;
    }

    public Terrain ToTerrain()
    {
        return new Terrain(Resolution, MaterialNames.ToArray(), ToTerrainData());
    }

    public TerrainBlock ToJsonTerrainBlock()
    {
        var block = new TerrainBlock(this);
        return block;
    }

    public void Save(string filePath)
    {
        using var stream = File.Create(filePath);
        Serialize(stream);
    }

    public void Serialize(Stream stream)
    {
        TerrainV9Serializer.Serialize(stream, this);
    }

    public ByteSize CalcApproxSize()
    {
        return TerrainV9Serializer.CalcApproxSize(Resolution, MaterialNames);
    }
}
