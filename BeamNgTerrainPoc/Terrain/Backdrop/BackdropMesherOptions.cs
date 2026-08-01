namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Tunables for <see cref="BackdropQuadtreeMesher"/> (spec §8): tolerance lerp bounds, edge band,
///     probe grid resolution and the lattice geometry needed to map lattice coords back to world meters.
/// </summary>
public sealed class BackdropMesherOptions
{
    public double MaxVerticalErrorNearMeters { get; init; } = 0.5;
    public double MaxVerticalErrorFarMeters { get; init; } = 8.0;
    public double EdgeBandMeters { get; init; } = 200;
    public required double MaxMarginMeters { get; init; }
    public int ErrorProbeGridSize { get; init; } = 4;        // (n+1)² samples per cell
    public bool SeamSkirt { get; init; } = true;
    public double SeamSkirtDepthMeters { get; init; } = 2.0;
    public required double LatticeUnitMeters { get; init; }  // u
    public required double HalfSizeMeters { get; init; }     // lattice origin offset
}

/// <summary>Contributor to quadtree refinement decisions beyond the vertical-error tolerance (spec §8).</summary>
public interface IBackdropImportanceSource
{
    /// <summary>Max allowed cell size (meters) for a cell intersecting this source, or null = no constraint.</summary>
    double? RequiredMaxCellSizeMeters(double worldMinX, double worldMinY, double worldMaxX, double worldMaxY);
}

/// <summary>V1 contributor: forces subdivision to the lattice unit inside the edge band (spec §8).</summary>
public sealed class EdgeBandImportanceSource(double halfSizeMeters, double edgeBandMeters, double latticeUnitMeters)
    : IBackdropImportanceSource
{
    public double? RequiredMaxCellSizeMeters(double minX, double minY, double maxX, double maxY)
    {
        // Distance of the cell rect to the terrain square; inside band → full resolution.
        var dx = Math.Max(Math.Max(-halfSizeMeters - maxX, minX - halfSizeMeters), 0);
        var dy = Math.Max(Math.Max(-halfSizeMeters - maxY, minY - halfSizeMeters), 0);
        var d = Math.Sqrt(dx * dx + dy * dy);
        return d < edgeBandMeters ? latticeUnitMeters : null;
    }
}
