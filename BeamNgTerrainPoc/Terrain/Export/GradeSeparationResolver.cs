using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Resolves grade-separated crossings (feature E-A, doc 07 §6 decision D-3a): where a road passes under a
/// bridge (or under a higher-layer road) and does not naturally clear it, the BRIDGE HOLDS its solved deck
/// elevation and the LOWER road is dipped locally to make the minimum vertical clearance. <c>Layer</c>
/// already decided who is up (in <see cref="Algorithms.NetworkJunctionDetector"/>); <c>Priority</c> is the
/// veto here: a road that outranks the bridge (e.g. a motorway under a minor footbridge) is never dipped —
/// instead the bridge is raised via an interior clearance constraint fed to <see cref="BridgeProfileSolver"/>.
///
/// Clearance is computed ROAD-vs-ROAD from the solved cross-section elevations (D-1), not from the (blurred,
/// nearest-neighbour) DEM under the span.
///
/// <para><b>Two modes (plan doc 14 Phase D):</b></para>
/// <list type="bullet">
/// <item><b>Merged corridor</b> — the deck-elevation decision moved upstream to
///   <see cref="BridgeElevationPlanner"/>, which pinned the deck Z pre-smoothing and stashed its per-crossing
///   outcomes on <c>UnifiedRoadNetwork.BridgeElevationPlan</c>. <see cref="PlanConstraints"/> is NOT called;
///   <see cref="ApplyLowerRoadDips"/> reads the stashed plan and lowers only the rule engine's "dip"/"split"
///   crossings against the final stamped deck Z (raise/veto crossings were handled by the pin).</item>
/// <item><b>Legacy whole-spline (flag off)</b> — the original two-phase flow, retired in Phase F:
///   <see cref="PlanConstraints"/> classifies dip-vs-veto BEFORE the solver and emits raise-constraints for
///   the veto crossings; <see cref="ApplyLowerRoadDips"/> dips the remaining non-veto crossings AFTER it.</item>
/// </list>
/// </summary>
public static class GradeSeparationResolver
{
    /// <summary>Default minimum road-vs-road vertical clearance at a crossing (m). UI tunable (D-3b).</summary>
    public const float DefaultMinClearanceMeters = 5f;

    /// <summary>
    /// Half-length (m) of the eased dip well applied to the lower road around a crossing. Longer = gentler
    /// dip (the lever; like the connector-grade ramp's length). No grade is clamped — only eased. The well
    /// is additionally clamped per-side so it can never reach a junction on the lower road (harmonization is
    /// an absolute no-go), so a generous default is safe — it auto-shrinks near junctions.
    /// </summary>
    public const float DefaultDipRampLengthMeters = 60f;

    /// <summary>
    /// Keep the dip well's far edge at least this far (m) from any junction on the lower road, so the
    /// harmonized junction elevation/grade is never disturbed (the well already eases to zero value AND
    /// slope at its edge; this margin adds headroom around the junction's blend zone). Derives from the
    /// single source of truth <see cref="BridgeElevationPlanner.JunctionMarginMeters"/> (same logical
    /// margin across the planner/resolver layers).
    /// </summary>
    public const float JunctionClearanceMarginMeters = BridgeElevationPlanner.JunctionMarginMeters;

    /// <summary>
    /// Legacy whole-spline mode only (flag off; retired in Phase F). Phase 1 (before the bridge profile
    /// solver): for each grade-separated crossing decides whether the lower road may be dipped or, under the
    /// priority veto, the bridge must be raised instead. Returns the interior min-Z clearance constraints for
    /// the bridges to be raised (feed to <see cref="BridgeProfileSolver.RefineSpans"/>). Does not move any
    /// road yet. Merged corridors do not call this — the rule engine
    /// (<see cref="BridgeElevationPlanner"/>) decides + pins the deck instead.
    /// </summary>
    public static IReadOnlyList<BridgeProfileSolver.BridgeInteriorConstraint> PlanConstraints(
        UnifiedRoadNetwork network,
        float minClearanceMeters = DefaultMinClearanceMeters,
        BridgeDeckProfile? deckProfile = null,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var constraints = new List<BridgeProfileSolver.BridgeInteriorConstraint>();
        var vetoes = 0;

        foreach (var crossing in network.GradeSeparatedCrossings)
        {
            var upper = network.GetSplineById(crossing.UpperSplineId);
            var lower = network.GetSplineById(crossing.LowerSplineId);
            if (upper == null || lower == null)
                continue;

            // Veto: the lower road outranks the upper member → dipping it would be wrong. Keep it; raise the
            // bridge instead (D-3a). Equal priority falls through to a dip (the bridge is up by layer anyway).
            var veto = crossing.LowerPriority > crossing.UpperPriority;
            if (!veto)
                continue; // dip case — resolved in phase 2 against the final deck Z

            var upperIsDeck = IsGeneratedDeckAt(network, upper, crossing.CrossingXY);
            if (!upperIsDeck)
            {
                // Upper is a non-bridge road we cannot regrade in E-A; lower is high-class → leave both alone.
                crossing.Action = GradeSeparationAction.NoOpNoBridge;
                continue;
            }

            var lowerSection = NearestSection(network, crossing.LowerSplineId, crossing.CrossingXY);
            var upperSection = NearestSection(network, crossing.UpperSplineId, crossing.CrossingXY);
            if (lowerSection == null || upperSection == null || !IsFinite(lowerSection.TargetElevation))
                continue;

            // The deck must arch so its SOFFIT (deck top − thickness) clears the lower road by the minimum.
            // The constraint targets the deck TOP (TargetElevation), so add the deck thickness so the soffit
            // — not the top — ends up minClearance above the road (the box hangs `thickness` below the top).
            var effectiveClearance = minClearanceMeters + DeckThicknessOffset(network, crossing.UpperSplineId, deckProfile);
            constraints.Add(new BridgeProfileSolver.BridgeInteriorConstraint(
                crossing.UpperSplineId,
                upperSection.DistanceAlongSpline,
                lowerSection.TargetElevation + effectiveClearance));
            crossing.Action = GradeSeparationAction.RaisedBridge;
            vetoes++;
        }

        if (log && network.GradeSeparatedCrossings.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[GRADE-SEP] plan crossings={network.GradeSeparatedCrossings.Count} " +
                $"priorityVetoes={vetoes} (bridge raised) constraints={constraints.Count}");

        return constraints;
    }

    /// <summary>
    /// Below this interior arch-shape weight (<c>16t²(1−t)²</c> at the crossing's span fraction t) a floor
    /// is skipped: enforcing a deficit that close to an abutment would demand a huge CENTRAL arch
    /// (lift = deficit / shape). End deficits are approach/junction territory, not deck curvature.
    /// 0.25 ⇒ floors only inside roughly the central 70 % of the span.
    /// </summary>
    public const float FloorMinArchShape = 0.25f;

