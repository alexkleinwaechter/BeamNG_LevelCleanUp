using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Unified junction elevation and banking blender.
///     Replaces the four overlapping systems (rubberband, banking adjustment,
///     elevation adaptation, edge constraint propagation) with a single system
///     that blends (elevation, bankAngle) simultaneously using Hermite basis functions.
///
///     Core principle: Junctions are fixed constraint nodes with known (elevation, slope, bankAngle).
///     Each road smoothly interpolates between its junction constraints and its natural
///     terrain-following profile using the same interpolation function for both elevation
///     and banking, so edge elevations (derived as TargetElevation ± halfWidth × sin(BankAngle))
///     are automatically smooth. No separate edge constraint system needed.
/// </summary>
public class UnifiedJunctionProfileBlender
{
    /// <summary>
    ///     Applies unified junction profiles to the entire network.
    ///     For each road with junction constraint(s) at one or both ends:
    ///     1. Compute junction constraints (elevation, slope, bankAngle)
    ///     2. Blend elevation AND bankAngle simultaneously using Hermite basis functions
    ///     3. Derive edge elevations from the blended (TargetElevation, BankAngle)
    /// </summary>
    /// <returns>Number of cross-sections modified.</returns>
    public UnifiedBlendResult ApplyUnifiedProfiles(
        UnifiedRoadNetwork network,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles,
        float[,] heightMap,
        float metersPerPixel)
    {
        var result = new UnifiedBlendResult();

        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // === TWO-PASS HERMITE: Process primary roads first, then terminating roads ===
        // This ensures T-junction constraints use the ACTUAL post-blend primary elevation,
        // eliminating the need for overlap snapping or blend-distance-based surface following.
        // ONE system, no boundaries, no bumps.

        // Step 1: Compute constraints for NON-T-junction roads (Y/X/Complex/Endpoint)
        // and identify which splines are terminating at T-junctions (processed in pass 2)
        var constraints = ComputeAllJunctionConstraints(network, crossSectionsBySpline, heightMap, metersPerPixel);
        result.ConstraintsComputed = constraints.Count;

        if (constraints.Count == 0)
        {
            TerrainLogger.Detail("  UnifiedProfileBlender: No junction constraints to apply");
            return result;
        }

        // Build set of splines that terminate at T-junctions or roundabouts (they need pass 2).
        // These roads are deferred so their constraints use ACTUAL post-pass-1 primary/ring elevations.
        var deferredTerminatingSplines = new HashSet<int>();
        foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.TJunction && !j.IsExcluded))
        foreach (var t in junction.GetTerminatingRoads())
            deferredTerminatingSplines.Add(t.Spline.SplineId);
        foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.Roundabout))
        foreach (var t in junction.GetTerminatingRoads())
            deferredTerminatingSplines.Add(t.Spline.SplineId);

        // Step 2 (Pass 1): Hermite blend ALL splines EXCEPT T-junction terminating roads.
        // This gives primary/continuous roads their correct elevation first.
        foreach (var (splineId, sections) in crossSectionsBySpline)
        {
            if (deferredTerminatingSplines.Contains(splineId))
                continue; // Defer to pass 2

            var hasStart = constraints.TryGetValue((splineId, true), out var startConstraint);
            var hasEnd = constraints.TryGetValue((splineId, false), out var endConstraint);

            if (!hasStart && !hasEnd)
                continue;

            result.ModifiedCrossSections += BlendSplineProfile(
                sections, startConstraint, endConstraint, originalElevations, originalBankAngles);
        }

        // Step 3 (Pass 2): Recompute T-junction and roundabout constraints from ACTUAL
        // post-pass-1 primary/ring elevations, then Hermite blend the terminating roads.
        if (deferredTerminatingSplines.Count > 0)
        {
            // Recompute constraints — primary/ring roads now have correct elevations
            var refinedConstraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>();
            foreach (var junction in network.Junctions.Where(j =>
                         (j.Type == JunctionType.TJunction && !j.IsExcluded) ||
                         j.Type == JunctionType.Roundabout))
            {
                if (junction.Type == JunctionType.TJunction)
                    ComputeTJunctionConstraints(junction, crossSectionsBySpline, refinedConstraints);
                else
                    ComputeRoundaboutConstraints(junction, crossSectionsBySpline, refinedConstraints);
            }

            // Apply Hermite to terminating splines with refined constraints
            foreach (var splineId in deferredTerminatingSplines)
            {
                if (!crossSectionsBySpline.TryGetValue(splineId, out var sections))
                    continue;

                var hasStart = refinedConstraints.TryGetValue((splineId, true), out var startConstraint);
                var hasEnd = refinedConstraints.TryGetValue((splineId, false), out var endConstraint);

                // Also check original constraints for non-T-junction endpoints on this spline
                // (e.g., a road terminating at a T-junction on one end and a Y-junction on the other)
                if (!hasStart)
                    constraints.TryGetValue((splineId, true), out startConstraint);
                if (!hasEnd)
                    constraints.TryGetValue((splineId, false), out endConstraint);

                if (startConstraint == null && endConstraint == null)
                    continue;

                result.ModifiedCrossSections += BlendSplineProfile(
                    sections, startConstraint, endConstraint, originalElevations, originalBankAngles);
            }

            TerrainLogger.Detail(
                $"  Pass 2: refined {refinedConstraints.Count} T-junction/roundabout constraints from post-blend primary/ring elevations");

            // Prevent JunctionBankingAdapter from overwriting T-junction terminating road
            // elevations. The unified blender already accounts for primary road banking via
            // GetPrimarySurfaceElevation. Mark CSes within the blend zone as MaintainBanking
            // so JunctionBankingAdapter skips them.
            foreach (var splineId in deferredTerminatingSplines)
            {
                if (!crossSectionsBySpline.TryGetValue(splineId, out var sections))
                    continue;

                // Find the blend zone extent for this spline
                refinedConstraints.TryGetValue((splineId, true), out var startC);
                refinedConstraints.TryGetValue((splineId, false), out var endC);
                if (startC == null) constraints.TryGetValue((splineId, true), out startC);
                if (endC == null) constraints.TryGetValue((splineId, false), out endC);

                var startExtent = (startC?.FlatZoneDistance ?? 0f) + (startC?.BlendDistanceMeters ?? 0f);
                var endExtent = (endC?.FlatZoneDistance ?? 0f) + (endC?.BlendDistanceMeters ?? 0f);

                var dists = CalculateDistancesFromEndpoint(sections, true);
                var roadLen = dists.Length > 0 ? dists[^1] : 0f;

                for (var i = 0; i < sections.Count; i++)
                {
                    if (dists[i] <= startExtent || (roadLen - dists[i]) <= endExtent)
                    {
                        if (sections[i].JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority)
                            sections[i].JunctionBankingBehavior = JunctionBankingBehavior.MaintainBanking;
                    }
                }
            }
        }

        // Step 4: Derive edge elevations from unified (TargetElevation, BankAngle)
        foreach (var cs in network.CrossSections)
        {
            if (float.IsNaN(cs.TargetElevation))
                continue;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            var elevDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
            cs.LeftEdgeElevation = cs.TargetElevation - elevDelta;
            cs.RightEdgeElevation = cs.TargetElevation + elevDelta;
        }

        // Step 5: Handle MidSplineCrossings (both roads continue through)
        result.MidSplineCrossingModified = ApplyMidSplineCrossingInfluences(
            network, crossSectionsBySpline, originalElevations);

        // Step 6: Apply endpoint tapering for dead ends
        result.EndpointsTapered = ApplyEndpointTapering(
            network, crossSectionsBySpline, heightMap, metersPerPixel);

        // Step 7: Compute IDW weight modifiers for terrain blending
        result.IdwModifiersSet = ComputeJunctionIdwWeightModifiers(
            network, crossSectionsBySpline);

        TerrainLogger.Detail(
            $"  UnifiedProfileBlender: {result.ConstraintsComputed} constraints, " +
            $"{result.ModifiedCrossSections} cross-sections modified, " +
            $"{result.MidSplineCrossingModified} mid-spline crossings, " +
            $"{result.EndpointsTapered} endpoints tapered");

        return result;
    }

    /// <summary>
    ///     Computes junction constraints for all junction+road pairs in the network.
    ///     Returns a lookup: (splineId, isStart) → JunctionEndpointConstraint.
    /// </summary>
    private Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> ComputeAllJunctionConstraints(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float[,] heightMap,
        float metersPerPixel)
    {
        var constraints = new Dictionary<(int, bool), JunctionEndpointConstraint>();
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        foreach (var junction in network.Junctions.Where(j => !j.IsExcluded || j.Type == JunctionType.Roundabout))
        {
            switch (junction.Type)
            {
                case JunctionType.TJunction:
                    ComputeTJunctionConstraints(junction, crossSectionsBySpline, constraints);
                    break;

                case JunctionType.Roundabout:
                    ComputeRoundaboutConstraints(junction, crossSectionsBySpline, constraints);
                    break;

                case JunctionType.YJunction:
                case JunctionType.CrossRoads:
                case JunctionType.Complex:
                    ComputeMultiWayConstraints(junction, constraints);
                    break;

                case JunctionType.Endpoint:
                    ComputeEndpointConstraints(junction, heightMap, metersPerPixel,
                        mapWidth, mapHeight, constraints);
                    break;

                case JunctionType.MidSplineCrossing:
                    // Handled separately in ApplyMidSplineCrossingInfluences
                    break;
            }
        }

        TerrainLogger.Detail($"  Computed {constraints.Count} junction endpoint constraints " +
                             $"from {network.Junctions.Count(j => !j.IsExcluded)} junctions");

        return constraints;
    }

    /// <summary>
    ///     Computes constraints for T-junction terminating roads.
    ///     The continuous (primary) road gets NO constraint — it passes through unmodified.
    ///     Each terminating road gets:
    ///       elevation = primary surface elevation at connection center
    ///       slope = primary road's longitudinal slope
    ///       bankAngle = angle that makes edges match primary surface
    /// </summary>
    private void ComputeTJunctionConstraints(
        NetworkJunction junction,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        var continuous = junction.GetContinuousRoads().ToList();
        if (continuous.Count == 0)
        {
            // No clear continuous road — treat as multi-way
            ComputeMultiWayConstraints(junction, constraints);
            return;
        }

        var primaryContributor = continuous.OrderByDescending(c => c.Spline.Priority).First();
        var primaryCS = primaryContributor.CrossSection;

        // Calculate primary road's local slope
        var primarySlope = 0f;
        if (crossSectionsBySpline.TryGetValue(primaryContributor.Spline.SplineId, out var primarySections))
        {
            var junctionIndex = primarySections.FindIndex(cs => cs.Index == primaryCS.Index);
            if (junctionIndex >= 0)
                primarySlope = CalculateSlopeAtIndex(primarySections, junctionIndex);
        }

        if (float.IsNaN(primarySlope))
            primarySlope = 0f;

        foreach (var terminating in junction.GetTerminatingRoads())
        {
            var terminatingCS = terminating.CrossSection;
            var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;
            var primaryHalfWidth = primaryCS.EffectiveRoadWidth / 2f;

            // --- Edge-Anchored Constraint with Slope ---
            // The junction blending should work identically to the non-banked case,
            // but the reference elevation is the primary road EDGE (not centerline).
            // Banking is "baked in" to the edge elevation — PrimaryBankAngleRadians = 0
            // prevents double-counting. PrimaryTangentDirection is kept for slope tracking.

            // Compute the exit point where the terminating road leaves the primary road
            var awayDirection = terminating.IsSplineStart
                ? terminatingCS.TangentDirection
                : -terminatingCS.TangentDirection;
            var edgeCenterPoint = terminatingCS.CenterPoint + awayDirection * primaryHalfWidth;

            // Primary surface elevation at the edge exit point
            var edgeCenterElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeCenterPoint, primaryCS, primarySlope);

            // Bank angle at the exit point from edge projections
            var edgeLeftPos = edgeCenterPoint - terminatingCS.NormalDirection * halfWidth;
            var edgeRightPos = edgeCenterPoint + terminatingCS.NormalDirection * halfWidth;
            var edgeLeftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeLeftPos, primaryCS, primarySlope);
            var edgeRightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeRightPos, primaryCS, primarySlope);
            var edgeDelta = (edgeRightElev - edgeLeftElev) / 2f;
            var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
            var edgeBankAngle = MathF.Asin(sinBank);

            junction.HarmonizedElevation = edgeCenterElev;

            var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var terminatingWidth = terminating.Spline.WidthProfile
                    ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
                ?? terminating.Spline.Parameters.RoadWidthMeters;
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(terminatingWidth),
                edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters);

            var key = (terminating.Spline.SplineId, terminating.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = edgeCenterElev,
                Slope = primarySlope,
                BankAngleRadians = edgeBankAngle,
                IsSplineStart = terminating.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = primaryHalfWidth,
                BlendDistanceMeters = blendDist,
                // Slope tracking via analytical delta: PrimaryTangentDirection set,
                // but PrimaryBankAngleRadians = 0 because banking is already baked
                // into edgeCenterElev. This works identically to the non-banked case.
                PrimaryTangentDirection = primaryCS.TangentDirection,
                PrimaryBankAngleRadians = 0f
            });

            TerrainCreationLogger.Current?.Detail(
                $"T-Junction #{junction.JunctionId}: Spline {terminating.Spline.SplineId} EDGE constraint: " +
                $"edgeElev={edgeCenterElev:F2}m, slope={primarySlope:F4}, " +
                $"bank={BankingCalculator.RadiansToDegrees(edgeBankAngle):F1}°, " +
                $"edges L={edgeLeftElev:F2}m R={edgeRightElev:F2}m");
            TerrainCreationLogger.Current?.Detail(
                $"  [T-SNAP DEBUG] Junction #{junction.JunctionId}, Spline {terminating.Spline.SplineId}:");
            TerrainCreationLogger.Current?.Detail(
                $"    primaryCS: idx={primaryCS.Index} center=({primaryCS.CenterPoint.X:F1},{primaryCS.CenterPoint.Y:F1}) " +
                $"targetElev={primaryCS.TargetElevation:F3}m bank={BankingCalculator.RadiansToDegrees(primaryCS.BankAngleRadians):F2}°");
            TerrainCreationLogger.Current?.Detail(
                $"    edgeCenter=({edgeCenterPoint.X:F1},{edgeCenterPoint.Y:F1}) edgeElev={edgeCenterElev:F3}m " +
                $"flatZone={primaryHalfWidth:F2}m blendDist={blendDist:F1}m bankAngleRadians=0(baked)");
        }
    }

    /// <summary>
    ///     Computes constraints for roundabout connecting roads.
    ///     The ring (continuous road) is the primary surface — connecting roads snap to it
    ///     using the same three-zone Hermite model as T-junctions.
    ///     The ring gets NO constraint — it keeps its Phase 2.6 harmonized elevation.
    ///     Each connecting (terminating) road gets:
    ///       elevation = ring surface elevation at connection center
    ///       slope = ring's longitudinal slope at connection point
    ///       bankAngle = angle that makes edges match ring surface
    /// </summary>
    private void ComputeRoundaboutConstraints(
        NetworkJunction junction,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        var continuous = junction.GetContinuousRoads().ToList();
        if (continuous.Count == 0)
        {
            ComputeMultiWayConstraints(junction, constraints);
            return;
        }

        var ringContributor = continuous.OrderByDescending(c => c.Spline.Priority).First();
        var ringCS = ringContributor.CrossSection;

        // Find the closest ring CS to the junction position for more accurate local data.
        UnifiedCrossSection? ringCSPrev = null;
        UnifiedCrossSection? ringCSNext = null;
        if (crossSectionsBySpline.TryGetValue(ringContributor.Spline.SplineId, out var ringSections))
        {
            var closestDist = float.MaxValue;
            var closestIdx = -1;
            for (var i = 0; i < ringSections.Count; i++)
            {
                var dist = Vector2.Distance(ringSections[i].CenterPoint, junction.Position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestIdx = i;
                    ringCS = ringSections[i];
                }
            }

            if (closestIdx > 0)
                ringCSPrev = ringSections[closestIdx - 1];
            if (closestIdx < ringSections.Count - 1)
                ringCSNext = ringSections[closestIdx + 1];
        }

        // Interpolate ring elevation at the exact junction position between
        // the nearest CS and its closest neighbor for sub-CS accuracy.
        if (ringCSPrev != null || ringCSNext != null)
        {
            var junctionPos = junction.Position;
            var distToNearest = Vector2.Distance(ringCS.CenterPoint, junctionPos);

            UnifiedCrossSection? neighbor = null;
            var neighborDist = float.MaxValue;

            if (ringCSPrev != null)
            {
                var d = Vector2.Distance(ringCSPrev.CenterPoint, junctionPos);
                if (d < neighborDist) { neighbor = ringCSPrev; neighborDist = d; }
            }
            if (ringCSNext != null)
            {
                var d = Vector2.Distance(ringCSNext.CenterPoint, junctionPos);
                if (d < neighborDist) { neighbor = ringCSNext; neighborDist = d; }
            }

            if (neighbor != null && !float.IsNaN(neighbor.TargetElevation) && !float.IsNaN(ringCS.TargetElevation))
            {
                var totalDist = distToNearest + neighborDist;
                if (totalDist > 0.01f)
                {
                    var t = distToNearest / totalDist;
                    var interpolatedElev = ringCS.TargetElevation * (1f - t) + neighbor.TargetElevation * t;

                    if (MathF.Abs(interpolatedElev - ringCS.TargetElevation) > 0.001f)
                    {
                        TerrainCreationLogger.Current?.Detail(
                            $"  Ring CS interpolation: nearest={ringCS.TargetElevation:F3}m, " +
                            $"neighbor={neighbor.TargetElevation:F3}m, interpolated={interpolatedElev:F3}m (t={t:F2})");

                        // Create a local copy with interpolated elevation.
                        // All { get; set; } properties from UnifiedCrossSection must be copied.
                        ringCS = new UnifiedCrossSection
                        {
                            Index = ringCS.Index,
                            OwnerSplineId = ringCS.OwnerSplineId,
                            LocalIndex = ringCS.LocalIndex,
                            CenterPoint = ringCS.CenterPoint,
                            TangentDirection = ringCS.TangentDirection,
                            NormalDirection = ringCS.NormalDirection,
                            TargetElevation = interpolatedElev,
                            BankAngleRadians = ringCS.BankAngleRadians,
                            EffectiveRoadWidth = ringCS.EffectiveRoadWidth,
                            EffectiveBlendRange = ringCS.EffectiveBlendRange,
                            LeftEdgeElevation = ringCS.LeftEdgeElevation,
                            RightEdgeElevation = ringCS.RightEdgeElevation,
                            JunctionBankingBehavior = ringCS.JunctionBankingBehavior,
                            Priority = ringCS.Priority,
                            DistanceAlongSpline = ringCS.DistanceAlongSpline,
                            OriginalTerrainElevation = ringCS.OriginalTerrainElevation,
                            Curvature = ringCS.Curvature,
                            JunctionIdwWeightModifier = ringCS.JunctionIdwWeightModifier,
                            IsExcluded = ringCS.IsExcluded,
                            IsFromOsmSource = ringCS.IsFromOsmSource,
                            IsSplineStart = ringCS.IsSplineStart,
                            IsSplineEnd = ringCS.IsSplineEnd,
                            IsRoundaboutBlended = ringCS.IsRoundaboutBlended,
                            BankedNormal3D = ringCS.BankedNormal3D,
                            ConstrainedLeftEdgeElevation = ringCS.ConstrainedLeftEdgeElevation,
                            ConstrainedRightEdgeElevation = ringCS.ConstrainedRightEdgeElevation,
                            JunctionBankingFactor = ringCS.JunctionBankingFactor,
                            DistanceToNearestJunction = ringCS.DistanceToNearestJunction,
                            HigherPrioritySplineId = ringCS.HigherPrioritySplineId
                        };
                    }
                }
            }
        }

        // Calculate ring's circumferential slope (along the ring tangent)
        var circumferentialSlope = 0f;
        if (crossSectionsBySpline.TryGetValue(ringContributor.Spline.SplineId, out var ringAllSections))
        {
            var ringIndex = ringAllSections.FindIndex(cs => cs.Index == ringCS.Index);
            if (ringIndex >= 0)
                circumferentialSlope = CalculateSlopeAtIndex(ringAllSections, ringIndex);
        }

        if (float.IsNaN(circumferentialSlope))
            circumferentialSlope = 0f;

        foreach (var terminating in junction.GetTerminatingRoads())
        {
            var terminatingCS = terminating.CrossSection;
            var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;
            var ringHalfWidth = ringCS.EffectiveRoadWidth / 2f;

            // === Edge-Anchored Constraint (matching T-junction pattern) ===
            var awayDirection = terminating.IsSplineStart
                ? terminatingCS.TangentDirection
                : -terminatingCS.TangentDirection;
            var edgeCenterPoint = terminatingCS.CenterPoint + awayDirection * ringHalfWidth;

            var edgeCenterElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeCenterPoint, ringCS, circumferentialSlope);

            var edgeLeftPos = edgeCenterPoint - terminatingCS.NormalDirection * halfWidth;
            var edgeRightPos = edgeCenterPoint + terminatingCS.NormalDirection * halfWidth;
            var edgeLeftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeLeftPos, ringCS, circumferentialSlope);
            var edgeRightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeRightPos, ringCS, circumferentialSlope);
            var edgeDelta = (edgeRightElev - edgeLeftElev) / 2f;
            var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
            var edgeBankAngle = MathF.Asin(sinBank);

            junction.HarmonizedElevation = edgeCenterElev;

            // Radial slope: project ring surface gradient onto approach direction
            var slopeAlongTangent = circumferentialSlope;
            var bankingSlopePerMeter = ringCS.EffectiveRoadWidth > 0.01f
                ? MathF.Sin(ringCS.BankAngleRadians)
                : 0f;
            var radialSlope =
                slopeAlongTangent * Vector2.Dot(awayDirection, ringCS.TangentDirection) +
                bankingSlopePerMeter * Vector2.Dot(awayDirection, ringCS.NormalDirection);

            if (float.IsNaN(radialSlope))
                radialSlope = 0f;

            var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var terminatingRoundaboutWidth = terminating.Spline.WidthProfile
                    ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
                ?? terminating.Spline.Parameters.RoadWidthMeters;
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveRoundaboutBlendDistance(terminatingRoundaboutWidth),
                edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters);

            var key = (terminating.Spline.SplineId, terminating.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = edgeCenterElev,
                Slope = radialSlope,
                BankAngleRadians = edgeBankAngle,
                IsSplineStart = terminating.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = ringHalfWidth,
                BlendDistanceMeters = blendDist,
                PrimaryTangentDirection = ringCS.TangentDirection,
                PrimaryBankAngleRadians = 0f
            });

            TerrainCreationLogger.Current?.Detail(
                $"Roundabout Junction #{junction.JunctionId}: Spline {terminating.Spline.SplineId} EDGE constraint: " +
                $"edgeElev={edgeCenterElev:F2}m, radialSlope={radialSlope:F4}, circumSlope={circumferentialSlope:F4}, " +
                $"bank={BankingCalculator.RadiansToDegrees(edgeBankAngle):F1}°, " +
                $"flatZone={ringHalfWidth:F2}m, blendDist={blendDist:F1}m");
        }
    }

    /// <summary>
    ///     Computes constraints for multi-way junctions (Y, X, Complex).
    ///     All endpoint roads get: elevation = negotiated average, bankAngle = 0° (flatten).
    /// </summary>
    private void ComputeMultiWayConstraints(
        NetworkJunction junction,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        // Compute harmonized elevation using priority-weighted average
        var totalPriority = 0f;
        var weightedElevation = 0f;

        foreach (var c in junction.Contributors)
        {
            if (float.IsNaN(c.CrossSection.TargetElevation))
                continue;
            float priority = c.Spline.Priority;
            totalPriority += priority;
            weightedElevation += c.CrossSection.TargetElevation * priority;
        }

        var harmonizedElev = totalPriority > 0
            ? weightedElevation / totalPriority
            : junction.Contributors.FirstOrDefault()?.CrossSection.TargetElevation ?? 0f;

        junction.HarmonizedElevation = harmonizedElev;

        foreach (var contributor in junction.Contributors.Where(c => c.IsEndpoint))
        {
            var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var contributorWidth = contributor.Spline.WidthProfile
                    ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                ?? contributor.Spline.Parameters.RoadWidthMeters;
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(contributorWidth),
                harmonizedElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);

            var key = (contributor.Spline.SplineId, contributor.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = harmonizedElev,
                Slope = 0f, // natural slope
                BankAngleRadians = 0f, // flatten at equal-priority junction
                IsSplineStart = contributor.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = 0f, // no overlap zone for multi-way junctions
                BlendDistanceMeters = blendDist
            });
        }
    }

    /// <summary>
    ///     Computes constraints for isolated endpoints (dead ends).
    ///     Elevation = terrain, bankAngle = 0° (flatten).
    /// </summary>
    private void ComputeEndpointConstraints(
        NetworkJunction junction,
        float[,] heightMap,
        float metersPerPixel,
        int mapWidth, int mapHeight,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        // Sample terrain at endpoint
        var px = (int)(junction.Position.X / metersPerPixel);
        var py = (int)(junction.Position.Y / metersPerPixel);
        px = Math.Clamp(px, 0, mapWidth - 1);
        py = Math.Clamp(py, 0, mapHeight - 1);
        var terrainElev = heightMap[py, px];

        junction.HarmonizedElevation = terrainElev;

        foreach (var contributor in junction.Contributors)
        {
            var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var endpointWidth = contributor.Spline.WidthProfile
                    ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                ?? contributor.Spline.Parameters.RoadWidthMeters;
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(endpointWidth),
                terrainElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);

            var key = (contributor.Spline.SplineId, contributor.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = terrainElev,
                Slope = 0f,
                BankAngleRadians = 0f,
                IsSplineStart = contributor.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = 0f, // no overlap zone for endpoints
                BlendDistanceMeters = blendDist
            });
        }
    }

    /// <summary>
    ///     Blends a single spline's elevation AND bankAngle profiles using localized Hermite
    ///     delta correction. Each junction's influence is confined to its blend distance zone
    ///     (plus flat zone). Beyond the blend distance, the road follows the terrain-following
    ///     profile from Phase 2 with no junction correction.
    ///     The Hermite h00 basis function (2t³-3t²+1) provides C1 continuity at the blend
    ///     boundary (zero value and zero slope), eliminating visible transition artifacts.
    /// </summary>
    /// <returns>Number of cross-sections modified.</returns>
    private static int BlendSplineProfile(
        List<UnifiedCrossSection> sections,
        JunctionEndpointConstraint? startConstraint,
        JunctionEndpointConstraint? endConstraint,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles)
    {
        if (sections.Count < 2)
            return 0;

        var modified = 0;

        // Calculate cumulative distances from start
        var distFromStart = new float[sections.Count];
        distFromStart[0] = 0;
        for (var i = 1; i < sections.Count; i++)
            distFromStart[i] = distFromStart[i - 1] +
                               Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);

        var roadLength = distFromStart[sections.Count - 1];
        if (roadLength < 0.01f)
            return 0;

        // Get natural (terrain-following) elevation and bank angle at each endpoint
        var startNaturalElev = originalElevations.GetValueOrDefault(
            sections[0].Index, sections[0].TargetElevation);
        var endNaturalElev = originalElevations.GetValueOrDefault(
            sections[^1].Index, sections[^1].TargetElevation);

        var startNaturalBank = originalBankAngles.GetValueOrDefault(
            sections[0].Index, sections[0].BankAngleRadians);
        var endNaturalBank = originalBankAngles.GetValueOrDefault(
            sections[^1].Index, sections[^1].BankAngleRadians);

        // Compute deltas: how much the junction shifts from the natural profile
        var startElevDelta = startConstraint != null
            ? startConstraint.Elevation - startNaturalElev : 0f;
        var endElevDelta = endConstraint != null
            ? endConstraint.Elevation - endNaturalElev : 0f;

        var startBankDelta = startConstraint != null
            ? startConstraint.BankAngleRadians - startNaturalBank : 0f;
        var endBankDelta = endConstraint != null
            ? endConstraint.BankAngleRadians - endNaturalBank : 0f;

        // Skip if corrections are negligible
        if (MathF.Abs(startElevDelta) < 0.001f && MathF.Abs(endElevDelta) < 0.001f &&
            MathF.Abs(startBankDelta) < 0.0001f && MathF.Abs(endBankDelta) < 0.0001f)
            return 0;

        // Flat zones: distance from each endpoint where correction stays at 100%.
        var startFlatZone = startConstraint?.FlatZoneDistance ?? 0f;
        var endFlatZone = endConstraint?.FlatZoneDistance ?? 0f;

        // Blend distances: how far past the flat zone the Hermite correction decays.
        var startBlendDist = startConstraint?.BlendDistanceMeters ?? 30f;
        var endBlendDist = endConstraint?.BlendDistanceMeters ?? 30f;

        // Transition zone: beyond flat zone, analytical delta blends to constant handoff
        // via quintic smootherstep. Sized = flatZoneDistance, capped at 25% of blendDistance.
        var startTransitionDist = startConstraint?.PrimaryTangentDirection != null
            ? MathF.Min(startFlatZone, startBlendDist * 0.25f)
            : 0f;
        var endTransitionDist = endConstraint?.PrimaryTangentDirection != null
            ? MathF.Min(endFlatZone, endBlendDist * 0.25f)
            : 0f;

        if (startTransitionDist > 0f || endTransitionDist > 0f)
        {
            TerrainCreationLogger.Current?.Detail(
                $"  [T-TRANSITION] Spline {sections[0].OwnerSplineId}: " +
                $"startTransition={startTransitionDist:F1}m endTransition={endTransitionDist:F1}m " +
                $"(flatZones: {startFlatZone:F1}/{endFlatZone:F1}, blendDists: {startBlendDist:F1}/{endBlendDist:F1})");
        }

        // Compute handoff deltas at the transition zone end for T-junction constraints.
        // This ensures continuity when switching from per-CS varying delta (inside flat zone)
        // to constant delta (beyond flat zone). Without this, the primary road's slope causes
        // a step at the boundary because the constant delta is computed at the junction center.
        var startHandoffDelta = startElevDelta;
        if (startConstraint?.PrimaryTangentDirection is { } startTangentForHandoff && startFlatZone > 0)
        {
            var startNormal = new Vector2(-startTangentForHandoff.Y, startTangentForHandoff.X);
            var startBankSin = MathF.Sin(startConstraint.PrimaryBankAngleRadians);
            for (var j = 0; j < sections.Count; j++)
            {
                if (distFromStart[j] >= startFlatZone + startTransitionDist)
                {
                    var offset = sections[j].CenterPoint - sections[0].CenterPoint;
                    var primarySurfElev = startConstraint.Elevation
                                          + startConstraint.Slope *
                                          Vector2.Dot(offset, startTangentForHandoff)
                                          + startBankSin *
                                          Vector2.Dot(offset, startNormal);
                    var natElev = originalElevations.GetValueOrDefault(
                        sections[j].Index, sections[j].TargetElevation);
                    startHandoffDelta = primarySurfElev - natElev;
                    break;
                }
            }
        }

        var endHandoffDelta = endElevDelta;
        if (endConstraint?.PrimaryTangentDirection is { } endTangentForHandoff && endFlatZone > 0)
        {
            var endNormal = new Vector2(-endTangentForHandoff.Y, endTangentForHandoff.X);
            var endBankSin = MathF.Sin(endConstraint.PrimaryBankAngleRadians);
            for (var j = sections.Count - 1; j >= 0; j--)
            {
                if (roadLength - distFromStart[j] >= endFlatZone + endTransitionDist)
                {
                    var offset = sections[j].CenterPoint - sections[^1].CenterPoint;
                    var primarySurfElev = endConstraint.Elevation
                                          + endConstraint.Slope *
                                          Vector2.Dot(offset, endTangentForHandoff)
                                          + endBankSin *
                                          Vector2.Dot(offset, endNormal);
                    var natElev = originalElevations.GetValueOrDefault(
                        sections[j].Index, sections[j].TargetElevation);
                    endHandoffDelta = primarySurfElev - natElev;
                    break;
                }
            }
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var cs = sections[i];

            if (cs.IsRoundaboutBlended)
                continue;

            if (!originalElevations.TryGetValue(cs.Index, out var naturalElev))
                continue;

            var naturalBank = originalBankAngles.GetValueOrDefault(cs.Index, cs.BankAngleRadians);

            var dist = distFromStart[i];
            var distFromEnd = roadLength - dist;

            // Compute per-end Hermite weights using localized blend distances.
            // h00(t) = 2t³ - 3t² + 1: 1 at junction, 0 at blend boundary, zero slope at both.
            float startH00 = 0f, endH00 = 0f;

            if (startConstraint != null)
            {
                // h00 = 1.0 in flat zone AND transition zone; decay starts at transition end
                var localDist = dist - startFlatZone - startTransitionDist;
                if (localDist <= 0f)
                    startH00 = 1f; // In flat zone or transition zone: full correction
                else
                {
                    var effectiveBlendDist = startBlendDist - startTransitionDist;
                    if (effectiveBlendDist > 0.01f && localDist < effectiveBlendDist)
                    {
                        var t = localDist / effectiveBlendDist;
                        var t2 = t * t;
                        var t3 = t2 * t;
                        startH00 = 2f * t3 - 3f * t2 + 1f;
                    }
                }
                // else: beyond blend zone, startH00 stays 0
            }

            if (endConstraint != null)
            {
                var localDist = distFromEnd - endFlatZone - endTransitionDist;
                if (localDist <= 0f)
                    endH00 = 1f;
                else
                {
                    var effectiveBlendDist = endBlendDist - endTransitionDist;
                    if (effectiveBlendDist > 0.01f && localDist < effectiveBlendDist)
                    {
                        var t = localDist / effectiveBlendDist;
                        var t2 = t * t;
                        var t3 = t2 * t;
                        endH00 = 2f * t3 - 3f * t2 + 1f;
                    }
                }
            }

            // Skip if outside all blend zones
            if (startH00 < 0.001f && endH00 < 0.001f)
                continue;

            // Compute spatially-varying elevation deltas for T-junctions.
            // Flat zone: per-CS analytical delta (exact primary surface match)
            // Transition zone: quintic blend from analytical to constant handoff delta
            // Beyond transition: constant handoff delta (decayed by h00)
            var adjStartElevDelta = startHandoffDelta;
            if (startConstraint?.PrimaryTangentDirection is { } startPrimaryTangent &&
                dist <= startFlatZone + startTransitionDist)
            {
                var offset = sections[i].CenterPoint - sections[0].CenterPoint;
                var startPrimaryNormal = new Vector2(-startPrimaryTangent.Y, startPrimaryTangent.X);
                var primarySurfaceElev = startConstraint.Elevation
                                         + startConstraint.Slope * Vector2.Dot(offset, startPrimaryTangent)
                                         + MathF.Sin(startConstraint.PrimaryBankAngleRadians) *
                                         Vector2.Dot(offset, startPrimaryNormal);
                var analyticalDelta = primarySurfaceElev - naturalElev;

                if (dist <= startFlatZone || startTransitionDist < 0.01f)
                {
                    adjStartElevDelta = analyticalDelta;
                }
                else
                {
                    // Transition zone: blend analytical → handoff via quintic smootherstep
                    var tTrans = (dist - startFlatZone) / startTransitionDist;
                    var blend = tTrans * tTrans * tTrans * (tTrans * (tTrans * 6f - 15f) + 10f);
                    adjStartElevDelta = analyticalDelta * (1f - blend) + startHandoffDelta * blend;
                }
            }

            var adjEndElevDelta = endHandoffDelta;
            if (endConstraint?.PrimaryTangentDirection is { } endPrimaryTangent &&
                distFromEnd <= endFlatZone + endTransitionDist)
            {
                var offset = sections[i].CenterPoint - sections[^1].CenterPoint;
                var endPrimaryNormal = new Vector2(-endPrimaryTangent.Y, endPrimaryTangent.X);
                var primarySurfaceElev = endConstraint.Elevation
                                         + endConstraint.Slope * Vector2.Dot(offset, endPrimaryTangent)
                                         + MathF.Sin(endConstraint.PrimaryBankAngleRadians) *
                                         Vector2.Dot(offset, endPrimaryNormal);
                var analyticalDelta = primarySurfaceElev - naturalElev;

                if (distFromEnd <= endFlatZone || endTransitionDist < 0.01f)
                {
                    adjEndElevDelta = analyticalDelta;
                }
                else
                {
                    var tTrans = (distFromEnd - endFlatZone) / endTransitionDist;
                    var blend = tTrans * tTrans * tTrans * (tTrans * (tTrans * 6f - 15f) + 10f);
                    adjEndElevDelta = analyticalDelta * (1f - blend) + endHandoffDelta * blend;
                }
            }

            // Apply corrections to BOTH elevation and bankAngle simultaneously
            var elevCorrection = adjStartElevDelta * startH00 + adjEndElevDelta * endH00;
            var bankCorrection = startBankDelta * startH00 + endBankDelta * endH00;

            var newElev = naturalElev + elevCorrection;
            var newBank = naturalBank + bankCorrection;

            // DEBUG: Log junction endpoint CS values
            if ((i == 0 && startConstraint?.PrimaryTangentDirection != null) ||
                (i == sections.Count - 1 && endConstraint?.PrimaryTangentDirection != null))
            {
                var halfW = cs.EffectiveRoadWidth / 2f;
                var leftEdge = newElev - halfW * MathF.Sin(newBank);
                var rightEdge = newElev + halfW * MathF.Sin(newBank);
                var whichEnd = i == 0 ? "START" : "END";
                TerrainCreationLogger.Current?.Detail(
                    $"  [T-SNAP BLEND] Spline {cs.OwnerSplineId} {whichEnd} endpoint CS #{cs.Index}:");
                TerrainCreationLogger.Current?.Detail(
                    $"    naturalElev={naturalElev:F3}m -> newElev={newElev:F3}m " +
                    $"(delta={newElev - naturalElev:F3}m)");
                TerrainCreationLogger.Current?.Detail(
                    $"    naturalBank={BankingCalculator.RadiansToDegrees(naturalBank):F2}° " +
                    $"-> newBank={BankingCalculator.RadiansToDegrees(newBank):F2}°");
                TerrainCreationLogger.Current?.Detail(
                    $"    edges: L={leftEdge:F3}m R={rightEdge:F3}m halfWidth={halfW:F2}m");
                TerrainCreationLogger.Current?.Detail(
                    $"    h00: start={startH00:F4} end={endH00:F4} " +
                    $"dist={dist:F2}m distFromEnd={distFromEnd:F2}m");
                TerrainCreationLogger.Current?.Detail(
                    $"    adjDelta: start={adjStartElevDelta:F4} end={adjEndElevDelta:F4}");
            }

            if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f ||
                MathF.Abs(newBank - cs.BankAngleRadians) > 0.0001f)
            {
                cs.TargetElevation = newElev;
                cs.BankAngleRadians = newBank;
                modified++;
            }
        }

        return modified;
    }

    /// <summary>
    ///     Handles MidSplineCrossing junctions where both roads pass through.
    ///     Uses bidirectional elevation influence from the crossing point.
    /// </summary>
    private int ApplyMidSplineCrossingInfluences(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<int, float> originalElevations)
    {
        var modifiedCount = 0;
        var crossSectionInfluences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>();

        foreach (var junction in network.Junctions.Where(j =>
                     j.Type == JunctionType.MidSplineCrossing && !j.IsExcluded))
        {
            if (float.IsNaN(junction.HarmonizedElevation))
            {
                // Compute harmonized elevation for mid-spline crossing
                var totalPriority = 0f;
                var weightedElev = 0f;
                foreach (var c in junction.Contributors)
                {
                    if (float.IsNaN(c.CrossSection.TargetElevation))
                        continue;
                    // Use squared priority so higher-priority roads dominate more
                    var prioritySq = (float)(c.Spline.Priority * c.Spline.Priority);
                    totalPriority += prioritySq;
                    weightedElev += c.CrossSection.TargetElevation * prioritySq;
                }

                junction.HarmonizedElevation = totalPriority > 0
                    ? weightedElev / totalPriority
                    : junction.Contributors.FirstOrDefault()?.CrossSection.TargetElevation ?? 0f;
            }

            foreach (var contributor in junction.Contributors)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                    continue;

                var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                     ?? new JunctionHarmonizationParameters();
                var crossingWidth = contributor.Spline.WidthProfile
                        ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                    ?? contributor.Spline.Parameters.RoadWidthMeters;
                var blendDistance = junctionParams.GetEffectiveBlendDistance(crossingWidth);

                // Find crossing index in spline
                var crossingIndex = splineSections.FindIndex(cs => cs.Index == contributor.CrossSection.Index);
                if (crossingIndex < 0)
                    continue;

                // Collect bidirectional influences (toward both spline ends)
                CollectInfluencesFromCrossing(splineSections, crossingIndex, junction,
                    blendDistance, crossSectionInfluences, originalElevations);
            }
        }

        // Apply weighted average of influences
        foreach (var (csIndex, influences) in crossSectionInfluences)
        {
            if (!originalElevations.TryGetValue(csIndex, out var originalElev))
                continue;

            var cs = network.CrossSections.FirstOrDefault(c => c.Index == csIndex);
            if (cs == null || cs.IsRoundaboutBlended)
                continue;

            var totalWeight = influences.Sum(inf => inf.weight);
            if (totalWeight < 0.001f)
                continue;

            var weightedJunctionElev = influences.Sum(inf => inf.elevation * inf.weight) / totalWeight;
            var totalInfluence = MathF.Min(totalWeight, 1.0f);
            var newElev = weightedJunctionElev * totalInfluence + originalElev * (1.0f - totalInfluence);

            if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
            {
                cs.TargetElevation = newElev;
                modifiedCount++;
            }
        }

        return modifiedCount;
    }

    /// <summary>
    ///     Collects elevation influences in both directions from a crossing point.
    /// </summary>
    private static void CollectInfluencesFromCrossing(
        List<UnifiedCrossSection> splineSections,
        int crossingIndex,
        NetworkJunction junction,
        float blendDistance,
        Dictionary<int, List<(float elevation, float weight, int junctionId)>> influences,
        Dictionary<int, float> originalElevations)
    {
        // Backward direction (toward spline start)
        var cumulativeDist = 0f;
        for (var i = crossingIndex; i >= 0; i--)
        {
            if (i < crossingIndex)
                cumulativeDist += Vector2.Distance(splineSections[i].CenterPoint, splineSections[i + 1].CenterPoint);

            if (cumulativeDist >= blendDistance) break;

            var t = cumulativeDist / blendDistance;
            // Quintic smoothstep for C2 continuity
            var blend = t * t * t * (t * (t * 6f - 15f) + 10f);
            var weight = 1f - blend;

            if (weight > 0.001f)
                AddInfluence(influences, splineSections[i].Index,
                    junction.HarmonizedElevation, weight, junction.JunctionId);
        }

        // Forward direction (toward spline end)
        cumulativeDist = 0f;
        for (var i = crossingIndex; i < splineSections.Count; i++)
        {
            if (i > crossingIndex)
                cumulativeDist += Vector2.Distance(splineSections[i].CenterPoint, splineSections[i - 1].CenterPoint);

            if (cumulativeDist >= blendDistance) break;

            var t = cumulativeDist / blendDistance;
            var blend = t * t * t * (t * (t * 6f - 15f) + 10f);
            var weight = 1f - blend;

            if (weight > 0.001f)
                AddInfluence(influences, splineSections[i].Index,
                    junction.HarmonizedElevation, weight, junction.JunctionId);
        }
    }

    private static void AddInfluence(
        Dictionary<int, List<(float elevation, float weight, int junctionId)>> influences,
        int csIndex, float elevation, float weight, int junctionId)
    {
        if (!influences.TryGetValue(csIndex, out var list))
        {
            list = new List<(float, float, int)>();
            influences[csIndex] = list;
        }

        list.Add((elevation, weight, junctionId));
    }

    /// <summary>
    ///     Applies endpoint tapering for isolated dead ends.
    ///     Gradually transitions elevation and banking toward terrain.
    /// </summary>
    private static int ApplyEndpointTapering(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float[,] heightMap,
        float metersPerPixel)
    {
        var taperedCount = 0;
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.Endpoint && !j.IsExcluded))
        foreach (var contributor in junction.Contributors)
        {
            if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                continue;

            var roadWidth = contributor.Spline.WidthProfile
                    ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                ?? contributor.Spline.Parameters.RoadWidthMeters;
            var taperDistance = Math.Clamp(roadWidth * 4f, 10f, 50f);

            var distances = CalculateDistancesFromEndpoint(splineSections, contributor.IsSplineStart);

            for (var i = 0; i < splineSections.Count; i++)
            {
                var dist = distances[i];
                if (dist >= taperDistance) continue;

                var cs = splineSections[i];
                if (cs.IsRoundaboutBlended)
                    continue;

                // Sample terrain at this cross-section's position
                var localTerrainElev = SampleTerrainBilinear(
                    heightMap, cs.CenterPoint.X, cs.CenterPoint.Y,
                    metersPerPixel, mapWidth, mapHeight);

                var originalElev = cs.TargetElevation;

                // Quintic smoothstep: 0 at endpoint, 1 at taper boundary
                var t = dist / taperDistance;
                var blend = t * t * t * (t * (t * 6f - 15f) + 10f);

                // Blend toward terrain at endpoint, road elevation at taper boundary
                cs.TargetElevation = localTerrainElev * (1f - blend) + originalElev * blend;

                // Taper banking angle proportionally
                cs.BankAngleRadians *= blend;

                // Update edges from tapered values
                var halfWidth = cs.EffectiveRoadWidth / 2f;
                var elevDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
                cs.LeftEdgeElevation = cs.TargetElevation - elevDelta;
                cs.RightEdgeElevation = cs.TargetElevation + elevDelta;

                if (MathF.Abs(cs.TargetElevation - originalElev) > 0.001f)
                    taperedCount++;
            }
        }

        return taperedCount;
    }

    /// <summary>
    ///     Pre-computes IDW weight modifiers for terrain blending (Phase 4).
    ///     Terminating roads near junctions get reduced weights so the continuous
    ///     road's elevation profile dominates the junction area.
    /// </summary>
    private static int ComputeJunctionIdwWeightModifiers(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var modifiedCount = 0;

        foreach (var junction in network.Junctions)
        {
            if (junction.IsExcluded || junction.Type == JunctionType.Endpoint)
                continue;

            var continuousRoads = junction.GetContinuousRoads().ToList();
            var terminatingRoads = junction.GetTerminatingRoads().ToList();

            List<JunctionContributor> contributorsToSuppress;

            if (continuousRoads.Count > 0 && terminatingRoads.Count > 0)
            {
                contributorsToSuppress = terminatingRoads;
            }
            else if (junction.HasMixedPriorities && terminatingRoads.Count > 0)
            {
                contributorsToSuppress = junction.GetLowerPriorityContributors()
                    .Where(c => c.IsEndpoint).ToList();
            }
            else
            {
                continue;
            }

            // Determine the flat zone distance from the primary road's half-width.
            // CSes within the flat zone have TargetElevation at the primary road edge
            // (not the primary centerline), but their IDW contribution still pulls
            // blend zone terrain to wrong values. Zero their weight so the primary
            // road's own CSes provide correct banked elevation to the blend zone.
            var flatZone = 0f;
            if ((junction.Type == JunctionType.TJunction || junction.Type == JunctionType.Roundabout)
                && continuousRoads.Count > 0)
            {
                var primaryCS = continuousRoads.OrderByDescending(c => c.Spline.Priority)
                    .First().CrossSection;
                flatZone = primaryCS.EffectiveRoadWidth / 2f;
            }

            foreach (var contributor in contributorsToSuppress)
            {
                if (!crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
                    continue;

                var junctionParams = contributor.Spline.Parameters.GetJunctionHarmonizationParameters();
                if (!junctionParams.EnableJunctionIdwFiltering)
                    continue;

                var minWeight = junctionParams.MinTerminatingIdwWeight;
                var idwWidth = contributor.Spline.WidthProfile
                        ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                    ?? contributor.Spline.Parameters.RoadWidthMeters;
                var taperDistance = junctionParams.IdwFilterTaperDistanceMeters
                                   ?? junctionParams.GetEffectiveBlendDistance(idwWidth);

                if (taperDistance <= 0) continue;

                var distances = CalculateDistancesFromEndpoint(splineSections, contributor.IsSplineStart);

                for (var i = 0; i < splineSections.Count; i++)
                {
                    var dist = distances[i];
                    if (dist >= taperDistance) continue;

                    if (dist <= flatZone)
                    {
                        // Within flat zone: zero IDW contribution. The primary road's
                        // CSes handle blend zone elevation correctly with their banking.
                        if (0f < splineSections[i].JunctionIdwWeightModifier)
                        {
                            splineSections[i].JunctionIdwWeightModifier = 0f;
                            modifiedCount++;
                        }
                    }
                    else
                    {
                        // Beyond flat zone: taper from minWeight to 1.0, measured from
                        // flat zone edge rather than from the endpoint.
                        var adjustedDist = dist - flatZone;
                        var adjustedTaper = taperDistance - flatZone;
                        if (adjustedTaper > 0.01f)
                        {
                            var t = adjustedDist / adjustedTaper;
                            var blendedT = t * t * t * (t * (t * 6f - 15f) + 10f);
                            var modifier = minWeight + (1f - minWeight) * blendedT;

                            if (modifier < splineSections[i].JunctionIdwWeightModifier)
                            {
                                splineSections[i].JunctionIdwWeightModifier = modifier;
                                modifiedCount++;
                            }
                        }
                    }
                }
            }
        }

        return modifiedCount;
    }

    /// <summary>
    ///     Final post-iteration snap for T-junction terminating road endpoints.
    ///     After the iterative convergence loop, the primary road's elevation and banking
    ///     may have drifted from iteration 0 values. This method reads the CURRENT primary
    ///     surface and directly corrects terminating road endpoints + flat zone CSes to match.
    ///     Must be called AFTER the iteration loop completes and all processing is final.
    /// </summary>
    /// <returns>Number of cross-sections corrected.</returns>
    public int FinalSnapTJunctionEndpoints(UnifiedRoadNetwork network)
    {
        var corrected = 0;

        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        foreach (var junction in network.Junctions.Where(j =>
                     (j.Type == JunctionType.TJunction && !j.IsExcluded) ||
                     j.Type == JunctionType.Roundabout))
        {
            var continuous = junction.GetContinuousRoads().ToList();
            if (continuous.Count == 0) continue;

            var primaryContributor = continuous.OrderByDescending(c => c.Spline.Priority).First();
            var primaryCS = primaryContributor.CrossSection;

            // For roundabouts, find the closest ring CS to the junction for more accurate data
            if (junction.Type == JunctionType.Roundabout &&
                crossSectionsBySpline.TryGetValue(primaryContributor.Spline.SplineId, out var ringSnapSections))
            {
                var closestDist = float.MaxValue;
                foreach (var cs in ringSnapSections)
                {
                    var dist = Vector2.Distance(cs.CenterPoint, junction.Position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        primaryCS = cs;
                    }
                }
            }

            // Calculate primary road's current slope
            var primarySlope = 0f;
            if (crossSectionsBySpline.TryGetValue(primaryContributor.Spline.SplineId, out var primarySections))
            {
                var junctionIndex = primarySections.FindIndex(cs => cs.Index == primaryCS.Index);
                if (junctionIndex >= 0)
                    primarySlope = CalculateSlopeAtIndex(primarySections, junctionIndex);
            }

            if (float.IsNaN(primarySlope)) primarySlope = 0f;

            foreach (var terminating in junction.GetTerminatingRoads())
            {
                if (!crossSectionsBySpline.TryGetValue(terminating.Spline.SplineId, out var termSections))
                    continue;

                var terminatingCS = terminating.CrossSection;
                var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;
                var flatZone = primaryCS.EffectiveRoadWidth / 2f;

                // Project terminating road edges onto CURRENT primary surface
                var leftPos = terminatingCS.CenterPoint - terminatingCS.NormalDirection * halfWidth;
                var rightPos = terminatingCS.CenterPoint + terminatingCS.NormalDirection * halfWidth;

                var leftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(leftPos, primaryCS, primarySlope);
                var rightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(rightPos, primaryCS, primarySlope);

                var centerElev = (leftElev + rightElev) / 2f;
                var edgeDelta = (rightElev - leftElev) / 2f;
                var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
                var targetBank = MathF.Asin(sinBank);

                // Snap endpoint, flat zone, and apply proportional correction in blend zone
                var isStart = terminating.IsSplineStart;
                var dists = CalculateDistancesFromEndpoint(termSections, isStart);
                var primaryTangent = primaryCS.TangentDirection;
                var primaryNormal = new Vector2(-primaryTangent.Y, primaryTangent.X);
                var primaryBankSin = MathF.Sin(primaryCS.BankAngleRadians);

                // Compute zone distances matching BlendSplineProfile logic
                var junctionParams = terminating.Spline.Parameters.GetJunctionHarmonizationParameters();
                var snapWidth = terminating.Spline.WidthProfile
                        ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
                    ?? terminating.Spline.Parameters.RoadWidthMeters;
                var blendDist = junctionParams.GetEffectiveBlendDistance(snapWidth);
                var transitionDist = MathF.Min(flatZone, blendDist * 0.25f);
                var effectiveBlendDist = blendDist - transitionDist;
                var totalExtent = flatZone + transitionDist + effectiveBlendDist;

                var endpointCS = isStart ? termSections[0] : termSections[^1];
                var snapExtent = flatZone + transitionDist;

                // Guard: Skip if endpointCS is far from the junction. This happens when
                // IsSplineStart points to the wrong end (e.g., MidSplineCrossing→TJunction
                // conversion sets IsSplineStart to the dead-end tip instead of the junction).
                // Snapping from the wrong reference point would corrupt remote cross-sections.
                var endpointDistToJunction = Vector2.Distance(endpointCS.CenterPoint, junction.Position);
                if (endpointDistToJunction > totalExtent * 2f)
                {
                    TerrainCreationLogger.Current?.Detail(
                        $"  [T-SNAP] Skipping spline {terminating.Spline.SplineId} at junction #{junction.JunctionId}: " +
                        $"endpointCS is {endpointDistToJunction:F0}m from junction (limit {totalExtent * 2f:F0}m)");
                    continue;
                }

                // Pass 1: Snap CSes to primary surface for drift correction.
                // When the primary road has banking, the BlendSplineProfile uses an edge-anchored
                // constraint (PrimaryBankAngleRadians = 0, banking baked into Elevation).
                // FinalSnap must NOT overwrite these values with centerline-based surface
                // formulas that extrapolate banking beyond the road edge. Skip the entire
                // snap zone — BlendSplineProfile's analytical delta already set correct values.
                var hasPrimaryBanking = MathF.Abs(primaryCS.BankAngleRadians) > 0.001f;
                var elevDriftAtBoundary = 0f;
                var bankDriftAtBoundary = 0f;
                var driftMeasured = false;

                for (var i = 0; i < termSections.Count; i++)
                {
                    if (dists[i] > snapExtent) continue;
                    // When primary road has banking, skip entire snap zone to preserve
                    // BlendSplineProfile's edge-anchored values. The primary road's
                    // protection mask covers the overlap, and the Hermite decay zone
                    // handles the transition. FinalSnap's centerline-based extrapolation
                    // would create wrong values beyond the road edge.
                    if (hasPrimaryBanking) continue;

                    var cs = termSections[i];
                    var offset = cs.CenterPoint - endpointCS.CenterPoint;

                    // Compute primary surface elevation at this CS center
                    var surfElev = centerElev
                                   + primarySlope * Vector2.Dot(offset, primaryTangent)
                                   + primaryBankSin * Vector2.Dot(offset, primaryNormal);

                    // Compute primary surface banking at this CS position by projecting edges
                    var csHalfWidth = cs.EffectiveRoadWidth / 2f;
                    var leftEdgePos = cs.CenterPoint - cs.NormalDirection * csHalfWidth;
                    var rightEdgePos = cs.CenterPoint + cs.NormalDirection * csHalfWidth;
                    var leftSurfElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                        leftEdgePos, primaryCS, primarySlope);
                    var rightSurfElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                        rightEdgePos, primaryCS, primarySlope);
                    var surfEdgeDelta = (rightSurfElev - leftSurfElev) / 2f;
                    var surfSinBank = csHalfWidth > 0.01f
                        ? Math.Clamp(surfEdgeDelta / csHalfWidth, -1f, 1f)
                        : 0f;
                    var surfBank = MathF.Asin(surfSinBank);

                    var elevError = surfElev - cs.TargetElevation;
                    var bankError = surfBank - cs.BankAngleRadians;

                    // Track drift at the outermost snapped CS (closest to blend zone boundary)
                    if (!driftMeasured || dists[i] > dists[i - 1])
                    {
                        elevDriftAtBoundary = elevError;
                        bankDriftAtBoundary = bankError;
                        driftMeasured = true;
                    }

                    if (MathF.Abs(elevError) > 0.005f)
                    {
                        cs.TargetElevation = surfElev;
                        corrected++;
                    }

                    // Snap bank angle at flat zone boundary + transition zone
                    if (MathF.Abs(bankError) > 0.001f)
                    {
                        cs.BankAngleRadians = surfBank;
                        corrected++;
                    }

                    // Re-derive edge elevations
                    var hw = cs.EffectiveRoadWidth / 2f;
                    var ed = hw * MathF.Sin(cs.BankAngleRadians);
                    cs.LeftEdgeElevation = cs.TargetElevation - ed;
                    cs.RightEdgeElevation = cs.TargetElevation + ed;
                }

                // Pass 2: Propagate drift correction through blend zone with h00 decay.
                // This corrects for stale constraint drift (elevation + banking) without
                // pulling the road toward the primary surface (preserves terrain transition).
                if (driftMeasured &&
                    (MathF.Abs(elevDriftAtBoundary) > 0.005f ||
                     MathF.Abs(bankDriftAtBoundary) > 0.001f))
                {
                    for (var i = 0; i < termSections.Count; i++)
                    {
                        var localDist = dists[i] - snapExtent;
                        if (localDist <= 0f || localDist >= effectiveBlendDist) continue;

                        var t = localDist / effectiveBlendDist;
                        var t2 = t * t;
                        var t3 = t2 * t;
                        var h00 = 2f * t3 - 3f * t2 + 1f;

                        var cs = termSections[i];
                        var anyChange = false;

                        var elevCorrection = elevDriftAtBoundary * h00;
                        if (MathF.Abs(elevCorrection) > 0.005f)
                        {
                            cs.TargetElevation += elevCorrection;
                            anyChange = true;
                        }

                        var bankCorrection = bankDriftAtBoundary * h00;
                        if (MathF.Abs(bankCorrection) > 0.001f)
                        {
                            cs.BankAngleRadians += bankCorrection;
                            anyChange = true;
                        }

                        if (anyChange)
                        {
                            corrected++;
                            var hw = cs.EffectiveRoadWidth / 2f;
                            var ed = hw * MathF.Sin(cs.BankAngleRadians);
                            cs.LeftEdgeElevation = cs.TargetElevation - ed;
                            cs.RightEdgeElevation = cs.TargetElevation + ed;
                        }
                    }
                }
            }
        }

        if (corrected > 0)
        {
            TerrainCreationLogger.Current?.Detail(
                $"  [T-SNAP FINAL] Corrected {corrected} cross-sections to match final primary surfaces");
        }

        return corrected;
    }

    /// <summary>
    ///     Calculates cumulative distances from a spline endpoint.
    /// </summary>
    private static float[] CalculateDistancesFromEndpoint(List<UnifiedCrossSection> sections, bool fromStart)
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
    ///     Calculates slope at a given cross-section index using central difference.
    /// </summary>
    private static float CalculateSlopeAtIndex(List<UnifiedCrossSection> sections, int index)
    {
        if (sections.Count < 2) return 0f;

        var prevIdx = Math.Max(0, index - 3);
        var nextIdx = Math.Min(sections.Count - 1, index + 3);
        if (prevIdx == nextIdx) return 0f;

        var distance = Vector2.Distance(sections[prevIdx].CenterPoint, sections[nextIdx].CenterPoint);
        if (distance < 0.1f) return 0f;

        return (sections[nextIdx].TargetElevation - sections[prevIdx].TargetElevation) / distance;
    }

    /// <summary>
    ///     Extends blend distance when the elevation gap between junction and terrain-following
    ///     profile requires a gentler ramp to stay within max slope constraints.
    /// </summary>
    private static float CalculateAdaptiveBlendDistance(
        float configuredBlendDistance,
        float harmonizedElevation,
        float contributorElevation,
        RoadSmoothingParameters parameters)
    {
        if (float.IsNaN(harmonizedElevation) || float.IsNaN(contributorElevation))
            return configuredBlendDistance;

        var elevDiff = MathF.Abs(harmonizedElevation - contributorElevation);
        if (elevDiff < 0.1f)
            return configuredBlendDistance;

        var effectiveSlopeDeg = parameters.EnableMaxSlopeConstraint
            ? parameters.RoadMaxSlopeDegrees
            : 6.0f;
        effectiveSlopeDeg = MathF.Max(effectiveSlopeDeg, 1.0f);

        var slopeBasedDistance = elevDiff / MathF.Tan(effectiveSlopeDeg * MathF.PI / 180f);
        return MathF.Max(configuredBlendDistance, slopeBasedDistance);
    }

    /// <summary>
    ///     Samples terrain elevation using bilinear interpolation.
    /// </summary>
    private static float SampleTerrainBilinear(
        float[,] heightMap, float worldX, float worldY,
        float metersPerPixel, int mapWidth, int mapHeight)
    {
        var fx = worldX / metersPerPixel;
        var fy = worldY / metersPerPixel;
        var x0 = Math.Clamp((int)fx, 0, mapWidth - 2);
        var y0 = Math.Clamp((int)fy, 0, mapHeight - 2);
        var dx = Math.Clamp(fx - x0, 0f, 1f);
        var dy = Math.Clamp(fy - y0, 0f, 1f);

        return heightMap[y0, x0] * (1f - dx) * (1f - dy)
             + heightMap[y0, x0 + 1] * dx * (1f - dy)
             + heightMap[y0 + 1, x0] * (1f - dx) * dy
             + heightMap[y0 + 1, x0 + 1] * dx * dy;
    }
}

/// <summary>
///     Result of the unified junction profile blending.
/// </summary>
public class UnifiedBlendResult
{
    public int ConstraintsComputed { get; set; }
    public int ModifiedCrossSections { get; set; }
    public int OverlapCrossSectionsSnapped { get; set; }
    public int MidSplineCrossingModified { get; set; }
    public int EndpointsTapered { get; set; }
    public int IdwModifiersSet { get; set; }

    public float MaxElevationChange { get; set; }
}
