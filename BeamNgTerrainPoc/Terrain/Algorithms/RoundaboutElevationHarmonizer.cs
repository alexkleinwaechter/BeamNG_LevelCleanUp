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
    /// <returns>Result containing statistics about the harmonization.</returns>
    public RoundaboutHarmonizationResult HarmonizeRoundaboutElevations(
        UnifiedRoadNetwork network,
        List<RoundaboutJunctionInfo> roundaboutJunctionInfos,
        float[,] heightMap,
        float metersPerPixel,
        bool useTiltedPlane = false)
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

            // Step 1+2: derive the ring elevation profile.
            //   No-blend path (useTiltedPlane): fit a single ≤6%-clamped tilted plane to terrain under
            //   the ring → drivable disk that follows the hillside (smaller embankment).
            //   Legacy path: uniform horizontal disk (CalculateRoundaboutElevation + ApplyUniformRingElevation).
            float ringElevation;
            int ringModified;
            var maxElevChange = result.MaxElevationChange;
            if (useTiltedPlane)
            {
                var mapH = heightMap.GetLength(0);
                var mapW = heightMap.GetLength(1);
                var maxTilt = network.GetSplineById(ringSplineId)?.Parameters
                                  .JunctionHarmonizationParameters?.RoundaboutMaxPlaneTilt
                              ?? 0.06f;
                var preTilt = ApplyTiltedRingPlane(
                    ringCrossSections,
                    p =>
                    {
                        var px = (int)(p.X / metersPerPixel);
                        var py = (int)(p.Y / metersPerPixel);
                        if (px < 0 || px >= mapW || py < 0 || py >= mapH) return float.NaN;
                        return heightMap[py, px];
                    },
                    maxTilt);
                ringElevation = ringCrossSections.Count > 0
                    ? ringCrossSections.Average(cs => cs.TargetElevation)
                    : float.NaN;
                ringModified = ringCrossSections.Count;
                TerrainCreationLogger.Current?.Detail(
                    $"  [NO-BLEND RAB PLANE] roundabout {ringSplineId}: tilted plane, " +
                    $"preClampTilt={preTilt * 100f:F1}% cap={maxTilt * 100f:F1}% meanZ={ringElevation:F2}");
            }
            else
            {
                ringElevation = CalculateRoundaboutElevation(
                    roundaboutInfo, ringCrossSections, heightMap, metersPerPixel, mapWidth, mapHeight, network);
                if (float.IsNaN(ringElevation))
                {
                    TerrainLogger.Warning($"  Roundabout {ringSplineId}: Could not calculate ring elevation");
                    continue;
                }

                ringModified = ApplyUniformRingElevation(
                    ringCrossSections, ringElevation, roundaboutInfo, network, ref maxElevChange);
            }

            roundaboutInfo.HarmonizedElevation = ringElevation;
            result.RoundaboutElevations[ringSplineId] = ringElevation;
            result.MaxElevationChange = maxElevChange;
            result.RingCrossSectionsModified += ringModified;

            // Connecting-road blending is handled downstream by the no-blend affine ThroughRoad
            // pipeline (UnifiedRoadSmoother §3/§4); the harmonizer only sets the ring elevation.
            TerrainLogger.Detail(
                $"  Roundabout {ringSplineId}: connecting-road blending deferred to the affine pipeline");

            result.RoundaboutsProcessed++;

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
    ///     No-blend path: fit a single tilted plane to terrain under the ring (clamped to
    ///     <paramref name="maxTilt" />) and write it to every ring cross-section, so the ring follows the
    ///     hillside as a drivable disk instead of a forced-uniform horizontal disk. Returns the pre-clamp
    ///     tilt (for diagnostics). Pure except for writing the cross-sections' TargetElevation.
    /// </summary>
    internal static float ApplyTiltedRingPlane(
        List<UnifiedCrossSection> ringCrossSections,
        Func<Vector2, float> sampleTerrain,
        float maxTilt)
    {
        var points = new List<(Vector2, float)>(ringCrossSections.Count);
        foreach (var cs in ringCrossSections)
            points.Add((cs.CenterPoint, sampleTerrain(cs.CenterPoint)));

        var (a, b, c, preTilt) = RoundaboutPlaneFit.FitClamped(points, maxTilt);

        // Civil placement: FitClamped pivots the plane through the terrain MEAN (minimizes cut/fill RMS).
        // For a roundabout we instead minimize the WORST-CASE cut/fill so the embankment never exceeds half
        // the residual range — center the plane on the midrange of the RESIDUALS (terrain minus the clamped
        // tilt), i.e. shift so the deepest cut equals the highest fill. On terrain steeper than the 6% cap
        // this gives the smallest possible embankment for a drivable ring while keeping entry/exit grades
        // gentle (the ring stays between the highest and lowest approach). Pure vertical shift, tilt intact.
        var minR = float.MaxValue;
        var maxR = float.MinValue;
        var anyR = false;
        foreach (var (xy, z) in points)
        {
            if (float.IsNaN(z) || float.IsInfinity(z)) continue;
            var r = z - RoundaboutPlaneFit.Evaluate(a, b, c, xy);
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            anyR = true;
        }

        if (anyR)
            c += (minR + maxR) / 2f;

        foreach (var cs in ringCrossSections)
            cs.TargetElevation = RoundaboutPlaneFit.Evaluate(a, b, c, cs.CenterPoint);
        return preTilt;
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