    /// <summary>
    /// Amendment 03 (sparse floor constraints): converts the rule engine's stashed
    /// <see cref="UnifiedRoadNetwork.BridgeElevationPlan"/> into interior FLOOR constraints for
    /// <see cref="BridgeProfileSolver.RefineSpans"/>. The planner emitted no pins — the span deck is
    /// re-curved G0+G1 from the SOLVED approaches, and each crossing's typed budget becomes a min-Z the
    /// curve must clear at the crossing station (arch lift only when short; overshoot untouched).
    ///
    /// <para>Per crossing: floor base = the lower road's FINAL solved Z (synthetic rail/water → the
    /// planner's obstacle estimate), plus its <see cref="CrossingPlan.RequiredSeparationMeters"/> (typed:
    /// kind clearance + structural depth ≈ mesh thickness; legacy C additionally gets the deck-thickness
    /// offset so the SOFFIT clears, mirroring <see cref="PlanConstraints"/>), minus a planned Split dip
    /// share (the resolver dips that part against the final deck afterwards). Pure-dip crossings move the
    /// road, not the deck — no floor. Floors too close to an abutment (<see cref="FloorMinArchShape"/>)
    /// are skipped with a warning — see the constant.</para>
    /// </summary>
    public static IReadOnlyList<BridgeProfileSolver.BridgeInteriorConstraint> PlanFloorConstraints(
        UnifiedRoadNetwork network,
        BridgeDeckProfile? deckProfile = null,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var constraints = new List<BridgeProfileSolver.BridgeInteriorConstraint>();
        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0)
            return constraints;

        var skippedNearAbutment = 0;
        foreach (var cp in plan.Crossings)
        {
            // A pure dip moves the lower road, never the deck.
            if (cp.Action == BridgeElevationAction.DipLowerRoad)
                continue;

            var upperId = cp.Crossing.UpperSplineId;
            var upper = network.GetSplineById(upperId);
            if (upper == null)
                continue;

            // Floor base: the lower member's FINAL solved Z — not the planner-time estimate.
            float lowerZ;
            if (cp.Crossing.HasLowerSpline)
            {
                var lowerSection = NearestSection(network, cp.Crossing.LowerSplineId, cp.Crossing.CrossingXY);
                lowerZ = lowerSection != null && IsFinite(lowerSection.TargetElevation)
                    ? lowerSection.TargetElevation
                    : cp.ObstacleZEstimate;
            }
            else
            {
                lowerZ = cp.ObstacleZEstimate;
            }

            if (!IsFinite(lowerZ))
                continue;

            // Typed budgets already include the §3.2 structural depth (aligned to the rendered mesh), so the
            // planned per-crossing separation is used as-is (obstacle typing is unconditional, doc 17 §4a).
            var required = cp.RequiredSeparationMeters;
            var dipShare = cp.Action == BridgeElevationAction.Split ? MathF.Max(0f, cp.DipDepthMeters) : 0f;
            var minZ = lowerZ + required - dipShare;

            var upperSection = NearestSection(network, upperId, cp.Crossing.CrossingXY);
            if (upperSection == null)
                continue;
            var station = upperSection.DistanceAlongSpline;

            // Near-abutment guard: a floor at span fraction t costs lift = deficit / (16t²(1−t)²) at the
            // span CENTER — enforcing an end deficit would hump the whole deck.
            var span = plan.Spans.FirstOrDefault(s =>
                s.OwnerSplineId == upperId &&
                station >= s.StartDistance - 0.01f && station <= s.EndDistance + 0.01f);
            if (span != null)
            {
                var len = span.EndDistance - span.StartDistance;
                var t = len > 0.01f ? Math.Clamp((station - span.StartDistance) / len, 0f, 1f) : 0.5f;
                var shape = 16f * t * t * (1f - t) * (1f - t);
                if (shape < FloorMinArchShape)
                {
                    skippedNearAbutment++;
                    TerrainCreationLogger.Current?.InfoFileOnly(
                        $"[BRIDGE-PLAN] floor SKIPPED near abutment: upper={upperId} " +
                        $"lower={cp.Crossing.LowerSplineId} ({cp.Crossing.LowerKind}) t={t:F2} " +
                        $"minZ={minZ:F2} — end deficits are approach territory (doc 03)");
                    continue;
                }
            }

            constraints.Add(new BridgeProfileSolver.BridgeInteriorConstraint(upperId, station, minZ));
        }

