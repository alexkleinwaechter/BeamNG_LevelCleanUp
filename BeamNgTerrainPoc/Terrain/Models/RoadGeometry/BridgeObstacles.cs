using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>What a bridge crosses (spec §2). Terrain = no crossing feature (valley bridge, R1).</summary>
public enum BridgeObstacleKind
{
    Road,
    Rail,
    Water,
    Terrain,

    /// <summary>
    ///     Doc 19: <c>building</c>/<c>building:part</c> polygons. NOT clearance-relevant (a deck may pass
    ///     over a roof), but pier-placement-relevant — a pier footprint must never stand inside one. Only
    ///     added to the obstacle set when <see cref="BridgeRuleSystemOptions.EnableBridgePiers"/> is on;
    ///     every clearance consumer filters by kind, so Building features are inert to the elevation rules.
    /// </summary>
    Building,
}

/// <summary>
///     One OSM feature a bridge could cross (rail line, waterway, water polygon, road), pre-projected to
///     terrain-local METER coordinates so the planner never touches WGS84 (V2 plan 0.4 / review P0-3).
///     Built once per generation by <c>BridgeObstacleClassifier.BuildObstacleSet</c>.
/// </summary>
public sealed class BridgeObstacleFeature
{
    public required long OsmId { get; init; }

    public required BridgeObstacleKind Kind { get; init; }

    /// <summary>Rail only: catenary clearance applies unless OSM says `electrified=no` (spec §3.1).</summary>
    public bool Electrified { get; init; }

    /// <summary>Water only: `boat=yes` / `CEMT=*` / canal / width ≥ threshold (spec §3.1).</summary>
    public bool Navigable { get; init; }

    /// <summary>Terrain-local meter polyline (LineString) or outer ring (Polygon).</summary>
    public required IReadOnlyList<Vector2> Points { get; init; }

    /// <summary>True when <see cref="Points"/> is a closed area ring (e.g. natural=water lake).</summary>
    public bool IsPolygon { get; init; }

    /// <summary>AABB over <see cref="Points"/> for the spatial bucket.</summary>
    public Vector2 Min { get; init; }

    public Vector2 Max { get; init; }

    /// <summary>Point-in-polygon (ray cast). Always false for polylines.</summary>
    public bool ContainsPoint(Vector2 p)
    {
        if (!IsPolygon || Points.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if (a.Y > p.Y != b.Y > p.Y &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }
}

/// <summary>
///     All potential bridge obstacles of one terrain, in a light uniform-grid spatial bucket so per-span
///     queries don't scan every OSM feature (V2 plan 0.4 — no spatial index existed before this).
///     Shared via <c>RoadSmoothingParameters.BridgeObstacles</c> (same instance on every material).
/// </summary>
public sealed class BridgeObstacleSet
{
    private readonly Dictionary<(int X, int Y), List<int>> _buckets = new();
    private readonly List<BridgeObstacleFeature> _features;

    public BridgeObstacleSet(IEnumerable<BridgeObstacleFeature> features, float cellSizeMeters = 64f)
    {
        CellSizeMeters = Math.Max(1f, cellSizeMeters);
        _features = features.ToList();

        for (var i = 0; i < _features.Count; i++)
        {
            var f = _features[i];
            foreach (var cell in CellsForAabb(f.Min, f.Max))
            {
                if (!_buckets.TryGetValue(cell, out var list))
                    _buckets[cell] = list = [];
                list.Add(i);
            }
        }
    }

    public float CellSizeMeters { get; }

    public IReadOnlyList<BridgeObstacleFeature> Features => _features;

    public int Count => _features.Count;

    /// <summary>Distinct features whose AABB-buckets overlap the query AABB (candidates; verify precisely).</summary>
    public IEnumerable<BridgeObstacleFeature> QueryAabb(Vector2 min, Vector2 max)
    {
        var seen = new HashSet<int>();
        foreach (var cell in CellsForAabb(min, max))
        {
            if (!_buckets.TryGetValue(cell, out var list)) continue;
            foreach (var idx in list)
                if (seen.Add(idx))
                    yield return _features[idx];
        }
    }

    private IEnumerable<(int, int)> CellsForAabb(Vector2 min, Vector2 max)
    {
        var x0 = (int)MathF.Floor(min.X / CellSizeMeters);
        var x1 = (int)MathF.Floor(max.X / CellSizeMeters);
        var y0 = (int)MathF.Floor(min.Y / CellSizeMeters);
        var y1 = (int)MathF.Floor(max.Y / CellSizeMeters);
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                yield return (x, y);
    }
}

/// <summary>A spot where an obstacle feature passes under a bridge span footprint.</summary>
public readonly record struct BridgeObstacleCrossing(
    BridgeObstacleFeature Feature,
    Vector2 CrossingPoint);
