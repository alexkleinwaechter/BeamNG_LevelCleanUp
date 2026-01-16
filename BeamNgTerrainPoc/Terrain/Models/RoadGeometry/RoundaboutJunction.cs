using System.Numerics;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Represents a junction where a road connects to a roundabout ring.
/// Extends standard junction logic with roundabout-specific handling for
/// elevation harmonization and junction geometry.
/// </summary>
public class RoundaboutJunction
{
    /// <summary>
    /// Reference to the parent NetworkJunction that contains this roundabout connection.
    /// </summary>
    public required NetworkJunction ParentJunction { get; init; }

    /// <summary>
    /// The roundabout ring spline ID in the unified network.
    /// </summary>
    public required int RoundaboutSplineId { get; init; }

    /// <summary>
    /// The connecting road spline ID in the unified network.
    /// </summary>
    public required int ConnectingRoadSplineId { get; init; }

    /// <summary>
    /// Position where the connection occurs on the roundabout ring (in meters).
    /// </summary>
    public Vector2 ConnectionPointMeters { get; set; }

    /// <summary>
    /// Distance along the roundabout ring spline where the connection occurs.
    /// Used for finding the closest cross-section on the ring.
    /// </summary>
    public float DistanceAlongRoundabout { get; set; }

    /// <summary>
    /// Angle around the roundabout center (in degrees, 0 = East, 90 = North).
    /// </summary>
    public float AngleDegrees { get; set; }

    /// <summary>
    /// Whether the connecting road is an entry, exit, or bidirectional.
    /// Based on OSM oneway tag analysis.
    /// </summary>
    public RoundaboutConnectionDirection Direction { get; set; }

    /// <summary>
    /// Target elevation at this connection point (from roundabout ring).
    /// All connecting roads should blend toward this elevation.
    /// </summary>
    public float TargetElevation { get; set; } = float.NaN;

    /// <summary>
    /// Whether the target elevation has been calculated.
    /// </summary>
    public bool HasTargetElevation => !float.IsNaN(TargetElevation);

    /// <summary>
    /// The center of the roundabout in meters.
    /// Used for distance calculations and direction analysis.
    /// </summary>
    public Vector2 RoundaboutCenterMeters { get; set; }

    /// <summary>
    /// The radius of the roundabout in meters.
    /// </summary>
    public float RoundaboutRadiusMeters { get; set; }

    /// <summary>
    /// Whether the connecting road endpoint is the start of the spline (vs the end).
    /// Used to determine which end of the road connects to the roundabout.
    /// </summary>
    public bool IsConnectingRoadStart { get; set; }

    /// <summary>
    /// Gets the approach angle of the connecting road toward the roundabout center.
    /// Returns the angle in radians, where:
    /// - Positive = approaching from the east/north quadrants
    /// - Negative = approaching from the west/south quadrants
    /// </summary>
    public float GetApproachAngleRadians()
    {
        var direction = RoundaboutCenterMeters - ConnectionPointMeters;
        if (direction.LengthSquared() < 0.0001f)
            return 0f;

        return MathF.Atan2(direction.Y, direction.X);
    }

    /// <summary>
    /// Calculates whether a connecting road is approaching the roundabout tangentially
    /// (versus radially). Returns a value from 0 to 1 where:
    /// - 0 = perfectly radial approach (perpendicular to ring)
    /// - 1 = perfectly tangential approach (parallel to ring)
    /// </summary>
    /// <param name="roadDirection">Direction vector of the road at the connection point.</param>
    public float CalculateTangentialApproachFactor(Vector2 roadDirection)
    {
        if (roadDirection.LengthSquared() < 0.0001f)
            return 0f;

        // Get the tangent direction at this point on the ring
        // (perpendicular to the radial direction)
        var radialDirection = Vector2.Normalize(ConnectionPointMeters - RoundaboutCenterMeters);
        var tangentDirection = new Vector2(-radialDirection.Y, radialDirection.X);

        // Calculate how aligned the road is with the tangent
        var normalizedRoad = Vector2.Normalize(roadDirection);
        var dotProduct = Math.Abs(Vector2.Dot(normalizedRoad, tangentDirection));

        return dotProduct;
    }

    /// <summary>
    /// Gets a descriptive string for logging.
    /// </summary>
    public override string ToString()
    {
        var directionStr = Direction switch
        {
            RoundaboutConnectionDirection.Entry => "Entry",
            RoundaboutConnectionDirection.Exit => "Exit",
            RoundaboutConnectionDirection.Bidirectional => "Bidir",
            _ => "Unknown"
        };

        return $"RoundaboutJunction: Road {ConnectingRoadSplineId} -> Roundabout {RoundaboutSplineId} " +
               $"at ({ConnectionPointMeters.X:F1}, {ConnectionPointMeters.Y:F1}) " +
               $"angle={AngleDegrees:F0}° [{directionStr}]" +
               (HasTargetElevation ? $" elev={TargetElevation:F2}m" : "");
    }
}

/// <summary>
/// Stores information about all roundabout junctions in a unified road network.
/// Used by the harmonization phase to apply consistent elevation around roundabouts.
/// </summary>
public class RoundaboutJunctionInfo
{
    /// <summary>
    /// The roundabout ring spline ID.
    /// </summary>
    public required int RoundaboutSplineId { get; init; }

    /// <summary>
    /// Center of the roundabout in meters.
    /// </summary>
    public Vector2 CenterMeters { get; set; }

    /// <summary>
    /// Radius of the roundabout in meters.
    /// </summary>
    public float RadiusMeters { get; set; }

    /// <summary>
    /// All junctions where roads connect to this roundabout.
    /// </summary>
    public List<RoundaboutJunction> Junctions { get; } = [];

    /// <summary>
    /// The harmonized elevation for the entire roundabout ring.
    /// All connecting roads should blend toward this elevation.
    /// </summary>
    public float HarmonizedElevation { get; set; } = float.NaN;

    /// <summary>
    /// Whether the harmonized elevation has been calculated.
    /// </summary>
    public bool HasHarmonizedElevation => !float.IsNaN(HarmonizedElevation);

    /// <summary>
    /// Gets the number of connecting roads at this roundabout.
    /// </summary>
    public int ConnectionCount => Junctions.Count;

    /// <summary>
    /// Gets the average angle between connections (in degrees).
    /// Returns 0 if fewer than 2 connections.
    /// </summary>
    public float AverageAngleBetweenConnections
    {
        get
        {
            if (Junctions.Count < 2)
                return 0;

            var angles = Junctions.OrderBy(j => j.AngleDegrees).ToList();
            float totalAngle = 0;

            for (int i = 0; i < angles.Count; i++)
            {
                var current = angles[i].AngleDegrees;
                var next = angles[(i + 1) % angles.Count].AngleDegrees;
                var diff = next - current;
                if (diff < 0) diff += 360;
                totalAngle += diff;
            }

            return totalAngle / angles.Count;
        }
    }

    /// <summary>
    /// Gets a descriptive string for logging.
    /// </summary>
    public override string ToString()
    {
        return $"Roundabout {RoundaboutSplineId}: " +
               $"center=({CenterMeters.X:F1}, {CenterMeters.Y:F1}), " +
               $"radius={RadiusMeters:F1}m, " +
               $"{ConnectionCount} connections" +
               (HasHarmonizedElevation ? $", elev={HarmonizedElevation:F2}m" : "");
    }
}