        if (log && (constraints.Count > 0 || skippedNearAbutment > 0))
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[GRADE-SEP] sparse floors constraints={constraints.Count} " +
                $"skippedNearAbutment={skippedNearAbutment}");

        return constraints;
    }

    /// <summary>
    /// After the bridge profile solver: reads each crossing's now-final upper/lower solved Z and dips the
    /// lower road locally with a smooth eased well so it sits at least <paramref name="minClearanceMeters"/>
    /// (plus the deck thickness, soffit-measured) below the upper member. No grade clamp.
    ///
    /// <para><b>Which crossings get dipped (plan doc 14 Phase D):</b> on a <b>merged corridor</b> the rule
    /// engine already decided who moves and stashed its outcomes on <c>network.BridgeElevationPlan</c> — so
    /// here we dip ONLY its <c>DipLowerRoad</c>/<c>Split</c> crossings (recomputing the residual against the
    /// final stamped deck Z, with the precise user clearance the pin's planner-default omitted); raise/veto
    /// crossings were handled by the deck pin and are left alone. On a <b>legacy</b> network (no plan) it
    /// keeps the original behaviour: dip every still-<see cref="GradeSeparationAction.Pending"/> crossing that
    /// doesn't clear (the vetoes were resolved by <see cref="PlanConstraints"/>).</para>
    /// </summary>
    public static void ApplyLowerRoadDips(
        UnifiedRoadNetwork network,
        float[,]? heightMap = null,
        float metersPerPixel = 1f,
        float minClearanceMeters = DefaultMinClearanceMeters,
        BridgeDeckProfile? deckProfile = null,
        float dipRampLengthMeters = DefaultDipRampLengthMeters,
        bool log = true,
        int[,]? roadSurfaceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(network);

        // The lower road was already stamped into the heightmap during Phase-4 terrain blending, so dipping
        // its cross-sections alone leaves the DRIVEN terrain surface unchanged. We therefore also carve the
        // same dip into the heightmap here (mirroring BridgeDeckExcavator). Accumulate the per-cell drop as a
        // max-combine across all sections/crossings, then apply once so overlapping wells don't double-dig.
        var canCarve = heightMap != null && metersPerPixel > 0f;
        var mapWidth = heightMap?.GetLength(1) ?? 0;
        var mapHeight = heightMap?.GetLength(0) ?? 0;
        var carveByCell = canCarve ? new Dictionary<int, float>() : null;

        // Merged-corridor mode: the rule engine's per-crossing outcomes (who moves) keyed by crossing. Null in
        // legacy mode, where the priority-veto decision lives on crossing.Action (set by PlanConstraints).
        var plannerActions = BuildPlannerActionLookup(network);

        // A6 dip-as-pin: when the dips were already emitted as PRE-smooth pins (the smoother solved the
        // well and Phase 4 stamped it into the heightmap), this pass is DEMOTED to verify-only for
        // dip/split crossings — no second TargetElevation drop, no carve (no double-dip). Any residual
        // shortfall is left to the A7 bounded local carve. (Doc 04 §8.3 B, 2026-06-11: sparse mode now
        // ALSO emits dip pins, so it takes the verify-only path too — previously sparse re-ran the active
        // well+carve here, which stepped the dip edges. Paired with the emitter so we never double-dip.)
        var dipRules = network.Splines
            .Select(s => s.Parameters.BridgeRules)
            .FirstOrDefault(r => r != null);
        var dipAsPin = dipRules?.EnableSparseDeckConstraints == true;

        var dipped = 0;
        var maxDip = 0f;
        var pinVerified = 0;
        var pinShort = 0;

        // Doc 28 Step C (active path): crossings belonging to a coherent-underpass cluster are NOT dipped
        // as independent wells — their residuals (vs the final deck Zs) are collected here and merged into
        // ONE envelope well per cluster after the loop. Null when dip-as-pin owns the dips or no plan.
        Dictionary<GradeSeparatedCrossing, int>? clusterIndexByCrossing = null;
        if (!dipAsPin && network.BridgeElevationPlan is { UnderpassClusters.Count: > 0 } clusterPlan)
        {
            clusterIndexByCrossing = new Dictionary<GradeSeparatedCrossing, int>();
            for (var i = 0; i < clusterPlan.UnderpassClusters.Count; i++)
            foreach (var member in clusterPlan.UnderpassClusters[i].Crossings)
                clusterIndexByCrossing[member] = i;
        }

        var deferredClusterDips =
            new List<(int Cluster, GradeSeparatedCrossing Crossing, ParameterizedRoadSpline Lower,
                float Station, float Required, float BaseZ)>();

        foreach (var crossing in network.GradeSeparatedCrossings)
        {
            var upperSection = NearestSection(network, crossing.UpperSplineId, crossing.CrossingXY);
            var lowerSection = NearestSection(network, crossing.LowerSplineId, crossing.CrossingXY);
            if (upperSection == null || lowerSection == null)
                continue;

            // Record the final road-vs-road clearance (D-1) for diagnostics regardless of action.
            crossing.UpperZ = upperSection.TargetElevation;
            crossing.LowerZ = lowerSection.TargetElevation;

            if (plannerActions != null && plannerActions.TryGetValue(crossing, out var planned))
            {
                // A7: estimate-vs-final delta per crossing — measures the A0 estimator's accuracy on a
                // render. LowerZ is the FINAL stamped Z (for dips it includes the applied dip, noted).
                if (IsFinite(planned.ObstacleZEstimate) && IsFinite(crossing.LowerZ))
                    TerrainCreationLogger.Current?.Detail(
                        $"[BRIDGE-PLAN] estimate-vs-final upper={crossing.UpperSplineId} " +
                        $"lower={crossing.LowerSplineId} estZ={planned.ObstacleZEstimate:F2} " +
                        $"finalZ={crossing.LowerZ:F2} delta={crossing.LowerZ - planned.ObstacleZEstimate:F2} " +
                        $"plannedDip={planned.DipDepthMeters:F2} action={planned.Action}");

                // Merged corridor (Phase D): the rule engine decided. Only dip its dip/split crossings here;
                // raise/veto were satisfied by the deck pin, and already-clear needs nothing. We still fall
                // through for dip/split so the residual is recomputed against the FINAL stamped deck Z below.
                if (planned.Action is not (BridgeElevationAction.DipLowerRoad or BridgeElevationAction.Split))
                {
                    crossing.Action = planned.Action == BridgeElevationAction.AlreadyClears
                        ? GradeSeparationAction.AlreadyClears
                        : GradeSeparationAction.RaisedBridge;
                    continue;
                }

                if (dipAsPin)
                {
                    // A6: the dip already happened via the pre-smooth pin (and was stamped in Phase 4).
                    // Verify-only for the ROAD PROFILE — TargetElevation is never dropped a second time
                    // (no double-dip). A7 backstop: when the final surfaces still come up short, a
                    // bounded local eased HEIGHTMAP carve makes up the residual daylight — never a
                    // re-smooth, never a re-pin.
                    var requiredSep = planned.RequiredSeparationMeters > 0f
                        ? planned.RequiredSeparationMeters
                        : minClearanceMeters + DeckThicknessOffset(network, crossing.UpperSplineId, deckProfile);
                    var pinClearance = crossing.UpperZ - crossing.LowerZ;
                    crossing.Action = GradeSeparationAction.DippedLowerRoad;
                    crossing.AppliedDipMeters = planned.DipDepthMeters;
                    pinVerified++;
                    if (IsFinite(pinClearance) && pinClearance < requiredSep - 0.05f)
                    {
                        pinShort++;
                        var residual = requiredSep - pinClearance;
                        var carvedSections = 0;
                        var lowerForCarve = network.GetSplineById(crossing.LowerSplineId);
                        if (lowerForCarve != null && !IsGeneratedDeckAt(network, lowerForCarve, crossing.CrossingXY))
                            carvedSections = DipLowerRoad(network, lowerForCarve,
                                lowerSection.DistanceAlongSpline, residual, dipRampLengthMeters,
                                carveByCell, metersPerPixel, mapWidth, mapHeight, carveOnly: true,
                                roadSurfaceOwner: roadSurfaceOwner);
                        TerrainCreationLogger.Current?.InfoFileOnly(
                            $"[GRADE-SEP] dip-as-pin VERIFY SHORT: upper={crossing.UpperSplineId} " +
                            $"lower={crossing.LowerSplineId} clearance={pinClearance:F2}m < " +
                            $"required={requiredSep:F2}m — A7 local carve residual={residual:F2}m " +
                            $"({carvedSections} sections)");
                    }

                    continue;
                }
            }
            else if (crossing.Action != GradeSeparationAction.Pending)
            {
                // Legacy: veto (RaisedBridge) / NoOpNoBridge crossings were resolved by PlanConstraints.
                continue;
            }

            if (!IsFinite(crossing.UpperZ) || !IsFinite(crossing.LowerZ))
                continue;

            // Measure clearance to the deck SOFFIT, not the deck top: add the bridge's deck thickness to the
            // minimum so the physical box (which hangs `thickness` below the solved deck-top Z) clears the
            // road by the configured amount. Without this the real gap under the box is too small by `thickness`.
            var effectiveClearance = minClearanceMeters + DeckThicknessOffset(network, crossing.UpperSplineId, deckProfile);

            var clearance = crossing.UpperZ - crossing.LowerZ;
            if (clearance >= effectiveClearance)
            {
                crossing.Action = GradeSeparationAction.AlreadyClears;
                continue;
            }

            // Don't carve a generated bridge deck if it happens to be the lower member (bridge-over-bridge).
            var lower = network.GetSplineById(crossing.LowerSplineId);
            if (lower == null || IsGeneratedDeckAt(network, lower, crossing.CrossingXY))
            {
                crossing.Action = GradeSeparationAction.NoOpNoBridge;
                continue;
            }

            var required = effectiveClearance - clearance;

            // Doc 28 Step C: defer cluster members — merged into one envelope well below.
            if (clusterIndexByCrossing != null &&
                clusterIndexByCrossing.TryGetValue(crossing, out var clusterIdx))
            {
                deferredClusterDips.Add(
                    (clusterIdx, crossing, lower, lowerSection.DistanceAlongSpline, required,
                        lowerSection.TargetElevation));
                continue;
            }

            var moved = DipLowerRoad(network, lower, lowerSection.DistanceAlongSpline, required,
                dipRampLengthMeters, carveByCell, metersPerPixel, mapWidth, mapHeight,
                roadSurfaceOwner: roadSurfaceOwner);
            if (moved == 0)
            {
                crossing.Action = GradeSeparationAction.NoOpNoBridge;
                continue;
            }

            crossing.Action = GradeSeparationAction.DippedLowerRoad;
            crossing.AppliedDipMeters = required;
            dipped++;
            maxDip = MathF.Max(maxDip, required);
        }

        // Doc 28 Step C/D: apply each deferred cluster as ONE merged envelope well — interior depth follows
        // the per-crossing residuals (exact clearance at every crossing), eased end ramps, bounded by
        // MaxUnderpassDipMeters (cap + warn, residual ACCEPTED — never converted into bridge raises).
        foreach (var clusterGroup in deferredClusterDips.GroupBy(d => d.Cluster))
        {
            var members = clusterGroup.OrderBy(m => m.Station).ToList();
            var lowerRoad = members[0].Lower;
            var cap = dipRules != null ? MathF.Max(0f, dipRules.MaxUnderpassDipMeters) : float.PositiveInfinity;

            // Per-crossing fallback — identical to the undeferred path (single well, junction-clamped).
            void ApplySingle((int Cluster, GradeSeparatedCrossing Crossing, ParameterizedRoadSpline Lower,
                float Station, float Required, float BaseZ) m)
            {
                var movedSingle = DipLowerRoad(network, m.Lower, m.Station, m.Required, dipRampLengthMeters,
                    carveByCell, metersPerPixel, mapWidth, mapHeight, roadSurfaceOwner: roadSurfaceOwner);
                if (movedSingle == 0)
                {
                    m.Crossing.Action = GradeSeparationAction.NoOpNoBridge;
                    return;
                }

                m.Crossing.Action = GradeSeparationAction.DippedLowerRoad;
                m.Crossing.AppliedDipMeters = m.Required;
                dipped++;
                maxDip = MathF.Max(maxDip, m.Required);
            }

            if (members.Count < 2)
            {
                ApplySingle(members[0]);
                continue;
            }

            var sFirst = members[0].Station;
            var sLast = members[^1].Station;

            // Absolute-Z targets: solved Z at the crossing minus the (capped) required dip. The interior
            // curve runs through these targets only — solved-profile detail between crossings does not
            // shape the well bottom (winningen render 2026-07-02 #2).
            var points = new List<(float Station, float Depth, float TargetZ)>(members.Count);
            var cappedResidual = 0f;
            foreach (var m in members)
            {
                var depth = MathF.Min(m.Required, cap);
                cappedResidual = MathF.Max(cappedResidual, m.Required - depth);
                points.Add((m.Station, depth, m.BaseZ - depth));
            }

            // Depth-aware end ramps (§3.3 class slope, winningen render 2026-07-02) — the flat default
            // ramp read as a ~15 % V-sag under the last deck; still junction-clamped per side.
            var desiredBack = UnderpassWellProfile.ClassRampLengthMeters(
                points[0].Depth, lowerRoad.OsmRoadType, dipRampLengthMeters);
            var desiredFwd = UnderpassWellProfile.ClassRampLengthMeters(
                points[^1].Depth, lowerRoad.OsmRoadType, dipRampLengthMeters);
            var (backRoom, _) = ClampRampToJunctions(network, lowerRoad.SplineId, sFirst, desiredBack);
            var (_, fwdRoom) = ClampRampToJunctions(network, lowerRoad.SplineId, sLast, desiredFwd);
            if (backRoom <= 0.01f || fwdRoom <= 0.01f ||
                HasJunctionOnSplineBetween(network, lowerRoad.SplineId, sFirst, sLast))
            {
                // An end is junction-boxed / a junction sits inside the cluster: the merged well would
                // disturb a harmonized junction — fall back to the junction-safe per-crossing wells.
                foreach (var m in members)
                    ApplySingle(m);
                continue;
            }

            var profile = new UnderpassWellProfile(
                points.Select(p => (p.Station, p.TargetZ)).ToList(), backRoom, fwdRoom);
            var movedSections = ApplyWell(network, lowerRoad,
                cs => cs.TargetElevation - profile.ZAt(cs.DistanceAlongSpline, cs.TargetElevation),
                carveByCell, metersPerPixel, mapWidth, mapHeight, carveOnly: false, roadSurfaceOwner);
            if (movedSections == 0)
            {
                foreach (var m in members)
                    m.Crossing.Action = GradeSeparationAction.NoOpNoBridge;
                continue;
            }

            for (var i = 0; i < members.Count; i++)
            {
                members[i].Crossing.Action = GradeSeparationAction.DippedLowerRoad;
                members[i].Crossing.AppliedDipMeters = points[i].Depth;
                dipped++;
                maxDip = MathF.Max(maxDip, points[i].Depth);
            }

            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[GRADE-SEP] coherent underpass lower={lowerRoad.SplineId} span=[{sFirst:F0},{sLast:F0}]m " +
                $"bridges={string.Join(",", members.Select(m => m.Crossing.UpperSplineId).Distinct())} " +
                $"crossings={members.Count} maxDip={points.Max(p => p.Depth):F2}m sections={movedSections}" +
                (cappedResidual > 0.01f
                    ? $" cappedResidual={cappedResidual:F2}m (accepted — bridges not raised)"
                    : ""));
        }

        // Apply the accumulated heightmap drop (lower-only; preserves gaps/water).
        var cellsLowered = 0;
        if (heightMap != null && carveByCell is { Count: > 0 })
        {
            foreach (var (key, drop) in carveByCell)
            {
                if (drop <= 0f)
                    continue;
                var px = key % mapWidth;
                var py = key / mapWidth;
                var current = heightMap[py, px];
                if (float.IsNaN(current) || float.IsInfinity(current))
                    continue;
                heightMap[py, px] = current - drop;
                cellsLowered++;
            }
        }

        if (log && network.GradeSeparatedCrossings.Count > 0)
        {
            var alreadyClear = network.GradeSeparatedCrossings.Count(c => c.Action == GradeSeparationAction.AlreadyClears);
            var raised = network.GradeSeparatedCrossings.Count(c => c.Action == GradeSeparationAction.RaisedBridge);
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[GRADE-SEP] resolve crossings={network.GradeSeparatedCrossings.Count} dippedRoads={dipped} " +
                $"maxDip={maxDip:F2}m cellsLowered={cellsLowered} bridgeRaised={raised} " +
                $"alreadyClear={alreadyClear} minClear={minClearanceMeters:F1}m" +
                (dipAsPin ? $" dipAsPin: verified={pinVerified} short={pinShort}" : ""));
        }
    }

    /// <summary>
    /// Solution A (doc 04 §4.A) — post-solve approach-raise ramps: the upward mirror of
    /// <see cref="DipLowerRoad"/>. In sparse-soft mode the span's end clearance has no other mechanism:
    /// the box filter dilutes the soft humps, near-abutment floors are skipped
    /// (<see cref="FloorMinArchShape"/> — "end deficits are approach territory"), and rail/water can never
    /// be dipped. So where a planned crossing OUTSIDE the floor band is still short of its typed budget
    /// after <see cref="BridgeProfileSolver.RefineSpans"/>, the WHOLE span is raised by ONE uniform
    /// delta — the worst end-crossing deficit — and the approaches on BOTH connected sides carry the
    /// lifted abutments back to the solved road with eased C1 ramps (`(1−u)²(1+2u)`, §3.3-class-slope
    /// run, junction-clamped via <see cref="BridgeElevationPlanner.MeasureRampLength"/>). Render #10
    /// lesson: a per-crossing local plateau reads as a HUMP on a long deck — the requirement is "equally
    /// raised, equally distributed cross-sections", so the deck keeps its solved curve shape and only
    /// translates up. No grade clamp (standing feedback): a room-clamped approach ramp gets STEEPER than
    /// the table and is warned, never shortened — the deck must clear.
    ///
    /// <para>Why this cannot re-introduce the render-#5 crumple: the base is the ACTUAL solved profile
    /// (zero estimate error), it runs AFTER all smoothing (nothing fights it), and it is the exact
    /// machinery the lower-road dips already use. The raise is applied to <c>TargetElevation</c> + banked
    /// edges, FILLED into the heightmap under the raised approach (mirror of the dip carve — this fill IS
    /// the Phase B-1 embankment), and any moved span is re-snapshotted so the deck mesh / excavator /
    /// bridge DecalRoads keep reading the same geometry. Pure-dip crossings never raise the deck (the
    /// pre-smooth dip pin + A7 residual carve own them). A junction inside the margin at an abutment
    /// would step that seam — the span is skipped with a log. No-op unless a sparse-mode plan exists —
    /// flag-off output is byte-identical.</para>
    /// </summary>
    public static void ApplyApproachRaiseRamps(
        UnifiedRoadNetwork network,
        float[,]? heightMap = null,
        float metersPerPixel = 1f,
        bool log = true,
        int[,]? roadSurfaceOwner = null,
        int[,]? deckFootprint = null)
    {
        ArgumentNullException.ThrowIfNull(network);

        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0 || plan.Spans.Count == 0)
            return;

        var raises = new List<SpanRaise>();
        var skipped = 0;
        var steepRamps = 0;

        foreach (var span in plan.Spans)
        {
            var spline = network.GetSplineById(span.OwnerSplineId);
            // Sparse-soft mode only: the hard-pin modes already deliver end clearance via held pins.
            if (spline?.Parameters.BridgeRules?.EnableSparseDeckConstraints != true)
                continue;

            var spanLength = span.EndDistance - span.StartDistance;
            if (spanLength <= 0.01f)
                continue;

            // The uniform raise = the worst still-short end-crossing budget under this span.
            var spanRaise = 0f;
            var worstNote = string.Empty;

            foreach (var cp in plan.Crossings)
            {
                if (cp.Crossing.UpperSplineId != span.OwnerSplineId || cp.RequiredSeparationMeters <= 0f)
                    continue;
                // A pure dip moves the lower road (pre-smooth pin + A7 residual carve) — never the deck end.
                if (cp.Action == BridgeElevationAction.DipLowerRoad)
                    continue;

                var upper = NearestSection(network, span.OwnerSplineId, cp.Crossing.CrossingXY);
                if (upper == null || upper.StructureSpanId != span.SpanId || !IsFinite(upper.TargetElevation))
                    continue;

                var station = upper.DistanceAlongSpline;
                var t = Math.Clamp((station - span.StartDistance) / spanLength, 0f, 1f);
                var shape = 16f * t * t * (1f - t) * (1f - t);
                if (shape >= FloorMinArchShape)
                    continue; // interior-floor band — the RefineSpans arch owns this crossing

                // Final clearance measured off the SOLVED surfaces (the lower road's Z already includes
                // its pre-smooth dip well; synthetic rail/water fall back to the planner's estimate).
                float lowerZ;
                if (cp.Crossing.HasLowerSpline)
                {
                    var lowerSection = NearestSection(network, cp.Crossing.LowerSplineId, cp.Crossing.CrossingXY);
                    lowerZ = lowerSection != null && IsFinite(lowerSection.TargetElevation)
                        ? lowerSection.TargetElevation
                        : cp.ObstacleZEstimate;
                }
                else
                {
                    lowerZ = IsFinite(cp.LowerRoadTargetZ) ? cp.LowerRoadTargetZ : cp.ObstacleZEstimate;
                }

                if (!IsFinite(lowerZ))
                    continue;

                var clearance = upper.TargetElevation - lowerZ;
                var deficit = cp.RequiredSeparationMeters - clearance;
                if (deficit <= 0.05f)
                    continue;

                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[BRIDGE-RAMP] crossing upper={span.OwnerSplineId} lower={cp.Crossing.LowerSplineId} " +
                    $"({cp.Crossing.LowerKind}) t={t:F2} " +
                    $"clearBefore={clearance:F2}/{cp.RequiredSeparationMeters:F2}m deficit={deficit:F2}m");

                if (deficit > spanRaise)
                {
                    spanRaise = deficit;
                    worstNote = $"{cp.Crossing.LowerSplineId} ({cp.Crossing.LowerKind}) t={t:F2}";
                }
            }

            if (spanRaise <= 0.05f)
                continue;

            // Approach ramps on every CONNECTED side (in-spline road beyond the span); an isolated side
            // has no seam to carry the raise to — but a junction sitting at that free end must still not
            // be stepped, so it boxes the raise like a too-close junction on a connected side does.
            var splineSections = network.GetCrossSectionsForSpline(span.OwnerSplineId);
            var hasRoadBefore = splineSections.Any(c => c.DistanceAlongSpline < span.StartDistance - 0.01f);
            var hasRoadAfter = splineSections.Any(c => c.DistanceAlongSpline > span.EndDistance + 0.01f);

            // §3.3 ramp sizing on the bridge's own class; junction-clamped per side (the junction-in-sag
            // analogue — MeasureRampLength already applies the 2 m margin).
            var classStep = BridgeRuleSystemOptions.ClassStepFor(spline.OsmRoadType);
            var normalSlope = BridgeRuleSystemOptions.NormalMaxSlopePercent(classStep) / 100f;
            var absSlope = BridgeRuleSystemOptions.AbsoluteMaxSlopePercent(classStep) / 100f;
            var desiredLength = spanRaise / MathF.Max(normalSlope, 1e-3f);

            var startRun = 0f;
            var endRun = 0f;
            var boxed = false;
            if (hasRoadBefore)
            {
                var room = BridgeElevationPlanner.MeasureRampLength(network, spline, span.StartDistance, forward: false);
                if (room <= 0.01f) boxed = true;
                else startRun = MathF.Min(desiredLength, room);
            }
            else if (SharedJunctionNear(network, span.OwnerSplineId, span.StartDistance))
            {
                boxed = true;
            }

            if (!boxed)
            {
                if (hasRoadAfter)
                {
                    var room = BridgeElevationPlanner.MeasureRampLength(network, spline, span.EndDistance, forward: true);
                    if (room <= 0.01f) boxed = true;
                    else endRun = MathF.Min(desiredLength, room);
                }
                else if (SharedJunctionNear(network, span.OwnerSplineId, span.EndDistance))
                {
                    boxed = true;
                }
            }

            if (boxed)
            {
                skipped++;
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[BRIDGE-RAMP] SKIPPED upper={span.OwnerSplineId} raise={spanRaise:F2}m (worst {worstNote}) " +
                    "— a junction at an abutment leaves no approach room (raising would step that seam)");
                continue;
            }

            // No grade clamp (standing feedback): the deck must clear — a room-clamped ramp gets steeper
            // than the §3.3 table and is warned, never shortened.
            var startPct = startRun > 0.01f ? spanRaise / startRun * 100f : 0f;
            var endPct = endRun > 0.01f ? spanRaise / endRun * 100f : 0f;
            var isSteep = MathF.Max(startPct, endPct) > absSlope * 100f + 0.01f;
            if (isSteep)
                steepRamps++;

            raises.Add(new SpanRaise(span.OwnerSplineId, span.StartDistance, span.EndDistance,
                spanRaise, startRun, endRun));

            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-RAMP] raise upper={span.OwnerSplineId} span=[{span.StartDistance:F0},{span.EndDistance:F0}] " +
                $"raise={spanRaise:F2}m uniform (worst {worstNote}) " +
                $"rampStart={startRun:F1}m@{startPct:F1}% rampEnd={endRun:F1}m@{endPct:F1}%" +
                (isSteep ? $" [STEEP — exceeds absolute {absSlope * 100f:F0}% at a clamped approach]" : ""));
        }

        if (raises.Count == 0)
        {
            if (log && skipped > 0)
                TerrainCreationLogger.Current?.InfoFileOnly($"[BRIDGE-RAMP] summary ramps=0 skipped={skipped}");
            return;
        }

        // Apply: per spline, max-combine its span raises per section (ramps of adjacent spans may
        // overlap between them), raise centerline + banked edges; FILL the heightmap under the raised
        // APPROACH only — span sections are bridge deck, the air below them stays (the excavator owns
        // the daylight).
        var canFill = heightMap != null && metersPerPixel > 0f;
        var mapWidth = heightMap?.GetLength(1) ?? 0;
        var mapHeight = heightMap?.GetLength(0) ?? 0;
        var fillByCell = canFill ? new Dictionary<int, float>() : null;
        var movedSections = 0;
        var maxRaise = 0f;
        var touchedSpans = new HashSet<(int SplineId, int SpanId)>();

        foreach (var group in raises.GroupBy(r => r.SplineId))
        {
            var spline = network.GetSplineById(group.Key);
            if (spline == null)
                continue;
            var affectedRange = MathF.Max(0f, spline.Parameters.TerrainAffectedRangeMeters);
            var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

            foreach (var cs in network.GetCrossSectionsForSpline(group.Key))
            {
                var raise = 0f;
                foreach (var spanRaise in group)
                    raise = MathF.Max(raise, spanRaise.RaiseAt(cs.DistanceAlongSpline));
                if (raise <= 1e-4f || !IsFinite(cs.TargetElevation))
                    continue;

                var z = cs.TargetElevation + raise;
                cs.TargetElevation = z;
                var halfWidth = cs.EffectiveRoadWidth / 2f;
                var bankDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
                cs.LeftEdgeElevation = z - bankDelta;
                cs.RightEdgeElevation = z + bankDelta;
                movedSections++;
                maxRaise = MathF.Max(maxRaise, raise);

                if (cs.StructureSpanId >= 0)
                    touchedSpans.Add((group.Key, cs.StructureSpanId));
                if (cs.IsExcluded || fillByCell == null)
                    continue; // deck-only sections get no terrain fill — the doc-06 overlap zone IS road

                // Mirror of the dip carve, fill instead of cut: the approach was stamped at its old
                // elevation in Phase 4 — lift the driven surface with it (this fill IS the embankment).
                var reach = halfWidth + affectedRange;
                var normal = cs.NormalDirection;
                for (var offset = -reach; offset <= reach; offset += lateralStep)
                {
                    var lateral = LateralFalloff(MathF.Abs(offset), halfWidth, affectedRange);
                    if (lateral <= 0f)
                        continue;

                    var worldX = cs.CenterPoint.X + normal.X * offset;
                    var worldY = cs.CenterPoint.Y + normal.Y * offset;
                    var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                    var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                    // Never raise a NEIGHBOURING road's protected surface — the embankment fills only this
                    // bridge's own approach + bare terrain (self/no-owner). No raster ⇒ legacy behaviour.
                    if (!RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, py, px, cs.OwnerSplineId))
                        continue;

                    // Never raise terrain over a FOREIGN bridge's deck footprint (doc 09 §9.2): approach
                    // embankment fill must not bury another bridge's driving surface.
                    if (!BridgeDeckFootprintRaster.CanRaise(deckFootprint, py, px, cs.OwnerSplineId))
                        continue;

                    var fill = raise * lateral;
                    var key = py * mapWidth + px;
                    if (!fillByCell.TryGetValue(key, out var existing) || fill > existing)
                        fillByCell[key] = fill;
                }
            }
        }

        var cellsRaised = 0;
        if (heightMap != null && fillByCell is { Count: > 0 })
        {
            foreach (var (key, fill) in fillByCell)
            {
                if (fill <= 0f)
                    continue;
                var px = key % mapWidth;
                var py = key / mapWidth;
                var current = heightMap[py, px];
                if (float.IsNaN(current) || float.IsInfinity(current))
                    continue;
                heightMap[py, px] = current + fill;
                cellsRaised++;
            }
        }

        // The deck mesh / excavator / bridge DecalRoads read the span SNAPSHOT — refresh any span whose
        // sections just moved (doc 04 §4.A watch-out).
        foreach (var (splineId, spanId) in touchedSpans)
            BridgeProfileSolver.RecaptureSpanSnapshot(network, splineId, spanId);

        if (log)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-RAMP] summary ramps={raises.Count} skipped={skipped} steep={steepRamps} " +
                $"maxRaise={maxRaise:F2}m sections={movedSections} cellsRaised={cellsRaised}");
    }

    /// <summary>
    /// True when a junction shared with another road (≥ 2 contributors) sits on this spline within the
    /// dip/ramp junction margin of <paramref name="station"/>. Used to box the uniform span raise at an
    /// in-spline-isolated abutment that still terminates AT a junction (e.g. a T-junction at the spline
    /// end): raising the deck there would step the harmonized junction seam. A single-contributor dead-end
    /// Endpoint junction does not box — there is nothing to step against.
    /// </summary>
    private static bool SharedJunctionNear(UnifiedRoadNetwork network, int splineId, float station)
    {
        foreach (var junction in network.Junctions)
        {
            if (junction.Contributors.Count < 2)
                continue;
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.Spline.SplineId != splineId)
                    continue;
                if (MathF.Abs(contributor.CrossSection.DistanceAlongSpline - station) <=
                    JunctionClearanceMarginMeters + 0.01f)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One uniform span raise (doc 04 §4.A, render #10 amendment), piecewise C1 over arc-length: the full
    /// raise across the WHOLE span [<see cref="SpanStart"/>, <see cref="SpanEnd"/>] — every deck
    /// cross-section equally, no local hump — easing to zero in value AND slope over
    /// <see cref="StartRunMeters"/> / <see cref="EndRunMeters"/> on the approaches (the same
    /// <c>(1−u)²(1+2u)</c> weight the dip well uses). A run of 0 = isolated side (no sections there).
    /// </summary>
    private readonly record struct SpanRaise(
        int SplineId,
        float SpanStart, float SpanEnd,
        float RaiseMeters, float StartRunMeters, float EndRunMeters)
    {
        public float RaiseAt(float distance)
        {
            float u;
            if (distance >= SpanStart && distance <= SpanEnd)
                return RaiseMeters;

            if (distance > SpanEnd)
            {
                if (EndRunMeters <= 1e-3f)
                    return 0f;
                u = (distance - SpanEnd) / EndRunMeters;
            }
            else
            {
                if (StartRunMeters <= 1e-3f)
                    return 0f;
                u = (SpanStart - distance) / StartRunMeters;
            }

            if (u >= 1f)
                return 0f;
            var w = (1f - u) * (1f - u) * (1f + 2f * u);
            return RaiseMeters * w;
        }
    }

    /// <summary>
    /// The vertical offset (m) to add to the minimum clearance so it is measured to the bridge deck's SOFFIT
    /// (underside) rather than its solved deck-top Z. Equals the upper member's 3D deck thickness when it
    /// generates a box deck; 0 otherwise (no profile, or the upper member is not a generated bridge). Computed
    /// per bridge from its span via the same <see cref="BridgeDeckProfile.ComputeDeckThicknessMeters"/> rule
    /// the mesh uses, so the clearance is precise against the actual geometry.
    /// </summary>
    private static float DeckThicknessOffset(UnifiedRoadNetwork network, int upperSplineId, BridgeDeckProfile? deckProfile)
    {
        if (deckProfile == null)
            return 0f;

        var upper = network.GetSplineById(upperSplineId);
        if (upper == null)
            return 0f;

        // Merged-corridor mode (plan doc 11, Phase 5): the deck is only the tagged span sub-range, so the
        // thickness span must be measured over the span sections — NOT the whole corridor — or the deck would
        // read as enormously thick. Legacy mode: every section of the whole bridge spline is the deck.
        var sections = network.GetCrossSectionsForSpline(upperSplineId).ToList();
        var spanSections = sections.Where(c => c.StructureSpanId >= 0).ToList();
        if (spanSections.Count == 0 && !BridgeDeckDaeExporter.ShouldGenerateDeck(upper))
            return 0f;
        var deckSections = spanSections.Count > 0 ? spanSections : sections;

        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        foreach (var cs in deckSections)
        {
            if (cs.DistanceAlongSpline < min) min = cs.DistanceAlongSpline;
            if (cs.DistanceAlongSpline > max) max = cs.DistanceAlongSpline;
        }
        if (float.IsInfinity(min))
            return 0f;

        return BridgeDeckProfile.ComputeDeckThicknessMeters(max - min, deckProfile);
    }

    /// <summary>
    /// True if <paramref name="spline"/> presents a generated bridge deck at <paramref name="atXY"/>: either a
    /// legacy whole-spline bridge (<see cref="BridgeDeckDaeExporter.ShouldGenerateDeck"/>), or — in
    /// merged-corridor mode (plan doc 11) — a corridor whose nearest cross-section at that point is inside a
    /// bridge span (<see cref="UnifiedCrossSection.StructureSpanId"/> &gt;= 0).
    /// </summary>
    private static bool IsGeneratedDeckAt(UnifiedRoadNetwork network, ParameterizedRoadSpline spline, System.Numerics.Vector2 atXY)
    {
        if (BridgeDeckDaeExporter.ShouldGenerateDeck(spline))
            return true;
        var near = NearestSection(network, spline.SplineId, atXY);
        return near is { StructureSpanId: >= 0 };
    }

    /// <summary>
    /// Applies a smooth two-sided dip "well" to the lower road centred on <paramref name="centerDist"/>,
    /// reaching <paramref name="depth"/> at the centre and easing to zero (value AND slope) at
    /// ±<paramref name="rampLength"/>. Reuses the connector-grade-ramp philosophy: an additive local
    /// correction on top of the settled profile, with banked edges recomputed. When <paramref name="carveByCell"/>
    /// is non-null, the same drop is accumulated into the heightmap footprint (road width + the spline's
    /// terrain-affected range, with a lateral smoothstep falloff so the trough blends into surrounding terrain
    /// without a wall). Returns the number of (non-excluded) sections moved.
    /// </summary>
    private static int DipLowerRoad(
        UnifiedRoadNetwork network,
        ParameterizedRoadSpline spline,
        float centerDist,
        float depth,
        float rampLength,
        Dictionary<int, float>? carveByCell,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        bool carveOnly = false,
        int[,]? roadSurfaceOwner = null)
    {
        if (depth <= 0f || rampLength <= 0.01f)
            return 0;
        if (carveOnly && carveByCell == null)
            return 0; // A7 residual carve has nowhere to go without a heightmap

        // Clamp the well so it eases to zero BEFORE any junction on this road — never disturb a harmonized
        // junction (absolute no-go). We take the SAME (nearer-junction) limit for BOTH sides so the dip is
        // SYMMETRIC: an asymmetric per-side clamp produced an ugly long-gentle-one-side / short-steep-other
        // result. Symmetric stays junction-safe (each side stops short of its own junction) and looks even.
        var (rampBack, rampFwd) = ClampRampToJunctions(network, spline.SplineId, centerDist, rampLength);
        var effRamp = MathF.Min(rampBack, rampFwd);
        if (effRamp <= 0.01f)
            return 0; // boxed in by a junction too close on one side → cannot dip without breaking harmonization

        return ApplyWell(network, spline,
            cs => depth * UnderpassWellProfile.EasedWellWeight(
                MathF.Abs(cs.DistanceAlongSpline - centerDist) / effRamp),
            carveByCell, metersPerPixel, mapWidth, mapHeight, carveOnly, roadSurfaceOwner);
    }

    /// <summary>
    /// The shared dip-application loop: drops each of the spline's cross-sections by
    /// <paramref name="dropAt"/>(section) — profile (TargetElevation + banked edges) unless
    /// <paramref name="carveOnly"/>, plus the max-combined heightmap carve. The drop shape is the caller's:
    /// the classic single eased well (<see cref="DipLowerRoad"/>) or a doc-28 cluster envelope
    /// (<see cref="UnderpassWellProfile"/>, which reads the section's solved Z to blend its ramps).
    /// Returns the number of sections moved.
    /// </summary>
    private static int ApplyWell(
        UnifiedRoadNetwork network,
        ParameterizedRoadSpline spline,
        Func<UnifiedCrossSection, float> dropAt,
        Dictionary<int, float>? carveByCell,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        bool carveOnly,
        int[,]? roadSurfaceOwner)
    {
        var affectedRange = MathF.Max(0f, spline.Parameters.TerrainAffectedRangeMeters);
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        var moved = 0;
        foreach (var cs in network.GetCrossSectionsForSpline(spline.SplineId))
        {
            if (cs.IsExcluded)
                continue; // never dip excluded/structure sections

            var sectionDrop = dropAt(cs);
            if (sectionDrop <= 0f)
                continue;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            if (!carveOnly)
            {
                // A7 carve-only mode never touches the road profile (no double-dip) — heightmap only.
                var z = cs.TargetElevation - sectionDrop;
                cs.TargetElevation = z;

                var bankDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
                cs.LeftEdgeElevation = z - bankDelta;
                cs.RightEdgeElevation = z + bankDelta;
            }

            moved++;

            if (carveByCell == null || sectionDrop <= 0f)
                continue;

            // Carve the same drop into the heightmap across the road footprint, fading out over the affected
            // range so the trough rejoins the surrounding (already-stamped) terrain smoothly.
            var reach = halfWidth + affectedRange;
            var normal = cs.NormalDirection;
            for (var offset = -reach; offset <= reach; offset += lateralStep)
            {
                var lateral = LateralFalloff(MathF.Abs(offset), halfWidth, affectedRange);
                if (lateral <= 0f)
                    continue;

                var worldX = cs.CenterPoint.X + normal.X * offset;
                var worldY = cs.CenterPoint.Y + normal.Y * offset;
                var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                // Never carve a NEIGHBOURING road's protected surface — only this road's own footprint +
                // bare terrain (self/no-owner). No raster ⇒ legacy behaviour.
                if (!RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, py, px, spline.SplineId))
                    continue;

                var drop = sectionDrop * lateral;
                var key = py * mapWidth + px;
                if (!carveByCell.TryGetValue(key, out var existing) || drop > existing)
                    carveByCell[key] = drop;
            }
        }

        return moved;
    }

    /// <summary>
    /// Lateral weight for the heightmap carve: 1 across the road half-width, smoothstepping to 0 over the
    /// terrain-affected range beyond it, so the dipped trough blends into the surrounding terrain.
    /// </summary>
    private static float LateralFalloff(float absOffset, float halfWidth, float affectedRange)
    {
        if (absOffset <= halfWidth)
            return 1f;
        if (affectedRange <= 0f || absOffset >= halfWidth + affectedRange)
            return 0f;
        var t = (absOffset - halfWidth) / affectedRange; // 0 at road edge → 1 at the blend edge
        return 1f - t * t * (3f - 2f * t);               // smoothstep 1 → 0
    }

    /// <summary>
    /// Returns the per-side maximum dip-ramp half-length (back = toward decreasing distance, fwd = toward
    /// increasing distance) such that the well stops at least <see cref="JunctionClearanceMarginMeters"/>
    /// short of the nearest junction on this spline on that side. Roads enter junctions (endpoints, T/cross,
    /// at-grade mid-spline crossings) as contributors in <c>network.Junctions</c>; their station along the
    /// road is the contributor cross-section's DistanceAlongSpline.
    /// </summary>
    private static (float back, float fwd) ClampRampToJunctions(
        UnifiedRoadNetwork network, int splineId, float centerDist, float rampLength)
    {
        var back = rampLength;
        var fwd = rampLength;

        foreach (var junction in network.Junctions)
        foreach (var contributor in junction.Contributors)
        {
            if (contributor.Spline.SplineId != splineId)
                continue;

            var d = contributor.CrossSection.DistanceAlongSpline - centerDist;
            if (d < -0.01f)
            {
                var allow = MathF.Max(0f, -d - JunctionClearanceMarginMeters);
                if (allow < back) back = allow;
            }
            else if (d > 0.01f)
            {
                var allow = MathF.Max(0f, d - JunctionClearanceMarginMeters);
                if (allow < fwd) fwd = allow;
            }
            else
            {
                // A junction essentially at the crossing centre (shouldn't happen — grade-separated crossings
                // are not junctions). Refuse to dip rather than risk the junction.
                back = 0f;
                fwd = 0f;
            }
        }

        return (back, fwd);
    }

    /// <summary>Doc 28: true when any junction has a contributor on <paramref name="splineId"/> whose
    /// station lies strictly INSIDE the cluster interval — the merged envelope well would override its
    /// harmonized elevation, so the caller falls back to the junction-safe per-crossing wells. KEEP IN
    /// SYNC with <c>UnifiedRoadSmoother.HasJunctionOnSplineBetween</c> — both gate the same doc-28
    /// merged-well-vs-per-crossing fallback on their respective application paths (post-solve vs pin);
    /// diverging bounds would make the two paths shape the same cluster differently.</summary>
    private static bool HasJunctionOnSplineBetween(
        UnifiedRoadNetwork network, int splineId, float startStation, float endStation)
    {
        foreach (var junction in network.Junctions)
        foreach (var contributor in junction.Contributors)
        {
            if (contributor.Spline.SplineId != splineId)
                continue;
            var s = contributor.CrossSection.DistanceAlongSpline;
            if (s > startStation + 0.01f && s < endStation - 0.01f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The rule engine's per-crossing decision (<see cref="BridgeElevationAction"/>) keyed by the
    /// <see cref="GradeSeparatedCrossing"/> it acted on, from <c>network.BridgeElevationPlan</c> (merged
    /// corridors, plan doc 14 Phase D). Null when there is no plan — legacy whole-spline mode, where the dip
    /// gating instead reads <c>crossing.Action</c> set by <see cref="PlanConstraints"/>.
    /// </summary>
    private static Dictionary<GradeSeparatedCrossing, CrossingPlan>? BuildPlannerActionLookup(
        UnifiedRoadNetwork network)
    {
        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0)
            return null;

        var map = new Dictionary<GradeSeparatedCrossing, CrossingPlan>(plan.Crossings.Count);
        foreach (var cp in plan.Crossings)
            map[cp.Crossing] = cp;
        return map;
    }

    /// <summary>Finds the cross-section on a spline whose centre is closest to the crossing point.</summary>
    private static UnifiedCrossSection? NearestSection(UnifiedRoadNetwork network, int splineId, Vector2 xy)
    {
        UnifiedCrossSection? best = null;
        var bestDist = float.MaxValue;
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            var d = Vector2.DistanceSquared(cs.CenterPoint, xy);
            if (d < bestDist)
            {
                bestDist = d;
                best = cs;
            }
        }

        return best;
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>
    ///     Doc 09 C5: read-only clearance check. Logs a [BRIDGE-CLEAR] WARN for every crossing whose
    ///     final deck-vs-lower-road clearance is below the required minimum. Writes NO elevation — a
    ///     firing warning means the pre-solve planner should have dipped / reduced clearance / not
    ///     raised, and is fixed there (in-solve), never with a post-solve raise. Returns the short count.
    /// </summary>
    public static int AssertCrossingClearances(UnifiedRoadNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0) return 0;

        var shortCount = 0;
        foreach (var cp in plan.Crossings)
        {
            if (cp.RequiredSeparationMeters <= 0f || cp.Action == BridgeElevationAction.DipLowerRoad)
                continue;

            var span = plan.Spans.FirstOrDefault(s => s.OwnerSplineId == cp.Crossing.UpperSplineId);
            if (span == null) continue;

            var upper = NearestSection(network, span.OwnerSplineId, cp.Crossing.CrossingXY);
            if (upper == null || upper.StructureSpanId != span.SpanId || !IsFinite(upper.TargetElevation))
                continue;

            var spanLength = span.EndDistance - span.StartDistance;
            if (spanLength <= 0.01f) continue;
            var t = Math.Clamp((upper.DistanceAlongSpline - span.StartDistance) / spanLength, 0f, 1f);
            if (16f * t * t * (1f - t) * (1f - t) >= FloorMinArchShape)
                continue; // interior floor band — RefineSpans arch owns this crossing

            float lowerZ;
            if (cp.Crossing.HasLowerSpline)
            {
                var lowerSection = NearestSection(network, cp.Crossing.LowerSplineId, cp.Crossing.CrossingXY);
                lowerZ = lowerSection != null && IsFinite(lowerSection.TargetElevation)
                    ? lowerSection.TargetElevation
                    : cp.ObstacleZEstimate;
            }
            else
            {
                lowerZ = IsFinite(cp.LowerRoadTargetZ) ? cp.LowerRoadTargetZ : cp.ObstacleZEstimate;
            }
            if (!IsFinite(lowerZ)) continue;

            var clearance = upper.TargetElevation - lowerZ;
            if (clearance < cp.RequiredSeparationMeters - 0.05f)
            {
                shortCount++;
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[BRIDGE-CLEAR] WARN upper={cp.Crossing.UpperSplineId} lower={cp.Crossing.LowerSplineId} " +
                    $"({cp.Crossing.LowerKind}) clearance={clearance:F2}/{cp.RequiredSeparationMeters:F2}m " +
                    "— planner should dip/reduce/not-raise (doc 09 C5, no post-solve correction)");
            }
        }

        if (shortCount > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-CLEAR] {shortCount} crossing(s) under required clearance after solve");
        return shortCount;
    }
}
