using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
///     Junction type classification for network junctions.
/// </summary>
public enum JunctionType
{
    /// <summary>
    ///     Single road ending (no connection to other roads).
    /// </summary>
    Endpoint,

    /// <summary>
    ///     One road ends at another's side (T-intersection).
    ///     The "continuous" road passes through; the "terminating" road ends.
    /// </summary>
    TJunction,

    /// <summary>
    ///     Two roads merge or split (Y-intersection).
    ///     Both roads end at the junction point.
    /// </summary>
    YJunction,

    /// <summary>
    ///     Four-way intersection (crossroads).
    ///     Four road segments meet at a single point.
    /// </summary>
    CrossRoads,

    /// <summary>
    ///     More than 4 roads meeting (roundabout, complex intersection).
    /// </summary>
    Complex,

    /// <summary>
    ///     Two roads cross each other without either terminating.
    ///     Both roads pass through the crossing point continuously.
    /// </summary>
    MidSplineCrossing,

    /// <summary>
    ///     Road connects to a roundabout ring.
    ///     The roundabout ring is continuous; the connecting road terminates
    ///     at the ring and forms a T-junction with it.
    /// </summary>
    Roundabout
}

/// <summary>
///     A contributor to a junction - a cross-section and its owning spline.
/// </summary>
public class JunctionContributor
{
    /// <summary>
    ///     The cross-section participating in this junction.
    /// </summary>
    public required UnifiedCrossSection CrossSection { get; init; }

    /// <summary>
    ///     The spline that owns this cross-section.
    /// </summary>
    public required ParameterizedRoadSpline Spline { get; init; }

    /// <summary>
    ///     Whether this is the start endpoint of the spline.
    /// </summary>
    public bool IsSplineStart { get; init; }

    /// <summary>
    ///     Whether this is the end endpoint of the spline.
    /// </summary>
    public bool IsSplineEnd { get; init; }

    /// <summary>
    ///     Whether this is an endpoint (either start or end).
    /// </summary>
    public bool IsEndpoint => IsSplineStart || IsSplineEnd;

    /// <summary>
    ///     Whether the spline passes THROUGH this junction (not an endpoint).
    ///     This indicates a T-junction where this spline is the "continuous" road.
    /// </summary>
    public bool IsContinuous => !IsEndpoint;
}

/// <summary>
///     Junction in the unified network with multi-material awareness.
///     Represents where roads from potentially different materials meet.
/// </summary>
public class NetworkJunction
{
    /// <summary>
    ///     World position of the junction center.
    ///     Calculated as the centroid of all contributing cross-section positions.
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    ///     All roads contributing to this junction.
    ///     For T-junctions, includes both the continuous and terminating roads.
    /// </summary>
    public List<JunctionContributor> Contributors { get; } = [];

    /// <summary>
    ///     The harmonized elevation calculated for this junction.
    ///     All contributing roads will blend toward this elevation.
    /// </summary>
    public float HarmonizedElevation { get; set; } = float.NaN;

    /// <summary>
    ///     The type of junction (T, Y, X, etc.).
    ///     Determines the harmonization strategy.
    /// </summary>
    public JunctionType Type { get; set; } = JunctionType.Endpoint;

    /// <summary>
    ///     Unique identifier for this junction.
    /// </summary>
    public int JunctionId { get; set; }

    /// <summary>
    ///     Whether this junction is excluded from harmonization.
    ///     Excluded junctions are skipped during elevation harmonization,
    ///     allowing the original terrain elevation to be used at this location.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    ///     Reason for exclusion (user-provided or auto-detected).
    /// </summary>
    public string? ExclusionReason { get; set; }

    /// <summary>
    ///     The maximum priority among all contributors.
    ///     Used for ordering junction processing.
    /// </summary>
    public int MaxPriority => Contributors.Count > 0
        ? Contributors.Max(c => c.Spline.Priority)
        : 0;

    /// <summary>
    ///     Whether this is a cross-material junction (involves roads from different materials).
    /// </summary>
    public bool IsCrossMaterial => GetMaterials().Count() > 1;

