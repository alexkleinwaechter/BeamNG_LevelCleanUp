using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Result of a corridor overlap check.
/// </summary>
public readonly record struct OverlapResult(bool IsOverlapping, int? OverlappingSplineId);

/// <summary>
/// Checks whether a 2D point falls inside a road's surface corridor.
/// Uses closest-section lookup and bracketing pair interpolation for
/// robust handling of curves and varying tangent directions.
/// </summary>
public static class RoadCorridorOverlapChecker
{
    /// <summary>
    /// Checks a point against a single corridor.
    /// Algorithm:
    /// 1. Find the closest section center to P
    /// 2. Check bracketing pairs (k-1,k) and (k,k+1)
    /// 3. Interpolate center and normal at P's longitudinal position
    /// 4. Check lateral distance against corridor half-width
    /// </summary>
    public static OverlapResult CheckPointAgainstCorridor(Vector2 point, RoadCorridor corridor)
    {
        var sections = corridor.Sections;
        if (sections.Count < 2)
            return new OverlapResult(false, null);

        // Step 1: Find closest section
        int closestIdx = 0;
        float closestDistSq = float.MaxValue;
        for (int i = 0; i < sections.Count; i++)
        {
            var distSq = Vector2.DistanceSquared(point, sections[i].Center);
            if (distSq < closestDistSq)
            {
                closestDistSq = distSq;
                closestIdx = i;
            }
        }

        // Step 2: Check bracketing pairs around closest section
        // Try (closestIdx-1, closestIdx) and (closestIdx, closestIdx+1)
        if (closestIdx > 0 &&
            TryBracketCheck(point, sections[closestIdx - 1], sections[closestIdx],
                corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);

        if (closestIdx < sections.Count - 1 &&
            TryBracketCheck(point, sections[closestIdx], sections[closestIdx + 1],
                corridor.CorridorHalfWidth))
            return new OverlapResult(true, corridor.SplineId);

        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Checks whether point P is longitudinally between sections A and B,
    /// and laterally within the corridor half-width.
    /// </summary>
    private static bool TryBracketCheck(
        Vector2 point, CorridorSection sA, CorridorSection sB, float halfWidth)
    {
        var ab = sB.Center - sA.Center;
        var abLenSq = ab.LengthSquared();
        if (abLenSq < 0.001f) return false; // Degenerate segment

        // Project P onto segment AB to get parameter t
        var ap = point - sA.Center;
        var t = Vector2.Dot(ap, ab) / abLenSq;

        // Must be longitudinally between A and B
        if (t < 0f || t > 1f) return false;

        // Interpolate center and normal
        var center = Vector2.Lerp(sA.Center, sB.Center, t);
        var normal = Vector2.Normalize(Vector2.Lerp(sA.Normal, sB.Normal, t));

        // Lateral distance
        var lateralDist = Vector2.Dot(point - center, normal);
        return MathF.Abs(lateralDist) < halfWidth;
    }

    /// <summary>
    /// Checks a point against all corridors except the point's own spline.
    /// Returns the first overlap found.
    /// </summary>
    public static OverlapResult CheckAgainstAllCorridors(
        Vector2 point,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors)
    {
        foreach (var (splineId, corridor) in corridors)
        {
            if (splineId == ownSplineId) continue;
            var result = CheckPointAgainstCorridor(point, corridor);
            if (result.IsOverlapping) return result;
        }
        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Checks a point against corridors, but only those contributing to
    /// junctions near the point. Uses junction proximity filter for performance.
    /// </summary>
    public static OverlapResult CheckWithJunctionFilter(
        Vector2 point,
        int ownSplineId,
        IReadOnlyDictionary<int, RoadCorridor> corridors,
        IReadOnlyList<JunctionInfluenceZone> junctionZones)
    {
        foreach (var zone in junctionZones)
        {
            if (Vector2.DistanceSquared(point, zone.Position) > zone.RadiusSquared)
                continue;

            // Point is near this junction — check contributing corridors
            foreach (var contributingSplineId in zone.ContributingSplineIds)
            {
                if (contributingSplineId == ownSplineId) continue;
                if (!corridors.TryGetValue(contributingSplineId, out var corridor)) continue;

                var result = CheckPointAgainstCorridor(point, corridor);
                if (result.IsOverlapping) return result;
            }
        }
        return new OverlapResult(false, null);
    }

    /// <summary>
    /// Builds junction influence zones for the proximity filter.
    /// Each zone covers the area where corridor overlaps can occur.
    /// </summary>
    public static List<JunctionInfluenceZone> BuildJunctionInfluenceZones(
        IReadOnlyList<NetworkJunction> junctions,
        IReadOnlyDictionary<int, RoadCorridor> corridors)
    {
        var zones = new List<JunctionInfluenceZone>();

        foreach (var junction in junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type == JunctionType.Endpoint) continue;

            var contributingIds = junction.Contributors
                .Select(c => c.Spline.SplineId)
                .Distinct()
                .ToList();

            // Radius = max corridor half-width among contributors + some padding
            float maxHalfWidth = 0f;
            foreach (var id in contributingIds)
            {
                if (corridors.TryGetValue(id, out var c))
                    maxHalfWidth = MathF.Max(maxHalfWidth, c.CorridorHalfWidth);
            }

            if (maxHalfWidth <= 0f) continue;

            // Use 2x max half-width to account for the check needing to cover
            // both the corridor being checked AND the offset node position
            var radius = maxHalfWidth * 2f;

            zones.Add(new JunctionInfluenceZone(
                junction.Position, radius, radius * radius, contributingIds));
        }

        return zones;
    }
}

/// <summary>
/// Pre-computed junction influence zone for quick spatial filtering.
/// </summary>
public readonly record struct JunctionInfluenceZone(
    Vector2 Position,
    float Radius,
    float RadiusSquared,
    IReadOnlyList<int> ContributingSplineIds);
