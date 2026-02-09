using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Harmonizes elevation for roundabout junctions.
///     The roundabout ring should have consistent elevation around its circumference.
///     This harmonizer:
///     1. Calculates a uniform elevation for each roundabout ring based on terrain and connection points
///     2. Applies the uniform elevation to all ring cross-sections
///     3. Blends connecting roads toward the roundabout elevation at their junction points
///     Integration points:
///     - Called after NetworkJunctionDetector.DetectRoundaboutJunctions() populates roundabout junction info
///     - Should be called AFTER initial elevation calculation but BEFORE general junction harmonization
///     so that roundabout junctions are already at their target elevation when other roads blend to them
/// </summary>
public class RoundaboutElevationHarmonizer
{
    /// <summary>
    ///     Harmonizes elevations for all roundabouts in the network.
    ///     Algorithm:
    ///     1. For each roundabout junction info:
    ///     a. Collect all ring cross-sections
    ///     b. Calculate the harmonized ring elevation (terrain average or weighted with connections)
    ///     c. Apply uniform elevation to all ring cross-sections
    ///     d. Store the harmonized elevation in RoundaboutJunctionInfo
    ///     2. For each connecting road junction:
    ///     a. Apply elevation blending from the roundabout elevation back along the road
    /// </summary>
    /// <param name="network">The unified road network.</param>
    /// <param name="roundaboutJunctionInfos">Information about detected roundabout junctions.</param>
    /// <param name="heightMap">The original terrain heightmap (for terrain elevation sampling).</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <param name="globalBlendDistance">Default blend distance for connecting roads.</param>
    /// <returns>Result containing statistics about the harmonization.</returns>
    public RoundaboutHarmonizationResult HarmonizeRoundaboutElevations(
        UnifiedRoadNetwork network,
        List<RoundaboutJunctionInfo> roundaboutJunctionInfos,
        float[,] heightMap,
        float metersPerPixel,
        float globalBlendDistance = 30.0f)
    {
        TerrainLogger.SuppressDetailedLogging = true;
        var result = new RoundaboutHarmonizationResult();
        var perfLog = TerrainCreationLogger.Current;

        if (roundaboutJunctionInfos.Count == 0)
        {
            TerrainLogger.Detail("RoundaboutElevationHarmonizer: No roundabout junctions to process");
            return result;
        }

        TerrainLogger.Info("=== ROUNDABOUT ELEVATION HARMONIZATION ===");
        TerrainLogger.Info($"  Processing {roundaboutJunctionInfos.Count} roundabout(s)");

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        // Cache cross-sections by spline for faster access
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        foreach (var roundaboutInfo in roundaboutJunctionInfos)
        {
            var ringSplineId = roundaboutInfo.RoundaboutSplineId;

            if (!crossSectionsBySpline.TryGetValue(ringSplineId, out var ringCrossSections))
            {
                TerrainLogger.Warning($"  Roundabout {ringSplineId}: No cross-sections found");
                continue;
            }

            // Step 1: Calculate the harmonized elevation for this roundabout ring
            var ringElevation = CalculateRoundaboutElevation(
                roundaboutInfo,
                ringCrossSections,
                heightMap,
                metersPerPixel,
                mapWidth,
                mapHeight,
                network);

            if (float.IsNaN(ringElevation))
            {
                TerrainLogger.Warning($"  Roundabout {ringSplineId}: Could not calculate ring elevation");
                continue;
            }

            // Store the harmonized elevation
            roundaboutInfo.HarmonizedElevation = ringElevation;
            result.RoundaboutElevations[ringSplineId] = ringElevation;

            // Step 2: Apply uniform elevation to all ring cross-sections
            var maxElevChange = result.MaxElevationChange;
            var ringModified = ApplyUniformRingElevation(
                ringCrossSections,
                ringElevation,
                roundaboutInfo,
                network,
                ref maxElevChange);
            result.MaxElevationChange = maxElevChange;

            result.RingCrossSectionsModified += ringModified;

            TerrainLogger.Detail($"  Roundabout {ringSplineId}: elevation={ringElevation:F2}m, " +
                                 $"{ringModified} ring cross-sections modified");

            // Step 3: Blend connecting roads toward the roundabout elevation
            var connectingBlended = BlendConnectingRoads(
                roundaboutInfo,
                crossSectionsBySpline,
                network,
                globalBlendDistance,
                ref maxElevChange);
            result.MaxElevationChange = maxElevChange;

            result.ConnectingRoadCrossSectionsBlended += connectingBlended;
            result.RoundaboutsProcessed++;

            TerrainLogger.Detail(
                $"  Roundabout {ringSplineId}: {connectingBlended} connecting road cross-sections blended");

            // Step 4: Mark all roundabout junctions as excluded from general harmonization
            // This prevents double-processing by NetworkJunctionHarmonizer
            foreach (var junction in roundaboutInfo.Junctions)
                if (junction.ParentJunction != null)
                {
                    junction.ParentJunction.IsExcluded = true;
                    junction.ParentJunction.ExclusionReason =
                        "Roundabout junction - handled by RoundaboutElevationHarmonizer";
                }
        }

        TerrainLogger.Info($"  RESULT: {result.RoundaboutsProcessed} roundabout(s) processed");
        TerrainLogger.Info($"  RESULT: {result.RingCrossSectionsModified} ring cross-sections modified");
        TerrainLogger.Info(
            $"  RESULT: {result.ConnectingRoadCrossSectionsBlended} connecting road cross-sections blended");
        TerrainLogger.Info($"  RESULT: Max elevation change: {result.MaxElevationChange:F3}m");
        TerrainLogger.Info("=== ROUNDABOUT ELEVATION HARMONIZATION COMPLETE ===");
        TerrainLogger.SuppressDetailedLogging = false;

        return result;
    }

