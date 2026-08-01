namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Integer rectangle in combined-GeoTIFF source pixel space (x → east/right, y = 0 at the TOP/north row).
///     Same space as <c>CropResult.OffsetX/OffsetY</c> in the app layer.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;    // exclusive
    public int Bottom => Y + Height;  // exclusive
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool ContainsRect(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}
