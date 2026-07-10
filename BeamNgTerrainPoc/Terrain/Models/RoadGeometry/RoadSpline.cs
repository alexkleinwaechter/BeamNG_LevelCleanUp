using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using MathNet.Numerics.Interpolation;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
///     Represents a smooth spline through road centerline points.
///     Provides position, tangent, and normal calculations along the road.
/// </summary>
public class RoadSpline
{
    /// <summary>
    ///     Minimum points required for Akima spline interpolation.
    /// </summary>
    private const int MinPointsForAkima = 5;

    private readonly List<float> _distances; // Cumulative arc length at each point
    private readonly IInterpolation _splineX;
    private readonly IInterpolation _splineY;

    /// <summary>
    ///     Creates a road spline with the specified interpolation type.
    /// </summary>
    /// <param name="controlPoints">Control points defining the road centerline.</param>
    /// <param name="interpolationType">How to interpolate between control points.</param>
    public RoadSpline(List<Vector2> controlPoints,
        SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated)
    {
        if (controlPoints == null || controlPoints.Count < 2)
            throw new ArgumentException("Need at least 2 control points for spline", nameof(controlPoints));

        InterpolationType = interpolationType;

        // Calculate cumulative arc lengths (parameter t for spline), enforcing STRICTLY increasing
        // knots — MathNet's *Sorted interpolators silently produce NaN coefficients on duplicate t
        // values, which turns every sampled position into (NaN, NaN) downstream (degenerate OSM ways,
        // Chaikin-coincident points, or tiny segments that collapse in float cumulative distance).
        // Points that don't advance the arc length (incl. non-finite points, whose distance compares
        // false) are dropped.
        var keptPoints = new List<Vector2>(controlPoints.Count) { controlPoints[0] };
        _distances = new List<float> { 0 };
        for (var i = 1; i < controlPoints.Count; i++)
        {
            var point = controlPoints[i];
            var cumulative = _distances[^1] + Vector2.Distance(keptPoints[^1], point);
            if (!(cumulative > _distances[^1]))
                continue;

            keptPoints.Add(point);
            _distances.Add(cumulative);
        }

        // Preserve the caller's list reference when nothing was dropped (the common case).
        ControlPoints = keptPoints.Count == controlPoints.Count ? controlPoints : keptPoints;

        if (ControlPoints.Count < 2)
            throw new ArgumentException("Control points result in zero-length spline", nameof(controlPoints));

        TotalLength = _distances[_distances.Count - 1];

        // Handle zero-length splines (duplicate points)
        if (TotalLength < 0.001f)
            throw new ArgumentException("Control points result in zero-length spline", nameof(controlPoints));

        // Create separate splines for X and Y coordinates
        var t = _distances.Select(d => (double)d).ToArray();
        var x = ControlPoints.Select(p => (double)p.X).ToArray();
        var y = ControlPoints.Select(p => (double)p.Y).ToArray();

        // Choose interpolation method based on type and number of points
        if (interpolationType == SplineInterpolationType.LinearControlPoints)
        {
            // Linear interpolation - follows original control points exactly
            _splineX = LinearSpline.InterpolateSorted(t, x);
            _splineY = LinearSpline.InterpolateSorted(t, y);
        }
        else // SmoothInterpolated (default)
        {
            // Choose smooth interpolation method based on number of points
            if (ControlPoints.Count >= MinPointsForAkima)
            {
                // Akima spline - good for avoiding overshoot, smooth for roads
                _splineX = CubicSpline.InterpolateAkimaSorted(t, x);
                _splineY = CubicSpline.InterpolateAkimaSorted(t, y);
            }
            else if (ControlPoints.Count >= 3)
            {
                // Natural cubic spline for 3-4 points
                _splineX = CubicSpline.InterpolateNaturalSorted(t, x);
                _splineY = CubicSpline.InterpolateNaturalSorted(t, y);
            }
            else
            {
                // Linear interpolation for 2 points (fallback when smooth isn't possible)
                _splineX = LinearSpline.InterpolateSorted(t, x);
                _splineY = LinearSpline.InterpolateSorted(t, y);
            }
        }
    }

    /// <summary>
    ///     Control points used to create the spline
    /// </summary>
    public List<Vector2> ControlPoints { get; }

    /// <summary>
    ///     The interpolation type used for this spline
    /// </summary>
    public SplineInterpolationType InterpolationType { get; }

    /// <summary>
    ///     Total arc length of the spline
    /// </summary>
    public float TotalLength { get; }

    // ========================================
    // OSM METADATA
    // ========================================

    /// <summary>
    ///     OSM highway type (e.g. "primary", "residential") if this spline originated from OSM data.
    ///     Set during spline creation in the orchestrator.
    /// </summary>
    public string? OsmRoadType { get; set; }

    /// <summary>
    ///     OSM node ID of the spline's start point, or null if not from OSM / cropped at boundary.
    ///     Set during spline creation from PathWithMetadata.StartNodeId.
    /// </summary>
    public long? StartOsmNodeId { get; set; }

