using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
///     Calculates junction banking behavior based on road priority.
///     This is a CRITICAL component for road banking. At junctions, the existing system
///     carefully harmonizes elevations between meeting roads. Banking MUST work correctly
///     with this system to ensure safety and realism.
///     Priority Rules:
///     1. Higher-priority road (e.g., highway) MAINTAINS its banking
///     - A driver at 130 km/h should NOT suddenly hit a flat curve because a dirt road crosses!
///     2. Lower-priority road (e.g., dirt road) ADAPTS to match the higher-priority road's banked surface
///     - Edge elevations transition smoothly to meet the banked highway
///     3. Equal-priority roads both REDUCE banking to flat at their mutual junction
///     4. Endpoints (dead ends) fade banking to flat
///     This ensures highways remain safe at high speeds while secondary roads
///     smoothly transition to meet them.
/// </summary>
public class PriorityAwareJunctionBankingCalculator
{
    /// <summary>
    ///     Analyzes all junctions and sets banking behavior for each cross-section.
    ///     IMPORTANT: Must be called AFTER:
    ///     - Junction detection (network.Junctions populated)
    ///     - Elevation harmonization (TargetElevation set)
    ///     - Curvature calculation (Curvature set)
    ///     But BEFORE:
    ///     - Bank angle calculation (BankAngleRadians will be set based on behavior)
    /// </summary>
    /// <param name="network">The road network with detected junctions.</param>
    /// <param name="transitionDistanceMeters">
    ///     Distance over which banking transitions occur near junctions.
    ///     Default: 30.0m (matches typical junction blend distance).
    /// </param>
    public void CalculateJunctionBankingBehavior(
        UnifiedRoadNetwork network,
        float transitionDistanceMeters = 30.0f)
    {
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("PriorityAwareJunctionBankingCalculator");

        // Initialize all cross-sections to normal behavior
        foreach (var cs in network.CrossSections)
        {
            cs.JunctionBankingBehavior = JunctionBankingBehavior.Normal;
            cs.JunctionBankingFactor = 1.0f;
            cs.DistanceToNearestJunction = float.MaxValue;
            cs.HigherPrioritySplineId = null;
        }

        if (network.Junctions.Count == 0)
        {
            TerrainLogger.Info("PriorityAwareJunctionBankingCalculator: No junctions to process");
            return;
        }

        // Build spline lookup for quick access
        var splineById = network.Splines.ToDictionary(s => s.SplineId);

        // Build cross-sections by spline lookup for efficient access
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var junctionsProcessed = 0;

        // Process each junction
        foreach (var junction in network.Junctions)
        {
            // Skip excluded junctions (user can mark junctions to skip harmonization)
            if (junction.IsExcluded)
                continue;

            ProcessJunction(
                network,
                junction,
                splineById,
                crossSectionsBySpline,
                transitionDistanceMeters);

            junctionsProcessed++;
        }

        // Log statistics
        var behaviorCounts = network.CrossSections
            .GroupBy(cs => cs.JunctionBankingBehavior)
            .ToDictionary(g => g.Key, g => g.Count());

        TerrainLogger.Info(
            $"PriorityAwareJunctionBankingCalculator: Processed {junctionsProcessed} junctions, " +
            $"Cross-section behaviors: " +
            $"Normal={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.Normal)}, " +
            $"Maintain={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.MaintainBanking)}, " +
            $"Adapt={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.AdaptToHigherPriority)}, " +
            $"Suppress={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.SuppressBanking)}");

        perfLog?.Timing($"Calculated banking behavior for {network.CrossSections.Count} cross-sections");
    }