    /// <summary>
    ///     Calculates the harmonized elevation for a roundabout ring.
    ///     Strategy (based on ForceUniformRoundaboutElevation setting):
    ///     - If ForceUniformRoundaboutElevation is true:
    ///     Use weighted average of:
    ///     1. Average terrain elevation around the ring (weight: 1.0)
    ///     2. Connecting road elevations at their endpoints (weight: road priority)
    ///     - If ForceUniformRoundaboutElevation is false:
    ///     Allow gradual elevation changes (not implemented yet - future enhancement)
    /// </summary>
    private float CalculateRoundaboutElevation(
        RoundaboutJunctionInfo roundaboutInfo,
        List<UnifiedCrossSection> ringCrossSections,
        float[,] heightMap,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        UnifiedRoadNetwork network)
    {
        // Get parameters from the roundabout spline
        var roundaboutSpline = network.GetSplineById(roundaboutInfo.RoundaboutSplineId);
        var junctionParams = roundaboutSpline?.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();

        // Calculate average terrain elevation around the ring
        var terrainElevationSum = 0f;
        var terrainCount = 0;

        foreach (var cs in ringCrossSections)
        {
            var px = (int)(cs.CenterPoint.X / metersPerPixel);
            var py = (int)(cs.CenterPoint.Y / metersPerPixel);

            if (px >= 0 && px < mapWidth && py >= 0 && py < mapHeight)
            {
                terrainElevationSum += heightMap[py, px];
                terrainCount++;
            }
        }

        var averageTerrainElevation = terrainCount > 0
            ? terrainElevationSum / terrainCount
            : 0f;

        // If no connections, use terrain average
        if (roundaboutInfo.Junctions.Count == 0) return averageTerrainElevation;

        // Collect connecting road elevations weighted by priority
        var connectionElevationSum = 0f;
        var connectionPrioritySum = 0f;

        foreach (var junction in roundaboutInfo.Junctions)
        {
            var connectingSpline = network.GetSplineById(junction.ConnectingRoadSplineId);
            if (connectingSpline == null)
                continue;

            // Get the endpoint cross-section of the connecting road
            var endpointCs = junction.ParentJunction.Contributors
                .FirstOrDefault(c => c.Spline.SplineId == junction.ConnectingRoadSplineId)
                ?.CrossSection;

            if (endpointCs != null && !float.IsNaN(endpointCs.TargetElevation))
            {
                var priority = (float)connectingSpline.Priority;
                connectionElevationSum += endpointCs.TargetElevation * priority;
                connectionPrioritySum += priority;
            }
        }

        // Calculate weighted average:
        // - Terrain elevation with weight 1.0
        // - Connection elevations with weight based on total priority (normalized)
        // This gives more influence to higher-priority roads while still considering terrain

        if (connectionPrioritySum <= 0) return averageTerrainElevation;

        // Normalize connection weight - use sqrt to reduce dominance of very high priority roads
        var connectionWeight = MathF.Sqrt(connectionPrioritySum / roundaboutInfo.Junctions.Count);
        var terrainWeight = 1.0f;

        var totalWeight = terrainWeight + connectionWeight;
        var connectionAverageElevation = connectionElevationSum / connectionPrioritySum;

        var harmonizedElevation = (averageTerrainElevation * terrainWeight +
                                   connectionAverageElevation * connectionWeight) / totalWeight;

        TerrainCreationLogger.Current?.Detail(
            $"  Roundabout {roundaboutInfo.RoundaboutSplineId}: " +
            $"terrain avg={averageTerrainElevation:F2}m, " +
            $"connection avg={connectionAverageElevation:F2}m, " +
            $"harmonized={harmonizedElevation:F2}m");

        return harmonizedElevation;
    }

