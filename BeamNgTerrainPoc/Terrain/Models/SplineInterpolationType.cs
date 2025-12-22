namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Defines how splines are interpolated between control points.
/// This affects both elevation smoothing and material painting consistency.
/// </summary>
public enum SplineInterpolationType
{
    /// <summary>
    /// Uses Akima/cubic spline interpolation for smooth curves.
    /// Best for: Nice curved roads, highways, racing circuits.
    /// Trade-off: May deviate slightly from original skeleton path.
    /// </summary>
    SmoothInterpolated,
    
    /// <summary>
    /// Uses linear interpolation between original control points.
    /// Best for: Accurate adherence to source skeleton/OSM geometry.
    /// Trade-off: Less smooth curves, may have visible segments.
    /// </summary>
    LinearControlPoints
}
