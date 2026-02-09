
/* Unmerged change from project 'Grille.BeamNG.Lib (net8)'
Before:
using Grille.BeamNG.IO.Binary;
using System.Collections;
After:
using Grille;
using Grille.BeamNG;
using Grille.BeamNG.IO.Binary;
using Grille.BeamNG.Terrain;
using System.Collections;
*/

/* Unmerged change from project 'Grille.BeamNG.Lib (net8)'
Before:
using Grille.BeamNG.IO.Binary;
After:
using Grille;
using Grille.BeamNG.IO.Binary;
*/
using Grille.BeamNG.IO.Binary;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;

using Grille.BeamNG.Imaging;

namespace Grille.BeamNG;

/// <summary>
/// Abstract Representation of an full BeamNG terrain, allows full access to all terrain properties.
/// </summary>
public class Terrain
{
    public TerrainDataBuffer Data { get; set; }

    public string[] MaterialNames { get; set; }

    public int Width => Data.Width;

    public int Height => Data.Height;

    /// <summary>
    /// Are <see cref="Width"/> and <see cref="Height"/> equal?
    /// </summary>
    public bool IsSquare => Width == Height;

    /// <summary>
    /// Gets value of <see cref="Width"/> and <see cref="Height"/>, throws <see cref="InvalidOperationException"/> if <see cref="IsSquare"/> is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Terrain in BeamNG can only be square at the moment but should that change in the future, code using <see cref="Size"/> will no longer work.
    /// </remarks>
    public int Size
    {
        get
        {
            if (!IsSquare)
                throw new InvalidOperationException("Terrain must be square.");

            return Width;
        }
    }

    public Terrain(string path, float maxHeight = 1)
    {
        Load(path, maxHeight);
    }

    public Terrain(Stream stream, float maxHeight = 1)
    {
        Deserialize(stream, maxHeight);
    }

    public Terrain() : this(0) { }

    public Terrain(int size) : this(size, size) { }

    public Terrain(int size, IList<string> materialNames) : this(size, size, materialNames) { }

    public Terrain(int size, IList<string> materialNames, TerrainData[] data) : this(size, size, materialNames, data) { }

    public Terrain(int width, int height) : this(width, height, Array.Empty<string>()) { }

    public Terrain(int width, int height, IList<string> materialNames) : this(width, height, materialNames, new TerrainData[width * height]) { }

    public Terrain(int width, int height, IList<string> materialNames, TerrainData[] data)
    {
        Data = new TerrainDataBuffer(width, height, data);
        MaterialNames = materialNames.ToArray();
    }

    [MemberNotNull(nameof(Data), nameof(MaterialNames))]
    public void Load(string path, float maxHeight = 1)
    {
        using var stream = File.OpenRead(path);
        Deserialize(stream, maxHeight);
    }

    public void Save(string path, float maxHeight = 1)
    {
        using var stream = File.Create(path);
        Serialize(stream, maxHeight);
    }

    public void Serialize(Stream stream, float maxHeight = 1)
    {
        TerrainSerializer.Serialize(stream, this, maxHeight);
    }

    [MemberNotNull(nameof(Data), nameof(MaterialNames))]
    public void Deserialize(Stream stream, float maxHeight = 1)
    {
        TerrainSerializer.Deserialize(stream, this, maxHeight);
        if (Data == null || MaterialNames == null)
        {
            throw new NullReferenceException();
        }
    }

    public Terrain Clone()
    {
        var terrain = new Terrain(Size, MaterialNames.ToArray());
        Data.CopyTo(terrain.Data);
        return terrain;
    }

    public void ResizeDataBuffer(int size)
    {
        ResizeDataBuffer(size, size);
    }

    public void ResizeDataBuffer(int width, int height)
    {
        if (width == Width && height == Height)
            return;
        
        Data = new TerrainDataBuffer(width, height);
    }

    public void Clear()
    {
        Data.Clear();
        MaterialNames = Array.Empty<string>();
    }

    public void Draw(Terrain terrain, Rectangle dstRect)
    {
        var srcRect = new Rectangle(0,0, terrain.Width, terrain.Height);
        Draw(terrain, dstRect, srcRect);
    }

    public void Draw(Terrain terrain, Rectangle dstRect, Rectangle srcRect, float heightScale = 1)
    {
        Draw(this, terrain, dstRect, srcRect, heightScale);
    }

    public unsafe static void Draw(Terrain dst, Terrain src, Rectangle dstRect, Rectangle srcRect, float heightScale = 1)
    {
        var result = CombineNamesAndMapIndices(dst.MaterialNames, src.MaterialNames);

        dst.MaterialNames = result.Names;
        var materialIndices = result.Indices;

        void Operator(DrawOperatorArguments<TerrainData> args)
        {
            args.DstPointer->Material = materialIndices[args.SrcPointer->Material];
            args.DstPointer->Height = args.SrcPointer->Height * heightScale;
        }

        var args = new DrawArguments<TerrainData>();

        args.Operator = Operator;

        args.DstRect = dstRect;
        args.SrcRect = srcRect;

        args.DstSize = new Size(dst.Width, dst.Height);
        args.SrcSize = new Size(src.Width, src.Height);

        fixed (TerrainData* dstPtr = dst.Data.RawData, srcPtr = src.Data.RawData)
        {
            args.DstBuffer = dstPtr;
            args.SrcBuffer = srcPtr;

            ImagingUtils.Draw(args);
        }
    }


    record NameMapingResult(string[] Names, int[] Indices);
    static NameMapingResult CombineNamesAndMapIndices(string[] baseNames, string[] newNames)
    {
        var combinedList = new List<string>(baseNames);
        var indexDict = new Dictionary<string, int>();
        var indexMap = new int[newNames.Length];

        for (int i = 0; i < baseNames.Length; i++)
        {
            indexDict[baseNames[i]] = i;
        }

        for (int i = 0; i < newNames.Length; i++)
        {
            var name = newNames[i];

            if (indexDict.ContainsKey(name))
            {
                indexMap[i] = indexDict[name];
            }
            else
            {
                combinedList.Add(name);
                indexMap[i] = combinedList.Count - 1;
            }
        }

        return new NameMapingResult(combinedList.ToArray(), indexMap);
    }
}