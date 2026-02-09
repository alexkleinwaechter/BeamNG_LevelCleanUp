using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Converts crossroad junctions (where two roads cross without either terminating)
/// into T-junctions by SPLITTING the secondary road at the crossing point.
/// 
/// Problem:
/// At a crossroad junction, both roads continue through (neither terminates).
/// The secondary road's cross-sections in the overlap zone don't follow the primary
/// road's surface elevation, causing elevation discontinuities ("cliffs").
/// 
/// Solution:
/// SPLIT the secondary road into two separate splines at the crossing point:
/// - The PRIMARY road (higher priority or longer) passes through continuously
/// - The SECONDARY road is physically split into two splines, each with a REAL
///   endpoint at the junction
/// 
/// This creates TWO T-junctions from one crossroad:
/// - T-junction 1: Primary road continuous + Secondary road segment A terminating
/// - T-junction 2: Primary road continuous + Secondary road segment B terminating
/// 
/// Benefits of actual splitting vs virtual endpoints:
/// - No special-case code needed - existing T-junction logic handles everything
/// - Real IsSplineStart/IsSplineEnd flags are set correctly
/// - All existing harmonization, banking, and blending code works as-is
/// 
/// The conversion must happen BEFORE elevation harmonization and terrain blending.
/// </summary>
public class CrossroadToTJunctionConverter
{
    /// <summary>
    /// Counter for generating unique spline IDs for split segments.
    /// </summary>
    private int _nextSplineId;