    /// <summary>
    ///     OSM node ID of the spline's end point, or null if not from OSM / cropped at boundary.
    ///     Set during spline creation from PathWithMetadata.EndNodeId.
    /// </summary>
    public long? EndOsmNodeId { get; set; }

    /// <summary>
    ///     OSM way ID(s) this spline was built from. A single spline may merge several OSM ways,
    ///     so this is a set. Set during spline creation from PathWithMetadata.AllWayIds.
    ///     Empty if not from OSM. Used for debugging / cross-referencing back to OSM.
    /// </summary>
    public HashSet<long> OsmWayIds { get; set; } = [];

    /// <summary>
    ///     Per-segment lane configuration from OSM tags.
    ///     Null if no lane data was parsed. StartDistance is populated during spline creation.
    /// </summary>
    public List<LaneSegment>? LaneSegments { get; set; }

    /// <summary>
    ///     Bridge/tunnel sub-ranges along this (possibly merged) spline, anchored by arc-length
    ///     (StartDistance/EndDistance, populated during spline creation). Empty/null for a plain road.
    ///     Lets a merged corridor remember which stretch is a bridge — the "merged-corridor bridge"
    ///     refactor (plan doc 11). The whole-spline <see cref="IsBridge"/>/<see cref="IsTunnel"/> flags
    ///     remain the source of truth while structures are still kept as separate splines; this list is
    ///     the per-sub-range record that survives merging.
    /// </summary>
    public List<StructureSegment>? StructureSegments { get; set; }

    // ========================================
    // STRUCTURE METADATA (Bridge/Tunnel)
    // ========================================

    /// <summary>
    ///     Whether this spline represents a bridge (from OSM bridge=* tag).
    ///     Set during spline creation from OsmFeature.
    /// </summary>
    public bool IsBridge { get; set; }

    /// <summary>
    ///     Whether this spline represents a tunnel (from OSM tunnel=* or covered=yes tag).
    ///     Set during spline creation from OsmFeature.
    /// </summary>
    public bool IsTunnel { get; set; }

    /// <summary>
    ///     Combined check for any elevated/underground structure.
    /// </summary>
    public bool IsStructure => IsBridge || IsTunnel;

    /// <summary>
    ///     Detailed structure type (None, Bridge, Tunnel, BuildingPassage, Culvert).
    ///     Set during spline creation from OsmFeature.GetStructureType().
    /// </summary>
    public StructureType StructureType { get; set; } = StructureType.None;

    /// <summary>
    ///     Vertical layer from OSM (0 = ground level, positive = elevated, negative = underground).
    ///     Set during spline creation from OsmFeature.Layer.
    /// </summary>
    public int Layer { get; set; } = 0;

    /// <summary>
    ///     Bridge structure type (beam, arch, suspension, etc.) for future DAE generation.
    ///     Set during spline creation from OsmFeature.BridgeStructureType.
    /// </summary>
    public string? BridgeStructureType { get; set; }

    /// <summary>
    ///     Full OSM tag dictionary from the source way(s), captured at spline creation (D-6).
    ///     Null if not from OSM. Lets downstream read raw tags like bridge=, maxheight=, man_made=
    ///     that aren't promoted to dedicated fields.
    /// </summary>
    public IReadOnlyDictionary<string, string>? OsmTags { get; set; }

    /// <summary>
    ///     Pre-computed width profile derived from OSM lane/width data.
    ///     Null if no width data is available (falls back to RoadSmoothingParameters.RoadWidthMeters).
    /// </summary>
    public RoadWidthProfile? WidthProfile { get; set; }

    /// <summary>
    ///     Creates a smooth interpolated road spline (Akima/cubic).
    ///     Best for nice curved roads, highways, racing circuits.
    /// </summary>
    public static RoadSpline CreateSmooth(List<Vector2> controlPoints)
    {
        return new RoadSpline(controlPoints);
    }

    /// <summary>
    ///     Creates a linear road spline that follows control points exactly.
    ///     Best for accurate adherence to source skeleton/OSM geometry.
    /// </summary>
    public static RoadSpline CreateLinear(List<Vector2> controlPoints)
    {
        return new RoadSpline(controlPoints, SplineInterpolationType.LinearControlPoints);
    }

    /// <summary>
    ///     Get position along spline at distance d from start
    /// </summary>
    public Vector2 GetPointAtDistance(float distance)
    {
        distance = Math.Clamp(distance, 0, TotalLength);

        var x = (float)_splineX.Interpolate(distance);
        var y = (float)_splineY.Interpolate(distance);

        return new Vector2(x, y);
    }

    /// <summary>
    ///     Get tangent (direction) at distance d from start
    /// </summary>
    public Vector2 GetTangentAtDistance(float distance)
    {
        distance = Math.Clamp(distance, 0, TotalLength);

        // Calculate derivative (tangent)
        var dx = (float)_splineX.Differentiate(distance);
        var dy = (float)_splineY.Differentiate(distance);

        var tangent = new Vector2(dx, dy);
        var length = tangent.Length();

        return length > 0.001f ? Vector2.Normalize(tangent) : new Vector2(1, 0);
    }

