using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Harmonizes elevations at junctions across the entire unified road network.
///     This harmonizer handles:
///     - T-junctions: Continuous road "wins", terminating road adapts
///     - Y/X junctions: Weighted average based on priority and angle
///     - Complex intersections: Priority-weighted elevation resolution
///     - Isolated endpoints: Taper toward terrain elevation
///     Operates on the unified network, enabling cross-material junction harmonization.
/// </summary>
public class NetworkJunctionHarmonizer
{
    /// <summary>
    ///     Small elevation difference threshold for determining if gradient ramp is needed.
    ///     If elevation difference is less than this, use weighted average instead.
    /// </summary>
    private const float SmallElevationDifferenceMeters = 0.5f;

    private readonly NetworkJunctionDetector _detector;

    /// <summary>
    ///     Cross-sections grouped by spline ID for slope calculations.
    ///     Built at the start of harmonization for efficient lookups.
    /// </summary>
    private Dictionary<int, List<UnifiedCrossSection>>? _crossSectionsBySpline;

    /// <summary>
    ///     The current network being processed. Set during HarmonizeNetwork.
    /// </summary>
    private UnifiedRoadNetwork? _currentNetwork;

    public NetworkJunctionHarmonizer()
    {
        _detector = new NetworkJunctionDetector();
    }

