using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A.5 — per-spline lookup of which directly-anchored junctions claim
///     each end and over what blend distance. Built once after constraint
///     propagation completes; consumed by Step 5b to taper propagated mid-spline
///     influence weights inside a contested claim's zone.
/// </summary>
public sealed class SplineClaimedZone
{
    public required int SplineId { get; init; }
    public required float RoadLength { get; init; }
    public SplineEndClaim? StartClaim { get; init; }
    public SplineEndClaim? EndClaim { get; init; }
    public required Dictionary<int, float> DistFromStartByCsIndex { get; init; }
}

public sealed class SplineEndClaim
{
    public required int JunctionId { get; init; }
    public required float BlendDistanceMeters { get; init; }
}

public static class SplineClaimedZones
{
    /// <summary>
    ///     Build the per-spline claimed-zones lookup from the constraints dictionary
    ///     produced by ComputeAllJunctionConstraints + PropagateConstraintsThroughShortSplines.
    /// </summary>
    public static Dictionary<int, SplineClaimedZone> Build(
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var result = new Dictionary<int, SplineClaimedZone>();

        var claimedSplineIds = new HashSet<int>();
        foreach (var key in constraints.Keys)
            claimedSplineIds.Add(key.splineId);

        foreach (var splineId in claimedSplineIds)
        {
            if (!crossSectionsBySpline.TryGetValue(splineId, out var sections) || sections.Count < 2)
                continue;

            var distFromStart = new Dictionary<int, float>(sections.Count);
            distFromStart[sections[0].Index] = 0f;
            var cumulative = 0f;
            for (var i = 1; i < sections.Count; i++)
            {
                cumulative += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
                distFromStart[sections[i].Index] = cumulative;
            }

            SplineEndClaim? startClaim = null;
            if (constraints.TryGetValue((splineId, true), out var startC))
                startClaim = new SplineEndClaim
                {
                    JunctionId = startC.Junction?.JunctionId ?? 0,
                    BlendDistanceMeters = startC.BlendDistanceMeters
                };

            SplineEndClaim? endClaim = null;
            if (constraints.TryGetValue((splineId, false), out var endC))
                endClaim = new SplineEndClaim
                {
                    JunctionId = endC.Junction?.JunctionId ?? 0,
                    BlendDistanceMeters = endC.BlendDistanceMeters
                };

            result[splineId] = new SplineClaimedZone
            {
                SplineId = splineId,
                RoadLength = cumulative,
                StartClaim = startClaim,
                EndClaim = endClaim,
                DistFromStartByCsIndex = distFromStart
            };
        }

        return result;
    }

    /// <summary>
    ///     For a given CS on a claimed spline, returns the strongest applicable
    ///     overlap taper to apply to a propagated influence from
    ///     <paramref name="sourceJunctionId" />. Returns 1 (no taper) when the CS
    ///     sits outside any claim, or when the only contested claim belongs to the
    ///     same junction as the propagated source.
    /// </summary>
    public static float GetTaperFor(
        SplineClaimedZone zone,
        int csIndex,
        int sourceJunctionId)
    {
        if (!zone.DistFromStartByCsIndex.TryGetValue(csIndex, out var d)) return 1f;

        var taper = 1f;

        if (zone.StartClaim != null && zone.StartClaim.JunctionId != sourceJunctionId)
        {
            var distFromStartAnchor = d;
            if (distFromStartAnchor < zone.StartClaim.BlendDistanceMeters)
            {
                var startTaper = OverlapTaper.Compute(distFromStartAnchor, zone.StartClaim.BlendDistanceMeters);
                if (startTaper < taper) taper = startTaper;
            }
        }

        if (zone.EndClaim != null && zone.EndClaim.JunctionId != sourceJunctionId)
        {
            var distFromEndAnchor = zone.RoadLength - d;
            if (distFromEndAnchor < zone.EndClaim.BlendDistanceMeters)
            {
                var endTaper = OverlapTaper.Compute(distFromEndAnchor, zone.EndClaim.BlendDistanceMeters);
                if (endTaper < taper) taper = endTaper;
            }
        }

        return taper;
    }

    /// <summary>
    ///     Phase B.3 nested-junction guard. Returns true if the sample point at
    ///     <paramref name="distFromStart" /> sits inside ANY claim other than the
    ///     own-side anchor identified by <paramref name="ownAnchorIsStart" />.
    ///     A non-zero <paramref name="marginMeters" /> expands the test zones by
    ///     that amount on each relevant side (used by the slope-sample point at
    ///     d=L+ε to defensively treat near-boundary cases as "inside").
    /// </summary>
    public static bool HasOtherClaimNear(
        SplineClaimedZone zone,
        float distFromStart,
        bool ownAnchorIsStart,
        float marginMeters)
    {
        if (zone.StartClaim != null && !ownAnchorIsStart)
        {
            var startZoneEnd = zone.StartClaim.BlendDistanceMeters + marginMeters;
            if (distFromStart < startZoneEnd) return true;
        }

        if (zone.EndClaim != null && ownAnchorIsStart)
        {
            var endZoneStart = zone.RoadLength - zone.EndClaim.BlendDistanceMeters - marginMeters;
            if (distFromStart > endZoneStart) return true;
        }

        return false;
    }
}
