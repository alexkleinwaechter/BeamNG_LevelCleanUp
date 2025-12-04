using System.Numerics;
using MathNet.Numerics.Interpolation;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Represents a smooth spline through road centerline points.
/// Provides position, tangent, and normal calculations along the road.
/// </summary>
public class RoadSpline
{
    private readonly IInterpolation _splineX;
    private readonly IInterpolation _splineY;
    private readonly List<float> _distances; // Cumulative arc length at each point
    private readonly float _totalLength;
    
    /// <summary>
    /// Control points used to create the spline
    /// </summary>
    public List<Vector2> ControlPoints { get; }
    
    /// <summary>
    /// Total arc length of the spline
    /// </summary>
    public float TotalLength => _totalLength;
    
    public RoadSpline(List<Vector2> controlPoints)
    {
        if (controlPoints == null || controlPoints.Count < 2)
            throw new ArgumentException("Need at least 2 control points for spline", nameof(controlPoints));
        
        ControlPoints = controlPoints;
        
        // Calculate cumulative arc lengths (parameter t for spline)
        _distances = new List<float> { 0 };
        for (int i = 1; i < controlPoints.Count; i++)
        {
            float segmentLength = Vector2.Distance(controlPoints[i - 1], controlPoints[i]);
            _distances.Add(_distances[i - 1] + segmentLength);
        }
        _totalLength = _distances[_distances.Count - 1];
        
        // Create separate splines for X and Y coordinates
        // Using Akima spline (good for avoiding overshoot, smooth for roads)
        var t = _distances.Select(d => (double)d).ToArray();
        var x = controlPoints.Select(p => (double)p.X).ToArray();
        var y = controlPoints.Select(p => (double)p.Y).ToArray();
        
        _splineX = CubicSpline.InterpolateAkimaSorted(t, x);
        _splineY = CubicSpline.InterpolateAkimaSorted(t, y);
    }
    
    /// <summary>
    /// Get position along spline at distance d from start
    /// </summary>
    public Vector2 GetPointAtDistance(float distance)
    {
        distance = Math.Clamp(distance, 0, _totalLength);
        
        float x = (float)_splineX.Interpolate(distance);
        float y = (float)_splineY.Interpolate(distance);
        
        return new Vector2(x, y);
    }
    
    /// <summary>
    /// Get tangent (direction) at distance d from start
    /// </summary>
    public Vector2 GetTangentAtDistance(float distance)
    {
        distance = Math.Clamp(distance, 0, _totalLength);
        
        // Calculate derivative (tangent)
        float dx = (float)_splineX.Differentiate(distance);
        float dy = (float)_splineY.Differentiate(distance);
        
        var tangent = new Vector2(dx, dy);
        float length = tangent.Length();
        
        return length > 0.001f ? Vector2.Normalize(tangent) : new Vector2(1, 0);
    }
    
    /// <summary>
    /// Get normal (perpendicular to road direction) at distance d from start
    /// </summary>
    public Vector2 GetNormalAtDistance(float distance)
    {
        var tangent = GetTangentAtDistance(distance);
        // Rotate 90 degrees counterclockwise: (x, y) -> (-y, x)
        return new Vector2(-tangent.Y, tangent.X);
    }
    
    /// <summary>
    /// Sample spline at regular distance intervals
    /// </summary>
    public List<SplineSample> SampleByDistance(float intervalMeters)
    {
        var samples = new List<SplineSample>();
        
        for (float distance = 0; distance <= _totalLength; distance += intervalMeters)
        {
            samples.Add(new SplineSample
            {
                Distance = distance,
                Position = GetPointAtDistance(distance),
                Tangent = GetTangentAtDistance(distance),
                Normal = GetNormalAtDistance(distance)
            });
        }
        
        // Always add final point if not already added
        if (samples.Count == 0 || MathF.Abs(samples[^1].Distance - _totalLength) > 0.01f)
        {
            samples.Add(new SplineSample
            {
                Distance = _totalLength,
                Position = GetPointAtDistance(_totalLength),
                Tangent = GetTangentAtDistance(_totalLength),
                Normal = GetNormalAtDistance(_totalLength)
            });
        }
        
        return samples;
    }
}

/// <summary>
/// A sample point along the road spline
/// </summary>
public struct SplineSample
{
    public float Distance;      // Distance along road from start
    public Vector2 Position;    // World position
    public Vector2 Tangent;     // Direction of road (normalized)
    public Vector2 Normal;      // Perpendicular to road (normalized)
}
