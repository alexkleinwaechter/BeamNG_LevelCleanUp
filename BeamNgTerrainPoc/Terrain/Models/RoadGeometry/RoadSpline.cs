using System.Numerics;
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

        ControlPoints = controlPoints;
        InterpolationType = interpolationType;

        // Calculate cumulative arc lengths (parameter t for spline)
        _distances = new List<float> { 0 };
        for (var i = 1; i < controlPoints.Count; i++)
        {
            var segmentLength = Vector2.Distance(controlPoints[i - 1], controlPoints[i]);
            _distances.Add(_distances[i - 1] + segmentLength);
        }

        TotalLength = _distances[_distances.Count - 1];

        // Handle zero-length splines (duplicate points)
        if (TotalLength < 0.001f)
            throw new ArgumentException("Control points result in zero-length spline", nameof(controlPoints));

        // Create separate splines for X and Y coordinates
        var t = _distances.Select(d => (double)d).ToArray();
        var x = controlPoints.Select(p => (double)p.X).ToArray();
        var y = controlPoints.Select(p => (double)p.Y).ToArray();

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
            if (controlPoints.Count >= MinPointsForAkima)
            {
                // Akima spline - good for avoiding overshoot, smooth for roads
                _splineX = CubicSpline.InterpolateAkimaSorted(t, x);
                _splineY = CubicSpline.InterpolateAkimaSorted(t, y);
            }
            else if (controlPoints.Count >= 3)
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