    /// <summary>
    ///     Applies uniform elevation to all ring cross-sections.
    ///     When ForceUniformRoundaboutElevation is false on ALL connecting roads, preserves the original
    ///     calculated elevation for the ring (allows gradual changes around the ring).
    ///     Also updates per-junction target elevations:
    ///     - When ForceUniformRoundaboutElevation is true on any connecting road: uses the global harmonized elevation for
    ///     that junction
    ///     - When false: each junction uses the ring elevation at its specific connection point
    ///     NOTE: The ring elevation is forced to uniform only if at least one connecting road has
    ///     ForceUniformRoundaboutElevation = true. If all connecting roads have it set to false,
    ///     the ring will follow terrain naturally.
    /// </summary>
    private int ApplyUniformRingElevation(
        List<UnifiedCrossSection> ringCrossSections,
        float ringElevation,
        RoundaboutJunctionInfo roundaboutInfo,
        UnifiedRoadNetwork network,
        ref float maxElevationChange)
    {
        var modifiedCount = 0;

        // Check if ANY connecting road wants uniform elevation
        // If so, we need to apply uniform elevation to the ring
        var anyConnectingRoadWantsUniform = roundaboutInfo.Junctions.Any(junction =>
        {
            var connectingSpline = network.GetSplineById(junction.ConnectingRoadSplineId);
            var junctionParams = connectingSpline?.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            return junctionParams.ForceUniformRoundaboutElevation;
        });

        foreach (var cs in ringCrossSections)
        {
            if (float.IsNaN(cs.TargetElevation))
            {
                cs.TargetElevation = ringElevation;
                modifiedCount++;
                continue;
            }

            var elevationChange = MathF.Abs(ringElevation - cs.TargetElevation);

            if (anyConnectingRoadWantsUniform)
                // Force uniform elevation around the entire ring because at least one
                // connecting road has ForceUniformRoundaboutElevation = true
                if (elevationChange > 0.001f)
                {
                    maxElevationChange = MathF.Max(maxElevationChange, elevationChange);
                    cs.TargetElevation = ringElevation;
                    modifiedCount++;
                }
            // When ALL roads have ForceUniformRoundaboutElevation = false, do NOT modify the ring cross-sections.
            // This preserves the original calculated elevations which may vary around the ring
            // to follow terrain slope. The ring will naturally follow the terrain rather than
            // being forced to a single elevation.
        }

        // Update target elevation on all roundabout junctions
        // Each junction uses its connecting road's ForceUniformRoundaboutElevation setting
        foreach (var junction in roundaboutInfo.Junctions)
        {
            var connectingSpline = network.GetSplineById(junction.ConnectingRoadSplineId);
            var junctionParams = connectingSpline?.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();

            if (junctionParams.ForceUniformRoundaboutElevation)
                // Use global harmonized elevation for this junction
                junction.TargetElevation = ringElevation;
            else
                // Use local ring elevation at the specific connection point
                junction.TargetElevation = GetRingElevationAtConnectionPoint(
                    junction.ConnectionPointMeters,
                    ringCrossSections,
                    ringElevation);
        }

        return modifiedCount;
    }

    /// <summary>
    ///     Gets the ring elevation at a specific connection point.
    ///     Finds the closest ring cross-section to the connection point and returns its elevation.
    ///     Falls back to the harmonized elevation if the connection point cannot be found.
    /// </summary>
    /// <param name="connectionPoint">The connection point on the roundabout ring (in meters).</param>
    /// <param name="ringCrossSections">All cross-sections of the roundabout ring.</param>
    /// <param name="fallbackElevation">Elevation to use if no valid cross-section is found.</param>
    /// <returns>The ring elevation at the connection point.</returns>
    private static float GetRingElevationAtConnectionPoint(
        Vector2 connectionPoint,
        List<UnifiedCrossSection> ringCrossSections,
        float fallbackElevation)
    {
        if (ringCrossSections.Count == 0)
            return fallbackElevation;

        // Find the ring cross-section closest to the connection point
        UnifiedCrossSection? closestCs = null;
        var closestDistanceSquared = float.MaxValue;

        foreach (var cs in ringCrossSections)
        {
            if (float.IsNaN(cs.TargetElevation))
                continue;

            var distSquared = Vector2.DistanceSquared(cs.CenterPoint, connectionPoint);
            if (distSquared < closestDistanceSquared)
            {
                closestDistanceSquared = distSquared;
                closestCs = cs;
            }
        }

        if (closestCs == null)
            return fallbackElevation;

        return closestCs.TargetElevation;
    }