    /// <summary>
    ///     Get normal (perpendicular to road direction) at distance d from start.
    ///     The normal points to the RIGHT side of the road when looking forward along the tangent.
    /// </summary>
    public Vector2 GetNormalAtDistance(float distance)
    {
        var tangent = GetTangentAtDistance(distance);
        // Rotate 90 degrees clockwise: (x, y) -> (y, -x)
        // This gives a vector pointing to the RIGHT when facing forward
        return new Vector2(tangent.Y, -tangent.X);
    }

    /// <summary>
    ///     Arc-length (m) of the point on this spline closest to <paramref name="point"/> (V2 plan 0.3a).
    ///     Works on the control-point polyline (the same chord-length parameterization the spline uses), so
    ///     the returned distance is directly comparable to <see cref="GetPointAtDistance"/> stations.
    ///     Closest-point on a curvy network can be ambiguous (switchbacks, parallel carriageways): among all
    ///     candidates within <paramref name="ambiguityToleranceMeters"/> of the true minimum lateral distance,
    ///     the one nearest <paramref name="seedDistance"/> wins — pass the best a-priori station estimate
    ///     (e.g. the pre-Chaikin arc-length sum) to disambiguate. NaN seed ⇒ pure global minimum.
    /// </summary>
    public float GetClosestDistanceTo(Vector2 point, float seedDistance = float.NaN,
        float ambiguityToleranceMeters = 2.0f)
    {
        var bestLateralSq = float.MaxValue;
        // (arcDistance, lateralSq) per local candidate; collected in one pass, filtered after.
        var candidates = new List<(float Arc, float LatSq)>(8);

        for (var i = 1; i < ControlPoints.Count; i++)
        {
            var a = ControlPoints[i - 1];
            var b = ControlPoints[i];
            var ab = b - a;
            var abLenSq = ab.LengthSquared();
            var t = abLenSq > 1e-12f ? Math.Clamp(Vector2.Dot(point - a, ab) / abLenSq, 0f, 1f) : 0f;
            var proj = a + ab * t;
            var latSq = (point - proj).LengthSquared();
            var arc = _distances[i - 1] + (_distances[i] - _distances[i - 1]) * t;

            if (latSq < bestLateralSq) bestLateralSq = latSq;
            candidates.Add((arc, latSq));
        }

        var cutoff = MathF.Sqrt(bestLateralSq) + ambiguityToleranceMeters;
        var cutoffSq = cutoff * cutoff;

        var bestArc = 0f;
        var bestScore = float.MaxValue;
        foreach (var (arc, latSq) in candidates)
        {
            if (latSq > cutoffSq) continue;
            var score = float.IsNaN(seedDistance) ? latSq : MathF.Abs(arc - seedDistance);
            if (score < bestScore)
            {
                bestScore = score;
                bestArc = arc;
            }
        }

        return bestArc;
    }

    /// <summary>
    ///     Sample spline at regular distance intervals
    /// </summary>
    public List<SplineSample> SampleByDistance(float intervalMeters)
    {
        var samples = new List<SplineSample>();

        for (float distance = 0; distance <= TotalLength; distance += intervalMeters)
            samples.Add(new SplineSample
            {
                Distance = distance,
                Position = GetPointAtDistance(distance),
                Tangent = GetTangentAtDistance(distance),
                Normal = GetNormalAtDistance(distance)
            });

        // Always add final point if not already added
        if (samples.Count == 0 || MathF.Abs(samples[^1].Distance - TotalLength) > 0.01f)
            samples.Add(new SplineSample
            {
                Distance = TotalLength,
                Position = GetPointAtDistance(TotalLength),
                Tangent = GetTangentAtDistance(TotalLength),
                Normal = GetNormalAtDistance(TotalLength)
            });

        return samples;
    }
}

/// <summary>
///     A sample point along the road spline with optional banking data.
/// </summary>
public struct SplineSample
{
    /// <summary>
    ///     Distance along road from start (meters).
    /// </summary>
    public float Distance;

    /// <summary>
    ///     World position (X, Y in meters).
    /// </summary>
    public Vector2 Position;

    /// <summary>
    ///     Direction of road (normalized, 2D tangent vector).
    /// </summary>
    public Vector2 Tangent;

    /// <summary>
    ///     Perpendicular to road direction (normalized, 2D normal vector).
    ///     Points to the right side of the road when facing forward.
    /// </summary>
    public Vector2 Normal;

    // === Banking Data (Phase 1) ===

    /// <summary>
    ///     Curvature at this point (1/radius in 1/meters).
    ///     Positive = curving left, Negative = curving right.
    ///     Default: 0 (straight road).
    /// </summary>
    public float Curvature;

    /// <summary>
    ///     Calculated bank angle at this point in radians.
    ///     Positive = tilted right-side-up (outer edge higher for left curve).
    ///     Default: 0 (flat road).
    /// </summary>
    public float BankAngleRadians;

    /// <summary>
    ///     3D normal after banking applied.
    ///     For flat road: (0, 0, 1) - pointing straight up
    ///     For banked road: rotated around tangent axis by BankAngleRadians
    /// </summary>
    public Vector3 BankedNormal;
}