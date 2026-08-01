namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Result of <see cref="BackdropSceneWriter.ExportChunkDae"/>: everything a chunk's scene-item
///     and materials-json entries need (spec §9) — chunk id, output file names, and vertex/triangle
///     counts for logging/diagnostics.
/// </summary>
public sealed class BackdropChunkExportItem
{
    public required int Cx { get; init; }
    public required int Cy { get; init; }
    public required string DaeFileName { get; init; }
    public required string MaterialName { get; init; }
    public required string TextureFileName { get; init; }
    public int Vertices { get; init; }
    public int Triangles { get; init; }
    /// <summary>Whether the backdrop is drivable — drives the TSStatic entry's explicit
    /// <c>collisionType</c>/<c>decalType</c> ("Visible Mesh Final" vs "None"). The DAE itself never
    /// carries collision geometry; the game builds physics from the visual mesh.</summary>
    public bool HasCollision { get; init; } = true;
}