    /// <summary>
    ///     Gets the continuous road(s) in a T-junction.
    ///     These are roads that pass THROUGH the junction (not endpoints).
    /// </summary>
    public IEnumerable<JunctionContributor> GetContinuousRoads()
    {
        return Contributors.Where(c => c.IsContinuous);
    }

    /// <summary>
    ///     Gets the terminating road(s) in a T-junction.
    ///     These are roads that END at the junction.
    /// </summary>
    public IEnumerable<JunctionContributor> GetTerminatingRoads()
    {
        return Contributors.Where(c => c.IsEndpoint);
    }

    /// <summary>
    ///     Gets all unique materials involved in this junction.
    /// </summary>
    public IEnumerable<string> GetMaterials()
    {
        return Contributors.Select(c => c.Spline.MaterialName).Distinct();
    }

    /// <summary>
    ///     Gets the highest-priority contributor.
    /// </summary>
    public JunctionContributor? GetHighestPriorityContributor()
    {
        return Contributors.OrderByDescending(c => c.Spline.Priority).FirstOrDefault();
    }

    /// <summary>
    ///     Calculates the centroid of all contributor positions.
    /// </summary>
    public void CalculateCentroid()
    {
        if (Contributors.Count == 0)
            return;

        var sumX = Contributors.Sum(c => c.CrossSection.CenterPoint.X);
        var sumY = Contributors.Sum(c => c.CrossSection.CenterPoint.Y);
        Position = new Vector2(sumX / Contributors.Count, sumY / Contributors.Count);
    }

    /// <summary>
    ///     Calculates the approach angle of a contributor toward the junction center.
    ///     Returns the angle in degrees (0-360).
    /// </summary>
    /// <param name="contributor">The junction contributor.</param>
    /// <returns>Approach angle in degrees.</returns>
    public float GetApproachAngle(JunctionContributor contributor)
    {
        // Direction from contributor to junction center
        var direction = Position - contributor.CrossSection.CenterPoint;
        if (direction.LengthSquared() < 0.0001f)
            return 0f;

        var angleRad = MathF.Atan2(direction.Y, direction.X);
        var angleDeg = angleRad * 180f / MathF.PI;

        // Normalize to 0-360 range
        if (angleDeg < 0) angleDeg += 360f;
        return angleDeg;
    }

    /// <summary>
    ///     Calculates the angle between two contributors approaching the junction.
    ///     Returns the acute angle (0-180 degrees).
    /// </summary>
    /// <param name="a">First contributor.</param>
    /// <param name="b">Second contributor.</param>
    /// <returns>Angle between approaches in degrees.</returns>
    public float GetAngleBetween(JunctionContributor a, JunctionContributor b)
    {
        var angleA = GetApproachAngle(a);
        var angleB = GetApproachAngle(b);

        var diff = MathF.Abs(angleA - angleB);
        if (diff > 180f) diff = 360f - diff;

        return diff;
    }

    /// <summary>
    ///     Gets the elevation difference between this junction's harmonized elevation
    ///     and a contributor's original elevation.
    ///     Positive = contributor needs to rise, negative = contributor needs to lower.
    /// </summary>
    /// <param name="contributor">The junction contributor.</param>
    /// <returns>Elevation difference in meters.</returns>
    public float GetElevationDifference(JunctionContributor contributor)
    {
        if (float.IsNaN(HarmonizedElevation))
            return 0f;

        return HarmonizedElevation - contributor.CrossSection.TargetElevation;
    }

    /// <summary>
    ///     Gets a descriptive string for logging.
    /// </summary>
    public override string ToString()
    {
        var materials = string.Join(" + ", GetMaterials());
        return $"Junction #{JunctionId} [{Type}] at ({Position.X:F1}, {Position.Y:F1}) - " +
               $"{Contributors.Count} roads ({materials}), " +
               $"elev={HarmonizedElevation:F2}m, priority={MaxPriority}";
    }
}