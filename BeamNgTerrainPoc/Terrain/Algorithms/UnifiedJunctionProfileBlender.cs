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
    // NO-BLEND TEST TOGGLE. Step 5b ("[PROPAGATE-CONTINUOUS]") nudges a CONTINUOUS (through) road's
    // mid-spline cross-sections toward a junction elevation that was propagated through a SHORT
    // terminating road which couldn't fit its own blend zone. On the no-blend path this violates the
    // "never move the through road" principle: it pulls the main road up/down to the side road's
    // far-end terrain (confirmed at OSM node 430808759 / J#312 — through spline 195 dragged to 198.28,
    // the terrain at the 21 m side road's free end). Flip to true + rebuild to SKIP that nudge and
    // test whether it is the cause. Does NOT affect endpoint propagation (that uses a separate dict),
    // only the through-road mid-spline influence.
    // TEST PROTOCOL: run 1 = false (BASELINE, nudge applied = production). Then flip to true (nudge
    // skipped) and re-render. Compare [NO-BLEND DIAG] for the suspect junction across the two logs.
    // CURRENTLY true = run 2 (nudge SKIPPED). Compare J#201 / node 663313796 vs the baseline log.
    private const bool SkipPropagatedMidSplineInfluences = true;

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

        // Sparse mode (V2 review R-1.1): the blender must not overwrite PINNED sections (bridge-deck pins
        // and lower-road dip wells incl. their ramps) — otherwise the per-iteration pin re-assert and the
        // junction blend fight each other (tug-of-war) and leave a kink at the well shoulder. Gated on the
        // flag so legacy output stays byte-identical when it is off.
        var pinRules = network.Splines
            .Select(s => s.Parameters.BridgeRules)
            .FirstOrDefault(r => r != null);
        var respectPins = pinRules?.EnableSparseDeckConstraints == true;

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

        // Phase A.5: built for the propagation overlap taper (Step 5b).
        if (jhParams.EnablePropagationOverlapTaper
            && _propagatedMidSplineInfluences is { Count: > 0 })
        {
            _splineClaimedZones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
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

        // No-blend path: roads keep their Phase-2 (terrain-following) elevations near junctions.
        // The affine ThroughRoad leveling in UnifiedRoadSmoother does the junction targeting after
        // this blender returns — there is no per-spline junction-profile blend here. Junction
        // constraints are still computed above so each junction's HarmonizedElevation is set (used
        // by RoadMaskBuilder center-fill) and so terminating-road blend zones can be marked below.

        // Step 3: Recompute T-junction and roundabout constraints so each junction's
        // HarmonizedElevation reflects the primary/ring surface, then mark terminating-road blend
        // zones MaintainBanking so JunctionBankingAdapter leaves them alone.
        if (deferredTerminatingSplines.Count > 0)
        {
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

            TerrainLogger.Detail(
                $"  Pass 2: refined {refinedConstraints.Count} T-junction/roundabout constraints");

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
            network, crossSectionsBySpline, originalElevations, respectPins);

        // Step 5b: Apply propagated mid-spline influences from short-segment propagation.
        // These nudge continuous roads near T-junctions where short terminating roads
        // couldn't accommodate their blend zones (e.g., roundabout → short entry → main road).
        // Phase A.5: when EnablePropagationOverlapTaper is on and the CS sits inside a
        // directly-anchored junction's blend zone (and that junction != the influence's
        // source junction), the per-influence weight is multiplied by a smoothstep taper
        // → 0 at the contested anchor, 1 at the contested-zone boundary. Prevents a
        // propagated influence from overriding a directly-anchored junction's elevation.
        if (_propagatedMidSplineInfluences is { Count: > 0 })
        {
            if (SkipPropagatedMidSplineInfluences)
            {
                // NO-BLEND TEST: skip nudging continuous roads — see the field comment. The influences
                // are still collected above; we just don't apply them.
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[NO-BLEND TEST] SKIPPED applying {_propagatedMidSplineInfluences.Count} propagated " +
                    "mid-spline influence(s) on continuous roads (SkipPropagatedMidSplineInfluences=true)");
            }
            else
            {
                var propagatedModified = ApplyPropagatedMidSplineInfluences(
                    network.CrossSections,
                    _propagatedMidSplineInfluences,
                    _splineClaimedZones,
                    respectPins);

                if (propagatedModified > 0)
                    TerrainCreationLogger.Current?.InfoFileOnly(
                        $"Applied {propagatedModified} propagated mid-spline influences on continuous roads" +
                        (_splineClaimedZones != null ? " (overlap-taper enabled)" : ""));
            }

            _propagatedMidSplineInfluences = null;
            _splineClaimedZones = null;
        }

        // Step 6: Apply endpoint tapering for dead ends.
        // Phase B.4: skip when EnableEndpointTerrainSlopeMatch is on — the affine no-blend path
        // already produces the slope-matched profile, and running the legacy taper here would
        // override and undo it.
        if (!jhParams.EnableEndpointTerrainSlopeMatch)
        {
            result.EndpointsTapered = ApplyEndpointTapering(
                network, crossSectionsBySpline, heightMap, metersPerPixel, respectPins);
        }

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
                            SurfaceWidth = ringCS.SurfaceWidth,
                            EffectiveBlendRange = ringCS.EffectiveBlendRange,
                            LeftEdgeElevation = ringCS.LeftEdgeElevation,
                            RightEdgeElevation = ringCS.RightEdgeElevation,
                            JunctionBankingBehavior = ringCS.JunctionBankingBehavior,
                            Priority = ringCS.Priority,
                            DistanceAlongSpline = ringCS.DistanceAlongSpline,
                            OriginalTerrainElevation = ringCS.OriginalTerrainElevation,
                            Curvature = ringCS.Curvature,
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
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(terminatingRoundaboutWidth),
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
    ///     All roads are treated as equal peers blending toward a shared average elevation.
    /// </summary>
    private void ComputeMultiWayConstraints(
        NetworkJunction junction,
        Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
    {
        var endpointContributors = junction.Contributors.Where(c => c.IsEndpoint).ToList();
        if (endpointContributors.Count == 0) return;

        ComputePeerJunctionConstraints(junction, endpointContributors, constraints);
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
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(contributorWidth),
                harmonizedElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);

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
            var blendDist = CalculateAdaptiveBlendDistance(
                junctionParams.GetEffectiveBlendDistance(endpointWidth),
                terrainElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);

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
    ///     Phase A.5 testable extraction of Step 5b. Applies propagated mid-spline
    ///     influences to <paramref name="crossSections" /> with optional overlap taper
    ///     via <paramref name="splineClaimedZones" />. Returns number of modified CSes.
    /// </summary>
    internal static int ApplyPropagatedMidSplineInfluences(
        IEnumerable<UnifiedCrossSection> crossSections,
        Dictionary<int, List<(float elevation, float weight, int junctionId)>> influencesByCsIndex,
        Dictionary<int, SplineClaimedZone>? splineClaimedZones,
        bool respectPins = false)
    {
        var modified = 0;
        var csIndexLookup = crossSections.ToDictionary(cs => cs.Index);

        foreach (var (csIndex, influences) in influencesByCsIndex)
        {
            if (!csIndexLookup.TryGetValue(csIndex, out var cs))
                continue;
            if (float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
                continue;
            if (respectPins && (cs.PinnedElevation.HasValue || cs.SoftDeckRiseMeters.HasValue))
                continue; // A6: never fight a bridge deck/dip pin

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
    ///     Handles MidSplineCrossing junctions where both roads pass through.
    ///     Uses bidirectional elevation influence from the crossing point.
    /// </summary>
    private int ApplyMidSplineCrossingInfluences(
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<int, float> originalElevations,
        bool respectPins = false)
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

            // Indexed lookup — the previous FirstOrDefault was a linear scan over ALL cross-sections
            // (~1M on big maps) for every influenced section, dominating this method's runtime.
            var cs = network.GetCrossSectionByIndex(csIndex);
            if (cs == null || cs.IsRoundaboutBlended)
                continue;
            if (respectPins && (cs.PinnedElevation.HasValue || cs.SoftDeckRiseMeters.HasValue))
                continue; // A6: never fight a bridge deck/dip pin

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
        float metersPerPixel,
        bool respectPins = false)
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
                if (respectPins && (cs.PinnedElevation.HasValue || cs.SoftDeckRiseMeters.HasValue))
                    continue; // A6: never fight a bridge deck/dip pin

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

        // Cap adaptive extension at 2.5× the configured distance.
        // Without this cap, steep terrain (e.g., 25m elevation diff) produces blend distances
        // of 200m+ that dominate entire roads and cause fighting Hermite corrections.
        // Roundabouts are unaffected (small elevation diffs, adaptive barely extends).
        var maxAdaptive = configuredBlendDistance * 2.5f;
        return MathF.Max(configuredBlendDistance, MathF.Min(slopeBasedDistance, maxAdaptive));
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

    public float MaxElevationChange { get; set; }
}