    /// <summary>
    ///     Processes a single junction to determine banking behavior for all participating roads.
    /// </summary>
    private void ProcessJunction(
        UnifiedRoadNetwork network,
        NetworkJunction junction,
        Dictionary<int, ParameterizedRoadSpline> splineById,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float transitionDistanceMeters)
    {
        var contributors = junction.Contributors;

        if (contributors.Count == 0)
            return;

        // Get participating spline IDs
        var participatingSplineIds = contributors.Select(c => c.Spline.SplineId).Distinct().ToList();

        // Handle endpoints (single spline at junction = dead end)
        if (junction.Type == JunctionType.Endpoint || participatingSplineIds.Count == 1)
        {
            ProcessEndpoint(junction, crossSectionsBySpline, transitionDistanceMeters);
            return;
        }

        // Find the highest priority among participating splines
        var priorityGroups = participatingSplineIds
            .Select(id => splineById.GetValueOrDefault(id))
            .Where(s => s != null)
            .GroupBy(s => s!.Priority)
            .OrderByDescending(g => g.Key)
            .ToList();

        if (priorityGroups.Count == 0)
            return;

        var highestPriority = priorityGroups[0].Key;
        var highestPrioritySplines = priorityGroups[0].Select(s => s!).ToList();

        // Check if there are multiple splines with the highest priority (equal priority case)
        var hasEqualPriorityConflict = highestPrioritySplines.Count > 1;

        // For equal-priority junctions, use a MUCH smaller transition distance.
        // When two roads of the same type meet, there's no safety concern - they both
        // have the same design speed and banking requirements. Using a small transition
        // (based on road width) prevents banking suppression from bleeding across the
        // entire road network when there are many junctions close together.
        var effectiveTransitionDistance = transitionDistanceMeters;
        if (hasEqualPriorityConflict)
        {
            // Use the maximum road width among participating roads as the transition distance
            // This ensures banking is only suppressed in the immediate junction area
            var maxRoadWidth = highestPrioritySplines.Max(s => s.Parameters.RoadWidthMeters);
            effectiveTransitionDistance = MathF.Max(maxRoadWidth * 1.5f, 10.0f);
            
            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId}: Equal priority conflict, using reduced transition distance {effectiveTransitionDistance:F1}m " +
                $"(from {transitionDistanceMeters:F1}m) for SuppressBanking behavior");
        }

        foreach (var splineId in participatingSplineIds)
        {
            var spline = splineById.GetValueOrDefault(splineId);
            if (spline == null)
                continue;

            if (!crossSectionsBySpline.TryGetValue(splineId, out var crossSections))
                continue;

            JunctionBankingBehavior behavior;
            int? higherPrioritySplineId = null;
            var useTransitionDistance = transitionDistanceMeters;

            if (spline.Priority == highestPriority)
            {
                if (hasEqualPriorityConflict)
                {
                    // Equal priority - all roads reduce banking at this junction
                    // Use the reduced transition distance for equal-priority junctions
                    behavior = JunctionBankingBehavior.SuppressBanking;
                    useTransitionDistance = effectiveTransitionDistance;
                }
                else
                {
                    // This is THE highest priority road - maintain full banking
                    behavior = JunctionBankingBehavior.MaintainBanking;
                }
            }
            else
            {
                // Lower priority - adapt to the highest priority road
                behavior = JunctionBankingBehavior.AdaptToHigherPriority;
                higherPrioritySplineId = highestPrioritySplines[0].SplineId;
            }

            // Apply behavior to cross-sections near this junction
            ApplyBehaviorToNearbyCrossSections(
                crossSections,
                junction.Position,
                behavior,
                higherPrioritySplineId,
                useTransitionDistance);
        }
    }

    /// <summary>
    ///     Processes an endpoint junction (dead end) - banking fades to flat.
    /// </summary>
    private void ProcessEndpoint(
        NetworkJunction junction,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float transitionDistanceMeters)
    {
        foreach (var contributor in junction.Contributors)
        {
            var splineId = contributor.Spline.SplineId;

            if (!crossSectionsBySpline.TryGetValue(splineId, out var crossSections))
                continue;

            // Endpoints always suppress banking (fade to flat at dead end)
            ApplyBehaviorToNearbyCrossSections(
                crossSections,
                junction.Position,
                JunctionBankingBehavior.SuppressBanking,
                null,
                transitionDistanceMeters);
        }
    }

    /// <summary>
    ///     Applies banking behavior to cross-sections near a junction.
    ///     Uses smooth cosine interpolation for gradual transitions.
    /// </summary>
    private void ApplyBehaviorToNearbyCrossSections(
        List<UnifiedCrossSection> crossSections,
        Vector2 junctionPosition,
        JunctionBankingBehavior behavior,
        int? higherPrioritySplineId,
        float transitionDistanceMeters)
    {
        foreach (var cs in crossSections)
        {
            var distToJunction = Vector2.Distance(cs.CenterPoint, junctionPosition);

            // Only affect cross-sections within transition distance
            if (distToJunction > transitionDistanceMeters)
                continue;

            // Track closest junction for this cross-section
            if (distToJunction < cs.DistanceToNearestJunction) cs.DistanceToNearestJunction = distToJunction;

            // Calculate transition factor
            // Raw factor: 0 at junction center, 1 at transition boundary
            var rawFactor = distToJunction / transitionDistanceMeters;

            // Apply smooth cosine interpolation for gradual transition
            // This creates an S-curve: slow start, fast middle, slow end
            // transitionFactor: 0 = at junction (full junction behavior), 1 = far from junction (normal)
            var transitionFactor = 0.5f - 0.5f * MathF.Cos(rawFactor * MathF.PI);

            // Only override if this junction has stronger influence
            // (closer junction wins, or if this is the first junction affecting this CS)
            var existingInfluence = 1.0f - cs.JunctionBankingFactor;
            var newInfluence = 1.0f - transitionFactor;

            if (newInfluence > existingInfluence ||
                cs.JunctionBankingBehavior == JunctionBankingBehavior.Normal)
            {
                cs.JunctionBankingBehavior = behavior;
                cs.JunctionBankingFactor = transitionFactor;
                cs.HigherPrioritySplineId = higherPrioritySplineId;
            }
        }
    }

    /// <summary>
    ///     Gets detailed statistics about junction banking behavior in the network.
    ///     Useful for debugging and verification.
    /// </summary>
    /// <param name="network">The road network after banking behavior calculation.</param>
    /// <returns>Dictionary of behavior type to list of affected spline IDs.</returns>
    public static Dictionary<JunctionBankingBehavior, List<int>> GetBehaviorStatistics(
        UnifiedRoadNetwork network)
    {
        return network.CrossSections
            .GroupBy(cs => cs.JunctionBankingBehavior)
            .ToDictionary(
                g => g.Key,
                g => g.Select(cs => cs.OwnerSplineId).Distinct().ToList());
    }

    /// <summary>
    ///     Finds cross-sections that are adapting to a higher-priority road.
    ///     Useful for debugging edge elevation adaptation.
    /// </summary>
    /// <param name="network">The road network after banking behavior calculation.</param>
    /// <returns>List of (adapting cross-section, higher-priority spline ID) pairs.</returns>
    public static List<(UnifiedCrossSection AdaptingCS, int HigherPrioritySplineId)> GetAdaptingCrossSections(
        UnifiedRoadNetwork network)
    {
        return network.CrossSections
            .Where(cs => cs.JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority &&
                         cs.HigherPrioritySplineId.HasValue)
            .Select(cs => (cs, cs.HigherPrioritySplineId!.Value))
            .ToList();
    }
}