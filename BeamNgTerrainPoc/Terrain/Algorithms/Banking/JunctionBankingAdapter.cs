using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
///     Adapts lower-priority road elevations to smoothly meet banked higher-priority road surfaces.
///     Also handles smooth transitions at equal-priority junctions where banking is suppressed.
///     The Problem:
///     When a secondary road meets a banked primary road, the junction harmonization sets the
///     secondary road's TargetElevation based on the primary road's centerline. However, the
///     actual surface of the banked primary road at the intersection point may be at a different
///     elevation (higher or lower depending on which side of the primary road the secondary
///     road connects to).
///     Additionally, at equal-priority junctions where banking is suppressed, the transition
///     from banked to flat needs to be smooth to avoid artifacts.
///     The Solution:
///     1. Find where the secondary road intersects the primary road
///     2. Calculate the primary road's surface elevation at that intersection point (considering banking)
///     3. Smoothly ramp the secondary road's elevation from its original path to this intersection elevation
///     4. For equal-priority junctions, ensure smooth elevation continuity across the junction
///     This creates a smooth transition without "speed bumps" at junctions.
/// </summary>
public class JunctionBankingAdapter
{
    /// <summary>
    ///     Adapts cross-section elevations for roads at junctions with banking considerations.
    ///     Must be called AFTER:
    ///     - Junction detection
    ///     - Junction elevation harmonization
    ///     - Banking calculation (bank angles computed)
    ///     This method modifies TargetElevation for:
    ///     - Cross-sections marked as AdaptToHigherPriority (smooth ramp to banked surface)
    ///     - Cross-sections marked as SuppressBanking (ensure smooth transition)
    /// </summary>
    /// <param name="network">The road network with computed banking</param>
    /// <param name="transitionDistanceMeters">Distance over which elevation adapts (should match junction blend distance)</param>
    /// <returns>Number of cross-sections whose elevation was adjusted</returns>
    public int AdaptElevationsToHigherPriorityBanking(
        UnifiedRoadNetwork network,
        float transitionDistanceMeters = 30.0f)
    {
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("JunctionBankingAdapter");

        // Build lookup structures
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var splineById = network.Splines.ToDictionary(s => s.SplineId);

        var totalAdjusted = 0;

        // Part 1: Handle AdaptToHigherPriority cross-sections
        totalAdjusted += AdaptToHigherPriorityElevations(
            network, crossSectionsBySpline, splineById, transitionDistanceMeters);

        // Part 2: Handle SuppressBanking cross-sections (equal priority junctions)
        totalAdjusted += SmoothSuppressedBankingTransitions(
            network, crossSectionsBySpline, transitionDistanceMeters);

        TerrainLogger.Info($"JunctionBankingAdapter: Adjusted elevation for {totalAdjusted} cross-sections total");
        perfLog?.Timing($"Adapted {totalAdjusted} cross-section elevations for banking");

        return totalAdjusted;
    }

