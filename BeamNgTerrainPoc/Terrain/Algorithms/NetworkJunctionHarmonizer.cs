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
///     The key difference from JunctionElevationHarmonizer is that this operates on
///     the unified network, enabling cross-material junction harmonization.
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
    ///     The current network being processed. Set during HarmonizeNetwork.
    /// </summary>
    private UnifiedRoadNetwork? _currentNetwork;
    
    /// <summary>
    ///     Cross-sections grouped by spline ID for slope calculations.
    ///     Built at the start of harmonization for efficient lookups.
    /// </summary>
    private Dictionary<int, List<UnifiedCrossSection>>? _crossSectionsBySpline;

    public NetworkJunctionHarmonizer()
    {
        _detector = new NetworkJunctionDetector();
    }

    /// <summary>
    ///     Harmonizes elevations across the entire unified road network.
    ///     Algorithm:
    ///     1. Detect all junctions (if not already detected)
    ///     2. Sort by priority (handle highest-priority roads first)
    ///     3. Harmonize each junction based on its type
    ///     4. Propagate elevation constraints along affected splines
    /// </summary>
    /// <param name="network">The unified road network with calculated target elevations.</param>
    /// <param name="heightMap">The original terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <param name="globalDetectionRadius">Global junction detection radius in meters.</param>
    /// <param name="globalBlendDistance">Global junction blend distance in meters.</param>
    public HarmonizationResult HarmonizeNetwork(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        float globalDetectionRadius = 10.0f,
        float globalBlendDistance = 30.0f)
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
        TerrainLogger.Detail($"  Global detection radius: {globalDetectionRadius}m");
        TerrainLogger.Detail($"  Global blend distance: {globalBlendDistance}m");

        // Capture pre-harmonization elevations for comparison
        var preHarmonizationElevations = CaptureElevations(network);
        result.PreHarmonizationElevations = preHarmonizationElevations;

        // Step 1: Detect junctions
        // IMPORTANT: We must ALWAYS run DetectJunctions() to find regular junctions (T, Y, X, etc.)
        // even when roundabout junctions already exist in network.Junctions.
        // Roundabout junctions are added by DetectRoundaboutJunctions() in Phase 2.6 of UnifiedRoadSmoother,
        // but that method does NOT detect regular road junctions - only roundabout connections.
        // If we skip DetectJunctions() when network.Junctions.Count > 0, we miss all regular junctions!
        
        // First, preserve any existing roundabout junctions (added by RoundaboutElevationHarmonizer)
        var existingRoundaboutJunctions = network.Junctions
            .Where(j => j.Type == JunctionType.Roundabout)
            .ToList();
        
        var existingRoundaboutCount = existingRoundaboutJunctions.Count;
        if (existingRoundaboutCount > 0)
        {
            TerrainLogger.Detail($"  Preserving {existingRoundaboutCount} existing roundabout junction(s)");
        }
        
        // Always run standard junction detection to find regular junctions
        // DetectJunctions() finds: Endpoint, TJunction, YJunction, CrossRoads, Complex, MidSplineCrossing
        // It does NOT create Roundabout type junctions - those come from DetectRoundaboutJunctions()
        var detectedJunctions = _detector.DetectJunctions(network, globalDetectionRadius);
        TerrainLogger.Detail($"  Detected {detectedJunctions.Count} regular junction(s)");
        
        // Merge: combine detected junctions with preserved roundabout junctions
        List<NetworkJunction> junctions = detectedJunctions;
        
        // Add back roundabout junctions that were already processed by RoundaboutElevationHarmonizer
        // These should already be marked as IsExcluded=true to prevent double-processing
        foreach (var roundaboutJunction in existingRoundaboutJunctions)
        {
            // Avoid duplicates by checking JunctionId
            if (!junctions.Any(j => j.JunctionId == roundaboutJunction.JunctionId))
            {
                junctions.Add(roundaboutJunction);
            }
        }
        
        // Update the network's junction list with the complete set
        network.Junctions.Clear();
        network.Junctions.AddRange(junctions);
        
        // Re-assign sequential junction IDs after merging
        for (int i = 0; i < junctions.Count; i++)
        {
            junctions[i].JunctionId = i;
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
        {
            TerrainLogger.Detail($"  {excludedCount} junction(s) marked as excluded, will be skipped");
        }

        // Step 3: Compute harmonized elevation for each junction (skip excluded)
        ComputeJunctionElevations(sortedJunctions, heightMap, metersPerPixel, globalBlendDistance);
        perfLog?.Timing("Computed junction elevations");

        // Step 4: Propagate junction constraints along affected splines
        var propagatedCount = PropagateJunctionConstraints(network, sortedJunctions, globalBlendDistance);
        result.PropagatedCrossSections = propagatedCount;
        perfLog?.Timing($"Propagated constraints to {propagatedCount} cross-sections");

        // Step 5: Apply endpoint tapering for isolated endpoints
        var taperedCount = ApplyEndpointTapering(network, sortedJunctions, heightMap, metersPerPixel);
        result.TaperedCrossSections = taperedCount;
        perfLog?.Timing($"Applied taper to {taperedCount} cross-sections");

        // Step 6: Apply multi-way junction plateau smoothing to reduce dents
        // IMPORTANT: Pass preHarmonizationElevations so we sample ORIGINAL elevations, not already-blended ones
        var plateauSmoothedCount = ApplyMultiWayJunctionPlateauSmoothing(network, sortedJunctions, globalBlendDistance,
            preHarmonizationElevations);
        result.PlateauSmoothedCrossSections = plateauSmoothedCount;
        if (plateauSmoothedCount > 0)
            perfLog?.Timing(
                $"Applied plateau smoothing to {plateauSmoothedCount} cross-sections at multi-way junctions");

        // Calculate statistics
        var stats = CalculateHarmonizationStats(network, preHarmonizationElevations);
        result.ModifiedCrossSections = stats.ModifiedCount;
        result.MaxElevationChange = stats.MaxChange;

        TerrainLogger.Info($"  RESULT: Modified {result.ModifiedCrossSections} cross-sections");
        TerrainLogger.Info(
            $"  RESULT: Propagated {propagatedCount}, Tapered {taperedCount}, Plateau {plateauSmoothedCount}");
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
        float metersPerPixel,
        float globalBlendDistance)
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
                    ComputeTJunctionElevation(junction, globalBlendDistance);
                    break;

                case JunctionType.MidSplineCrossing:
                                ComputeMidSplineCrossingElevation(junction);
                                break;

                            case JunctionType.YJunction:
                            case JunctionType.CrossRoads:
                            case JunctionType.Complex:
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

        var contributor = junction.Contributors[0];
        var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();

        // Get terrain elevation at endpoint
        var px = (int)(junction.Position.X / metersPerPixel);
        var py = (int)(junction.Position.Y / metersPerPixel);
        px = Math.Clamp(px, 0, mapWidth - 1);
        py = Math.Clamp(py, 0, mapHeight - 1);
        var terrainElevation = heightMap[py, px];

        var roadElevation = contributor.CrossSection.TargetElevation;

        // Blend: mostly road elevation with slight pull toward terrain
        junction.HarmonizedElevation = roadElevation * (1.0f - junctionParams.EndpointTerrainBlendStrength)
                                       + terrainElevation * junctionParams.EndpointTerrainBlendStrength;
    }

    /// <summary>
    ///     Computes elevation for T-junctions using the gradient-aware algorithm.
    ///     For T-junctions:
    ///     1. Identify continuous (C) and terminating (T) roads
    ///     2. If elevation difference is small: Use weighted average based on priority
    ///     3. If elevation difference is large: Apply gradient ramp on terminating road
    ///     
    ///     SURFACE-AWARE: The harmonized elevation is calculated at the ACTUAL surface
    ///     where the terminating road connects, accounting for BOTH:
    ///     - Banking (lateral tilt for curves)
    ///     - Longitudinal slope (grade/pitch of the primary road)
    ///     
    ///     This prevents both "cliff" artifacts from banking AND "step" artifacts from
    ///     slope mismatches at T-junctions.
    /// </summary>
    private void ComputeTJunctionElevation(NetworkJunction junction, float globalBlendDistance)
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
                if (!float.IsNaN(primarySlope))
                {
                    longitudinalSlopeContribution = longitudinalOffset * primarySlope;
                }
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
            
            if (float.IsNaN(E_c))
            {
                E_c = E_c_centerline; // Fallback
            }
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
    /// Calculates the longitudinal slope of the primary road at/near the junction.
    /// Returns the slope as rise/run (tangent of the angle).
    /// Uses 3 cross-sections before and after the junction cross-section to get a local gradient.
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
    ///     
    ///     IMPORTANT: When all contributors have equal priority (same-material junctions),
    ///     uses geometric heuristics as tiebreakers to determine the "dominant" road:
    ///     1. Road length (longer roads are more important)
    ///     2. Approach angle (roads approaching at sharp angles are typically joining)
    ///     
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
    ///     
    ///     1. Road length - Longer roads are typically main roads
    ///     2. Straightness - Roads that approach at ~180� from each other are likely
    ///        the same road (dominant), while roads at 90� are likely joining (secondary)
    ///     
    ///     The dominant road's elevation is used directly; secondary roads adapt to it.
    /// </summary>
    private float ComputeEqualPriorityJunctionElevation(NetworkJunction junction)
    {
        var contributors = junction.Contributors.ToList();
        
        // Log detailed info for debugging
        TerrainCreationLogger.Current?.Detail(
            $"Junction #{junction.JunctionId} ({junction.Type}): Computing equal-priority elevation for {contributors.Count} contributor(s)");
        
        foreach (var c in contributors)
        {
            TerrainCreationLogger.Current?.Detail(
                $"  - Spline {c.Spline.SplineId} ({c.Spline.MaterialName}): " +
                $"priority={c.Spline.Priority}, length={c.Spline.TotalLengthMeters:F0}m, " +
                $"elevation={c.CrossSection.TargetElevation:F2}m, " +
                $"isStart={c.IsSplineStart}, isEnd={c.IsSplineEnd}");
        }
        
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
                    $"(ratio={lengthRatio:F2}, angle={angleBetween:F0}�), using average elevation {avgElev:F2}m " +
                    $"(dominant={dominant.CrossSection.TargetElevation:F2}m, secondary={secondary.CrossSection.TargetElevation:F2}m)");
                
                return avgElev;
            }
            
            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId}: Equal priority, dominant road is longer " +
                $"({dominant.Spline.TotalLengthMeters:F0}m vs {secondary.Spline.TotalLengthMeters:F0}m, angle={angleBetween:F0}�), " +
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
                    $"(angle={angle:F0}�), using main road elevation {mainRoadElev:F2}m");
                
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
        
        // "Aligned" means approaching from opposite directions (angle close to 180�)
        const float minAlignmentAngle = 140f; // At least 140� to be considered "aligned"
        
        for (int i = 0; i < contributors.Count; i++)
        {
            for (int j = i + 1; j < contributors.Count; j++)
            {
                var angle = junction.GetAngleBetween(contributors[i], contributors[j]);
                
                // Higher angle = more aligned (opposite directions)
                if (angle > bestAlignmentScore && angle >= minAlignmentAngle)
                {
                    bestAlignmentScore = angle;
                    bestPair = (contributors[i], contributors[j], angle);
                }
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
    ///     
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
        {
            junction.HarmonizedElevation = weightedSum / totalPriority;
        }
        else
        {
            // Fallback to simple average
            junction.HarmonizedElevation = junction.Contributors.Average(c => c.CrossSection.TargetElevation);
        }

        TerrainCreationLogger.Current?.Detail($"MidSplineCrossing #{junction.JunctionId}: " +
                          $"harmonized elevation = {junction.HarmonizedElevation:F2}m " +
                          $"(from {junction.Contributors.Count} continuous roads, mixed priority)");
    }

    /// <summary>
    ///     Computes elevation for mid-spline crossings where all roads have equal priority.
    ///     Uses geometric heuristics to determine which road is "dominant":
    ///     
    ///     1. Road length - Longer roads are typically main roads
    ///     2. Straightness at crossing - Roads that are straighter at the crossing point dominate
    ///     
    ///     The dominant road's elevation is preserved; other roads adapt to it.
    /// </summary>
    private float ComputeEqualPriorityMidSplineCrossingElevation(NetworkJunction junction)
    {
        var contributors = junction.Contributors.ToList();
        
        TerrainCreationLogger.Current?.Detail(
            $"MidSplineCrossing #{junction.JunctionId}: Computing equal-priority elevation for {contributors.Count} continuous road(s)");
        
        foreach (var c in contributors)
        {
            TerrainCreationLogger.Current?.Detail(
                $"  - Spline {c.Spline.SplineId} ({c.Spline.MaterialName}): " +
                $"priority={c.Spline.Priority}, length={c.Spline.TotalLengthMeters:F0}m, " +
                $"elevation at crossing={c.CrossSection.TargetElevation:F2}m");
        }
        
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
    ///     Propagates junction elevation constraints back along each affected spline.
    ///     Uses smooth blending over the configured blend distance.
    ///     For T-junctions: Only propagates along terminating roads.
    ///     For Y/X junctions: Propagates along all contributing roads.
    ///     For Mid-spline crossings: Propagates in both directions from the crossing point.
    ///     
    ///     CRITICAL FIX FOR DENSE NETWORKS:
    ///     When multiple junctions have overlapping blend zones (common in dense road networks),
    ///     each cross-section accumulates weighted influences from ALL nearby junctions rather than
    ///     letting each junction independently overwrite. This prevents elevation "steps" at the
    ///     boundaries where different junction blend zones meet.
    ///     
    ///     Algorithm:
    ///     1. First pass: Collect all junction influences for each cross-section
    ///     2. Second pass: Compute final elevation as weighted average of all influences
    ///     
    ///     The weight for each junction is based on: (1 - blend_factor) where blend_factor 
    ///     increases with distance. Closer junctions have more influence.
    /// </summary>
    /// <returns>Number of cross-sections modified.</returns>
    private int PropagateJunctionConstraints(
        UnifiedRoadNetwork network,
        List<NetworkJunction> junctions,
        float globalBlendDistance)
    {
        var modifiedCount = 0;

        // Cache cross-sections by spline for faster access
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // CRITICAL FIX: Capture ORIGINAL elevations BEFORE any propagation
        // This prevents reading already-modified values when processing overlapping blend zones
        var originalElevations = network.CrossSections
            .Where(cs => !float.IsNaN(cs.TargetElevation))
            .ToDictionary(cs => cs.Index, cs => cs.TargetElevation);

        // NEW: Track all junction influences for each cross-section
        // Key: cross-section Index, Value: list of (harmonized elevation, weight, junction ID)
        var crossSectionInfluences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>();

        // First pass: Collect all junction influences
        foreach (var junction in junctions.Where(j => j.Type != JunctionType.Endpoint && !j.IsExcluded))
        {
            // For T-junctions, only propagate along terminating roads
            // For mid-spline crossings and other types, propagate along all roads
            IEnumerable<JunctionContributor> contributorsToPropagate;
            
            if (junction.Type == JunctionType.TJunction)
            {
                contributorsToPropagate = junction.GetTerminatingRoads();
            }
            else
            {
                contributorsToPropagate = junction.Contributors;
            }

            foreach (var contributor in contributorsToPropagate)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                    continue;

                // Get blend distance from spline parameters or use global
                var blendDistance = globalBlendDistance;
                var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters;
                if (junctionParams != null) blendDistance = junctionParams.JunctionBlendDistanceMeters;

                // Get blend function type
                var blendFunctionType = junctionParams?.BlendFunctionType ?? JunctionBlendFunctionType.Cosine;

                // For mid-spline crossings, collect influences bidirectionally
                if (junction.Type == JunctionType.MidSplineCrossing)
                {
                    CollectBidirectionalInfluences(
                        splineSections, 
                        contributor.CrossSection, 
                        junction,
                        blendDistance, 
                        blendFunctionType,
                        crossSectionInfluences);
                }
                else
                {
                    // For endpoints (Y, X, Complex), collect influences from the endpoint
                    var distances = CalculateDistancesFromEndpoint(splineSections, contributor.IsSplineStart);

                    for (var i = 0; i < splineSections.Count; i++)
                    {
                        var dist = distances[i];
                        if (dist >= blendDistance) continue; // Outside blend zone

                        var cs = splineSections[i];
                        
                        // Calculate blend factor using configured function
                        var t = dist / blendDistance;
                        var blend = ApplyBlendFunction(t, blendFunctionType);

                        // Weight is inverse of blend: closer = higher weight
                        // At junction center (t=0, blend=0): weight = 1.0 (full junction influence)
                        // At blend boundary (t=1, blend=1): weight = 0.0 (no junction influence)
                        var weight = 1.0f - blend;
                        
                        if (weight > 0.001f)
                        {
                            if (!crossSectionInfluences.TryGetValue(cs.Index, out var influences))
                            {
                                influences = new List<(float, float, int)>();
                                crossSectionInfluences[cs.Index] = influences;
                            }
                            influences.Add((junction.HarmonizedElevation, weight, junction.JunctionId));
                        }
                    }
                }
            }
        }

        // Log junction influence statistics
        var multiInfluenceCount = crossSectionInfluences.Count(kvp => kvp.Value.Count > 1);
        if (multiInfluenceCount > 0)
        {
            TerrainLogger.Detail($"  {multiInfluenceCount} cross-sections have overlapping junction influences (will be blended)");
            var maxInfluences = crossSectionInfluences.Max(kvp => kvp.Value.Count);
            if (maxInfluences > 2)
            {
                TerrainLogger.Detail($"  Maximum overlapping influences: {maxInfluences}");
            }
        }

        // Second pass: Apply weighted average of all influences
        foreach (var (csIndex, influences) in crossSectionInfluences)
        {
            if (!originalElevations.TryGetValue(csIndex, out var originalElevation))
                continue;

            var cs = network.CrossSections.FirstOrDefault(c => c.Index == csIndex);
            if (cs == null)
                continue;

            // Calculate weighted average of all junction influences
            var totalWeight = influences.Sum(inf => inf.weight);
            
            if (totalWeight < 0.001f)
                continue;

            // Weighted elevation from all junctions
            var weightedJunctionElevation = influences.Sum(inf => inf.elevation * inf.weight) / totalWeight;

            // The total junction influence determines how much we blend from original
            // Cap at 1.0 to avoid over-correction when many junctions overlap
            var totalInfluence = MathF.Min(totalWeight, 1.0f);

            // Final elevation: blend between weighted junction average and original
            var newElevation = weightedJunctionElevation * totalInfluence + originalElevation * (1.0f - totalInfluence);

            if (MathF.Abs(newElevation - cs.TargetElevation) > 0.001f)
            {
                cs.TargetElevation = newElevation;
                modifiedCount++;
            }
        }

        // Third pass: Propagate edge constraints for T-junctions
        // This is separate from elevation propagation because edge constraints only apply to terminating roads
        var edgeConstraintCount = PropagateEdgeConstraintsForTJunctions(
            network, 
            junctions.Where(j => j.Type == JunctionType.TJunction && !j.IsExcluded).ToList(),
            crossSectionsBySpline,
            globalBlendDistance);

        TerrainLogger.Detail($"  Propagated junction constraints to {modifiedCount} cross-sections");
        if (edgeConstraintCount > 0)
        {
            TerrainLogger.Detail($"  Propagated edge constraints to {edgeConstraintCount} cross-sections along terminating roads");
        }

        return modifiedCount;
    }

    /// <summary>
    /// Propagates edge constraints from T-junction terminating cross-sections along the terminating road.
    /// Uses SURFACE-FOLLOWING approach: instead of interpolating from a fixed junction constraint,
    /// each cross-section's edges are projected onto the primary road's surface to calculate fresh
    /// constraints. This handles sloped primary roads correctly - the terminating road "wraps around"
    /// to match the continuously changing primary surface elevation.
    /// 
    /// CRITICAL: Also updates the centerline TargetElevation to follow the primary surface,
    /// which prevents the "jagged junction" artifact where edges are correct but the center doesn't match.
    /// 
    /// IMPROVEMENT: For each terminating cross-section, finds the NEAREST primary road cross-section
    /// rather than using the fixed junction cross-section. This handles curved/varying-slope primary
    /// roads more accurately.
    /// </summary>
    /// <returns>Number of cross-sections that received propagated edge constraints.</returns>
    private int PropagateEdgeConstraintsForTJunctions(
        UnifiedRoadNetwork network,
        List<NetworkJunction> tJunctions,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float globalBlendDistance)
    {
        var propagatedCount = 0;

        foreach (var junction in tJunctions)
        {
            // Get the primary (continuous) road for surface calculations
            var continuousRoads = junction.GetContinuousRoads().ToList();
            if (continuousRoads.Count == 0)
                continue;
            
            var primaryContributor = continuousRoads.OrderByDescending(c => c.Spline.Priority).First();
            var primarySplineId = primaryContributor.Spline.SplineId;
            
            // Get all cross-sections for the primary road (needed for finding nearest)
            if (!crossSectionsBySpline.TryGetValue(primarySplineId, out var primarySections))
                continue;
            
            // Calculate the primary road's slope for surface calculations
            var primarySlope = _crossSectionsBySpline != null 
                ? CalculatePrimaryRoadSlope(primaryContributor) 
                : 0f;
            if (float.IsNaN(primarySlope))
                primarySlope = 0f;

            foreach (var terminating in junction.GetTerminatingRoads())
            {
                var terminatingCs = terminating.CrossSection;
                
                // Skip if no constraints were set on this terminating cross-section
                if (!terminatingCs.HasJunctionConstraint)
                    continue;

                if (!crossSectionsBySpline.TryGetValue(terminating.Spline.SplineId, out var splineSections))
                    continue;

                // Get blend distance from spline parameters or use global
                var blendDistance = globalBlendDistance;
                var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters;
                if (junctionParams != null)
                    blendDistance = junctionParams.JunctionBlendDistanceMeters;

                // Get blend function type
                var blendFunctionType = junctionParams?.BlendFunctionType ?? JunctionBlendFunctionType.Cosine;

                // Calculate distances from the terminating endpoint
                var distances = CalculateDistancesFromEndpoint(splineSections, terminating.IsSplineStart);

                // Propagate constraints along the terminating road using SURFACE-FOLLOWING approach
                for (var i = 0; i < splineSections.Count; i++)
                {
                    var dist = distances[i];
                    
                    // Skip the junction cross-section itself (it already has constraints)
                    if (splineSections[i].Index == terminatingCs.Index)
                        continue;
                    
                    // Outside blend zone
                    if (dist >= blendDistance)
                        continue;

                    var cs = splineSections[i];
                    
                    // Calculate blend factor using configured function
                    var t = dist / blendDistance;
                    var blend = ApplyBlendFunction(t, blendFunctionType);

                    // Weight decreases with distance: at junction = 1.0, at blend boundary = 0.0
                    var weight = 1.0f - blend;
                    
                    if (weight > 0.001f)
                    {
                        // IMPROVEMENT: Find the nearest primary cross-section for this terminating cross-section
                        // This provides more accurate surface projection for curved/varying-slope primary roads
                        var nearestPrimaryCs = FindNearestPrimaryCrossSection(cs.CenterPoint, primarySections);
                        
                        // Calculate local slope at the nearest primary cross-section
                        var localPrimarySlope = CalculateLocalSlopeAtCrossSection(primarySections, nearestPrimaryCs);
                        if (float.IsNaN(localPrimarySlope))
                            localPrimarySlope = primarySlope; // Fallback to junction slope
                        
                        // SURFACE-FOLLOWING: Calculate fresh constraints by projecting this cross-section's
                        // edges AND CENTERLINE onto the primary road's surface, then blend with natural elevations
                        var (interpolatedLeft, interpolatedRight, interpolatedCenter) = 
                            JunctionSurfaceCalculator.CalculateFullSurfaceFollowingConstraints(
                                cs,
                                nearestPrimaryCs,
                                localPrimarySlope,
                                weight);

                        // Apply interpolated edge constraints
                        if (interpolatedLeft.HasValue)
                            cs.ConstrainedLeftEdgeElevation = interpolatedLeft.Value;
                        if (interpolatedRight.HasValue)
                            cs.ConstrainedRightEdgeElevation = interpolatedRight.Value;
                        
                        // CRITICAL: Also update centerline TargetElevation to follow the primary surface
                        // This ensures the entire cross-section lies flat on the primary road, not just the edges
                        if (interpolatedCenter.HasValue)
                            cs.TargetElevation = interpolatedCenter.Value;

                        propagatedCount++;
                    }
                }
            }
        }

        return propagatedCount;
    }
    
    /// <summary>
    /// Finds the nearest cross-section on the primary road to a given world position.
    /// </summary>
    private static UnifiedCrossSection FindNearestPrimaryCrossSection(
        Vector2 worldPos,
        List<UnifiedCrossSection> primarySections)
    {
        UnifiedCrossSection nearest = primarySections[0];
        var minDist = float.MaxValue;
        
        foreach (var cs in primarySections)
        {
            var dist = Vector2.Distance(worldPos, cs.CenterPoint);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = cs;
            }
        }
        
        return nearest;
    }
    
    /// <summary>
    /// Calculates the local longitudinal slope at a specific cross-section.
    /// </summary>
    private static float CalculateLocalSlopeAtCrossSection(
        List<UnifiedCrossSection> sections,
        UnifiedCrossSection targetCs)
    {
        var targetIndex = sections.FindIndex(cs => cs.Index == targetCs.Index);
        if (targetIndex < 0)
            return float.NaN;
        
        return JunctionSurfaceCalculator.CalculateLocalSlope(sections, targetIndex, sampleRadius: 3);
    }

    /// <summary>
    ///     Collects junction influences bidirectionally from a mid-spline crossing point.
    ///     Used in the first pass of the multi-junction influence algorithm.
    /// </summary>
    private void CollectBidirectionalInfluences(
        List<UnifiedCrossSection> splineSections,
        UnifiedCrossSection crossingSection,
        NetworkJunction junction,
        float blendDistance,
        JunctionBlendFunctionType blendFunctionType,
        Dictionary<int, List<(float elevation, float weight, int junctionId)>> crossSectionInfluences)
    {
        // Find the index of the crossing section (or closest to it)
        var crossingIndex = splineSections.FindIndex(cs => cs.Index == crossingSection.Index);
        if (crossingIndex < 0)
        {
            // Find closest by position
            var minDist = float.MaxValue;
            for (int i = 0; i < splineSections.Count; i++)
            {
                var dist = Vector2.Distance(splineSections[i].CenterPoint, crossingSection.CenterPoint);
                if (dist < minDist)
                {
                    minDist = dist;
                    crossingIndex = i;
                }
            }
        }

        if (crossingIndex < 0) return;

        // Propagate in the "backward" direction (toward spline start)
        var cumulativeDist = 0f;
        for (int i = crossingIndex; i >= 0; i--)
        {
            if (i < crossingIndex)
            {
                cumulativeDist += Vector2.Distance(splineSections[i].CenterPoint, splineSections[i + 1].CenterPoint);
            }

            if (cumulativeDist >= blendDistance) break;

            var cs = splineSections[i];
            var t = cumulativeDist / blendDistance;
            var blend = ApplyBlendFunction(t, blendFunctionType);
            var weight = 1.0f - blend;

            if (weight > 0.001f)
            {
                if (!crossSectionInfluences.TryGetValue(cs.Index, out var influences))
                {
                    influences = new List<(float, float, int)>();
                    crossSectionInfluences[cs.Index] = influences;
                }
                influences.Add((junction.HarmonizedElevation, weight, junction.JunctionId));
            }
        }

        // Propagate in the "forward" direction (toward spline end)
        cumulativeDist = 0f;
        for (int i = crossingIndex; i < splineSections.Count; i++)
        {
            if (i > crossingIndex)
            {
                cumulativeDist += Vector2.Distance(splineSections[i].CenterPoint, splineSections[i - 1].CenterPoint);
            }

            if (cumulativeDist >= blendDistance) break;

            var cs = splineSections[i];
            var t = cumulativeDist / blendDistance;
            var blend = ApplyBlendFunction(t, blendFunctionType);
            var weight = 1.0f - blend;

            if (weight > 0.001f)
            {
                if (!crossSectionInfluences.TryGetValue(cs.Index, out var influences))
                {
                    influences = new List<(float, float, int)>();
                    crossSectionInfluences[cs.Index] = influences;
                }
                influences.Add((junction.HarmonizedElevation, weight, junction.JunctionId));
            }
        }
    }

    /// <summary>
    ///     Applies endpoint tapering for isolated endpoints (dead ends).
    ///     Gradually transitions the road elevation back toward terrain.
    /// </summary>
    /// <returns>Number of cross-sections modified.</returns>
    private int ApplyEndpointTapering(
        UnifiedRoadNetwork network,
        List<NetworkJunction> junctions,
        float[,] heightMap,
        float metersPerPixel)
    {
        var taperedCount = 0;
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        // Cache cross-sections by spline
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        foreach (var junction in junctions.Where(j => j.Type == JunctionType.Endpoint && !j.IsExcluded))
        foreach (var contributor in junction.Contributors)
        {
            var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();

            if (!junctionParams.EnableEndpointTaper)
                continue;

            if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                continue;

            var taperDistance = junctionParams.EndpointTaperDistanceMeters;

            // Get terrain elevation at endpoint
            var px = (int)(junction.Position.X / metersPerPixel);
            var py = (int)(junction.Position.Y / metersPerPixel);
            px = Math.Clamp(px, 0, mapWidth - 1);
            py = Math.Clamp(py, 0, mapHeight - 1);
            var terrainElevation = heightMap[py, px];

            // Calculate distances from endpoint
            var distances = CalculateDistancesFromEndpoint(splineSections, contributor.IsSplineStart);

            // Apply taper
            for (var i = 0; i < splineSections.Count; i++)
            {
                var dist = distances[i];
                if (dist >= taperDistance) continue;

                var cs = splineSections[i];
                var originalElevation = cs.TargetElevation;

                // Use quintic smoothstep for very smooth taper
                var t = dist / taperDistance;
                var blend = t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);

                // Calculate target at endpoint (blend toward terrain)
                var targetAtEndpoint = originalElevation * (1.0f - junctionParams.EndpointTerrainBlendStrength)
                                       + terrainElevation * junctionParams.EndpointTerrainBlendStrength;

                cs.TargetElevation = targetAtEndpoint * (1.0f - blend) + originalElevation * blend;

                if (MathF.Abs(cs.TargetElevation - originalElevation) > 0.001f)
                    taperedCount++;
            }
        }

        return taperedCount;
    }

    /// <summary>
    ///     Calculates cumulative distances from a spline endpoint.
    /// </summary>
    private float[] CalculateDistancesFromEndpoint(List<UnifiedCrossSection> sections, bool fromStart)
    {
        var distances = new float[sections.Count];

        if (fromStart)
        {
            distances[0] = 0;
            for (var i = 1; i < sections.Count; i++)
                distances[i] = distances[i - 1] +
                               Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
        }
        else
        {
            distances[sections.Count - 1] = 0;
            for (var i = sections.Count - 2; i >= 0; i--)
                distances[i] = distances[i + 1] +
                               Vector2.Distance(sections[i].CenterPoint, sections[i + 1].CenterPoint);
        }

        return distances;
    }

    /// <summary>
    ///     Applies plateau smoothing to multi-way junctions (Y, CrossRoads, Complex) to reduce dents.
    ///     The problem: When multiple roads at different elevations meet, the weighted average
    ///     can create a "dent" where one road's lower elevation pulls down the junction center.
    ///     The solution: Sample ORIGINAL elevations (before harmonization) from further back along
    ///     each road, compute a smoother plateau elevation from these stable samples, and apply it
    ///     to cross-sections within a "plateau radius" of the junction center.
    ///     CRITICAL: We must use preHarmonizationElevations to get the original road elevations,
    ///     NOT the current TargetElevation values which have already been modified by propagation.
    ///     
    ///     NOTE: This method intentionally EXCLUDES T-junctions. T-junction surface matching is
    ///     handled by junction surface constraints (Phase 3) which set ConstrainedLeftEdgeElevation
    ///     and ConstrainedRightEdgeElevation on terminating road cross-sections.
    ///     See JUNCTION_SURFACE_CONSTRAINT_IMPLEMENTATION_PLAN.md for details.
    /// </summary>
    /// <returns>Number of cross-sections modified.</returns>
    private int ApplyMultiWayJunctionPlateauSmoothing(
        UnifiedRoadNetwork network,
        List<NetworkJunction> junctions,
        float globalBlendDistance,
        Dictionary<int, float> preHarmonizationElevations)
    {
        var smoothedCount = 0;

        // Only process multi-way junctions (Y, CrossRoads, Complex) - not T-junctions, isolated endpoints, or excluded
        var multiWayJunctions = junctions.Where(j =>
            !j.IsExcluded &&
            (j.Type == JunctionType.YJunction ||
            j.Type == JunctionType.CrossRoads ||
            j.Type == JunctionType.Complex)).ToList();

        if (multiWayJunctions.Count == 0)
            return 0;

        TerrainLogger.Detail($"  Applying plateau smoothing to {multiWayJunctions.Count} multi-way junction(s)...");

        // Cache cross-sections by spline for faster access
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        foreach (var junction in multiWayJunctions)
        {
            // Get the maximum road width at this junction (used to determine plateau radius)
            var maxRoadWidth = junction.Contributors.Max(c => c.Spline.Parameters.RoadWidthMeters);

            // Plateau radius: covers the junction area where all roads meet
            // For complex junctions with many contributors, increase the radius
            // Base: 2x max road width, plus additional for each contributor beyond 2
            var contributorCount = junction.Contributors.Count;
            var baseRadius = maxRoadWidth * 2.0f;
            var additionalRadius = Math.Max(0, contributorCount - 2) * maxRoadWidth * 0.5f;
            var plateauRadius = baseRadius + additionalRadius;

            // Also ensure minimum plateau radius based on blend distance
            plateauRadius = MathF.Max(plateauRadius, globalBlendDistance * 0.5f);

            // Reference distance: sample ORIGINAL elevations from this far back along each road
            // Use 100% of blend distance to get samples OUTSIDE the blend zone
            // This ensures we're sampling truly stable, unmodified road elevations
            var referenceDistance = globalBlendDistance * 1.0f;

            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId} ({junction.Type}): {contributorCount} contributors, " +
                $"plateau radius={plateauRadius:F1}m, ref dist={referenceDistance:F1}m");

            // Collect ORIGINAL (pre-harmonization) reference elevations from each contributing spline
            var referenceElevations = new List<(float elevation, float priority, int splineId)>();

            foreach (var contributor in junction.Contributors)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                    continue;

                // Calculate distances from the endpoint
                var distances = CalculateDistancesFromEndpoint(splineSections, contributor.IsSplineStart);

                // Find the cross-section closest to the reference distance
                UnifiedCrossSection? referenceCs = null;
                var closestDistDiff = float.MaxValue;

                for (var i = 0; i < splineSections.Count; i++)
                {
                    var distDiff = MathF.Abs(distances[i] - referenceDistance);
                    if (distDiff < closestDistDiff)
                    {
                        closestDistDiff = distDiff;
                        referenceCs = splineSections[i];
                    }
                }

                if (referenceCs != null)
                {
                    // CRITICAL: Use the ORIGINAL elevation from preHarmonizationElevations
                    // This is the elevation before any junction blending was applied
                    if (preHarmonizationElevations.TryGetValue(referenceCs.Index, out var originalElevation) &&
                        !float.IsNaN(originalElevation))
                        referenceElevations.Add((originalElevation, contributor.Spline.Priority,
                            contributor.Spline.SplineId));
                    else if (!float.IsNaN(referenceCs.TargetElevation))
                        // Fallback: if not in dictionary, use current value (shouldn't happen normally)
                        referenceElevations.Add((referenceCs.TargetElevation, contributor.Spline.Priority,
                            contributor.Spline.SplineId));
                }
            }

            if (referenceElevations.Count == 0)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"Junction #{junction.JunctionId}: No valid reference elevations found, skipping");
                continue;
            }

            // Compute plateau elevation as priority-weighted average of ORIGINAL reference elevations
            var totalPriority = referenceElevations.Sum(r => r.priority);
            var plateauElevation = totalPriority > 0
                ? referenceElevations.Sum(r => r.elevation * r.priority) / totalPriority
                : referenceElevations.Average(r => r.elevation);

            // Log elevation range for debugging
            var minElev = referenceElevations.Min(r => r.elevation);
            var maxElev = referenceElevations.Max(r => r.elevation);
            var elevRange = maxElev - minElev;

            TerrainCreationLogger.Current?.Detail(
                $"Junction #{junction.JunctionId}: ORIGINAL reference elevations range [{minElev:F2}, {maxElev:F2}] (range={elevRange:F2}m), " +
                $"plateau={plateauElevation:F2}m, harmonized={junction.HarmonizedElevation:F2}m");

            // For junctions with significant elevation differences, use the HIGHER elevation
            // to prevent dents. Roads going downhill will ramp down; roads going uphill will ramp up.
            if (elevRange > 1.0f)
            {
                // Use the maximum elevation weighted by priority to prevent dents
                // The highest-priority road at the highest elevation should dominate
                var maxPriorityAtHighElev = referenceElevations
                    .Where(r => r.elevation >= maxElev - 0.5f)
                    .Sum(r => r.priority);
                var totalMaxElev = referenceElevations
                    .Where(r => r.elevation >= maxElev - 0.5f)
                    .Sum(r => r.elevation * r.priority);

                if (maxPriorityAtHighElev > 0)
                {
                    var highElevAvg = totalMaxElev / maxPriorityAtHighElev;
                    // Bias toward higher elevation to prevent dents (70% high, 30% weighted average)
                    plateauElevation = highElevAvg * 0.7f + plateauElevation * 0.3f;
                    TerrainCreationLogger.Current?.Detail(
                        $"Junction #{junction.JunctionId}: Large elev range detected, biasing plateau UP to {plateauElevation:F2}m");
                }
            }

            // Apply plateau elevation to cross-sections within plateau radius
            var junctionSmoothedCount = 0;
            foreach (var contributor in junction.Contributors)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                    continue;

                foreach (var cs in splineSections)
                {
                    var distToJunction = Vector2.Distance(cs.CenterPoint, junction.Position);

                    if (distToJunction > plateauRadius)
                        continue;

                    var currentElevation = cs.TargetElevation;
                    if (float.IsNaN(currentElevation))
                        continue;

                    // Smooth blend: full plateau at center, gradual transition at edge
                    // Using quintic smoothstep for very smooth transition
                    var t = distToJunction / plateauRadius;
                    var blend = t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f); // quintic smoothstep

                    // At center (t=0): use plateauElevation
                    // At edge (t=1): keep current elevation
                    var newElevation = plateauElevation * (1.0f - blend) + currentElevation * blend;

                    if (MathF.Abs(newElevation - currentElevation) > 0.001f)
                                    {
                                        cs.TargetElevation = newElevation;
                                        junctionSmoothedCount++;
                                        smoothedCount++;
                                    }
                                }
                            }

                            TerrainCreationLogger.Current?.Detail($"Junction #{junction.JunctionId}: Smoothed {junctionSmoothedCount} cross-sections");
                        }

                        return smoothedCount;
                    }

    /// <summary>
    ///     Applies the configured blend function.
    /// </summary>
    private float ApplyBlendFunction(float t, JunctionBlendFunctionType functionType)
    {
        return functionType switch
        {
            JunctionBlendFunctionType.Linear => t,
            JunctionBlendFunctionType.Cosine => 0.5f - 0.5f * MathF.Cos(MathF.PI * t),
            JunctionBlendFunctionType.Cubic => t * t * (3.0f - 2.0f * t),
            JunctionBlendFunctionType.Quintic => t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f),
            _ => t
        };
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

        // Draw detected junctions
        foreach (var junction in network.Junctions)
        {
            var jx = (int)(junction.Position.X / metersPerPixel);
            var jy = imageHeight - 1 - (int)(junction.Position.Y / metersPerPixel);

            var junctionColor = junction.Type switch
            {
                JunctionType.Endpoint => new Rgba32(255, 255, 0, 255), // Yellow
                JunctionType.TJunction => new Rgba32(0, 255, 255, 255), // Cyan
                JunctionType.YJunction => new Rgba32(0, 255, 0, 255), // Green
                JunctionType.CrossRoads => new Rgba32(255, 128, 0, 255), // Orange
                JunctionType.Complex => new Rgba32(255, 0, 255, 255), // Magenta
                JunctionType.MidSplineCrossing => new Rgba32(255, 64, 128, 255), // Pink/Coral
                _ => new Rgba32(255, 255, 255, 255)
            };

            var radius = junction.Type == JunctionType.Endpoint ? 4 : 7;
            DrawFilledCircle(image, jx, jy, radius, junctionColor);

            // Draw cross-material indicator
            if (junction.IsCrossMaterial) DrawCircleOutline(image, jx, jy, radius + 3, new Rgba32(255, 255, 255, 200));
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

    #endregion
}

/// <summary>
///     Result of network junction harmonization.
/// </summary>
public class HarmonizationResult
{
    /// <summary>
    ///     Number of cross-sections whose elevation was modified during propagation.
    /// </summary>
    public int PropagatedCrossSections { get; set; }

    /// <summary>
    ///     Number of cross-sections modified during endpoint tapering.
    /// </summary>
    public int TaperedCrossSections { get; set; }

    /// <summary>
    ///     Number of cross-sections modified during multi-way junction plateau smoothing.
    /// </summary>
    public int PlateauSmoothedCrossSections { get; set; }

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
    public bool Success => ModifiedCrossSections > 0 || PropagatedCrossSections == 0;
}