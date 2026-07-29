namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Height source for the backdrop mesh implementing the seam rules of spec §7:
///     exact terrain-edge snap at distance 0, delta-field blend across the edge band,
///     unclamped DEM with the −cropMin+baseHeight datum beyond the band.
/// </summary>
public sealed class BackdropHeightField
{
    private readonly BackdropRaster _farRaster;
    // Concrete array, not IReadOnlyList: SampleDemElevation runs per error-metric probe and per
    // vertex (millions of calls per generation), and a foreach over the interface would box a new
    // enumerator on every call (perf plan §4).
    private readonly BackdropRaster[] _bandRasters;
    private readonly float[,] _terrainHeightMap;
    private readonly BackdropCoordinateMapper _mapper;
    private readonly int _terrainSizePixels;
    private readonly double _u;              // terrain meters per pixel
    private readonly double _half;
    private readonly float _terrainBaseHeight;
    private readonly double _cropMinElevation;
    private readonly double _edgeBandMeters;

    public BackdropHeightField(
        BackdropRaster farRaster,
        IReadOnlyList<BackdropRaster> bandRasters,
        float[,] terrainHeightMap,
        BackdropCoordinateMapper mapper,
        int terrainSizePixels, float terrainMetersPerPixel,
        float terrainBaseHeight, double terrainCropMinElevation,
        double edgeBandMeters)
    {
        _farRaster = farRaster;
        _bandRasters = [.. bandRasters];
        _terrainHeightMap = terrainHeightMap;
        _mapper = mapper;
        _terrainSizePixels = terrainSizePixels;
        _u = terrainMetersPerPixel;
        _half = terrainSizePixels * (double)terrainMetersPerPixel / 2.0;
        _terrainBaseHeight = terrainBaseHeight;
        _cropMinElevation = terrainCropMinElevation;
        _edgeBandMeters = edgeBandMeters;
    }

    public double SignedDistanceToTerrainRect(double worldX, double worldY)
    {
        var dx = Math.Abs(worldX) - _half;
        var dy = Math.Abs(worldY) - _half;
        if (dx <= 0 && dy <= 0)
            return Math.Max(dx, dy);                       // inside/on boundary: ≤ 0
        var ox = Math.Max(dx, 0);
        var oy = Math.Max(dy, 0);
        return Math.Sqrt(ox * ox + oy * oy);               // Euclidean outside (correct at corners)
    }

    public double SampleDemElevation(double worldX, double worldY)
    {
        var (srcX, srcY) = _mapper.WorldToSourcePixel(worldX, worldY);
        for (var i = 0; i < _bandRasters.Length; i++)
        {
            var strip = _bandRasters[i];
            if (strip.ContainsSourcePoint(srcX, srcY))
                return strip.SampleBilinearAtSource(srcX, srcY);
        }
        return _farRaster.SampleBilinearAtSource(srcX, srcY);
    }

    public double SampleWorldZ(double worldX, double worldY)
    {
        var d = SignedDistanceToTerrainRect(worldX, worldY);
        if (d <= 0)
            return TerrainEdgeWorldZ(worldX, worldY);       // §7.1 exact snap

        var demZ = SampleDemElevation(worldX, worldY) - _cropMinElevation + _terrainBaseHeight;
        if (_edgeBandMeters <= 0 || d >= _edgeBandMeters)
            return demZ;                                    // §7.3 pure DEM, unclamped

        // §7.2: fade the (terrainEdge − demAtSeam) delta across the band.
        var qx = Math.Clamp(worldX, -_half, _half);
        var qy = Math.Clamp(worldY, -_half, _half);
        var demZAtSeam = SampleDemElevation(qx, qy) - _cropMinElevation + _terrainBaseHeight;
        var delta = TerrainEdgeWorldZ(qx, qy) - demZAtSeam;

        var t = d / _edgeBandMeters;
        var w = 1.0 - (t * t * (3.0 - 2.0 * t));            // 1 − smoothstep(t)
        return demZ + delta * w;
    }

    /// <summary>
    ///     Terrain height at the boundary point nearest to (worldX, worldY), bilinear along the
    ///     final terrain output heightmap. The outermost sample row/column (index size−1) covers
    ///     the seam line at ±half — see the "last half-cell" watch item in the plan header.
    /// </summary>
    internal double TerrainEdgeWorldZ(double worldX, double worldY)
    {
        var qx = Math.Clamp(worldX, -_half, _half);
        var qy = Math.Clamp(worldY, -_half, _half);

        var px = Math.Clamp((qx + _half) / _u, 0, _terrainSizePixels - 1);
        var py = Math.Clamp((qy + _half) / _u, 0, _terrainSizePixels - 1);

        var x0 = (int)Math.Floor(px);
        var y0 = (int)Math.Floor(py);
        var x1 = Math.Min(x0 + 1, _terrainSizePixels - 1);
        var y1 = Math.Min(y0 + 1, _terrainSizePixels - 1);
        var fx = px - x0;
        var fy = py - y0;

        double v00 = _terrainHeightMap[y0, x0];
        double v10 = _terrainHeightMap[y0, x1];
        double v01 = _terrainHeightMap[y1, x0];
        double v11 = _terrainHeightMap[y1, x1];

        var south = v00 + (v10 - v00) * fx;
        var north = v01 + (v11 - v01) * fx;
        return south + (north - south) * fy + _terrainBaseHeight;
    }
}
