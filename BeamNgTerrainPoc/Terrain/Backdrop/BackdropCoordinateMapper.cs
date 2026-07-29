namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Source-pixel ↔ world mapping derived from the terrain crop rect (spec §7.4).
///     World origin = terrain center, X=East, Y=North (matches BeamNgCoordinateTransformer);
///     source pixel y grows southward (raster top = north).
///     This is the ONLY sanctioned horizontal datum for backdrop geometry (spec §14.2).
/// </summary>
public sealed class BackdropCoordinateMapper
{
    private readonly PixelRect _terrainRect;

    public double HalfSizeMeters { get; }
    public double MetersPerSourcePixelX { get; }
    public double MetersPerSourcePixelY { get; }

    public BackdropCoordinateMapper(PixelRect terrainRect, int terrainSizePixels, float terrainMetersPerPixel)
    {
        if (terrainRect.IsEmpty) throw new ArgumentException("Terrain rect must be non-empty.", nameof(terrainRect));
        if (terrainSizePixels <= 0) throw new ArgumentOutOfRangeException(nameof(terrainSizePixels));
        if (terrainMetersPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(terrainMetersPerPixel));

        _terrainRect = terrainRect;
        HalfSizeMeters = terrainSizePixels * (double)terrainMetersPerPixel / 2.0;
        MetersPerSourcePixelX = terrainSizePixels * (double)terrainMetersPerPixel / terrainRect.Width;
        MetersPerSourcePixelY = terrainSizePixels * (double)terrainMetersPerPixel / terrainRect.Height;
    }

    public (double WorldX, double WorldY) SourcePixelToWorld(double srcX, double srcY) =>
        ((srcX - _terrainRect.X) * MetersPerSourcePixelX - HalfSizeMeters,
         HalfSizeMeters - (srcY - _terrainRect.Y) * MetersPerSourcePixelY);

    public (double SrcX, double SrcY) WorldToSourcePixel(double worldX, double worldY) =>
        (_terrainRect.X + (worldX + HalfSizeMeters) / MetersPerSourcePixelX,
         _terrainRect.Y + (HalfSizeMeters - worldY) / MetersPerSourcePixelY);
}