    /// <summary>
    /// Converts all MidSplineCrossing junctions to T-junctions by splitting secondary roads.
    /// This should be called after junction detection but before elevation harmonization.
    /// </summary>
    /// <param name="network">The unified road network with detected junctions.</param>
    /// <param name="globalBlendDistance">Global blend distance (not used in splitting, kept for API compatibility).</param>
    /// <returns>Number of crossings converted to T-junctions.</returns>
    public int ConvertCrossroadsToTJunctions(
        UnifiedRoadNetwork network,
        float globalBlendDistance)
    {
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("CrossroadToTJunctionConverter");

        // Initialize spline ID counter to be higher than any existing spline
        _nextSplineId = network.Splines.Count > 0 
            ? network.Splines.Max(s => s.SplineId) + 1 
            : 1;

        var midSplineCrossings = network.Junctions
            .Where(j => j.Type == JunctionType.MidSplineCrossing && !j.IsExcluded)
            .ToList();

        if (midSplineCrossings.Count == 0)
        {
            TerrainLogger.Detail("No mid-spline crossings to convert");
            return 0;
        }

        TerrainLogger.Info($"Converting {midSplineCrossings.Count} mid-spline crossing(s) to T-junctions by splitting secondary roads...");

        // Build cross-section lookup for efficient access
        var crossSectionsBySpline = network.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.DistanceAlongSpline).ToList());

        var convertedCount = 0;
        var junctionsToRemove = new List<NetworkJunction>();
        var junctionsToAdd = new List<NetworkJunction>();

        foreach (var junction in midSplineCrossings)
        {
            var result = ConvertSingleCrossing(junction, network, crossSectionsBySpline);
            if (result.Success)
            {
                convertedCount++;
                junctionsToRemove.Add(junction);
                junctionsToAdd.AddRange(result.NewJunctions);
            }
        }

        // Remove old MidSplineCrossing junctions and add new T-junctions
        foreach (var oldJunction in junctionsToRemove)
        {
            network.Junctions.Remove(oldJunction);
        }
        network.Junctions.AddRange(junctionsToAdd);

        // Re-assign junction IDs
        for (int i = 0; i < network.Junctions.Count; i++)
        {
            network.Junctions[i].JunctionId = i;
        }

        TerrainLogger.Info($"Converted {convertedCount} mid-spline crossing(s) to {junctionsToAdd.Count} T-junction(s)");
        perfLog?.Timing($"Converted {convertedCount} crossings, created {junctionsToAdd.Count} T-junctions");

        return convertedCount;
    }

    /// <summary>
    /// Result of converting a single crossing.
    /// </summary>
    private class ConversionResult
    {
        public bool Success { get; init; }
        public List<NetworkJunction> NewJunctions { get; init; } = [];
    }

    /// <summary>
    /// Converts a single MidSplineCrossing junction by splitting the secondary road.
    /// </summary>
    private ConversionResult ConvertSingleCrossing(
        NetworkJunction junction,
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        if (junction.Contributors.Count < 2)
        {
            TerrainLogger.Warning($"Junction #{junction.JunctionId}: Expected 2+ contributors for MidSplineCrossing, got {junction.Contributors.Count}");
            return new ConversionResult { Success = false };
        }

        // Determine primary and secondary roads
        var (primaryContributor, secondaryContributors) = DeterminePrimaryAndSecondaryRoads(junction);

        if (primaryContributor == null || secondaryContributors.Count == 0)
        {
            TerrainLogger.Warning($"Junction #{junction.JunctionId}: Could not determine primary/secondary roads");
            return new ConversionResult { Success = false };
        }

        var primarySpline = primaryContributor.Spline;
        var primaryCs = primaryContributor.CrossSection;

        // Use file-only logging for per-junction details to avoid UI spam
        TerrainCreationLogger.Current?.Detail(
            $"Junction #{junction.JunctionId}: Primary road = Spline {primarySpline.SplineId} " +
            $"(priority={primarySpline.Priority}, length={primarySpline.TotalLengthMeters:F0}m), " +
            $"Secondary roads = [{string.Join(", ", secondaryContributors.Select(c => c.Spline.SplineId))}]");

        var newJunctions = new List<NetworkJunction>();

        // For each secondary road, split it at the crossing and create T-junctions
        foreach (var secondaryContributor in secondaryContributors)
        {
            var secondarySpline = secondaryContributor.Spline;
            var crossingCs = secondaryContributor.CrossSection;

            if (!crossSectionsBySpline.TryGetValue(secondarySpline.SplineId, out var secondarySections))
            {
                TerrainLogger.Warning($"Junction #{junction.JunctionId}: No cross-sections found for secondary spline {secondarySpline.SplineId}");
                continue;
            }

            // Find the crossing index
            var crossingIndex = FindCrossingIndex(crossingCs, secondarySections);
            if (crossingIndex < 0)
            {
                TerrainLogger.Warning($"Junction #{junction.JunctionId}: Could not find crossing index in secondary road");
                continue;
            }

            // Split the secondary road's cross-sections into two segments
            var (segmentA, segmentB) = SplitCrossSectionsAtIndex(secondarySections, crossingIndex);

            if (segmentA.Count < 2 || segmentB.Count < 2)
            {
                TerrainLogger.Warning($"Junction #{junction.JunctionId}: Split would create segments too small (A={segmentA.Count}, B={segmentB.Count})");
                continue;
            }

            // Mark the original cross-sections' endpoints correctly
            // Segment A: starts at original start, ENDS at crossing
            // Segment B: STARTS at crossing, ends at original end
            MarkSegmentEndpoints(segmentA, segmentB);

            // Create T-junction for segment A (ending at crossing)
            var tJunctionA = CreateTJunctionForSegment(
                junction.Position,
                primaryContributor,
                secondarySpline,
                segmentA.Last(), // The new endpoint
                isSegmentStart: false);
            newJunctions.Add(tJunctionA);

            // Create T-junction for segment B (starting at crossing)
            var tJunctionB = CreateTJunctionForSegment(
                junction.Position,
                primaryContributor,
                secondarySpline,
                segmentB.First(), // The new endpoint
                isSegmentStart: true);
            newJunctions.Add(tJunctionB);

            // Use file-only logging for per-junction split details
            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId}: Split spline {secondarySpline.SplineId} at index {crossingIndex}. " +
                $"Segment A: {segmentA.Count} CS (ends at crossing), " +
                $"Segment B: {segmentB.Count} CS (starts at crossing)");
        }

        return new ConversionResult
        {
            Success = newJunctions.Count > 0,
            NewJunctions = newJunctions
        };
    }

    /// <summary>
    /// Finds the index of the crossing cross-section in the list.
    /// </summary>
    private int FindCrossingIndex(UnifiedCrossSection crossingCs, List<UnifiedCrossSection> sections)
    {
        var crossingIndex = sections.FindIndex(cs => cs.Index == crossingCs.Index);
        if (crossingIndex >= 0)
            return crossingIndex;

        // Find closest by position
        var minDist = float.MaxValue;
        for (int i = 0; i < sections.Count; i++)
        {
            var dist = Vector2.Distance(sections[i].CenterPoint, crossingCs.CenterPoint);
            if (dist < minDist)
            {
                minDist = dist;
                crossingIndex = i;
            }
        }

        return crossingIndex;
    }

    /// <summary>
    /// Splits the cross-sections into two segments at the given index.
    /// The crossing cross-section is INCLUDED in BOTH segments (it becomes the endpoint of each).
    /// </summary>
    private (List<UnifiedCrossSection> segmentA, List<UnifiedCrossSection> segmentB) SplitCrossSectionsAtIndex(
        List<UnifiedCrossSection> sections,
        int crossingIndex)
    {
        // Segment A: from start to crossing (inclusive)
        var segmentA = sections.Take(crossingIndex + 1).ToList();

        // Segment B: from crossing to end (inclusive)
        var segmentB = sections.Skip(crossingIndex).ToList();

        return (segmentA, segmentB);
    }

    /// <summary>
    /// Marks the endpoint flags on the cross-sections for the split segments.
    /// </summary>
    private void MarkSegmentEndpoints(
        List<UnifiedCrossSection> segmentA,
        List<UnifiedCrossSection> segmentB)
    {
        // The first CS of segmentA keeps its original IsSplineStart status
        // The LAST CS of segmentA is now an endpoint (was mid-spline, now ends here)
        if (segmentA.Count > 0)
        {
            var endOfA = segmentA.Last();
            endOfA.IsSplineEnd = true;
            // Clear start flag if it was somehow set (shouldn't be, but be safe)
            if (endOfA.IsSplineStart && segmentA.Count > 1)
                endOfA.IsSplineStart = false;
        }

        // The FIRST CS of segmentB is now an endpoint (was mid-spline, now starts here)
        // The last CS of segmentB keeps its original IsSplineEnd status
        if (segmentB.Count > 0)
        {
            var startOfB = segmentB.First();
            startOfB.IsSplineStart = true;
            // Clear end flag if it was somehow set
            if (startOfB.IsSplineEnd && segmentB.Count > 1)
                startOfB.IsSplineEnd = false;
        }
    }

    /// <summary>
    /// Creates a T-junction for a segment that now terminates at the crossing.
    /// </summary>
    private NetworkJunction CreateTJunctionForSegment(
        Vector2 junctionPosition,
        JunctionContributor primaryContributor,
        ParameterizedRoadSpline secondarySpline,
        UnifiedCrossSection newEndpointCs,
        bool isSegmentStart)
    {
        var junction = new NetworkJunction
        {
            Position = junctionPosition,
            Type = JunctionType.TJunction
        };

        // Add the primary road as continuous contributor
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = primaryContributor.CrossSection,
            Spline = primaryContributor.Spline,
            IsSplineStart = false,
            IsSplineEnd = false
            // IsContinuous = true (because neither IsSplineStart nor IsSplineEnd)
        });

        // Add the secondary road segment as terminating contributor
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = newEndpointCs,
            Spline = secondarySpline,
            IsSplineStart = isSegmentStart,
            IsSplineEnd = !isSegmentStart
            // IsEndpoint = true, IsContinuous = false
        });

        junction.CalculateCentroid();

        return junction;
    }

    /// <summary>
    /// Determines which road is the primary (continuous) road and which are secondary (to be split).
    /// 
    /// Priority rules:
    /// 1. Higher priority value wins (from OSM road type or road width)
    /// 2. If equal priority, longer road wins
    /// 3. If still tied, first in the list wins (deterministic)
    /// </summary>
    private (JunctionContributor? primary, List<JunctionContributor> secondary) DeterminePrimaryAndSecondaryRoads(
        NetworkJunction junction)
    {
        var contributors = junction.Contributors.ToList();

        if (contributors.Count < 2)
            return (null, []);

        // Sort by priority (descending), then by length (descending), then by spline ID (ascending for determinism)
        var sorted = contributors
            .OrderByDescending(c => c.Spline.Priority)
            .ThenByDescending(c => c.Spline.TotalLengthMeters)
            .ThenBy(c => c.Spline.SplineId)
            .ToList();

        var primary = sorted[0];
        var secondary = sorted.Skip(1).ToList();

        // Log if there's a tie that was resolved by length or spline ID
        if (contributors.Count >= 2)
        {
            var first = sorted[0];
            var second = sorted[1];

            if (first.Spline.Priority == second.Spline.Priority)
            {
                if (Math.Abs(first.Spline.TotalLengthMeters - second.Spline.TotalLengthMeters) < 1.0f)
                {
                    TerrainCreationLogger.Current?.Detail(
                        $"Junction #{junction.JunctionId}: Equal priority AND length - " +
                        $"using spline ID {first.Spline.SplineId} < {second.Spline.SplineId} as tiebreaker");
                }
                else
                {
                    TerrainCreationLogger.Current?.Detail(
                        $"Junction #{junction.JunctionId}: Equal priority - using length " +
                        $"({first.Spline.TotalLengthMeters:F0}m > {second.Spline.TotalLengthMeters:F0}m) as tiebreaker");
                }
            }
        }

        return (primary, secondary);
    }
}