    /// <summary>
    ///     Blends connecting roads toward the roundabout elevation.
    ///     When ForceUniformRoundaboutElevation is true (on the CONNECTING road's parameters):
    ///     The connecting road blends toward the uniform harmonized ring elevation.
    ///     When ForceUniformRoundaboutElevation is false (on the CONNECTING road's parameters):
    ///     The connecting road blends toward the local ring elevation at its specific
    ///     connection point. This allows roads to naturally meet the roundabout at their
    ///     own terrain-following elevation, avoiding artificial bumps or dips.
    ///     NOTE: The ForceUniformRoundaboutElevation setting is read from EACH CONNECTING ROAD's
    ///     parameters, not from the roundabout ring's parameters. This allows different road
    ///     materials to have different blending behaviors at the same roundabout.
    ///     IMPORTANT: The blend zone is limited to at most half the road length to avoid
    ///     affecting the other end of the road (which may have its own junction).
    /// </summary>
    private int BlendConnectingRoads(
        RoundaboutJunctionInfo roundaboutInfo,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        UnifiedRoadNetwork network,
        float globalBlendDistance,
        ref float maxElevationChange)
    {
        var blendedCount = 0;
        var perfLog = TerrainCreationLogger.Current;

        // Get the ring cross-sections for per-connection-point elevation lookup
        crossSectionsBySpline.TryGetValue(roundaboutInfo.RoundaboutSplineId, out var ringCrossSections);

        foreach (var junction in roundaboutInfo.Junctions)
        {
            var connectingSplineId = junction.ConnectingRoadSplineId;

            if (!crossSectionsBySpline.TryGetValue(connectingSplineId, out var connectingCrossSections))
                continue;

            var connectingSpline = network.GetSplineById(connectingSplineId);
            if (connectingSpline == null)
                continue;

            // Get ForceUniformRoundaboutElevation from the CONNECTING ROAD's parameters
            // This allows each road material to control its own blending behavior
            var connectingJunctionParams = connectingSpline.Parameters.JunctionHarmonizationParameters
                                           ?? new JunctionHarmonizationParameters();
            var forceUniform = connectingJunctionParams.ForceUniformRoundaboutElevation;

            // Determine the target elevation for this specific connection:
            // - If ForceUniformRoundaboutElevation is true: use the global harmonized elevation
            // - If false: use the per-junction target elevation (local ring elevation at connection point)
            float targetElevation;
            if (forceUniform)
            {
                // Use uniform ring elevation (existing behavior)
                targetElevation = roundaboutInfo.HarmonizedElevation;
            }
            else
            {
                // Use the per-junction target elevation which was calculated in ApplyUniformRingElevation
                // This is the local ring elevation at the specific connection point
                targetElevation = junction.TargetElevation;

                // If the junction target elevation was not set, fall back to looking it up directly
                if (float.IsNaN(targetElevation) && ringCrossSections != null)
                    targetElevation = GetRingElevationAtConnectionPoint(
                        junction.ConnectionPointMeters,
                        ringCrossSections,
                        roundaboutInfo.HarmonizedElevation);
            }

            if (float.IsNaN(targetElevation))
                continue;

            // Get blend distance from spline parameters or use global
            // IMPORTANT: Use EffectiveRoundaboutBlendDistanceMeters for roundabout-specific blending
            var junctionParams = connectingSpline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var blendDistance = junctionParams.EffectiveRoundaboutBlendDistanceMeters;
            if (junctionParams.UseGlobalSettings)
                // When using global settings, still prefer RoundaboutBlendDistanceMeters if set,
                // otherwise fall back to global blend distance
                blendDistance = junctionParams.RoundaboutBlendDistanceMeters ?? globalBlendDistance;

            // Calculate the total length of the connecting road
            var isSplineStart = junction.IsConnectingRoadStart;
            var distances = CalculateDistancesFromEndpoint(connectingCrossSections, isSplineStart);

            // Get the road length (max distance from the roundabout end)
            var roadLength = distances.Length > 0 ? distances.Max() : 0f;

            // IMPORTANT: Limit blend distance to at most half the road length
            // This prevents the roundabout blending from affecting the other end of the road,
            // which may have its own junction that needs separate handling.
            // Without this limit, short roads (shorter than 2x blend distance) would have
            // their entire length modified, causing dents at the other junction.
            var effectiveBlendDistance = MathF.Min(blendDistance, roadLength * 0.5f);

            // If the road is very short, skip blending entirely to avoid issues
            if (effectiveBlendDistance < 5.0f)
            {
                perfLog?.Detail($"    Skipping blend for spline {connectingSplineId}: " +
                                $"road too short (length={roadLength:F1}m, effectiveBlend would be {effectiveBlendDistance:F1}m)");
                continue;
            }

            // Get blend function type
            var blendFunctionType = junctionParams.BlendFunctionType;

            perfLog?.Detail($"    Blending spline {connectingSplineId}: " +
                            $"isStart={isSplineStart}, blendDistance={blendDistance:F1}m -> effective={effectiveBlendDistance:F1}m, " +
                            $"roadLength={roadLength:F1}m, targetElevation={targetElevation:F2}m, " +
                            $"forceUniform={forceUniform} (from connecting road), crossSections={connectingCrossSections.Count}");

            // Collect blend info for logging (to show the ones CLOSEST to the roundabout)
            var blendLog =
                new List<(int idx, float dist, float t, float blend, float orig, float newElev, float delta)>();

            // Apply blend
            var splineBlendedCount = 0;
            for (var i = 0; i < connectingCrossSections.Count; i++)
            {
                var dist = distances[i];
                if (dist >= effectiveBlendDistance)
                    continue; // Outside blend zone

                var cs = connectingCrossSections[i];
                if (float.IsNaN(cs.TargetElevation))
                    continue;

                var originalElevation = cs.TargetElevation;

                // Calculate blend factor using configured function
                // t=0 at roundabout connection -> blend=0 -> use targetElevation
                // t=1 at effectiveBlendDistance away -> blend=1 -> use originalElevation
                var t = dist / effectiveBlendDistance;
                var blend = ApplyBlendFunction(t, blendFunctionType);

                // Blend between target elevation (ring at connection point) and original road elevation
                var newElevation = targetElevation * (1.0f - blend) + originalElevation * blend;

                var elevationChange = MathF.Abs(newElevation - originalElevation);
                if (elevationChange > 0.001f)
                {
                    maxElevationChange = MathF.Max(maxElevationChange, elevationChange);
                    cs.TargetElevation = newElevation;
                    blendedCount++;
                    splineBlendedCount++;

                    // Store for logging (we'll show the ones closest to the roundabout)
                    blendLog.Add((i, dist, t, blend, originalElevation, newElevation, elevationChange));
                }
            }

            // Log the cross-sections CLOSEST to the roundabout (smallest distance)
            var closestBlends = blendLog.OrderBy(b => b.dist).Take(3).ToList();
            foreach (var b in closestBlends)
                perfLog?.Detail($"      CS[{b.idx}]: dist={b.dist:F1}m, t={b.t:F3}, blend={b.blend:F3}, " +
                                $"orig={b.orig:F2}m -> new={b.newElev:F2}m (delta={b.delta:F3}m)");

            perfLog?.Detail($"    Spline {connectingSplineId}: blended {splineBlendedCount} cross-sections");
        }

        return blendedCount;
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
    ///     Result of roundabout elevation harmonization.
    /// </summary>
    public class RoundaboutHarmonizationResult
    {
        /// <summary>
        ///     Number of roundabout rings processed.
        /// </summary>
        public int RoundaboutsProcessed { get; set; }

        /// <summary>
        ///     Number of roundabout ring cross-sections modified.
        /// </summary>
        public int RingCrossSectionsModified { get; set; }

        /// <summary>
        ///     Number of connecting road cross-sections blended.
        /// </summary>
        public int ConnectingRoadCrossSectionsBlended { get; set; }

        /// <summary>
        ///     Maximum elevation change applied to any cross-section.
        /// </summary>
        public float MaxElevationChange { get; set; }

        /// <summary>
        ///     Elevation assigned to each roundabout (by spline ID).
        /// </summary>
        public Dictionary<int, float> RoundaboutElevations { get; set; } = new();
    }
}