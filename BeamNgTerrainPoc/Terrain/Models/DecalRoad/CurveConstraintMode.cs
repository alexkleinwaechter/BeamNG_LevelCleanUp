namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Controls how curve detection affects layer generation.
/// </summary>
public enum CurveConstraintMode
{
    /// <summary>No curve constraint — layer generated everywhere.</summary>
    None,

    /// <summary>Layer generated only in curve sections (existing behavior).</summary>
    CurveOnly,

    /// <summary>Main material on straights, replacement material in curves.</summary>
    ReplaceInCurve
}