    /// <summary>
    ///     Harmonizes elevations across the entire unified road network.
    ///     Algorithm:
    ///     1. Detect all junctions (if not already detected)
    ///     2. Sort by priority (handle highest-priority roads first)
    ///     3. Compute harmonized elevation for each junction based on its type
    /// </summary>
    /// <param name="network">The unified road network with calculated target elevations.</param>
    /// <param name="heightMap">The original terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <param name="skipDetection">When true, reuses existing junctions (for iterative refinement).</param>
    public HarmonizationResult HarmonizeNetwork(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        bool skipDetection = false)
    {
        var result = new HarmonizationResult();
        var perfLog = TerrainCreationLogger.Current;

        if (network.CrossSections.Count == 0)
        {
            TerrainLogger.Info("NetworkJunctionHarmonizer: No cross-sections to harmonize");
            return result;
        }

        // Store network reference and build cross-section lookup for slope calculations
        _currentNetwork = network;
        _crossSectionsBySpline = network.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.DistanceAlongSpline).ToList());

        perfLog?.LogSection("NetworkJunctionHarmonizer");
        TerrainLogger.Info("=== UNIFIED NETWORK JUNCTION HARMONIZATION ===");
        if (skipDetection)
            TerrainLogger.Detail("  skipDetection=true: reusing existing junctions");

        // Capture pre-harmonization elevations for comparison
        var preHarmonizationElevations = CaptureElevations(network);
        result.PreHarmonizationElevations = preHarmonizationElevations;

        List<NetworkJunction> junctions;

        if (skipDetection)
        {
            // Iterative refinement: reuse already-detected junctions, skip detection and crossroad conversion
            junctions = network.Junctions.ToList();
            TerrainLogger.Detail($"  Reusing {junctions.Count} existing junction(s) (iterative refinement)");
        }
        else
        {
            // Step 1: Get junctions from network
            // Junction detection may have already been run by UnifiedRoadSmoother.
            // In that case, network.Junctions already contains detected junctions.
            //
            // The detection flow is:
            // 1. UnifiedRoadSmoother calls DetectJunctions()
            // 2. That populates network.Junctions with detected junctions
            // 3. HarmonizeNetwork is called - it should USE those junctions, not re-detect
            //
            // We only re-run detection if no regular junctions exist yet (only roundabout junctions
            // from Phase 2.6 might exist before general detection runs).

            // Check if we already have regular (non-roundabout) junctions detected
            var existingRegularJunctions = network.Junctions
                .Where(j => j.Type != JunctionType.Roundabout)
                .ToList();

            var existingRoundaboutJunctions = network.Junctions
                .Where(j => j.Type == JunctionType.Roundabout)
                .ToList();

            if (existingRegularJunctions.Count > 0)
            {
                // Junctions were already detected - use them as-is
                junctions = network.Junctions.ToList();

                TerrainLogger.Detail($"  Using {junctions.Count} pre-detected junction(s) " +
                                     $"({existingRoundaboutJunctions.Count} roundabout, " +
                                     $"{existingRegularJunctions.Count} regular)");
            }
            else
            {
                // No regular junctions yet - run detection now
                TerrainLogger.Detail("  No pre-detected junctions found, running detection...");

                if (existingRoundaboutJunctions.Count > 0)
                    TerrainLogger.Detail(
                        $"  Preserving {existingRoundaboutJunctions.Count} existing roundabout junction(s)");

                // Run standard junction detection
                var detectedJunctions = _detector.DetectJunctions(network);
                TerrainLogger.Detail($"  Detected {detectedJunctions.Count} regular junction(s)");

                // Merge: combine detected junctions with preserved roundabout junctions
                junctions = detectedJunctions;

                foreach (var roundaboutJunction in existingRoundaboutJunctions)
                    if (!junctions.Any(j => j.JunctionId == roundaboutJunction.JunctionId))
                        junctions.Add(roundaboutJunction);

                // Update the network's junction list
                network.Junctions.Clear();
                network.Junctions.AddRange(junctions);

                // Re-assign sequential junction IDs after merging
                for (var i = 0; i < junctions.Count; i++) junctions[i].JunctionId = i;
            }
        }

        if (junctions.Count == 0)
        {
            TerrainLogger.Info("  No junctions to harmonize");
            return result;
        }

        // Step 2: Sort by priority (handle highest-priority junctions first)
        var sortedJunctions = junctions.OrderByDescending(j => j.MaxPriority).ToList();

        // Count excluded junctions
        var excludedCount = sortedJunctions.Count(j => j.IsExcluded);
        if (excludedCount > 0)
            TerrainLogger.Detail($"  {excludedCount} junction(s) marked as excluded, will be skipped");

        // Step 3: Compute harmonized elevation for each junction (skip excluded)
        ComputeJunctionElevations(sortedJunctions, heightMap, metersPerPixel);
        perfLog?.Timing("Computed junction elevations");

        // Calculate statistics
        var stats = CalculateHarmonizationStats(network, preHarmonizationElevations);
        result.ModifiedCrossSections = stats.ModifiedCount;
        result.MaxElevationChange = stats.MaxChange;

        TerrainLogger.Info($"  RESULT: Modified {result.ModifiedCrossSections} cross-sections");
        TerrainLogger.Info($"  RESULT: Max elevation change: {result.MaxElevationChange:F3}m");
        TerrainLogger.Info("=== NETWORK HARMONIZATION COMPLETE ===");

        return result;
    }

    /// <summary>
    ///     Captures current elevations for later comparison.
    /// </summary>
    private Dictionary<int, float> CaptureElevations(UnifiedRoadNetwork network)
    {
        return network.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .ToDictionary(cs => cs.Index, cs => cs.TargetElevation);
    }

    /// <summary>
    ///     Computes the harmonized elevation for each junction.
    ///     Strategy based on junction type:
    ///     - T-Junction: Continuous road elevation wins, terminating road adapts with gradient
    ///     - Y-Junction: Priority-weighted average
    ///     - X-Junction: Priority-weighted average with approach angle consideration
    ///     - Mid-Spline Crossing: Priority-weighted average, both roads are continuous
    ///     - Isolated Endpoint: Blend toward terrain
    /// </summary>
    private void ComputeJunctionElevations(
        List<NetworkJunction> junctions,
        float[,] heightMap,
        float metersPerPixel)
    {
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        foreach (var junction in junctions)
        {
            // Skip excluded junctions - they won't be harmonized
            if (junction.IsExcluded)
            {
                junction.HarmonizedElevation = float.NaN;
                continue;
            }

            switch (junction.Type)
            {
                case JunctionType.Endpoint:
                    ComputeEndpointElevation(junction, heightMap, metersPerPixel, mapWidth, mapHeight);
                    break;

                case JunctionType.TJunction:
                    ComputeTJunctionElevation(junction);
                    break;

                case JunctionType.MidSplineCrossing:
                    ComputeMidSplineCrossingElevation(junction);
                    break;

                case JunctionType.YJunction:
                case JunctionType.CrossRoads:
                case JunctionType.Complex:
                    // Cross-material junctions where both roads terminate:
                    // Treat like endpoints — use terrain elevation, both roads taper down.
                    // This prevents bumps where neither road's smoothed profile matches terrain.
                    if (junction.IsCrossMaterial && junction.HasMixedPriorities)
                        ComputeEndpointElevation(junction, heightMap, metersPerPixel, mapWidth, mapHeight);
                    else
                        ComputeMultiWayJunctionElevation(junction);
                    break;
            }
        }
    }

    /// <summary>
    ///     Computes elevation for isolated endpoints (roads that end without connecting to another).
    ///     Blends toward terrain elevation based on configuration.
    /// </summary>
    private void ComputeEndpointElevation(
        NetworkJunction junction,
        float[,] heightMap,
        float metersPerPixel,
        int mapWidth,
        int mapHeight)
    {
        if (junction.Contributors.Count == 0)
            return;

        // Get terrain elevation at endpoint
        var px = (int)(junction.Position.X / metersPerPixel);
        var py = (int)(junction.Position.Y / metersPerPixel);
        px = Math.Clamp(px, 0, mapWidth - 1);
        py = Math.Clamp(py, 0, mapHeight - 1);

        // Isolated endpoints always blend fully to terrain
        junction.HarmonizedElevation = heightMap[py, px];
    }

    /// <summary>
    ///     Computes elevation for T-junctions using the gradient-aware algorithm.
    ///     For T-junctions:
    ///     1. Identify continuous (C) and terminating (T) roads
    ///     2. If elevation difference is small: Use weighted average based on priority
    ///     3. If elevation difference is large: Apply gradient ramp on terminating road
    ///     SURFACE-AWARE: The harmonized elevation is calculated at the ACTUAL surface
    ///     where the terminating road connects, accounting for BOTH:
    ///     - Banking (lateral tilt for curves)
    ///     - Longitudinal slope (grade/pitch of the primary road)
    ///     This prevents both "cliff" artifacts from banking AND "step" artifacts from
    ///     slope mismatches at T-junctions.
    /// </summary>
    private void ComputeTJunctionElevation(NetworkJunction junction)
    {
        // Get continuous and terminating contributors
        var continuous = junction.GetContinuousRoads().ToList();
        var terminating = junction.GetTerminatingRoads().ToList();

        if (continuous.Count == 0)
        {
            // Fallback to weighted average if no clear continuous road
            ComputeMultiWayJunctionElevation(junction);
            return;
        }

        // Use the highest-priority continuous road's elevation
        var primaryContinuous = continuous.OrderByDescending(c => c.Spline.Priority).First();
        var primaryCS = primaryContinuous.CrossSection;

        // Get the base elevation (centerline) of the continuous road AT ITS CROSS-SECTION LOCATION
        var E_c_centerline = primaryCS.TargetElevation;

        // Calculate the surface elevation at the ACTUAL connection point
        // This must account for BOTH:
        // 1. Banking (lateral tilt) - handled by BankedTerrainHelper
        // 2. Longitudinal slope (grade) - the primary road going uphill/downhill
        var E_c = E_c_centerline;

        if (terminating.Count > 0)
        {
            var terminatingEndpoint = terminating[0].CrossSection.CenterPoint;

            // Calculate how far the terminating endpoint is from the primary road's cross-section center
            // in both the lateral (normal) and longitudinal (tangent) directions
            var toEndpoint = terminatingEndpoint - primaryCS.CenterPoint;
            var lateralOffset = Vector2.Dot(toEndpoint, primaryCS.NormalDirection);
            var longitudinalOffset = Vector2.Dot(toEndpoint, primaryCS.TangentDirection);

            // Start with centerline elevation
            var surfaceElevation = E_c_centerline;

            // Add banking contribution (lateral offset)
            if (BankedTerrainHelper.HasBanking(primaryCS))
            {
                var bankingContribution = lateralOffset * MathF.Sin(primaryCS.BankAngleRadians);
                surfaceElevation += bankingContribution;
            }

            // Add longitudinal slope contribution
            // Calculate the primary road's local slope from neighboring cross-sections
            var longitudinalSlopeContribution = 0f;
            var primarySlope = 0f;
            if (MathF.Abs(longitudinalOffset) > 0.1f && _crossSectionsBySpline != null)
            {
                primarySlope = CalculatePrimaryRoadSlope(primaryContinuous);
                if (!float.IsNaN(primarySlope)) longitudinalSlopeContribution = longitudinalOffset * primarySlope;
            }

            surfaceElevation += longitudinalSlopeContribution;
            E_c = surfaceElevation;

            if (!float.IsNaN(E_c) && MathF.Abs(E_c - E_c_centerline) > 0.001f)
            {
                var slopeDegrees = MathF.Atan(primarySlope) * 180f / MathF.PI;
                TerrainCreationLogger.Current?.Detail(
                    $"T-Junction #{junction.JunctionId}: Surface elevation at connection = {E_c:F2}m " +
                    $"(centerline={E_c_centerline:F2}m, lateral={lateralOffset:F2}m, " +
                    $"longitudinal={longitudinalOffset:F2}m, slope={slopeDegrees:F1}°, " +
                    $"slopeContrib={longitudinalSlopeContribution:F3}m, " +
                    $"bank={BankingCalculator.RadiansToDegrees(primaryCS.BankAngleRadians):F1}°)");
            }

            if (float.IsNaN(E_c)) E_c = E_c_centerline; // Fallback
        }

        // Calculate priority-weighted elevation from all terminating roads
        var totalTerminatingPriority = 0f;
        var weightedTerminatingElevation = 0f;

        foreach (var t in terminating)
        {
            float priority = t.Spline.Priority;
            totalTerminatingPriority += priority;
            weightedTerminatingElevation += t.CrossSection.TargetElevation * priority;
        }

        var E_t = totalTerminatingPriority > 0
            ? weightedTerminatingElevation / totalTerminatingPriority
            : E_c;

        var deltaE = MathF.Abs(E_c - E_t);

        if (deltaE < SmallElevationDifferenceMeters)
        {
            // Small difference - use priority-weighted average
            var continuousPriority = continuous.Sum(c => (float)c.Spline.Priority);
            var terminatingPrioritySum = terminating.Sum(t => (float)t.Spline.Priority);
            var totalPriority = continuousPriority + terminatingPrioritySum;

            if (totalPriority > 0)
                junction.HarmonizedElevation =
                    (E_c * continuousPriority + E_t * terminatingPrioritySum) / totalPriority;
            else
                junction.HarmonizedElevation = E_c;
        }
        else
        {
            // Significant difference - continuous road wins
            // The gradient ramp on terminating roads is applied during propagation
            junction.HarmonizedElevation = E_c;
        }

        // PHASE 3: Set edge constraints on terminating roads
        // Calculate the primary road's slope for edge constraint calculations
        var primarySlopeForConstraints = _crossSectionsBySpline != null
            ? CalculatePrimaryRoadSlope(primaryContinuous)
            : 0f;
        if (float.IsNaN(primarySlopeForConstraints))
            primarySlopeForConstraints = 0f;

        // Apply edge constraints to each terminating road's cross-section
        foreach (var t in terminating)
        {
            var terminatingCs = t.CrossSection;

            // Calculate constrained edge elevations where this road meets the primary surface
            JunctionSurfaceCalculator.ApplyEdgeConstraints(
                terminatingCs,
                primaryCS,
                primarySlopeForConstraints);

            TerrainCreationLogger.Current?.Detail(
                $"T-Junction #{junction.JunctionId}: Spline {t.Spline.SplineId} CS#{terminatingCs.Index} " +
                $"edges constrained to L={terminatingCs.ConstrainedLeftEdgeElevation:F3}m, " +
                $"R={terminatingCs.ConstrainedRightEdgeElevation:F3}m (from primary surface)");
        }
    }

    /// <summary>
    ///     Calculates the longitudinal slope of the primary road at/near the junction.
    ///     Returns the slope as rise/run (tangent of the angle).
    ///     Uses 3 cross-sections before and after the junction cross-section to get a local gradient.
    /// </summary>
    private float CalculatePrimaryRoadSlope(JunctionContributor primaryContributor)
    {
        if (_crossSectionsBySpline == null)
            return float.NaN;

        if (!_crossSectionsBySpline.TryGetValue(primaryContributor.Spline.SplineId, out var primarySections))
            return float.NaN;

        var junctionCs = primaryContributor.CrossSection;
        var junctionIndex = primarySections.FindIndex(cs => cs.Index == junctionCs.Index);

        if (junctionIndex < 0)
            return float.NaN;

        // Get neighboring cross-sections to calculate slope
        var prevIndex = Math.Max(0, junctionIndex - 3);
        var nextIndex = Math.Min(primarySections.Count - 1, junctionIndex + 3);

        if (prevIndex == nextIndex)
            return 0f;

        var cs1 = primarySections[prevIndex];
        var cs2 = primarySections[nextIndex];

        var distance = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
        if (distance < 0.1f)
            return 0f;

        var elevDiff = cs2.TargetElevation - cs1.TargetElevation;

        return elevDiff / distance; // rise/run = slope
    }

    /// <summary>
    ///     Computes elevation for multi-way junctions (Y, X, Complex).
    ///     Uses priority-weighted average of all contributors.
    ///     IMPORTANT: When all contributors have equal priority (same-material junctions),
    ///     uses geometric heuristics as tiebreakers to determine the "dominant" road:
    ///     1. Road length (longer roads are more important)
    ///     2. Approach angle (roads approaching at sharp angles are typically joining)
    ///     This prevents the "jagged junction" problem where equal-priority roads
    ///     have no deterministic strategy for which adapts to which.
    /// </summary>
    private void ComputeMultiWayJunctionElevation(NetworkJunction junction)
    {
        if (junction.Contributors.Count == 0)
        {
            junction.HarmonizedElevation = 0f;
            return;
        }

        // Check if this is an equal-priority junction (same material roads meeting)
        var priorities = junction.Contributors.Select(c => c.Spline.Priority).Distinct().ToList();
        var isEqualPriority = priorities.Count == 1;

        if (isEqualPriority && junction.Contributors.Count >= 2)
        {
            // Use geometric heuristics to determine dominant road
            junction.HarmonizedElevation = ComputeEqualPriorityJunctionElevation(junction);
            return;
        }

        // Standard priority-weighted average for mixed-priority junctions
        var totalWeight = 0f;
        var weightedSum = 0f;

        foreach (var contributor in junction.Contributors)
        {
            // Weight by priority (and inverse distance to center for endpoints)
            float priorityWeight = contributor.Spline.Priority;
            var dist = Vector2.Distance(contributor.CrossSection.CenterPoint, junction.Position);
            var distanceWeight = 1.0f / (dist + 0.1f); // Add epsilon to avoid division by zero

            var weight = priorityWeight * distanceWeight;
            totalWeight += weight;
            weightedSum += contributor.CrossSection.TargetElevation * weight;
        }

        junction.HarmonizedElevation = totalWeight > 0
            ? weightedSum / totalWeight
            : junction.Contributors.Average(c => c.CrossSection.TargetElevation);
    }

    /// <summary>
    ///     Computes elevation for junctions where all roads have equal priority.
    ///     Uses geometric heuristics to determine which road is "dominant":
    ///     1. Road length - Longer roads are typically main roads
    ///     2. Straightness - Roads that approach at ~180° from each other are likely
    ///     the same road (dominant), while roads at 90° are likely joining (secondary)
    ///     The dominant road's elevation is used directly; secondary roads adapt to it.
    /// </summary>
    private float ComputeEqualPriorityJunctionElevation(NetworkJunction junction)
    {
        var contributors = junction.Contributors.ToList();

        // Log detailed info for debugging
        TerrainCreationLogger.Current?.Detail(
            $"Junction #{junction.JunctionId} ({junction.Type}): Computing equal-priority elevation for {contributors.Count} contributor(s)");

        foreach (var c in contributors)
            TerrainCreationLogger.Current?.Detail(
                $"  - Spline {c.Spline.SplineId} ({c.Spline.MaterialName}): " +
                $"priority={c.Spline.Priority}, length={c.Spline.TotalLengthMeters:F0}m, " +
                $"elevation={c.CrossSection.TargetElevation:F2}m, " +
                $"isStart={c.IsSplineStart}, isEnd={c.IsSplineEnd}");

        if (contributors.Count == 2)
        {
            // For Y-junctions with 2 equal-priority roads:
            // The LONGER road wins (more likely to be a main road)
            var sorted = contributors.OrderByDescending(c => c.Spline.TotalLengthMeters).ToList();
            var dominant = sorted[0];
            var secondary = sorted[1];

            // Calculate the angle between the two roads
            var angleBetween = junction.GetAngleBetween(dominant, secondary);

            // If lengths are very similar (within 20%), use elevation that requires less change
            var lengthRatio = secondary.Spline.TotalLengthMeters / dominant.Spline.TotalLengthMeters;
            if (lengthRatio > 0.8f)
            {
                // Lengths are similar - use average to minimize overall change
                var avgElev = (dominant.CrossSection.TargetElevation + secondary.CrossSection.TargetElevation) / 2f;

                TerrainCreationLogger.Current?.Detail(
                    $"Junction #{junction.JunctionId}: Equal priority, similar lengths " +
                    $"(ratio={lengthRatio:F2}, angle={angleBetween:F0}°), using average elevation {avgElev:F2}m " +
                    $"(dominant={dominant.CrossSection.TargetElevation:F2}m, secondary={secondary.CrossSection.TargetElevation:F2}m)");

                return avgElev;
            }

            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId}: Equal priority, dominant road is longer " +
                $"({dominant.Spline.TotalLengthMeters:F0}m vs {secondary.Spline.TotalLengthMeters:F0}m, angle={angleBetween:F0}°), " +
                $"using elevation {dominant.CrossSection.TargetElevation:F2}m");

            return dominant.CrossSection.TargetElevation;
        }

        if (contributors.Count >= 3)
        {
            // For complex junctions (3+ roads):
            // Find the two roads that are most "aligned" (approaching from opposite directions)
            // These form the "through" route; other roads are joining

            var bestAlignmentPair = FindMostAlignedPair(junction, contributors);

            if (bestAlignmentPair != null)
            {
                var (roadA, roadB, angle) = bestAlignmentPair.Value;

                // The aligned pair forms the "main road" - use their average elevation
                var mainRoadElev = (roadA.CrossSection.TargetElevation + roadB.CrossSection.TargetElevation) / 2f;

                TerrainCreationLogger.Current?.Detail(
                    $"Junction #{junction.JunctionId}: Equal priority, found aligned pair " +
                    $"(angle={angle:F0}°), using main road elevation {mainRoadElev:F2}m");

                return mainRoadElev;
            }

            // Fallback: use length-weighted average
            return ComputeLengthWeightedElevation(contributors);
        }

        // Fallback for single contributor
        return contributors[0].CrossSection.TargetElevation;
    }

    /// <summary>
    ///     Finds the pair of contributors that approach the junction from the most opposite directions.
    ///     This identifies the "through" route at complex junctions.
    /// </summary>
    /// <returns>Tuple of (contributorA, contributorB, angle) or null if no good pair found.</returns>
    private static (JunctionContributor, JunctionContributor, float)? FindMostAlignedPair(
        NetworkJunction junction,
        List<JunctionContributor> contributors)
    {
        (JunctionContributor, JunctionContributor, float)? bestPair = null;
        var bestAlignmentScore = 0f;

        // "Aligned" means approaching from opposite directions (angle close to 180°)
        const float minAlignmentAngle = 140f; // At least 140° to be considered "aligned"

        for (var i = 0; i < contributors.Count; i++)
        for (var j = i + 1; j < contributors.Count; j++)
        {
            var angle = junction.GetAngleBetween(contributors[i], contributors[j]);

            // Higher angle = more aligned (opposite directions)
            if (angle > bestAlignmentScore && angle >= minAlignmentAngle)
            {
                bestAlignmentScore = angle;
                bestPair = (contributors[i], contributors[j], angle);
            }
        }

        return bestPair;
    }

    /// <summary>
    ///     Computes elevation weighted by road length.
    ///     Longer roads get more weight.
    /// </summary>
    private static float ComputeLengthWeightedElevation(List<JunctionContributor> contributors)
    {
        var totalLength = contributors.Sum(c => c.Spline.TotalLengthMeters);
        if (totalLength < 0.001f)
            return contributors.Average(c => c.CrossSection.TargetElevation);

        var weightedSum = contributors.Sum(c =>
            c.CrossSection.TargetElevation * c.Spline.TotalLengthMeters);

        return weightedSum / totalLength;
    }

    /// <summary>
    ///     Computes elevation for mid-spline crossings where two roads cross without either terminating.
    ///     Both roads pass through continuously, so we use priority-weighted average.
    ///     The higher-priority road has more influence on the crossing elevation.
    ///     IMPORTANT: For equal-priority crossings (same material roads crossing), we use
    ///     geometric heuristics similar to Y-junctions to determine which road dominates.
    /// </summary>
    private void ComputeMidSplineCrossingElevation(NetworkJunction junction)
    {
        if (junction.Contributors.Count == 0)
        {
            junction.HarmonizedElevation = 0f;
            return;
        }

        // Check if this is an equal-priority crossing (same material roads meeting)
        var priorities = junction.Contributors.Select(c => c.Spline.Priority).Distinct().ToList();
        var isEqualPriority = priorities.Count == 1;

        if (isEqualPriority && junction.Contributors.Count >= 2)
        {
            // Use geometric heuristics to determine dominant road, same as Y-junctions
            junction.HarmonizedElevation = ComputeEqualPriorityMidSplineCrossingElevation(junction);
            return;
        }

        // For mid-spline crossings with DIFFERENT priorities, all contributors are continuous (no endpoints)
        // Use priority-weighted average with emphasis on the higher-priority road
        var totalPriority = 0f;
        var weightedSum = 0f;

        foreach (var contributor in junction.Contributors)
        {
            // Square the priority to give more weight to higher-priority roads
            // This helps the main road "win" at crossings
            float priorityWeight = contributor.Spline.Priority * contributor.Spline.Priority;
            totalPriority += priorityWeight;
            weightedSum += contributor.CrossSection.TargetElevation * priorityWeight;
        }

        if (totalPriority > 0)
            junction.HarmonizedElevation = weightedSum / totalPriority;
        else
            // Fallback to simple average
            junction.HarmonizedElevation = junction.Contributors.Average(c => c.CrossSection.TargetElevation);

        TerrainCreationLogger.Current?.Detail($"MidSplineCrossing #{junction.JunctionId}: " +
                                              $"harmonized elevation = {junction.HarmonizedElevation:F2}m " +
                                              $"(from {junction.Contributors.Count} continuous roads, mixed priority)");
    }

    /// <summary>
    ///     Computes elevation for mid-spline crossings where all roads have equal priority.
    ///     Uses geometric heuristics to determine which road is "dominant":
    ///     1. Road length - Longer roads are typically main roads
    ///     2. Straightness at crossing - Roads that are straighter at the crossing point dominate
    ///     The dominant road's elevation is preserved; other roads adapt to it.
    /// </summary>
    private float ComputeEqualPriorityMidSplineCrossingElevation(NetworkJunction junction)
    {
        var contributors = junction.Contributors.ToList();

        TerrainCreationLogger.Current?.Detail(
            $"MidSplineCrossing #{junction.JunctionId}: Computing equal-priority elevation for {contributors.Count} continuous road(s)");

        foreach (var c in contributors)
            TerrainCreationLogger.Current?.Detail(
                $"  - Spline {c.Spline.SplineId} ({c.Spline.MaterialName}): " +
                $"priority={c.Spline.Priority}, length={c.Spline.TotalLengthMeters:F0}m, " +
                $"elevation at crossing={c.CrossSection.TargetElevation:F2}m");

        if (contributors.Count == 2)
        {
            // For 2 roads crossing:
            // The LONGER road wins (more likely to be a main road)
            var sorted = contributors.OrderByDescending(c => c.Spline.TotalLengthMeters).ToList();
            var dominant = sorted[0];
            var secondary = sorted[1];

            // If lengths are very similar (within 30% for crossings), use average
            var lengthRatio = secondary.Spline.TotalLengthMeters / dominant.Spline.TotalLengthMeters;
            if (lengthRatio > 0.7f)
            {
                // Lengths are similar - use average to minimize overall change
                var avgElev = (dominant.CrossSection.TargetElevation + secondary.CrossSection.TargetElevation) / 2f;

                TerrainCreationLogger.Current?.Detail(
                    $"MidSplineCrossing #{junction.JunctionId}: Equal priority, similar lengths " +
                    $"(ratio={lengthRatio:F2}), using average elevation {avgElev:F2}m");

                return avgElev;
            }

            TerrainCreationLogger.Current?.Detail(
                $"MidSplineCrossing #{junction.JunctionId}: Equal priority, dominant road is longer " +
                $"({dominant.Spline.TotalLengthMeters:F0}m vs {secondary.Spline.TotalLengthMeters:F0}m), " +
                $"using elevation {dominant.CrossSection.TargetElevation:F2}m");

            return dominant.CrossSection.TargetElevation;
        }

        // For 3+ roads crossing: use length-weighted average
        return ComputeLengthWeightedElevation(contributors);
    }

    /// <summary>
    ///     Calculates statistics about the harmonization changes.
    /// </summary>
    private (int ModifiedCount, float MaxChange) CalculateHarmonizationStats(
        UnifiedRoadNetwork network,
        Dictionary<int, float> preHarmonizationElevations)
    {
        var modifiedCount = 0;
        var maxChange = 0f;

        foreach (var cs in network.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
            if (preHarmonizationElevations.TryGetValue(cs.Index, out var preElev))
            {
                var change = MathF.Abs(cs.TargetElevation - preElev);
                if (change > 0.001f)
                {
                    modifiedCount++;
                    if (change > maxChange)
                        maxChange = change;
                }
            }

        return (modifiedCount, maxChange);
    }

    /// <summary>
    ///     Exports a debug image showing junction detection and elevation changes.
    ///     Includes visualization for:
    ///     - Cross-sections colored by elevation change (gray=unchanged, blue=lowered, red=raised)
    ///     - Network junction types (color-coded markers)
    ///     - OSM junction hints (colored outer rings when OSM data available)
    ///     - Cross-material indicators (white outer ring)
    ///     - OSM-sourced junctions (dotted outer circle)
    /// </summary>
    public void ExportJunctionDebugImage(
        UnifiedRoadNetwork network,
        Dictionary<int, float> preHarmonizationElevations,
        int imageWidth,
        int imageHeight,
        float metersPerPixel,
        string outputPath)
    {
        TerrainLogger.Detail($"  Exporting junction debug image ({imageWidth}x{imageHeight})...");

        using var image = new Image<Rgba32>(imageWidth, imageHeight, new Rgba32(0, 0, 0, 255));

        // Compute elevation change range for color mapping
        var maxLower = 0f;
        var maxRaise = 0f;
        foreach (var cs in network.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
            if (preHarmonizationElevations.TryGetValue(cs.Index, out var preElev))
            {
                var change = cs.TargetElevation - preElev;
                if (change < 0) maxLower = MathF.Max(maxLower, MathF.Abs(change));
                else maxRaise = MathF.Max(maxRaise, change);
            }

        var maxChange = MathF.Max(maxLower, maxRaise);
        if (maxChange < 0.01f) maxChange = 1f;

        // Draw cross-sections colored by elevation change
        foreach (var cs in network.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            var change = 0f;
            if (preHarmonizationElevations.TryGetValue(cs.Index, out var preElev))
                change = cs.TargetElevation - preElev;

            // Color: gray=unchanged, blue=lowered, red=raised
            Rgba32 color;
            if (MathF.Abs(change) < 0.001f)
            {
                color = new Rgba32(80, 80, 80, 255);
            }
            else if (change < 0)
            {
                var intensity = MathF.Abs(change) / maxChange;
                color = new Rgba32((byte)(80 * (1 - intensity)), (byte)(80 * (1 - intensity)),
                    (byte)(80 + 175 * intensity), 255);
            }
            else
            {
                var intensity = change / maxChange;
                color = new Rgba32((byte)(80 + 175 * intensity), (byte)(80 * (1 - intensity)),
                    (byte)(80 * (1 - intensity)), 255);
            }

            // Draw cross-section line
            var halfWidth = cs.EffectiveRoadWidth / 2.0f;
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;
            var lx = (int)(left.X / metersPerPixel);
            var ly = (int)(left.Y / metersPerPixel);
            var rx = (int)(right.X / metersPerPixel);
            var ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, color);
        }

        // Draw detected junctions with OSM hint visualization
        foreach (var junction in network.Junctions)
        {
            var jx = (int)(junction.Position.X / metersPerPixel);
            var jy = imageHeight - 1 - (int)(junction.Position.Y / metersPerPixel);

            var radius = junction.Type switch
            {
                JunctionType.Complex => 6,
                JunctionType.CrossRoads => 5,
                JunctionType.Roundabout => 7,
                _ => 4
            };

            var junctionColor = junction.Type switch
            {
                JunctionType.TJunction => new Rgba32(255, 165, 0, 200),
                JunctionType.CrossRoads => new Rgba32(255, 0, 0, 200),
                JunctionType.Complex => new Rgba32(255, 0, 255, 200),
                JunctionType.Roundabout => new Rgba32(0, 255, 255, 200),
                JunctionType.MidSplineCrossing => new Rgba32(255, 255, 0, 200),
                _ => new Rgba32(0, 255, 0, 200)
            };

            DrawFilledCircle(image, jx, jy, radius, junctionColor);

            // Draw cross-material indicator (white outline)
            if (junction.IsCrossMaterial)
                DrawCircleOutline(image, jx, jy, radius + 3, new Rgba32(255, 255, 255, 200));
        }

        // Save image
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        image.SaveAsPng(outputPath);

        TerrainLogger.Detail($"  Exported junction debug image: {outputPath}");
    }

    #region Drawing Helpers

    private void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        var height = img.Height;
        y0 = height - 1 - y0;
        y1 = height - 1 - y1;

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (x * x + y * y <= radius * radius)
            {
                var px = cx + x;
                var py = cy + y;
                if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    img[px, py] = color;
            }
    }

    private void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 2)
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    /// <summary>
    ///     Draws a dotted circle outline (used for OSM-sourced junction indicators).
    /// </summary>
    private void DrawDottedCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 15) // Skip every few degrees for dotted effect
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    #endregion
}

/// <summary>
///     Result of network junction harmonization.
/// </summary>
public class HarmonizationResult
{
    /// <summary>
    ///     Total number of cross-sections with elevation changes.
    /// </summary>
    public int ModifiedCrossSections { get; set; }

    /// <summary>
    ///     Maximum elevation change in meters.
    /// </summary>
    public float MaxElevationChange { get; set; }

    /// <summary>
    ///     Elevations captured before harmonization (for debugging and comparison).
    /// </summary>
    public Dictionary<int, float> PreHarmonizationElevations { get; set; } = new();

    /// <summary>
    ///     Whether the harmonization was successful (had junctions to process).
    /// </summary>
    public bool Success => ModifiedCrossSections > 0;
}