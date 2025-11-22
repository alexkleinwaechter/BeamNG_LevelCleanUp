using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG;

public struct TerrainData
{
    public TerrainData(float height)
    {
        Height = height;
    }

    public float Height;
    public int Material;
    public bool IsHole;

    public override string ToString()
    {
        return $"Height: {Height} Material: {Material} IsHole: {IsHole}";
    }
}

public class TerrainDataBuffer : IReadOnlyCollection<TerrainData>
{
    readonly TerrainData[] _data;

    public int Width { get; }

    public int Height { get; }

    public int Length { get; }

    readonly Vector2 _fsize;

    public TerrainData[] RawData => _data;

    public TerrainDataBuffer(int width, int height, TerrainData[] data)
    {
        _fsize = new Vector2(width, height);
        Width = width;
        Height = height;
        Length = width * height;

        if (data.Length != Length)
        {
            throw new ArgumentException($"data.Length must be Size^2 == {Length}", nameof(data));
        }

        _data = data;
    }

    public TerrainDataBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Length = width * height;
        _data = new TerrainData[Length];
    }

    public void Clear()
    {
        _data.AsSpan(0, Length).Clear();
    }

    public int IndexFromNormalized(float x, float y)
    {
        return (int)(y * _fsize.Y + 0.5f) * Width + (int)(x * _fsize.X + 0.5f);
    }

    public void CopyTo(TerrainDataBuffer data)
    {
        _data.CopyTo(data._data, 0);
    }

    public ref TerrainData this[int index] => ref _data[index];

    public ref TerrainData this[int x, int y] => ref _data[y * Width + x];

    int IReadOnlyCollection<TerrainData>.Count => _data.Length;

    public IEnumerator<TerrainData> GetEnumerator() => ((IEnumerable<TerrainData>)_data).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _data.GetEnumerator();
}


