using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Pre-built spatial index for fast higher-priority protection zone queries.
/// 
/// Instead of iterating over ALL splines for every blend-zone pixel to find
/// higher-priority roads (O(pixels × splines)), this index pre-computes which
/// splines have protection zones covering each grid cell.
/// 
/// For each pixel, we only need to check splines in the nearby grid cells,
/// reducing the per-pixel cost from O(all_splines) to O(nearby_splines).
/// 
/// The index is built once before the parallel blending loop and is read-only
/// during blending, making it safe for concurrent access.
/// </summary>
public class PriorityProtectionIndex
{
    /// <summary>
    /// Information about a spline's protection zone, cached for fast lookup.
    /// </summary>
    public readonly struct SplineProtectionInfo
    {
        public readonly int SplineId;
        public readonly int Priority;
        public readonly float ProtectionRadius;
        public readonly float HalfWidth;

        public SplineProtectionInfo(int splineId, int priority, float protectionRadius, float halfWidth)
        {
            SplineId = splineId;
            Priority = priority;
            ProtectionRadius = protectionRadius;
            HalfWidth = halfWidth;
        }
    }

    private readonly Dictionary<(int, int), SplineProtectionInfo[]> _index;
    private readonly float _metersPerPixel;
    private readonly int _cellSize;

    /// <summary>
    /// Gets the grid cell size in pixels used by this index.
    /// </summary>
    public int CellSize => _cellSize;

    /// <summary>
    /// Creates a priority protection index from the road network.
    /// 
    /// For each spline, we determine which grid cells its protection zone covers
    /// by iterating over its cross-sections and expanding by the protection radius.
    /// </summary>
    /// <param name="network">The road network</param>
    /// <param name="splineParams">Pre-computed spline parameters keyed by spline ID</param>
    /// <param name="crossSectionsBySpline">Spatial index for cross-section lookups</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="cellSize">Grid cell size in pixels (default: 32, matching CrossSectionSpatialIndex)</param>
    public PriorityProtectionIndex(
        UnifiedRoadNetwork network,
        Dictionary<int, (float HalfWidth, float ProtectionBuffer, int Priority)> splineParams,
        float metersPerPixel,
        int cellSize = CrossSectionSpatialIndex.DefaultCellSize)
    {
        _metersPerPixel = metersPerPixel;
        _cellSize = cellSize;

        // Temporary mutable dictionary for building the index
        var tempIndex = new Dictionary<(int, int), List<SplineProtectionInfo>>();

        foreach (var spline in network.Splines)
        {
            if (!splineParams.TryGetValue(spline.SplineId, out var sp))
                continue;

            var protectionRadius = sp.HalfWidth + sp.ProtectionBuffer;
            var info = new SplineProtectionInfo(spline.SplineId, sp.Priority, protectionRadius, sp.HalfWidth);

            // Find all grid cells covered by this spline's protection zone
            // by iterating over its cross-sections
            var crossSections = network.CrossSections
                .Where(cs => cs.OwnerSplineId == spline.SplineId && !cs.IsExcluded);

            var coveredCells = new HashSet<(int, int)>();

            foreach (var cs in crossSections)
            {
                if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation < -1000f)
                    continue;

                // Expand from the cross-section center by the protection radius
                // to find all grid cells that could be within the protection zone
                var centerPixelX = cs.CenterPoint.X / metersPerPixel;
                var centerPixelY = cs.CenterPoint.Y / metersPerPixel;
                var radiusPixels = protectionRadius / metersPerPixel;

                var minGx = (int)((centerPixelX - radiusPixels) / cellSize);
                var maxGx = (int)((centerPixelX + radiusPixels) / cellSize);
                var minGy = (int)((centerPixelY - radiusPixels) / cellSize);
                var maxGy = (int)((centerPixelY + radiusPixels) / cellSize);

                for (var gy = minGy; gy <= maxGy; gy++)
                for (var gx = minGx; gx <= maxGx; gx++)
                {
                    coveredCells.Add((gx, gy));
                }
            }

            // Add this spline's info to all covered cells
            foreach (var cell in coveredCells)
            {
                if (!tempIndex.TryGetValue(cell, out var list))
                {
                    list = [];
                    tempIndex[cell] = list;
                }

                // Avoid duplicate entries for the same spline in the same cell
                if (!list.Exists(e => e.SplineId == info.SplineId))
                {
                    list.Add(info);
                }
            }
        }

        // Convert lists to arrays for cache-friendly iteration during blending
        _index = new Dictionary<(int, int), SplineProtectionInfo[]>(tempIndex.Count);
        foreach (var (key, list) in tempIndex)
        {
            _index[key] = list.ToArray();
        }

        // Log statistics
        var totalEntries = _index.Values.Sum(a => a.Length);
        var cellCount = _index.Count;
        TerrainCreationLogger.Current?.Detail(
            $"PriorityProtectionIndex: {cellCount} cells, {totalEntries} total spline entries, " +
            $"{network.Splines.Count} splines indexed");
    }

    /// <summary>
    /// Gets the grid key for a pixel position.
    /// </summary>
    private (int, int) GetGridKey(int pixelX, int pixelY)
    {
        return (pixelX / _cellSize, pixelY / _cellSize);
    }

    /// <summary>
    /// Gets candidate splines that might have protection zones covering the given pixel.
    /// Returns an empty span if no splines are nearby.
    /// 
    /// The caller should further filter by:
    /// 1. Excluding the current owner spline
    /// 2. Checking only splines with higher priority than current
    /// 3. Computing actual distance to verify within protection radius
    /// </summary>
    /// <param name="pixelX">Pixel X coordinate</param>
    /// <param name="pixelY">Pixel Y coordinate</param>
    /// <returns>Array of candidate spline protection infos (may be empty)</returns>
    public SplineProtectionInfo[] GetCandidates(int pixelX, int pixelY)
    {
        var key = GetGridKey(pixelX, pixelY);
        return _index.TryGetValue(key, out var candidates) ? candidates : [];
    }
}
