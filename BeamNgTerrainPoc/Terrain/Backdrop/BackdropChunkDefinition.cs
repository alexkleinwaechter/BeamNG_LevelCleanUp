namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     A single backdrop ring chunk: lattice-aligned rect, derived world/source rects,
///     WGS84 bounds (best-effort), and output naming/texture sizing (spec §10).
/// </summary>
public sealed class BackdropChunkDefinition
{
    public required int Cx { get; init; }                 // column index, 0 = west-most
    public required int Cy { get; init; }                 // row index, 0 = south-most
    // Lattice rect (units of u, origin at terrain SW corner; iy grows north):
    public required int LatticeX { get; init; }
    public required int LatticeY { get; init; }
    public required int LatticeWidth { get; init; }
    public required int LatticeHeight { get; init; }
    // Derived world rect in meters:
    public double WorldMinX { get; init; }
    public double WorldMinY { get; init; }
    public double WorldMaxX { get; init; }
    public double WorldMaxY { get; init; }
    // Source-pixel rect (double precision; for the texture warp + MtSettings):
    public required double SourceRectX { get; init; }
    public required double SourceRectY { get; init; }
    public required double SourceRectWidth { get; init; }
    public required double SourceRectHeight { get; init; }
    public GeoTiff.GeoBoundingBox? Wgs84Bounds { get; init; }   // null when neither WKT nor mosaic bbox usable
    public required string DaeFileName { get; init; }           // $"backdrop_{Cx}_{Cy}.dae"
    public required string TextureFileName { get; init; }       // $"backdrop_{Cx}_{Cy}.color.png"
    public required string MaterialName { get; init; }          // $"mt_backdrop_{Cx}_{Cy}"
    public required int TextureSize { get; init; }              // pow2, clamped [256, MaxChunkTextureSize]
    public required double DistanceToTerrainMeters { get; init; } // chunk-center distance to terrain rect
}

/// <summary>Full ring chunk plan: the chunk list plus the whole-ring lattice bounds used by the mesher.</summary>
public sealed class BackdropChunkPlan
{
    public required IReadOnlyList<BackdropChunkDefinition> Chunks { get; init; }
    public required double MaxMarginMeters { get; init; }          // for tolerance/texel lerps
    // Backdrop rect snapped inward to the lattice (whole-ring bounds used by the mesher):
    public required int LatticeMinX { get; init; }
    public required int LatticeMinY { get; init; }
    public required int LatticeMaxX { get; init; }
    public required int LatticeMaxY { get; init; }
}
