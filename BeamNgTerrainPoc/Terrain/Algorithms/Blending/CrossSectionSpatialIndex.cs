using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Spatial index for fast cross-section lookups.
/// Uses a grid-based spatial hash for O(1) average lookup time.
/// </summary>
public class CrossSectionSpatialIndex
{
    /// <summary>
    /// Default cell size in meters for the spatial grid.
    /// </summary>
    public const int DefaultCellSize = 32;

    private readonly Dictionary<(int, int), List<UnifiedCrossSection>> _index;
    private readonly float _metersPerPixel;
    private readonly int _cellSize;

    /// <summary>
    /// Creates a new spatial index from a list of cross-sections.
    /// </summary>
    /// <param name="sections">Cross-sections to index</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="cellSize">Grid cell size in meters (default: 32)</param>
    public CrossSectionSpatialIndex(
        IEnumerable<UnifiedCrossSection> sections,
        float metersPerPixel,
        int cellSize = DefaultCellSize)
    {
        _metersPerPixel = metersPerPixel;
        _cellSize = cellSize;
        _index = new Dictionary<(int, int), List<UnifiedCrossSection>>();
        
        var skippedInvalid = 0;

        foreach (var cs in sections.Where(s => !s.IsExcluded))
        {
            // Skip cross-sections with invalid target elevations
            if (!IsValidTargetElevation(cs.TargetElevation))
            {
                skippedInvalid++;
                continue;
            }

            var key = GetGridKey(cs.CenterPoint);

            if (!_index.TryGetValue(key, out var list))
            {
                list = [];
                _index[key] = list;
            }

            list.Add(cs);
        }

        if (skippedInvalid > 0)
            TerrainLogger.Warning($"Skipped {skippedInvalid} cross-sections with invalid target elevations");
    }

    /// <summary>
    /// Gets the grid key for a world position.
    /// </summary>
    public (int, int) GetGridKey(Vector2 worldPos)
    {
        var gridX = (int)(worldPos.X / _metersPerPixel / _cellSize);
        var gridY = (int)(worldPos.Y / _metersPerPixel / _cellSize);
        return (gridX, gridY);
    }

    /// <summary>
    /// Finds the nearest cross-section to a world position.
    /// </summary>
    /// <param name="worldPos">World position in meters</param>
    /// <returns>Tuple of (nearest cross-section, distance in meters)</returns>
    public (UnifiedCrossSection? nearest, float distance) FindNearest(Vector2 worldPos)
    {
        var (gridX, gridY) = GetGridKey(worldPos);

        UnifiedCrossSection? nearest = null;
        var minDist = float.MaxValue;

        // Search 3x3 grid around the position
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var key = (gridX + dx, gridY + dy);
            if (_index.TryGetValue(key, out var sections))
            {
                foreach (var cs in sections)
                {
                    var dist = Vector2.Distance(worldPos, cs.CenterPoint);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = cs;
                    }
                }
            }
        }

        return (nearest, minDist);
    }

    /// <summary>
    /// Finds all cross-sections within a search radius of a world position.
    /// </summary>
    /// <param name="worldPos">World position in meters</param>
    /// <param name="searchRadius">Search radius in meters</param>
    /// <returns>Enumerable of cross-sections within the radius</returns>
    public IEnumerable<(UnifiedCrossSection cs, float distance)> FindWithinRadius(Vector2 worldPos, float searchRadius)
    {
        var (gridX, gridY) = GetGridKey(worldPos);
        
        // Calculate grid search range based on search radius
        var gridSearchRange = (int)MathF.Ceiling(searchRadius / _metersPerPixel / _cellSize) + 1;
        gridSearchRange = Math.Max(1, Math.Min(gridSearchRange, 3)); // Clamp to reasonable range

        for (var dy = -gridSearchRange; dy <= gridSearchRange; dy++)
        for (var dx = -gridSearchRange; dx <= gridSearchRange; dx++)
        {
            var key = (gridX + dx, gridY + dy);
            if (!_index.TryGetValue(key, out var sections))
                continue;

            foreach (var cs in sections)
            {
                var dist = Vector2.Distance(worldPos, cs.CenterPoint);
                if (dist <= searchRadius)
                {
                    yield return (cs, dist);
                }
            }
        }
    }

    /// <summary>
    /// Gets the underlying dictionary for direct access.
    /// </summary>
    public Dictionary<(int, int), List<UnifiedCrossSection>> GetIndex() => _index;

    /// <summary>
    /// Validates that a target elevation is valid.
    /// </summary>
    private static bool IsValidTargetElevation(float elevation)
    {
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;
        if (elevation < -1000.0f)
            return false;
        return true;
    }
}