    /// <summary>
    ///     Adapts elevations for roads connecting to higher-priority banked roads.
    ///     CRITICAL FIX: The key insight is that we need to:
    ///     1. Find where the secondary road connects to the primary road's SURFACE (not centerline)
    ///     2. Adjust the secondary road's elevation to match that surface elevation at the junction
    ///     3. Apply a smooth ramp from the junction back along the secondary road
    /// </summary>
    private int AdaptToHigherPriorityElevations(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<int, ParameterizedRoadSpline> splineById,
        float transitionDistanceMeters)
    {
        // Find all cross-sections that need adaptation
        var adaptingCrossSections = network.CrossSections
            .Where(cs => cs.JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority
                         && cs.HigherPrioritySplineId.HasValue)
            .ToList();

        if (adaptingCrossSections.Count == 0)
        {
            TerrainLogger.Info("JunctionBankingAdapter: No cross-sections need higher-priority banking adaptation");
            return 0;
        }

        TerrainLogger.Info(
            $"JunctionBankingAdapter: Adapting elevations for {adaptingCrossSections.Count} cross-sections to higher-priority roads...");

        var adjustedCount = 0;

        // Group by spline for efficient processing
        var adaptingBySpline = adaptingCrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (splineId, adaptingCS) in adaptingBySpline)
        {
            if (!crossSectionsBySpline.TryGetValue(splineId, out var allSplineCS))
                continue;

            // Get the higher-priority spline ID (should be same for all in this group)
            var higherPrioritySplineId = adaptingCS.First().HigherPrioritySplineId!.Value;
            if (!crossSectionsBySpline.TryGetValue(higherPrioritySplineId, out var higherPriorityCS))
                continue;

            // Find the junction point (the adapting CS closest to the higher-priority road)
            var junctionCS = adaptingCS
                .OrderBy(cs => GetMinDistanceToSpline(cs.CenterPoint, higherPriorityCS))
                .First();

            // Find where this road intersects the higher-priority road
            var (nearestHigherCS, intersectionPoint) = FindIntersectionWithHigherPriorityRoad(
                junctionCS, higherPriorityCS);

            if (nearestHigherCS == null)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"Spline {splineId}: Could not find intersection with higher-priority road {higherPrioritySplineId}");
                continue;
            }

            // CRITICAL: Calculate the surface elevation at the ACTUAL intersection point
            // This accounts for banking - we want the elevation where we actually connect
            var surfaceElevationAtIntersection = BankedTerrainHelper.GetBankedElevation(
                nearestHigherCS, junctionCS.CenterPoint);

            if (float.IsNaN(surfaceElevationAtIntersection))
                surfaceElevationAtIntersection = nearestHigherCS.TargetElevation;

            // Calculate how much the elevation differs from what was set by junction harmonization
            var currentJunctionElevation = junctionCS.TargetElevation;
            var elevationDifference = surfaceElevationAtIntersection - currentJunctionElevation;

            TerrainCreationLogger.Current?.Detail(
                $"Spline {splineId} -> Higher {higherPrioritySplineId}: " +
                $"junction elev={currentJunctionElevation:F2}m, " +
                $"surface elev={surfaceElevationAtIntersection:F2}m, " +
                $"diff={elevationDifference:F2}m, " +
                $"primary bank angle={nearestHigherCS.BankAngleRadians * 180 / MathF.PI:F1}°");

            // If the difference is negligible, skip
            if (MathF.Abs(elevationDifference) < 0.01f)
                continue;

            // Apply smooth elevation ramp to all cross-sections in the transition zone
            adjustedCount += ApplyElevationRamp(
                allSplineCS,
                junctionCS,
                surfaceElevationAtIntersection,
                transitionDistanceMeters,
                JunctionBankingBehavior.AdaptToHigherPriority);
        }

        return adjustedCount;
    }

    /// <summary>
    ///     Smooths transitions at equal-priority junctions where banking is suppressed.
    ///     This ensures there are no artifacts when two banked roads meet and both reduce to flat.
    /// </summary>
    private int SmoothSuppressedBankingTransitions(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float transitionDistanceMeters)
    {
        // Find junctions where banking is suppressed (equal priority or endpoints)
        var suppressedJunctions = network.Junctions
            .Where(j => !j.IsExcluded && j.Contributors.Count >= 2)
            .Where(j => j.Contributors.All(c =>
            {
                var cs = crossSectionsBySpline.GetValueOrDefault(c.Spline.SplineId)?
                    .FirstOrDefault(x => Vector2.Distance(x.CenterPoint, j.Position) < transitionDistanceMeters);
                return cs?.JunctionBankingBehavior == JunctionBankingBehavior.SuppressBanking;
            }))
            .ToList();

        if (suppressedJunctions.Count == 0)
            return 0;

        TerrainLogger.Info(
            $"JunctionBankingAdapter: Smoothing {suppressedJunctions.Count} equal-priority junction(s)...");

        var adjustedCount = 0;

        foreach (var junction in suppressedJunctions)
        {
            // For equal-priority junctions, we need to ensure all roads meet at a common elevation
            // that represents an average of their surface elevations at the junction point

            var contributorElevations = new List<(int splineId, float elevation)>();

            foreach (var contributor in junction.Contributors)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineCS))
                    continue;

                // Find the cross-section closest to the junction
                var nearestCS = splineCS
                    .OrderBy(cs => Vector2.Distance(cs.CenterPoint, junction.Position))
                    .FirstOrDefault();

                if (nearestCS == null)
                    continue;

                // Calculate surface elevation at junction point (even if banking is being suppressed,
                // we need to know what the "natural" banked elevation would have been)
                var surfaceElev = nearestCS.TargetElevation;
                contributorElevations.Add((contributor.Spline.SplineId, surfaceElev));
            }

            if (contributorElevations.Count < 2)
                continue;

            // Use the average elevation at the junction
            var targetJunctionElevation = contributorElevations.Average(c => c.elevation);
            var maxDiff = contributorElevations.Max(c => MathF.Abs(c.elevation - targetJunctionElevation));

            // If all roads are already close to the target, no adjustment needed
            if (maxDiff < 0.05f)
                continue;

            TerrainCreationLogger.Current?.Detail(
                $"Junction at ({junction.Position.X:F0},{junction.Position.Y:F0}): " +
                $"target elev={targetJunctionElevation:F2}m, max diff={maxDiff:F2}m");

            // Apply smooth transition for each contributor
            foreach (var (splineId, originalElev) in contributorElevations)
            {
                if (!crossSectionsBySpline.TryGetValue(splineId, out var splineCS))
                    continue;

                var elevDiff = targetJunctionElevation - originalElev;
                if (MathF.Abs(elevDiff) < 0.01f)
                    continue;

                // Find the junction cross-section
                var junctionCS = splineCS
                    .OrderBy(cs => Vector2.Distance(cs.CenterPoint, junction.Position))
                    .First();

                adjustedCount += ApplyElevationRamp(
                    splineCS,
                    junctionCS,
                    targetJunctionElevation,
                    transitionDistanceMeters,
                    JunctionBankingBehavior.SuppressBanking);
            }
        }

        return adjustedCount;
    }

    /// <summary>
    ///     Finds the intersection point between an adapting road and the higher-priority road.
    /// </summary>
    private (UnifiedCrossSection? nearestCS, Vector2 intersectionPoint) FindIntersectionWithHigherPriorityRoad(
        UnifiedCrossSection junctionCS,
        List<UnifiedCrossSection> higherPriorityCS)
    {
        // Find the nearest cross-section on the higher-priority road
        UnifiedCrossSection? nearestCS = null;
        var minDist = float.MaxValue;

        foreach (var cs in higherPriorityCS)
        {
            var dist = Vector2.Distance(junctionCS.CenterPoint, cs.CenterPoint);
            if (dist < minDist)
            {
                minDist = dist;
                nearestCS = cs;
            }
        }

        if (nearestCS == null)
            return (null, Vector2.Zero);

        // Project the junction point onto the higher-priority road's cross-section line
        // This gives us the actual intersection point on the banked surface
        var intersectionPoint = ProjectPointOntoCrossSection(junctionCS.CenterPoint, nearestCS);

        return (nearestCS, intersectionPoint);
    }

    /// <summary>
    ///     Projects a point onto a cross-section line (the line from left edge to right edge).
    ///     Returns the closest point on the cross-section line.
    /// </summary>
    private Vector2 ProjectPointOntoCrossSection(Vector2 point, UnifiedCrossSection cs)
    {
        // The cross-section line runs from (center - normal*halfWidth) to (center + normal*halfWidth)
        // We want to find the point on this line closest to 'point'

        // Calculate lateral offset (signed distance from centerline in the normal direction)
        var toPoint = point - cs.CenterPoint;
        var lateralOffset = Vector2.Dot(toPoint, cs.NormalDirection);

        // Clamp to road width
        var halfWidth = cs.EffectiveRoadWidth / 2.0f;
        lateralOffset = Math.Clamp(lateralOffset, -halfWidth, halfWidth);

        // Return the point on the cross-section at this offset
        return cs.CenterPoint + cs.NormalDirection * lateralOffset;
    }

    /// <summary>
    ///     Applies a smooth elevation ramp from the junction point back along the adapting road.
    /// </summary>
    private int ApplyElevationRamp(
        List<UnifiedCrossSection> splineCS,
        UnifiedCrossSection junctionCS,
        float targetJunctionElevation,
        float transitionDistanceMeters,
        JunctionBankingBehavior targetBehavior)
    {
        var adjustedCount = 0;

        // Find the junction cross-section index
        var junctionIndex = splineCS.FindIndex(cs => cs.Index == junctionCS.Index);
        if (junctionIndex < 0)
        {
            // Find by position if index doesn't match
            var minDist = float.MaxValue;
            for (var i = 0; i < splineCS.Count; i++)
            {
                var dist = Vector2.Distance(splineCS[i].CenterPoint, junctionCS.CenterPoint);
                if (dist < minDist)
                {
                    minDist = dist;
                    junctionIndex = i;
                }
            }
        }

        if (junctionIndex < 0)
            return 0;

        // Calculate cumulative distances from the junction point (in both directions)
        var distances = CalculateDistancesFromJunction(splineCS, junctionIndex);

        // Apply the elevation ramp
        for (var i = 0; i < splineCS.Count; i++)
        {
            var cs = splineCS[i];
            var distFromJunction = distances[i];

            // Only modify cross-sections within the transition zone
            if (distFromJunction > transitionDistanceMeters)
                continue;

            // Only modify cross-sections with the matching behavior
            if (cs.JunctionBankingBehavior != targetBehavior)
                continue;

            var originalElevation = cs.TargetElevation;
            if (float.IsNaN(originalElevation))
                continue;

            // Calculate blend factor using smooth cosine interpolation
            // t = 0 at junction, t = 1 at transition boundary
            var t = distFromJunction / transitionDistanceMeters;

            // Use quintic smoothstep for very smooth transition (zero first and second derivatives at endpoints)
            var blend = t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);

            // Calculate the elevation offset needed
            // At junction (blend=0): full offset to reach targetJunctionElevation
            // Far from junction (blend=1): no offset (keep original elevation)
            var baseElevationAtJunction = splineCS[junctionIndex].TargetElevation;
            var elevationOffset = targetJunctionElevation - baseElevationAtJunction;
            var adjustedOffset = elevationOffset * (1.0f - blend);

            var newElevation = originalElevation + adjustedOffset;

            if (MathF.Abs(newElevation - originalElevation) > 0.001f)
            {
                cs.TargetElevation = newElevation;
                adjustedCount++;
            }
        }

        return adjustedCount;
    }

    /// <summary>
    ///     Calculates the distance from each cross-section to the junction point.
    ///     Uses cumulative distance along the spline path.
    /// </summary>
    private float[] CalculateDistancesFromJunction(List<UnifiedCrossSection> splineCS, int junctionIndex)
    {
        var distances = new float[splineCS.Count];

        // Calculate distances toward the start of the spline
        distances[junctionIndex] = 0;
        for (var i = junctionIndex - 1; i >= 0; i--)
            distances[i] = distances[i + 1] +
                           Vector2.Distance(splineCS[i].CenterPoint, splineCS[i + 1].CenterPoint);

        // Calculate distances toward the end of the spline
        for (var i = junctionIndex + 1; i < splineCS.Count; i++)
            distances[i] = distances[i - 1] +
                           Vector2.Distance(splineCS[i].CenterPoint, splineCS[i - 1].CenterPoint);

        return distances;
    }

    /// <summary>
    ///     Gets the minimum distance from a point to any cross-section on a spline.
    /// </summary>
    private static float GetMinDistanceToSpline(Vector2 point, List<UnifiedCrossSection> splineCS)
    {
        var minDist = float.MaxValue;
        foreach (var cs in splineCS)
        {
            var dist = Vector2.Distance(point, cs.CenterPoint);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }
}