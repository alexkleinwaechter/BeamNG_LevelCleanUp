using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Diagnostics;
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
    private Dictionary<int, List<UnifiedCrossSection>>? _currentCrossSectionsBySpline;

    /// <summary>
    ///     Mid-spline elevation influences collected during constraint propagation.
    ///     Applied after blending to nudge continuous roads near junctions where
    ///     short terminating roads couldn't accommodate their blend zones.
    /// </summary>
    private Dictionary<int, List<(float elevation, float weight, int junctionId)>>? _propagatedMidSplineInfluences;

    /// <summary>
    ///     Phase A.5 — per-spline claimed-zones lookup, built once after constraint
    ///     propagation. Used by Step 5b to taper propagated mid-spline influences
    ///     inside contested directly-anchored junction blend zones. Cleared at the
    ///     end of <see cref="ApplyUnifiedProfiles" /> alongside _propagatedMidSplineInfluences.
    /// </summary>
    private Dictionary<int, SplineClaimedZone>? _splineClaimedZones;

    /// <summary>
    ///     Phase C — per-spline ascending list of distFromStart values for
    ///     non-own-anchor (MidSplineCrossing) junction contributors on this spline.
    ///     Built alongside <see cref="_splineClaimedZones" /> when stretch-L is on,
    ///     consumed by <see cref="BlendSplineProfileParabolic" />'s third clamp to
    ///     prevent the stretched zone from running into a smooth mid-spline-crossing
    ///     junction's harmonized elevation (franco OSM 282534720 regression class).
    /// </summary>
    private Dictionary<int, List<float>>? _midSplineCrossingDistancesBySpline;

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

        var jhParams = network.Splines.FirstOrDefault()?.Parameters.JunctionHarmonizationParameters
                       ?? new JunctionHarmonizationParameters();

        // === TWO-PASS HERMITE: Process primary roads first, then terminating roads ===
        // This ensures T-junction constraints use the ACTUAL post-blend primary elevation,
        // eliminating the need for overlap snapping or blend-distance-based surface following.
        // ONE system, no boundaries, no bumps.

        // Step 1: Compute constraints for NON-T-junction roads (Y/X/Complex/Endpoint)
        // and identify which splines are terminating at T-junctions (processed in pass 2)
        var constraints = ComputeAllJunctionConstraints(network, crossSectionsBySpline, heightMap, metersPerPixel);

        // Propagation pass: find short splines and extend constraints into neighboring splines
        _currentCrossSectionsBySpline = crossSectionsBySpline;
        PropagateConstraintsThroughShortSplines(constraints, network);
        _currentCrossSectionsBySpline = null;

        result.ConstraintsComputed = constraints.Count;

        // Phase A.5: built for propagation overlap taper.
        // Phase B.3/B.2: also built when EnableBlendZoneEndC1 or EnableShortConnectorBlend is on
        // (nested-guard lookup + short-connector dispatch read the per-spline claim).
        var buildForA5 = jhParams.EnablePropagationOverlapTaper
                         && _propagatedMidSplineInfluences is { Count: > 0 };
        var buildForB3OrB2 = jhParams.EnableBlendZoneEndC1 || jhParams.EnableShortConnectorBlend;
        if (buildForA5 || buildForB3OrB2)
        {
            _splineClaimedZones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        }

        // Phase C: when stretch-L is on, build the per-spline lookup of non-own-anchor
        // junction CS distances. The blender's third clamp uses it to refuse stretching
        // past a MidSplineCrossing contributor's CS on the same spline.
        if (jhParams.EnableBlendDistanceStretchToMatchSlope)
        {
            _midSplineCrossingDistancesBySpline = BuildMidSplineCrossingDistances(network, crossSectionsBySpline);
        }

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

        // Multi-way junctions with a dominant road also need deferral:
        // terminating roads should wait for the dominant road to get its pass-1 elevation.
        foreach (var junction in network.Junctions.Where(j =>
            (j.Type == JunctionType.YJunction || j.Type == JunctionType.CrossRoads || j.Type == JunctionType.Complex)
            && !j.IsExcluded))
        {
            var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
            var junctionParams = endpoints.FirstOrDefault()?.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            if (!junctionParams.EnableMultiWayDominantRoadDetection) continue;

            var dominant = DetectDominantRoad(endpoints, junctionParams.DominantRoadWidthRatio, crossSectionsBySpline);
            if (dominant != null)
            {
                foreach (var t in endpoints.Where(c =>
                    c.Spline.SplineId != dominant.Spline.SplineId || c.IsSplineStart != dominant.IsSplineStart))
                {
                    deferredTerminatingSplines.Add(t.Spline.SplineId);
                }
            }
        }

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

            result.ModifiedCrossSections += jhParams.EnableParabolicJunctionBlend
                ? BlendSplineProfileParabolic(
                    sections, startConstraint, endConstraint, originalElevations, originalBankAngles,
                    enableC1: jhParams.EnableBlendZoneEndC1,
                    claimedZone: _splineClaimedZones?.GetValueOrDefault(splineId),
                    enableShortConnectorBlend: jhParams.EnableShortConnectorBlend,
                    enableStretchL: jhParams.EnableBlendDistanceStretchToMatchSlope,
                    otherJunctionDistancesOnSpline: _midSplineCrossingDistancesBySpline?.GetValueOrDefault(splineId),
                    enableBankBlend: jhParams.EnableParabolicBankBlend)
                : BlendSplineProfile(
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

            // Also recompute multi-T junction constraints using post-pass-1 dominant road elevations
            _currentCrossSectionsBySpline = crossSectionsBySpline;
            foreach (var junction in network.Junctions.Where(j =>
                (j.Type == JunctionType.YJunction || j.Type == JunctionType.CrossRoads || j.Type == JunctionType.Complex)
                && !j.IsExcluded))
            {
                var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
                var junctionParams = endpoints.FirstOrDefault()?.Spline.Parameters.JunctionHarmonizationParameters
                                     ?? new JunctionHarmonizationParameters();
                if (!junctionParams.EnableMultiWayDominantRoadDetection) continue;

                var dominant = DetectDominantRoad(endpoints, junctionParams.DominantRoadWidthRatio, crossSectionsBySpline);
                if (dominant != null)
                    ComputeMultiTJunctionConstraints(junction, dominant, endpoints, refinedConstraints);
            }
            _currentCrossSectionsBySpline = null;

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

                result.ModifiedCrossSections += jhParams.EnableParabolicJunctionBlend
                    ? BlendSplineProfileParabolic(
                        sections, startConstraint, endConstraint, originalElevations, originalBankAngles,
                        enableC1: jhParams.EnableBlendZoneEndC1,
                        claimedZone: _splineClaimedZones?.GetValueOrDefault(splineId),
                        enableShortConnectorBlend: jhParams.EnableShortConnectorBlend,
                        enableStretchL: jhParams.EnableBlendDistanceStretchToMatchSlope,
                        otherJunctionDistancesOnSpline: _midSplineCrossingDistancesBySpline?.GetValueOrDefault(splineId),
                        enableBankBlend: jhParams.EnableParabolicBankBlend)
                    : BlendSplineProfile(
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

        // Step 5b: Apply propagated mid-spline influences from short-segment propagation.
        // These nudge continuous roads near T-junctions where short terminating roads
        // couldn't accommodate their blend zones (e.g., roundabout → short entry → main road).
        // Phase A.5: when EnablePropagationOverlapTaper is on and the CS sits inside a
        // directly-anchored junction's blend zone (and that junction != the influence's
        // source junction), the per-influence weight is multiplied by a smoothstep taper
        // → 0 at the contested anchor, 1 at the contested-zone boundary. Prevents a
        // propagated influence from overriding a parabolic blend's edge anchor.
        if (_propagatedMidSplineInfluences is { Count: > 0 })
        {
            var propagatedModified = ApplyPropagatedMidSplineInfluences(
                network.CrossSections,
                _propagatedMidSplineInfluences,
                _splineClaimedZones);

            if (propagatedModified > 0)
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Applied {propagatedModified} propagated mid-spline influences on continuous roads" +
                    (_splineClaimedZones != null ? " (overlap-taper enabled)" : ""));

            _propagatedMidSplineInfluences = null;
            _splineClaimedZones = null;
        }

        // Phase C: clear the per-spline MidSplineCrossing lookup (kept independent of
        // the A.5 cleanup above so it's freed even when Step 5b didn't run).
        _midSplineCrossingDistancesBySpline = null;

        // Phase B diagnostic emission. Side-effect free; gated on EnablePhaseBDiagnostics.
        if (jhParams.EnablePhaseBDiagnostics)
        {
            var outputDir = ResolvePhaseBDiagnosticsOutputDirectory(network);
            if (!string.IsNullOrEmpty(outputDir))
            {
                PhaseBDiagnostics.Emit(
                    outputDir,
                    crossSectionsBySpline,
                    constraints,
                    originalElevations);
            }
        }

        // Step 6: Apply endpoint tapering for dead ends.
        // Phase B.4: skip when EnableEndpointTerrainSlopeMatch is on — the blender's
        // parabolic/cubic path already produces the slope-matched profile, and running
        // the legacy taper here would override and undo it.
        if (!jhParams.EnableEndpointTerrainSlopeMatch)
        {
            result.EndpointsTapered = ApplyEndpointTapering(
                network, crossSectionsBySpline, heightMap, metersPerPixel);
        }

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
        _currentCrossSectionsBySpline = crossSectionsBySpline;
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

                case JunctionType.Continuation:
                    // No constraint — elevation handled by chain-based smoothing (Phase 2).
                    // These are OSM way boundaries, not real junctions.
                    break;

                case JunctionType.MidSplineCrossing:
                    // Handled separately in ApplyMidSplineCrossingInfluences
                    break;
            }
        }

        TerrainLogger.Detail($"  Computed {constraints.Count} junction endpoint constraints " +
                             $"from {network.Junctions.Count(j => !j.IsExcluded)} junctions");

        _currentCrossSectionsBySpline = null;
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

            // Phase 1.9 (C3): preserve an upstream pin if present; otherwise compute as before.
            if (!junction.IsPinned)
                junction.HarmonizedElevation = edgeCenterElev;

            var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var terminatingWidth = terminating.Spline.WidthProfile
                    ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
                ?? terminating.Spline.Parameters.RoadWidthMeters;
            var matSpeed1 = terminating.Spline.Parameters.JunctionHarmonizationParameters?.DesignSpeedKmh;
            var effectiveSpeed1 = AashtoKValueTable.ResolveDesignSpeed(terminating.Spline.OsmRoadType, matSpeed1);
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(terminatingWidth),
                edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters,
                effectiveDesignSpeedKmh: effectiveSpeed1,
                jhParams: junctionParams);

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
                            SurfaceWidth = ringCS.SurfaceWidth,
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

            // Phase 1.9 (C3): preserve an upstream pin if present; otherwise compute as before.
            if (!junction.IsPinned)
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
            var matSpeed2 = terminating.Spline.Parameters.JunctionHarmonizationParameters?.DesignSpeedKmh;
            var effectiveSpeed2 = AashtoKValueTable.ResolveDesignSpeed(terminating.Spline.OsmRoadType, matSpeed2);
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveRoundaboutBlendDistance(terminatingRoundaboutWidth),
                edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters,
                effectiveDesignSpeedKmh: effectiveSpeed2,
                jhParams: junctionParams);

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
    ///     First attempts to detect a dominant road (significantly wider or higher priority).
    ///     If found: treats as multi-T-junction (dominant passes through, others snap to it).
    ///     If not: computes peer-to-peer average with flat zone and analytical deltas.
    /// </summary>
    private void ComputeMultiWayConstraints(
        NetworkJunction junction,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        var endpointContributors = junction.Contributors.Where(c => c.IsEndpoint).ToList();
        if (endpointContributors.Count == 0) return;

        // Check if dominant road detection is enabled via any contributor's parameters
        var junctionParams = endpointContributors[0].Spline.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();
        var enableDominant = junctionParams.EnableMultiWayDominantRoadDetection;
        var widthRatio = junctionParams.DominantRoadWidthRatio;

        // Try to detect a dominant road
        var dominant = enableDominant
            ? DetectDominantRoad(endpointContributors, widthRatio, _currentCrossSectionsBySpline)
            : null;

        if (dominant != null)
        {
            ComputeMultiTJunctionConstraints(junction, dominant, endpointContributors, constraints);
        }
        else
        {
            ComputePeerJunctionConstraints(junction, endpointContributors, constraints);
        }
    }

    /// <summary>
    ///     Detects a dominant road at a multi-way junction.
    ///     A road is dominant if it has strictly higher priority than all others,
    ///     OR its width >= widthRatio × the average width of the others,
    ///     OR it is significantly longer than all others (length-based dominance).
    /// </summary>
    internal static JunctionContributor? DetectDominantRoad(
        List<JunctionContributor> endpointContributors, float widthRatio = 1.5f,
        Dictionary<int, List<UnifiedCrossSection>>? crossSectionsBySpline = null)
    {
        if (endpointContributors.Count < 2) return null;

        // Sort by (priority descending, width descending)
        var sorted = endpointContributors
            .OrderByDescending(c => c.Spline.Priority)
            .ThenByDescending(c => c.Spline.WidthProfile
                ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
                ?? c.Spline.Parameters.RoadWidthMeters)
            .ToList();

        var candidate = sorted[0];

        // Check 1: Strictly higher priority than all others
        var candidatePriority = candidate.Spline.Priority;
        if (sorted.Skip(1).All(c => c.Spline.Priority < candidatePriority))
            return candidate;

        // Check 2: Width >= widthRatio × average of others
        var candidateWidth = candidate.Spline.WidthProfile
                ?.GetWidthsAtDistance(candidate.CrossSection.DistanceAlongSpline).corridor
            ?? candidate.Spline.Parameters.RoadWidthMeters;

        var otherWidths = sorted.Skip(1).Select(c =>
            c.Spline.WidthProfile
                ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
            ?? c.Spline.Parameters.RoadWidthMeters).ToList();

        if (otherWidths.Count > 0 && otherWidths.Average() > 0)
        {
            var avgOtherWidth = otherWidths.Average();
            if (candidateWidth >= avgOtherWidth * widthRatio)
                return candidate;
        }

        // Check 3: Length-based dominance — when priority and width are similar,
        // the longest road is dominant if it's >= 3× the average length of others.
        // This catches cases like a 796m main road meeting two 25m entry/exit segments
        // where all have the same priority/width (same OSM road classification).
        if (crossSectionsBySpline != null)
        {
            var lengths = endpointContributors
                .Select(c => (contributor: c, length: ComputeRoadLength(
                    crossSectionsBySpline.GetValueOrDefault(c.Spline.SplineId) ?? [])))
                .OrderByDescending(x => x.length)
                .ToList();

            if (lengths.Count >= 2 && lengths[0].length > 0)
            {
                var longestLength = lengths[0].length;
                var otherAvgLength = lengths.Skip(1).Average(x => x.length);
                if (otherAvgLength > 0 && longestLength >= otherAvgLength * 3f)
                    return lengths[0].contributor;
            }
        }

        return null;
    }

    /// <summary>
    ///     Computes constraints for a multi-way junction with a detected dominant road.
    ///     The dominant road gets NO constraint (passes through like a T-junction primary).
    ///     All other roads get edge-anchored constraints snapping to the dominant road's surface.
    ///     Uses the same calculation pattern as ComputeTJunctionConstraints.
    /// </summary>
    private void ComputeMultiTJunctionConstraints(
        NetworkJunction junction,
        JunctionContributor dominant,
        List<JunctionContributor> allEndpoints,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        var dominantCS = dominant.CrossSection;
        var dominantHalfWidth = dominantCS.EffectiveRoadWidth / 2f;

        // Calculate dominant road's local slope
        var dominantSlope = 0f;
        var dominantSections = _currentCrossSectionsBySpline?.GetValueOrDefault(dominant.Spline.SplineId);
        if (dominantSections != null)
        {
            var idx = dominantSections.FindIndex(cs => cs.Index == dominantCS.Index);
            if (idx >= 0)
                dominantSlope = CalculateSlopeAtIndex(dominantSections, idx);
        }
        if (float.IsNaN(dominantSlope)) dominantSlope = 0f;

        // Phase 1.9 (C3): preserve an upstream pin if present; otherwise compute as before.
        if (!junction.IsPinned)
            junction.HarmonizedElevation = dominantCS.TargetElevation;

        TerrainCreationLogger.Current?.Detail(
            $"Multi-T Junction #{junction.JunctionId}: dominant=Spline {dominant.Spline.SplineId} " +
            $"(width={dominantCS.EffectiveRoadWidth:F1}m, priority={dominant.Spline.Priority}), " +
            $"{allEndpoints.Count - 1} terminator(s)");

        foreach (var terminating in allEndpoints)
        {
            // Skip the dominant road — it gets no constraint
            if (terminating.Spline.SplineId == dominant.Spline.SplineId
                && terminating.IsSplineStart == dominant.IsSplineStart)
                continue;

            var terminatingCS = terminating.CrossSection;
            var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;

            // Edge-anchored constraint: compute exit point and surface elevation
            var awayDirection = terminating.IsSplineStart
                ? terminatingCS.TangentDirection
                : -terminatingCS.TangentDirection;
            var edgeCenterPoint = terminatingCS.CenterPoint + awayDirection * dominantHalfWidth;

            var edgeCenterElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeCenterPoint, dominantCS, dominantSlope);

            // Bank angle from edge projections
            var edgeLeftPos = edgeCenterPoint - terminatingCS.NormalDirection * halfWidth;
            var edgeRightPos = edgeCenterPoint + terminatingCS.NormalDirection * halfWidth;
            var edgeLeftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeLeftPos, dominantCS, dominantSlope);
            var edgeRightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
                edgeRightPos, dominantCS, dominantSlope);
            var edgeDelta = (edgeRightElev - edgeLeftElev) / 2f;
            var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
            var edgeBankAngle = MathF.Asin(sinBank);

            var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var terminatingWidth = terminating.Spline.WidthProfile
                    ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
                ?? terminating.Spline.Parameters.RoadWidthMeters;
            var matSpeed3 = terminating.Spline.Parameters.JunctionHarmonizationParameters?.DesignSpeedKmh;
            var effectiveSpeed3 = AashtoKValueTable.ResolveDesignSpeed(terminating.Spline.OsmRoadType, matSpeed3);
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(terminatingWidth),
                edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters,
                effectiveDesignSpeedKmh: effectiveSpeed3,
                jhParams: junctionParams);

            var key = (terminating.Spline.SplineId, terminating.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = edgeCenterElev,
                Slope = dominantSlope,
                BankAngleRadians = edgeBankAngle,
                IsSplineStart = terminating.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = dominantHalfWidth,
                BlendDistanceMeters = blendDist,
                PrimaryTangentDirection = dominantCS.TangentDirection,
                PrimaryBankAngleRadians = 0f
            });

            TerrainCreationLogger.Current?.Detail(
                $"  Multi-T terminator Spline {terminating.Spline.SplineId}: " +
                $"edgeElev={edgeCenterElev:F2}m, slope={dominantSlope:F4}, " +
                $"flatZone={dominantHalfWidth:F2}m, blendDist={blendDist:F1}m");
        }
    }

    /// <summary>
    ///     Computes constraints for multi-way junctions where no dominant road exists.
    ///     All roads are equal peers: they all blend toward a shared average elevation.
    ///     Improved over the original ComputeMultiWayConstraints by adding:
    ///     - FlatZone based on max half-width of all contributors
    ///     - PrimaryTangentDirection from weighted average slope for analytical delta mode
    ///     - Slope from priority-weighted average of contributor slopes
    /// </summary>
    private void ComputePeerJunctionConstraints(
        NetworkJunction junction,
        List<JunctionContributor> endpointContributors,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        // Compute harmonized elevation using priority-weighted average
        var totalPriority = 0f;
        var weightedElevation = 0f;
        var weightedSlope = 0f;
        var weightedTangentX = 0f;
        var weightedTangentY = 0f;
        var maxHalfWidth = 0f;

        foreach (var c in endpointContributors)
        {
            if (float.IsNaN(c.CrossSection.TargetElevation))
                continue;

            float priority = c.Spline.Priority;
            totalPriority += priority;
            weightedElevation += c.CrossSection.TargetElevation * priority;

            // Calculate contributor slope
            var slope = 0f;
            var sections = _currentCrossSectionsBySpline?.GetValueOrDefault(c.Spline.SplineId);
            if (sections != null)
            {
                var idx = sections.FindIndex(cs => cs.Index == c.CrossSection.Index);
                if (idx >= 0)
                    slope = CalculateSlopeAtIndex(sections, idx);
            }
            if (float.IsNaN(slope)) slope = 0f;
            weightedSlope += slope * priority;

            // Accumulate tangent direction (pointing away from junction)
            var tangent = c.IsSplineStart
                ? -c.CrossSection.TangentDirection
                : c.CrossSection.TangentDirection;
            weightedTangentX += tangent.X * priority;
            weightedTangentY += tangent.Y * priority;

            // Track maximum half-width for flat zone
            var width = c.Spline.WidthProfile
                    ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
                ?? c.Spline.Parameters.RoadWidthMeters;
            maxHalfWidth = MathF.Max(maxHalfWidth, width / 2f);
        }

        var harmonizedElev = totalPriority > 0
            ? weightedElevation / totalPriority
            : endpointContributors.FirstOrDefault()?.CrossSection.TargetElevation ?? 0f;

        var harmonizedSlope = totalPriority > 0 ? weightedSlope / totalPriority : 0f;

        // Average tangent direction (may be zero if roads cancel out — that's fine)
        var avgTangent = new Vector2(weightedTangentX, weightedTangentY);
        Vector2? primaryTangent = null;
        if (avgTangent.LengthSquared() > 0.0001f)
            primaryTangent = Vector2.Normalize(avgTangent);

        // Phase 1.9 (C3): preserve an upstream pin if present; otherwise compute as before.
        if (!junction.IsPinned)
            junction.HarmonizedElevation = harmonizedElev;

        TerrainCreationLogger.Current?.Detail(
            $"Peer Junction #{junction.JunctionId}: {endpointContributors.Count} contributors, " +
            $"harmonizedElev={harmonizedElev:F2}m, slope={harmonizedSlope:F4}, " +
            $"flatZone={maxHalfWidth:F2}m");

        foreach (var contributor in endpointContributors)
        {
            var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var contributorWidth = contributor.Spline.WidthProfile
                    ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                ?? contributor.Spline.Parameters.RoadWidthMeters;
            var matSpeed4 = contributor.Spline.Parameters.JunctionHarmonizationParameters?.DesignSpeedKmh;
            var effectiveSpeed4 = AashtoKValueTable.ResolveDesignSpeed(contributor.Spline.OsmRoadType, matSpeed4);
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(contributorWidth),
                harmonizedElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters,
                effectiveDesignSpeedKmh: effectiveSpeed4,
                jhParams: junctionParams);

            var key = (contributor.Spline.SplineId, contributor.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = harmonizedElev,
                Slope = harmonizedSlope,
                BankAngleRadians = 0f, // flatten at peer junction
                IsSplineStart = contributor.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = maxHalfWidth,
                BlendDistanceMeters = blendDist,
                PrimaryTangentDirection = primaryTangent,
                PrimaryBankAngleRadians = 0f
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

        // Phase 1.9 (C3): preserve an upstream pin if present; otherwise compute as before.
        if (!junction.IsPinned)
            junction.HarmonizedElevation = terrainElev;

        foreach (var contributor in junction.Contributors)
        {
            var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                                 ?? new JunctionHarmonizationParameters();
            var endpointWidth = contributor.Spline.WidthProfile
                    ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
                ?? contributor.Spline.Parameters.RoadWidthMeters;
            var matSpeed5 = contributor.Spline.Parameters.JunctionHarmonizationParameters?.DesignSpeedKmh;
            var effectiveSpeed5 = AashtoKValueTable.ResolveDesignSpeed(contributor.Spline.OsmRoadType, matSpeed5);
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(endpointWidth),
                terrainElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters,
                effectiveDesignSpeedKmh: effectiveSpeed5,
                jhParams: junctionParams);

            // Phase B.4: sample terrain slope along the spline tangent at the endpoint
            // position, project onto direction-of-travel-away-from-endpoint.
            var endpointSlope = 0f;
            if (junctionParams.EnableEndpointTerrainSlopeMatch)
            {
                // The contributor's tangent points along the spline; flip if this is the END so
                // "direction of travel away from endpoint" is positive d for the blender.
                var tangentAwayFromEndpoint = contributor.IsSplineStart
                    ? contributor.CrossSection.TangentDirection
                    : -contributor.CrossSection.TangentDirection;
                endpointSlope = HeightmapSlopeSampler.SampleAlongTangent(
                    heightMap, metersPerPixel,
                    junction.Position, tangentAwayFromEndpoint,
                    sampleDistanceMeters: 2.0f);
            }

            var key = (contributor.Spline.SplineId, contributor.IsSplineStart);
            constraints.TryAdd(key, new JunctionEndpointConstraint
            {
                Elevation = terrainElev,
                Slope = endpointSlope,
                BankAngleRadians = 0f,
                IsSplineStart = contributor.IsSplineStart,
                Junction = junction,
                FlatZoneDistance = 0f, // no overlap zone for endpoints
                BlendDistanceMeters = blendDist
            });
        }
    }

    /// <summary>
    ///     Phase A parabolic alternative to BlendSplineProfile. Replaces the legacy
    ///     h00-weighted additive delta with a direct parabolic substitution inside
    ///     each end's single blend zone. When a CS is in only the start blend zone,
    ///     its elevation is set to ParabolicJunctionProfile.Sample(d, L, zJunction,
    ///     mJunction, zNaturalAtL). Likewise from the end. When a CS is in BOTH
    ///     blend zones (short spline) or in NEITHER, the legacy BlendSplineProfile
    ///     path runs instead — this method only changes the single-end case.
    ///     Bank-angle correction continues to use the existing h00 logic (banking
    ///     overshoot is not the Phase A problem).
    /// </summary>
    internal static int BlendSplineProfileParabolic(
        List<UnifiedCrossSection> sections,
        JunctionEndpointConstraint? startConstraint,
        JunctionEndpointConstraint? endConstraint,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles,
        bool enableC1 = false,
        SplineClaimedZone? claimedZone = null,
        bool enableShortConnectorBlend = false,
        bool enableStretchL = false,
        float stretchLMaxCap = float.PositiveInfinity,
        IReadOnlyList<float>? otherJunctionDistancesOnSpline = null,
        float midCrossingSafetyMarginMeters = 2.0f,
        bool enableBankBlend = false)
    {
        if (sections.Count < 2) return 0;
        if (startConstraint == null && endConstraint == null) return 0;

        var modified = 0;

        var distFromStart = new float[sections.Count];
        distFromStart[0] = 0;
        for (var i = 1; i < sections.Count; i++)
            distFromStart[i] = distFromStart[i - 1] +
                               Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);

        var roadLength = distFromStart[sections.Count - 1];
        if (roadLength < 0.01f) return 0;

        var startBlendDist = startConstraint?.BlendDistanceMeters ?? 0f;
        var endBlendDist = endConstraint?.BlendDistanceMeters ?? 0f;

        // Two-end overlap (short splines). Phase B.2 dispatches to the compositional blend
        // when enableShortConnectorBlend is on; otherwise the legacy h00 fall-through runs.
        if (startConstraint != null && endConstraint != null &&
            startBlendDist + endBlendDist > roadLength)
        {
            if (enableShortConnectorBlend)
            {
                return BlendShortConnectorCompositional(
                    sections, distFromStart, roadLength,
                    startConstraint, endConstraint,
                    originalElevations, originalBankAngles,
                    enableC1, claimedZone, enableBankBlend);
            }

            return BlendSplineProfile(
                sections, startConstraint, endConstraint,
                originalElevations, originalBankAngles);
        }

        // Look up natural elevation + slope at d=L for each side.
        var startNaturalAtL = 0f;
        var startNaturalSlopeAtL = 0f;
        var startNaturalAtLValid = false;
        var startSampleIdx = -1;
        if (startConstraint != null && startBlendDist > 0.01f)
        {
            for (var i = 0; i < sections.Count; i++)
            {
                if (distFromStart[i] >= startBlendDist)
                {
                    startSampleIdx = i;
                    startNaturalAtL = originalElevations.GetValueOrDefault(
                        sections[i].Index, sections[i].TargetElevation);
                    if (i + 1 < sections.Count)
                    {
                        var zNext = originalElevations.GetValueOrDefault(
                            sections[i + 1].Index, sections[i + 1].TargetElevation);
                        var dDelta = distFromStart[i + 1] - distFromStart[i];
                        startNaturalSlopeAtL = dDelta > 0.001f ? (zNext - startNaturalAtL) / dDelta : 0f;
                    }
                    startNaturalAtLValid = true;
                    break;
                }
            }
        }

        var endNaturalAtL = 0f;
        var endNaturalSlopeAtL = 0f;
        var endNaturalAtLValid = false;
        var endSampleIdx = -1;
        if (endConstraint != null && endBlendDist > 0.01f)
        {
            var endThresh = roadLength - endBlendDist;
            for (var i = sections.Count - 1; i >= 0; i--)
            {
                if (distFromStart[i] <= endThresh)
                {
                    endSampleIdx = i;
                    endNaturalAtL = originalElevations.GetValueOrDefault(
                        sections[i].Index, sections[i].TargetElevation);
                    if (i - 1 >= 0)
                    {
                        var zPrev = originalElevations.GetValueOrDefault(
                            sections[i - 1].Index, sections[i - 1].TargetElevation);
                        var dDelta = distFromStart[i] - distFromStart[i - 1];
                        // Slope INTO the end zone (from outside, moving toward d=roadLength).
                        endNaturalSlopeAtL = dDelta > 0.001f ? (endNaturalAtL - zPrev) / dDelta : 0f;
                    }
                    endNaturalAtLValid = true;
                    break;
                }
            }
        }

        // Phase C — stretch L so the parabola's emergent slope at d=L matches natural.
        // The stretched L extends the parabolic blend zone past the original sample point,
        // so we must re-sample zNaturalAtL/mNaturalAtL at the new L. Hard ceiling is
        // roadLength minus the opposite-end blend distance (minus a 1m safety margin)
        // so the stretched zone never overlaps the other end's claim. stretchLMaxCap is
        // the caller-supplied K-cap ceiling (default +inf = no cap).
        if (enableStretchL && startConstraint != null && startNaturalAtLValid)
        {
            var stretched = BlendDistanceStretcher.ComputeStretchTarget(
                currentL: startBlendDist,
                zJunction: startConstraint.Elevation,
                mJunction: startConstraint.Slope,
                zNaturalAtL: startNaturalAtL,
                mNaturalAtL: startNaturalSlopeAtL);
            var hardCeiling = roadLength - endBlendDist - 1.0f;
            stretched = MathF.Min(stretched, stretchLMaxCap);
            stretched = MathF.Min(stretched, hardCeiling);
            // Phase C third clamp: don't extend past the nearest non-own-anchor
            // junction CS on this spline that sits beyond the current blend zone.
            // Pre-existing inclusions inside currentL are ignored — the parabola
            // was already overwriting them pre-stretch (option b in the plan).
            // Guards the franco OSM node 282534720 regression class.
            if (otherJunctionDistancesOnSpline != null)
                stretched = MathF.Min(stretched, NearestStartSideMidCrossingCeiling(
                    otherJunctionDistancesOnSpline, startBlendDist, midCrossingSafetyMarginMeters));
            if (stretched > startBlendDist + 0.01f)
            {
                startBlendDist = stretched;
                // Re-sample natural at the new L.
                for (var i = 0; i < sections.Count; i++)
                {
                    if (distFromStart[i] >= startBlendDist)
                    {
                        startSampleIdx = i;
                        startNaturalAtL = originalElevations.GetValueOrDefault(
                            sections[i].Index, sections[i].TargetElevation);
                        if (i + 1 < sections.Count)
                        {
                            var zNext = originalElevations.GetValueOrDefault(
                                sections[i + 1].Index, sections[i + 1].TargetElevation);
                            var dDelta = distFromStart[i + 1] - distFromStart[i];
                            startNaturalSlopeAtL = dDelta > 0.001f ? (zNext - startNaturalAtL) / dDelta : 0f;
                        }
                        break;
                    }
                }
            }
        }

        if (enableStretchL && endConstraint != null && endNaturalAtLValid)
        {
            var stretched = BlendDistanceStretcher.ComputeStretchTarget(
                currentL: endBlendDist,
                zJunction: endConstraint.Elevation,
                mJunction: endConstraint.Slope,
                zNaturalAtL: endNaturalAtL,
                mNaturalAtL: endNaturalSlopeAtL);
            var hardCeiling = roadLength - startBlendDist - 1.0f;
            stretched = MathF.Min(stretched, stretchLMaxCap);
            stretched = MathF.Min(stretched, hardCeiling);
            // Phase C third clamp (end-side mirror of the start-side guard above).
            if (otherJunctionDistancesOnSpline != null)
                stretched = MathF.Min(stretched, NearestEndSideMidCrossingCeiling(
                    otherJunctionDistancesOnSpline, roadLength, endBlendDist, midCrossingSafetyMarginMeters));
            if (stretched > endBlendDist + 0.01f)
            {
                endBlendDist = stretched;
                // Re-sample natural at the new L (distance-from-end frame).
                var endThresh = roadLength - endBlendDist;
                for (var i = sections.Count - 1; i >= 0; i--)
                {
                    if (distFromStart[i] <= endThresh)
                    {
                        endSampleIdx = i;
                        endNaturalAtL = originalElevations.GetValueOrDefault(
                            sections[i].Index, sections[i].TargetElevation);
                        if (i - 1 >= 0)
                        {
                            var zPrev = originalElevations.GetValueOrDefault(
                                sections[i - 1].Index, sections[i - 1].TargetElevation);
                            var dDelta = distFromStart[i] - distFromStart[i - 1];
                            endNaturalSlopeAtL = dDelta > 0.001f ? (endNaturalAtL - zPrev) / dDelta : 0f;
                        }
                        break;
                    }
                }
            }
        }

        // Phase D — bank deltas (computed once; written per-CS inside the loop).
        // Mirror of the legacy h00 path's startBankDelta / endBankDelta at line 1725-1728.
        // Placed AFTER the Phase C stretch-L logic so any extension of startBlendDist /
        // endBlendDist is reflected in the bank zone, keeping bank and elevation
        // boundaries coincident.
        var startBankDelta = 0f;
        var endBankDelta = 0f;
        if (enableBankBlend)
        {
            if (startConstraint != null)
            {
                var startEndpointBank = originalBankAngles.GetValueOrDefault(
                    sections[0].Index, sections[0].BankAngleRadians);
                startBankDelta = startConstraint.BankAngleRadians - startEndpointBank;
            }
            if (endConstraint != null)
            {
                var endEndpointBank = originalBankAngles.GetValueOrDefault(
                    sections[^1].Index, sections[^1].BankAngleRadians);
                endBankDelta = endConstraint.BankAngleRadians - endEndpointBank;
            }
        }

        // Decide per-side whether the cubic dispatch is safe (no nested junction at sample point).
        var startUseCubic = enableC1 && startSampleIdx >= 0
            && (claimedZone == null || !SplineClaimedZones.HasOtherClaimNear(
                claimedZone, distFromStart[startSampleIdx], ownAnchorIsStart: true, marginMeters: 2.0f));
        var endUseCubic = enableC1 && endSampleIdx >= 0
            && (claimedZone == null || !SplineClaimedZones.HasOtherClaimNear(
                claimedZone, distFromStart[endSampleIdx], ownAnchorIsStart: false, marginMeters: 2.0f));

        for (var i = 0; i < sections.Count; i++)
        {
            var cs = sections[i];
            if (cs.IsRoundaboutBlended) continue;

            var d = distFromStart[i];
            var distFromEnd = roadLength - d;
            var inStartZone = startConstraint != null && d < startBlendDist;
            var inEndZone = endConstraint != null && distFromEnd < endBlendDist;

            if (!inStartZone && !inEndZone) continue;
            if (inStartZone && inEndZone) continue;

            float newElev;
            if (inStartZone && startNaturalAtLValid)
            {
                newElev = startUseCubic
                    ? CubicJunctionProfile.Sample(
                        d, startBlendDist,
                        zJunction: startConstraint!.Elevation,
                        mJunction: startConstraint.Slope,
                        zNaturalAtL: startNaturalAtL,
                        mNaturalAtL: startNaturalSlopeAtL)
                    : ParabolicJunctionProfile.Sample(
                        d, startBlendDist,
                        zJunction: startConstraint!.Elevation,
                        mJunction: startConstraint.Slope,
                        zNaturalAtL: startNaturalAtL);
            }
            else if (inEndZone && endNaturalAtLValid)
            {
                newElev = endUseCubic
                    ? CubicJunctionProfile.Sample(
                        distFromEnd, endBlendDist,
                        zJunction: endConstraint!.Elevation,
                        mJunction: endConstraint.Slope,
                        zNaturalAtL: endNaturalAtL,
                        mNaturalAtL: endNaturalSlopeAtL)
                    : ParabolicJunctionProfile.Sample(
                        distFromEnd, endBlendDist,
                        zJunction: endConstraint!.Elevation,
                        mJunction: endConstraint.Slope,
                        zNaturalAtL: endNaturalAtL);
            }
            else
            {
                continue;
            }

            if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
            {
                cs.TargetElevation = newElev;
                modified++;
            }

            // Phase D — symmetric bank correction. Bank ramps from natural at d=L
            // to constraint bank at d=0 via Hermite h00, C1 at both ends.
            if (enableBankBlend && (inStartZone || inEndZone))
            {
                float startH00 = 0f, endH00 = 0f;
                if (inStartZone && startBlendDist > 0.01f)
                {
                    var t = d / startBlendDist;
                    startH00 = 2f * t * t * t - 3f * t * t + 1f;
                }
                if (inEndZone && endBlendDist > 0.01f)
                {
                    var t = distFromEnd / endBlendDist;
                    endH00 = 2f * t * t * t - 3f * t * t + 1f;
                }

                var naturalBank = originalBankAngles.GetValueOrDefault(cs.Index, cs.BankAngleRadians);
                var newBank = naturalBank + startBankDelta * startH00 + endBankDelta * endH00;

                if (MathF.Abs(newBank - cs.BankAngleRadians) > 0.0001f)
                {
                    cs.BankAngleRadians = newBank;
                    // Do not increment `modified` again — elevation already accounted for it.
                }
            }
        }

        return modified;
    }

    /// <summary>
    ///     Phase C — builds the per-spline ascending list of distFromStart values for
    ///     non-own-anchor junction contributors (MidSplineCrossings) on each spline.
    ///     Iterates every <see cref="NetworkJunction.Contributors" /> across the network
    ///     once; contributors with <c>IsEndpoint==true</c> are this spline's own start
    ///     or end anchor and never appear in the result. Splines with no MidSplineCrossings
    ///     are absent from the dictionary (callers use <c>GetValueOrDefault</c>).
    /// </summary>
    private static Dictionary<int, List<float>> BuildMidSplineCrossingDistances(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var distByCsIndex = new Dictionary<int, Dictionary<int, float>>(crossSectionsBySpline.Count);
        foreach (var (splineId, sections) in crossSectionsBySpline)
        {
            if (sections.Count == 0) continue;
            var dist = new Dictionary<int, float>(sections.Count);
            dist[sections[0].Index] = 0f;
            var cumulative = 0f;
            for (var i = 1; i < sections.Count; i++)
            {
                cumulative += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
                dist[sections[i].Index] = cumulative;
            }
            distByCsIndex[splineId] = dist;
        }

        var result = new Dictionary<int, List<float>>();
        foreach (var junction in network.Junctions)
        {
            if (junction.IsExcluded) continue;
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.IsEndpoint) continue;
                var splineId = contributor.Spline.SplineId;
                if (!distByCsIndex.TryGetValue(splineId, out var distMap)) continue;
                if (!distMap.TryGetValue(contributor.CrossSection.Index, out var d)) continue;
                if (!result.TryGetValue(splineId, out var list))
                {
                    list = new List<float>();
                    result[splineId] = list;
                }
                list.Add(d);
            }
        }

        foreach (var list in result.Values)
            list.Sort();

        return result;
    }

    /// <summary>
    ///     Phase C — third stretch-L clamp (start-side). Given an ascending list of
    ///     non-own-anchor junction CS distances on this spline, returns the highest
    ///     distance the start-side stretch may reach (= nearest such CS beyond the
    ///     current blend zone, minus a safety margin). Returns +infinity when no
    ///     other junction CS sits beyond <paramref name="currentStartBlendDist" />,
    ///     leaving the existing clamps unaffected. CSes already inside currentL are
    ///     intentionally ignored — those inclusions pre-exist the stretch.
    /// </summary>
    private static float NearestStartSideMidCrossingCeiling(
        IReadOnlyList<float> distancesAscending,
        float currentStartBlendDist,
        float safetyMargin)
    {
        foreach (var d in distancesAscending)
            if (d > currentStartBlendDist)
                return d - safetyMargin;
        return float.PositiveInfinity;
    }

    /// <summary>
    ///     Phase C — third stretch-L clamp (end-side mirror). The threshold is
    ///     expressed in distance-from-end space: we look for the largest distFromStart
    ///     in the list whose corresponding distFromEnd exceeds
    ///     <paramref name="currentEndBlendDist" />, and return that distFromEnd
    ///     minus the safety margin.
    /// </summary>
    private static float NearestEndSideMidCrossingCeiling(
        IReadOnlyList<float> distancesAscending,
        float roadLength,
        float currentEndBlendDist,
        float safetyMargin)
    {
        var threshold = roadLength - currentEndBlendDist;
        var bestD = float.NaN;
        foreach (var d in distancesAscending)
        {
            if (d < threshold) bestD = d;
            else break;
        }
        return float.IsNaN(bestD) ? float.PositiveInfinity : (roadLength - bestD) - safetyMargin;
    }

    /// <summary>
    ///     Phase B.2 compositional blend for short connector splines. Each end's
    ///     per-CS profile (parabola or cubic per enableC1) is computed independently,
    ///     then weighted by OverlapTaper so each end dominates near its own anchor
    ///     and the two compose smoothly in the overlap region. Replaces the legacy
    ///     h00 fall-through that Phase A inherited.
    /// </summary>
    private static int BlendShortConnectorCompositional(
        List<UnifiedCrossSection> sections,
        float[] distFromStart,
        float roadLength,
        JunctionEndpointConstraint startConstraint,
        JunctionEndpointConstraint endConstraint,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles,
        bool enableC1,
        SplineClaimedZone? claimedZone,
        bool enableBankBlend)
    {
        var modified = 0;

        var startBlendDist = startConstraint.BlendDistanceMeters;
        var endBlendDist = endConstraint.BlendDistanceMeters;
        if (startBlendDist <= 0.01f || endBlendDist <= 0.01f) return 0;

        // Look up the natural elevation and slope at d=L for each side (same logic as the
        // single-end path; duplicated rather than refactored because the short-connector
        // case treats them differently when L > roadLength).
        var startNaturalAtL = 0f;
        var startNaturalSlopeAtL = 0f;
        var startSampleIdx = -1;
        for (var i = 0; i < sections.Count; i++)
        {
            if (distFromStart[i] >= MathF.Min(startBlendDist, roadLength))
            {
                startSampleIdx = i;
                startNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[i].Index, sections[i].TargetElevation);
                if (i + 1 < sections.Count)
                {
                    var zNext = originalElevations.GetValueOrDefault(
                        sections[i + 1].Index, sections[i + 1].TargetElevation);
                    var dDelta = distFromStart[i + 1] - distFromStart[i];
                    startNaturalSlopeAtL = dDelta > 0.001f ? (zNext - startNaturalAtL) / dDelta : 0f;
                }
                break;
            }
        }

        var endNaturalAtL = 0f;
        var endNaturalSlopeAtL = 0f;
        var endSampleIdx = -1;
        var endThresh = MathF.Max(0f, roadLength - endBlendDist);
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            if (distFromStart[i] <= endThresh)
            {
                endSampleIdx = i;
                endNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[i].Index, sections[i].TargetElevation);
                if (i - 1 >= 0)
                {
                    var zPrev = originalElevations.GetValueOrDefault(
                        sections[i - 1].Index, sections[i - 1].TargetElevation);
                    var dDelta = distFromStart[i] - distFromStart[i - 1];
                    endNaturalSlopeAtL = dDelta > 0.001f ? (endNaturalAtL - zPrev) / dDelta : 0f;
                }
                break;
            }
        }

        // For short connectors the natural-at-L sample may fall outside the spline entirely
        // (when L exceeds roadLength). Use the opposite anchor's elevation as the "natural"
        // fallback so the per-end profile remains well-defined.
        if (startSampleIdx < 0)
        {
            startNaturalAtL = endConstraint.Elevation;
            startNaturalSlopeAtL = 0f;
        }
        if (endSampleIdx < 0)
        {
            endNaturalAtL = startConstraint.Elevation;
            endNaturalSlopeAtL = 0f;
        }

        var startUseCubic = enableC1 && startSampleIdx >= 0
            && (claimedZone == null || !SplineClaimedZones.HasOtherClaimNear(
                claimedZone, distFromStart[startSampleIdx], ownAnchorIsStart: true, marginMeters: 2.0f));
        var endUseCubic = enableC1 && endSampleIdx >= 0
            && (claimedZone == null || !SplineClaimedZones.HasOtherClaimNear(
                claimedZone, distFromStart[endSampleIdx], ownAnchorIsStart: false, marginMeters: 2.0f));

        // Phase D — bank deltas (computed once; per-CS write inside the loop).
        var startBankDelta = 0f;
        var endBankDelta = 0f;
        if (enableBankBlend)
        {
            var startEndpointBank = originalBankAngles.GetValueOrDefault(
                sections[0].Index, sections[0].BankAngleRadians);
            var endEndpointBank = originalBankAngles.GetValueOrDefault(
                sections[^1].Index, sections[^1].BankAngleRadians);
            startBankDelta = startConstraint.BankAngleRadians - startEndpointBank;
            endBankDelta   = endConstraint.BankAngleRadians   - endEndpointBank;
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var cs = sections[i];
            if (cs.IsRoundaboutBlended) continue;

            var d = distFromStart[i];
            var distFromEnd = roadLength - d;

            float zFromStart = startUseCubic
                ? CubicJunctionProfile.Sample(
                    d, startBlendDist,
                    zJunction: startConstraint.Elevation,
                    mJunction: startConstraint.Slope,
                    zNaturalAtL: startNaturalAtL,
                    mNaturalAtL: startNaturalSlopeAtL)
                : ParabolicJunctionProfile.Sample(
                    d, startBlendDist,
                    zJunction: startConstraint.Elevation,
                    mJunction: startConstraint.Slope,
                    zNaturalAtL: startNaturalAtL);

            float zFromEnd = endUseCubic
                ? CubicJunctionProfile.Sample(
                    distFromEnd, endBlendDist,
                    zJunction: endConstraint.Elevation,
                    mJunction: endConstraint.Slope,
                    zNaturalAtL: endNaturalAtL,
                    mNaturalAtL: endNaturalSlopeAtL)
                : ParabolicJunctionProfile.Sample(
                    distFromEnd, endBlendDist,
                    zJunction: endConstraint.Elevation,
                    mJunction: endConstraint.Slope,
                    zNaturalAtL: endNaturalAtL);

            // OverlapTaper.Compute(d, L) returns 0 at the anchor (d=0) and 1 at the boundary (d=L).
            // We want w_start ≈ 1 near the start anchor and 0 near the end anchor → use the END's
            // taper evaluated at distFromEnd. Symmetric for w_end.
            var wStart = OverlapTaper.Compute(distFromEnd, endBlendDist);
            var wEnd = OverlapTaper.Compute(d, startBlendDist);
            var wTotal = wStart + wEnd;
            if (wTotal < 0.0001f) wTotal = 1f; // defensive; shouldn't hit on well-formed inputs.

            var newElev = (zFromStart * wStart + zFromEnd * wEnd) / wTotal;

            if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
            {
                cs.TargetElevation = newElev;
                modified++;
            }

            // Phase D — bank composition. Each end contributes a per-anchor h00 profile;
            // the two are composed with the same OverlapTaper weights as elevation.
            if (enableBankBlend)
            {
                float startH00 = 0f, endH00 = 0f;
                if (startBlendDist > 0.01f && d < startBlendDist)
                {
                    var t = d / startBlendDist;
                    startH00 = 2f * t * t * t - 3f * t * t + 1f;
                }
                if (endBlendDist > 0.01f && distFromEnd < endBlendDist)
                {
                    var t = distFromEnd / endBlendDist;
                    endH00 = 2f * t * t * t - 3f * t * t + 1f;
                }

                var naturalBank = originalBankAngles.GetValueOrDefault(cs.Index, cs.BankAngleRadians);
                var bankFromStart = naturalBank + startBankDelta * startH00;
                var bankFromEnd   = naturalBank + endBankDelta   * endH00;

                // Reuse the same wStart/wEnd/wTotal already computed for elevation above.
                var newBank = (bankFromStart * wStart + bankFromEnd * wEnd) / wTotal;

                if (MathF.Abs(newBank - cs.BankAngleRadians) > 0.0001f)
                    cs.BankAngleRadians = newBank;
            }
        }

        return modified;
    }

    /// <summary>
    ///     Phase A.5 testable extraction of Step 5b. Applies propagated mid-spline
    ///     influences to <paramref name="crossSections" /> with optional overlap taper
    ///     via <paramref name="splineClaimedZones" />. Returns number of modified CSes.
    /// </summary>
    internal static int ApplyPropagatedMidSplineInfluences(
        IEnumerable<UnifiedCrossSection> crossSections,
        Dictionary<int, List<(float elevation, float weight, int junctionId)>> influencesByCsIndex,
        Dictionary<int, SplineClaimedZone>? splineClaimedZones)
    {
        var modified = 0;
        var csIndexLookup = crossSections.ToDictionary(cs => cs.Index);

        foreach (var (csIndex, influences) in influencesByCsIndex)
        {
            if (!csIndexLookup.TryGetValue(csIndex, out var cs))
                continue;
            if (float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
                continue;

            var totalWeight = 0f;
            var weightedElevSum = 0f;
            foreach (var inf in influences)
            {
                var w = inf.weight;
                if (splineClaimedZones != null
                    && splineClaimedZones.TryGetValue(cs.OwnerSplineId, out var zone))
                {
                    w *= SplineClaimedZones.GetTaperFor(zone, cs.Index, inf.junctionId);
                }
                totalWeight += w;
                weightedElevSum += inf.elevation * w;
            }

            if (totalWeight < 0.001f) continue;

            var weightedElev = weightedElevSum / totalWeight;
            var influenceFactor = MathF.Min(totalWeight, 1.0f);
            var newElev = weightedElev * influenceFactor + cs.TargetElevation * (1f - influenceFactor);

            if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
            {
                cs.TargetElevation = newElev;
                modified++;
            }
        }

        return modified;
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

        // Short-spline overlap protection: when both ends have constraints and their
        // total zones (flat + blend) would cover > 80% of the road, reduce proportionally.
        // First reduce flat zones if needed, then scale blend distances into remaining space.
        if (startConstraint != null && endConstraint != null)
        {
            var totalFlatZones = startFlatZone + endFlatZone;
            var maxTotal = roadLength * 0.8f;

            // Step 1: If flat zones alone exceed 60% of road, scale them down proportionally.
            // This prevents flat zones from consuming the entire road on very short segments
            // (e.g., 13.5m entry with 5.5m + 5.0m = 10.5m flat zones = 78%).
            if (totalFlatZones > roadLength * 0.6f && totalFlatZones > 0.1f)
            {
                var maxFlatTotal = roadLength * 0.6f;
                var flatScale = maxFlatTotal / totalFlatZones;
                startFlatZone *= flatScale;
                endFlatZone *= flatScale;
                totalFlatZones = startFlatZone + endFlatZone;
            }

            var startTotal = startFlatZone + startBlendDist;
            var endTotal = endFlatZone + endBlendDist;
            var totalCoverage = startTotal + endTotal;

            if (totalCoverage > maxTotal && roadLength > 0.1f)
            {
                // Step 2: Scale blend distances proportionally into remaining space after flat zones
                var availableForBlend = maxTotal - totalFlatZones;
                if (availableForBlend > 0)
                {
                    var totalBlend = startBlendDist + endBlendDist;
                    if (totalBlend > 0)
                    {
                        startBlendDist = availableForBlend * (startBlendDist / totalBlend);
                        endBlendDist = availableForBlend * (endBlendDist / totalBlend);
                    }
                }
                else
                {
                    startBlendDist = MathF.Max(1f, roadLength * 0.1f);
                    endBlendDist = MathF.Max(1f, roadLength * 0.1f);
                }

                TerrainCreationLogger.Current?.Detail(
                    $"  [OVERLAP-PROTECT] Spline {sections[0].OwnerSplineId}: " +
                    $"roadLength={roadLength:F1}m, reduced blendDists to " +
                    $"start={startBlendDist:F1}m end={endBlendDist:F1}m");

                // LINEAR INTERPOLATION BYPASS for very short splines:
                // When overlap protection fires, the Hermite delta approach fails because
                // neither correction reaches the middle — the natural terrain peeks through
                // as a ditch between the two elevated endpoints.
                // Fix: directly set each cross-section to a linear interpolation between
                // the two constraint elevations. The road is too short for terrain-following.
                if (roadLength < 40f)
                {
                    var startElev = startConstraint!.Elevation;
                    var endElev = endConstraint!.Elevation;
                    var startBank = startConstraint.BankAngleRadians;
                    var endBank = endConstraint.BankAngleRadians;

                    for (var i = 0; i < sections.Count; i++)
                    {
                        var t = roadLength > 0.01f ? distFromStart[i] / roadLength : 0f;
                        // Quintic smootherstep for C2 interpolation
                        var smooth = t * t * t * (t * (t * 6f - 15f) + 10f);
                        sections[i].TargetElevation = startElev + (endElev - startElev) * smooth;
                        sections[i].BankAngleRadians = startBank + (endBank - startBank) * smooth;
                        modified++;
                    }

                    TerrainCreationLogger.Current?.Detail(
                        $"  [SHORT-LERP] Spline {sections[0].OwnerSplineId}: " +
                        $"roadLength={roadLength:F1}m, lerp {startElev:F2}→{endElev:F2}m");

                    return modified;
                }
            }
        }

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

                // Outer guard at L1395 (float.IsNaN check) already filters out pinned junctions —
                // no inner Phase 1.9 guard needed here.
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
    ///     Capped at 2.5× the configured distance to prevent dominating entire roads on steep terrain.
    ///     Phase B.1: when EnableAashtoBlendDistanceCap is on, further capped by
    ///     AASHTO K-value geometry for the spline's effective design speed.
    /// </summary>
    private static float CalculateAdaptiveBlendDistance(
        float configuredBlendDistance,
        float harmonizedElevation,
        float contributorElevation,
        RoadSmoothingParameters parameters,
        int? effectiveDesignSpeedKmh = null,
        JunctionHarmonizationParameters? jhParams = null)
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

        // Cap adaptive extension at 2.5× the configured distance.
        // Without this cap, steep terrain (e.g., 25m elevation diff) produces blend distances
        // of 200m+ that dominate entire roads and cause fighting Hermite corrections.
        // Roundabouts are unaffected (small elevation diffs, adaptive barely extends).
        var maxAdaptive = configuredBlendDistance * 2.5f;
        var result = MathF.Max(configuredBlendDistance, MathF.Min(slopeBasedDistance, maxAdaptive));

        // Phase B.1: apply K-value cap from above when flag is on.
        if (jhParams?.EnableAashtoBlendDistanceCap == true)
        {
            var speed = effectiveDesignSpeedKmh ?? 30; // residential fallback if caller didn't resolve
            var kCap = AashtoKValueTable.ComputeCap(
                speedKmh: speed,
                zJunction: harmonizedElevation,
                mJunction: 0f,
                zNaturalAtL: contributorElevation,
                blendLength: result);
            result = MathF.Min(result, kCap);
            result = MathF.Max(result, configuredBlendDistance); // never below configured
        }

        return result;
    }

    // Test seam: expose the private method through an internal forwarder.
    internal static float CalculateAdaptiveBlendDistanceForTesting(
        float configuredBlendDistance,
        float harmonizedElevation,
        float contributorElevation,
        float roadMaxSlopeDegrees,
        bool enableMaxSlopeConstraint,
        int? effectiveDesignSpeedKmh,
        JunctionHarmonizationParameters jhParams)
    {
        var fakeParams = new RoadSmoothingParameters
        {
            RoadMaxSlopeDegrees = roadMaxSlopeDegrees,
            EnableMaxSlopeConstraint = enableMaxSlopeConstraint
        };
        return CalculateAdaptiveBlendDistance(
            configuredBlendDistance, harmonizedElevation, contributorElevation,
            fakeParams, effectiveDesignSpeedKmh, jhParams);
    }


    /// <summary>
    ///     Builds a lookup from (splineId, isStart) to the junction at that endpoint.
    ///     Used by the propagation pass to find the junction at the "other end" of a short spline.
    /// </summary>
    private static Dictionary<(int splineId, bool isStart), NetworkJunction> BuildSplineEndpointJunctionIndex(
        UnifiedRoadNetwork network)
    {
        var index = new Dictionary<(int, bool), NetworkJunction>();
        foreach (var junction in network.Junctions)
        {
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.IsSplineStart)
                    index.TryAdd((contributor.Spline.SplineId, true), junction);
                else if (contributor.IsSplineEnd)
                    index.TryAdd((contributor.Spline.SplineId, false), junction);
            }
        }

        return index;
    }

    /// <summary>
    ///     Computes the total road length of a spline from its cross-sections.
    /// </summary>
    private static float ComputeRoadLength(List<UnifiedCrossSection> sections)
    {
        if (sections.Count < 2) return 0f;
        var length = 0f;
        for (var i = 1; i < sections.Count; i++)
            length += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
        return length;
    }

    /// <summary>
    ///     Scans all constraints for splines that are too short to accommodate their blend zones.
    ///     For each such spline, propagates the constraint through the junction at the far end
    ///     into neighboring splines where there IS room for a smooth transition.
    ///
    ///     A spline is "too short" when roadLength &lt; flatZone + blendDistance * 0.5,
    ///     meaning less than half the blend zone fits within the spline.
    ///
    ///     The short spline itself keeps its original constraints — the overlap protection
    ///     in BlendSplineProfile handles the squeeze. The propagation adds constraints to
    ///     neighbors so the transition extends beyond the short segment.
    /// </summary>
    private void PropagateConstraintsThroughShortSplines(
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        UnifiedRoadNetwork network)
    {
        var splineJunctionIndex = BuildSplineEndpointJunctionIndex(network);
        var propagated = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>();
        var shortSplineIds = new HashSet<int>();

        // Scan all constraints to find short splines
        foreach (var ((splineId, isStart), constraint) in constraints)
        {
            if (constraint.IsPropagated) continue;

            var sections = _currentCrossSectionsBySpline?.GetValueOrDefault(splineId);
            if (sections == null || sections.Count < 2) continue;

            var roadLength = ComputeRoadLength(sections);
            var neededDistance = constraint.FlatZoneDistance + constraint.BlendDistanceMeters * 0.5f;

            if (roadLength >= neededDistance) continue;

            // This spline is too short. Find the junction at the OTHER end.
            var otherEnd = !isStart;
            if (!splineJunctionIndex.TryGetValue((splineId, otherEnd), out var farJunction))
                continue;

            // Find neighboring splines at the far junction (excluding this spline)
            var endpointNeighbors = farJunction.Contributors
                .Where(c => c.Spline.SplineId != splineId && c.IsEndpoint)
                .ToList();

            // Also find continuous roads at the far junction (T-junction case: merged main road)
            var continuousNeighbors = farJunction.Contributors
                .Where(c => c.Spline.SplineId != splineId && c.IsContinuous)
                .ToList();

            if (endpointNeighbors.Count == 0 && continuousNeighbors.Count == 0) continue;

            var remainingBlend = MathF.Max(1f, constraint.BlendDistanceMeters - roadLength);

            foreach (var neighbor in endpointNeighbors)
            {
                var neighborIsStart = neighbor.IsSplineStart;
                var neighborKey = (neighbor.Spline.SplineId, neighborIsStart);

                // If neighbor already has a direct constraint, blend its elevation toward
                // the propagated elevation instead of skipping. This reduces the elevation gap
                // the short segment has to bridge (e.g., roundabout → short entry → CrossRoads).
                if (constraints.TryGetValue(neighborKey, out var directConstraint))
                {
                    // Weight: propagated influence = remainingBlend / (remainingBlend + directBlend)
                    var directBlend = directConstraint.BlendDistanceMeters;
                    var totalBlend = remainingBlend + directBlend;
                    var propagatedWeight = totalBlend > 0 ? remainingBlend / totalBlend : 0.5f;
                    var blendedElev = directConstraint.Elevation * (1f - propagatedWeight)
                                      + constraint.Elevation * propagatedWeight;

                    if (MathF.Abs(blendedElev - directConstraint.Elevation) > 0.01f)
                    {
                        constraints[neighborKey] = directConstraint with { Elevation = blendedElev };

                        TerrainCreationLogger.Current?.Detail(
                            $"  [PROPAGATE-BLEND] Junction #{constraint.Junction.JunctionId} " +
                            $"through short Spline {splineId} (len={roadLength:F1}m) " +
                            $"→ Spline {neighbor.Spline.SplineId}: " +
                            $"elev {directConstraint.Elevation:F2}→{blendedElev:F2}m " +
                            $"(weight={propagatedWeight:F2})");
                    }

                    continue;
                }

                // Don't overwrite a previous propagation with higher blend distance
                if (propagated.TryGetValue(neighborKey, out var existing)
                    && existing.BlendDistanceMeters >= remainingBlend) continue;

                propagated[neighborKey] = new JunctionEndpointConstraint
                {
                    Elevation = constraint.Elevation,
                    Slope = constraint.Slope,
                    BankAngleRadians = 0f,
                    IsSplineStart = neighborIsStart,
                    Junction = constraint.Junction,
                    FlatZoneDistance = 0f,
                    BlendDistanceMeters = remainingBlend,
                    PrimaryTangentDirection = null,
                    PrimaryBankAngleRadians = 0f,
                    IsPropagated = true,
                    PropagatedThroughSplineId = splineId
                };

                TerrainCreationLogger.Current?.Detail(
                    $"  [PROPAGATE] Constraint from Junction #{constraint.Junction.JunctionId} " +
                    $"propagated through short Spline {splineId} (len={roadLength:F1}m) " +
                    $"→ Spline {neighbor.Spline.SplineId} (blend={remainingBlend:F1}m)");
            }

            // For continuous roads at the far junction (T-junction case with merged splines):
            // We can't add an endpoint constraint — the road passes through, not an endpoint.
            // Instead, collect mid-spline elevation influences that nudge the continuous road
            // toward the propagated elevation near the junction point.
            // EXCEPTION: Never nudge roundabout rings — they have their own Phase 2.6 elevation.
            if (farJunction.Type == JunctionType.Roundabout) continue;

            foreach (var continuous in continuousNeighbors)
            {
                // Skip roundabout ring splines — their elevation is authoritative
                if (continuous.Spline.IsRoundabout) continue;

                var continuousSections = _currentCrossSectionsBySpline
                    ?.GetValueOrDefault(continuous.Spline.SplineId);
                if (continuousSections == null || continuousSections.Count < 2) continue;

                var crossingIndex = continuousSections.FindIndex(
                    cs => cs.Index == continuous.CrossSection.Index);
                if (crossingIndex < 0) continue;

                _propagatedMidSplineInfluences ??= new Dictionary<int,
                    List<(float elevation, float weight, int junctionId)>>();

                // Use a temporary junction with the propagated elevation for the influence system
                var tempJunction = new NetworkJunction
                {
                    HarmonizedElevation = constraint.Elevation,
                    JunctionId = constraint.Junction.JunctionId
                };

                CollectInfluencesFromCrossing(continuousSections, crossingIndex, tempJunction,
                    remainingBlend, _propagatedMidSplineInfluences, new Dictionary<int, float>());

                TerrainCreationLogger.Current?.Detail(
                    $"  [PROPAGATE-CONTINUOUS] Constraint from Junction #{constraint.Junction.JunctionId} " +
                    $"through short Spline {splineId} (len={roadLength:F1}m) " +
                    $"→ continuous Spline {continuous.Spline.SplineId} " +
                    $"(mid-spline influence, blend={remainingBlend:F1}m, targetElev={constraint.Elevation:F2}m)");
            }

            shortSplineIds.Add(splineId);
        }

        // Add propagated constraints to the main dictionary
        foreach (var (key, constraint) in propagated)
            constraints.TryAdd(key, constraint);

        if (shortSplineIds.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"Blend propagation: {shortSplineIds.Count} short spline(s), " +
                $"{propagated.Count} propagated constraint(s)");
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

    /// <summary>
    ///     Phase B diagnostic output directory resolution. Every spline's
    ///     <c>Spline.Parameters.DebugOutputDirectory</c> is set by
    ///     <c>BuildRoadSmoothingParameters</c> to a per-material subfolder of
    ///     <c>MT_TerrainGeneration</c>; we return the shared parent (=
    ///     <c>MT_TerrainGeneration</c>) so the diagnostic CSVs land alongside
    ///     <c>junction_residuals.csv</c> and friends.
    /// </summary>
    private string? ResolvePhaseBDiagnosticsOutputDirectory(UnifiedRoadNetwork network)
    {
        foreach (var spline in network.Splines)
        {
            var dir = spline.Parameters?.DebugOutputDirectory;
            if (!string.IsNullOrEmpty(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent))
                    return parent;
            }
        }
        return null;
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