/// <summary>
/// Spatial index for cross-sections grouped by spline ID.
/// Allows efficient lookup of nearby cross-sections for a specific spline.
/// </summary>
public class SplineGroupedSpatialIndex
{
    private readonly Dictionary<int, Dictionary<(int, int), List<UnifiedCrossSection>>> _indexBySpline;
    private readonly float _metersPerPixel;
    private readonly int _cellSize;

    /// <summary>
    /// Creates a new spline-grouped spatial index.
    /// </summary>
    public SplineGroupedSpatialIndex(
        IEnumerable<UnifiedCrossSection> sections,
        float metersPerPixel,
        int cellSize = CrossSectionSpatialIndex.DefaultCellSize)
    {
        _metersPerPixel = metersPerPixel;
        _cellSize = cellSize;
        _indexBySpline = new Dictionary<int, Dictionary<(int, int), List<UnifiedCrossSection>>>();

        foreach (var cs in sections.Where(s => !s.IsExcluded && IsValidTargetElevation(s.TargetElevation)))
        {
            if (!_indexBySpline.TryGetValue(cs.OwnerSplineId, out var splineIndex))
            {
                splineIndex = new Dictionary<(int, int), List<UnifiedCrossSection>>();
                _indexBySpline[cs.OwnerSplineId] = splineIndex;
            }

            var key = GetGridKey(cs.CenterPoint);

            if (!splineIndex.TryGetValue(key, out var list))
            {
                list = [];
                splineIndex[key] = list;
            }

            list.Add(cs);
        }
    }

    private (int, int) GetGridKey(Vector2 worldPos)
    {
        var gridX = (int)(worldPos.X / _metersPerPixel / _cellSize);
        var gridY = (int)(worldPos.Y / _metersPerPixel / _cellSize);
        return (gridX, gridY);
    }

    /// <summary>
    /// Gets the index for a specific spline.
    /// </summary>
    public Dictionary<(int, int), List<UnifiedCrossSection>>? GetSplineIndex(int splineId)
    {
        return _indexBySpline.GetValueOrDefault(splineId);
    }

    /// <summary>
    /// Finds the nearest cross-section for a specific spline within a search radius.
    /// </summary>
    public UnifiedCrossSection? FindNearestForSpline(
        Vector2 worldPos,
        int splineId,
        float searchRadius)
    {
        if (!_indexBySpline.TryGetValue(splineId, out var splineIndex))
            return null;

        var (gridX, gridY) = GetGridKey(worldPos);
        UnifiedCrossSection? nearest = null;
        var minDist = float.MaxValue;

        var gridSearchRange = (int)MathF.Ceiling(searchRadius / _metersPerPixel / _cellSize) + 1;
        gridSearchRange = Math.Max(1, Math.Min(gridSearchRange, 3));

        for (var dy = -gridSearchRange; dy <= gridSearchRange; dy++)
        for (var dx = -gridSearchRange; dx <= gridSearchRange; dx++)
        {
            var key = (gridX + dx, gridY + dy);
            if (!splineIndex.TryGetValue(key, out var sections))
                continue;

            foreach (var cs in sections)
            {
                var dist = Vector2.Distance(worldPos, cs.CenterPoint);
                if (dist < minDist && dist <= searchRadius)
                {
                    minDist = dist;
                    nearest = cs;
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// Gets the underlying dictionary for direct access.
    /// </summary>
    public Dictionary<int, Dictionary<(int, int), List<UnifiedCrossSection>>> GetIndex() => _indexBySpline;

    private static bool IsValidTargetElevation(float elevation)
    {
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;
        if (elevation < -1000.0f)
            return false;
        return true;
    }
}
