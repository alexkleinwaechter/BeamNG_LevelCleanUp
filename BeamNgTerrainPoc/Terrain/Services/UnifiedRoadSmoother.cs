using System.Diagnostics;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     Top-level orchestrator for unified road network smoothing.
///     This replaces the material-centric processing in MultiMaterialRoadSmoother with
///     a network-centric approach that:
///     1. Builds a unified road network from all materials
///     1.5. Identifies roundabout splines (before banking)
///     1.8. Detects junctions early (topology-only, before elevation calculation) (WI-5)
///     2. Calculates target elevations per-spline with endpoint anchoring to junctions (WI-6)
///     2.6. Detects roundabout junctions and harmonizes roundabout elevations
///     3. Harmonizes junctions across the entire network (including cross-material)
///     4. Applies protected terrain blending in a single pass
///     5. Paints material layers separately (using surface width, not elevation width)
///     Key benefits:
///     - Single EDT computation (faster than per-material)
///     - Road core pixels are protected from neighbor's blend zones
///     - Proper cross-material junction handling
///     - Roundabout rings have uniform elevation with smooth connecting road transitions
///     - Per-spline parameter respect in unified processing
/// </summary>
public class UnifiedRoadSmoother
{
    private readonly BankingOrchestrator _bankingOrchestrator;
    private readonly IHeightCalculator _elevationCalculator;
    private readonly NetworkJunctionDetector _junctionDetector;
    private readonly NetworkJunctionHarmonizer _junctionHarmonizer;
    private readonly MaterialPainter _materialPainter;
    private readonly UnifiedRoadNetworkBuilder _networkBuilder;
    private readonly RoundaboutElevationHarmonizer _roundaboutHarmonizer;
    private readonly UnifiedTerrainBlender _terrainBlender;
    private readonly UnifiedJunctionProfileBlender _unifiedProfileBlender;
    private StructureElevationIntegrator _structureElevationIntegrator;

    /// <summary>
    ///     Cached elevation chain data, built once in Phase 2 iteration 0 and reused for re-smooth iterations.
    ///     Null when chain-based smoothing is not active (legacy per-spline mode).
    /// </summary>
    private List<ElevationChain>? _cachedElevationChains;
    private NetworkElevationGraph? _cachedElevationGraph;

    // NO-BLEND DIAGNOSTICS master switch. Flip to true + rebuild to emit the [NO-BLEND DIAG/OWN/
    // PROFILE/RAB/RAMP] dumps (junction state right before Phase 4, suspect through-road profiles,
    // roundabout connector seams, connector grade welds). Kept off in production: the no-blend work
    // (§2 absolute-depth, connector ramp, roundabout tilt) is still being validated, so these traces
    // are worth keeping rather than deleting. When false the JIT can dead-strip the gated blocks.
    private const bool EmitNoBlendDiagnostics = true;

    // NO-BLEND DIAGNOSTIC support: snapshot of each cross-section's pure chain low-pass elevation,
    // captured right before the per-spline affine/anchoring correction. Lets the pre-Phase-4 dump
    // separate "the low-pass" from "the correction". Only populated when EmitNoBlendDiagnostics.
    private readonly Dictionary<(int splineId, int localIndex), float> _preCorrectionElevations = new();

    public UnifiedRoadSmoother()
    {
        _networkBuilder = new UnifiedRoadNetworkBuilder();
        _junctionDetector = new NetworkJunctionDetector();
        _junctionHarmonizer = new NetworkJunctionHarmonizer();
        _roundaboutHarmonizer = new RoundaboutElevationHarmonizer();
        _bankingOrchestrator = new BankingOrchestrator();
        _terrainBlender = new UnifiedTerrainBlender();
        _materialPainter = new MaterialPainter();
        _elevationCalculator = new OptimizedElevationSmoother();
        _structureElevationIntegrator = new StructureElevationIntegrator();
        _unifiedProfileBlender = new UnifiedJunctionProfileBlender();
    }

    /// <summary>
    /// Clears all cached data from internal components to release memory.
    /// Call this after terrain generation is complete and intermediate data is no longer needed.
    /// </summary>
    public void ClearCachedData()
    {
        _terrainBlender.ClearCachedData();
    }

    /// <summary>
    ///     Configures the structure elevation integrator with parameters from TerrainCreationParameters.
    ///     Should be called before SmoothAllRoads if custom structure elevation parameters are needed.
    /// </summary>
    /// <param name="parameters">The terrain creation parameters containing structure elevation settings.</param>
    public void ConfigureStructureElevationParameters(TerrainCreationParameters parameters)
    {
        _structureElevationIntegrator = StructureElevationIntegrator.FromParameters(parameters);
    }

    /// <summary>
    ///     Smooths all roads in the unified network.
    ///     This is the main entry point that orchestrates the entire pipeline:
    ///     1. Build unified network from all road materials
    ///     2. Calculate target elevations for each spline
    ///     3. Detect and harmonize junctions across the network
    ///     4. Apply terrain blending in a single pass
    ///     5. Paint material layers based on spline ownership
    /// </summary>
    /// <param name="heightMap">The original terrain heightmap.</param>
    /// <param name="materials">List of material definitions (only those with RoadParameters are processed).</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <param name="size">Terrain size in pixels.</param>
    /// <param name="enableCrossMaterialHarmonization">Whether to harmonize junctions across materials.</param>
    /// <param name="flipMaterialProcessingOrder">
    ///     When true, materials at top of list (index 0) get higher priority for road
    ///     smoothing.
    /// </param>
    /// <returns>Result containing smoothed heightmap, material layers, and network data.</returns>
    public UnifiedSmoothingResult? SmoothAllRoads(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization = true,
        bool flipMaterialProcessingOrder = true,
        DecalRoadSettings? decalRoadSettings = null,
        IReadOnlyDictionary<string, DecalRoadLayerSet>? appDataDefaults = null)
    {
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        // Clear cached elevation chains from any previous run
        _cachedElevationChains = null;
        _cachedElevationGraph = null;

        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();

        if (roadMaterials.Count == 0)
        {
            TerrainLogger.Info("UnifiedRoadSmoother: No road materials to process");
            return null;
        }

        TerrainLogger.Info("=== UNIFIED ROAD SMOOTHING ===");
        TerrainLogger.Info($"  Materials: {roadMaterials.Count}");
        TerrainLogger.Info($"  Cross-material harmonization: {enableCrossMaterialHarmonization}");
        perfLog?.LogSection("UnifiedRoadSmoother");

        // Phase 1: Build unified road network from all materials
        perfLog?.LogSection("Phase 1: Network Building");
        TerrainLogger.Info("Phase 1: Building unified road network...");
        var sw = Stopwatch.StartNew();
        var network =
            _networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size, flipMaterialProcessingOrder,
                decalRoadSettings, appDataDefaults);
        perfLog?.Timing($"BuildNetwork: {sw.Elapsed.TotalSeconds:F2}s");

        if (network.Splines.Count == 0)
        {
            TerrainLogger.Warning("No splines extracted from materials");
            return null;
        }

        TerrainCreationLogger.Current?.InfoFileOnly(
            $"  Network built: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");

        // Mark paint-only splines and temporarily remove them from elevation phases.
        // Paint-only splines participate in material painting and master spline export
        // but must NOT modify terrain elevation in any way.
        var paintOnlyMaterialNames = new HashSet<string>(
            roadMaterials.Where(m => m.RoadParameters?.PaintOnlyMode == true)
                         .Select(m => m.MaterialName));

        foreach (var spline in network.Splines.Where(s => paintOnlyMaterialNames.Contains(s.MaterialName)))
            spline.IsPaintOnly = true;

        var paintOnlySplines = network.Splines.Where(s => s.IsPaintOnly).ToList();
        var paintOnlySplineIds = new HashSet<int>(paintOnlySplines.Select(s => s.SplineId));
        var paintOnlyCrossSections = network.CrossSections
            .Where(cs => paintOnlySplineIds.Contains(cs.OwnerSplineId)).ToList();

        if (paintOnlySplines.Count > 0)
        {
            TerrainLogger.Info($"  Paint-only splines: {paintOnlySplines.Count} (excluded from elevation phases)");
            // Remove from network for elevation phases (2-4 + post-processing)
            foreach (var s in paintOnlySplines) network.Splines.Remove(s);
            foreach (var cs in paintOnlyCrossSections) network.CrossSections.Remove(cs);
        }

        var allPaintOnly = network.Splines.Count == 0 && paintOnlySplines.Count > 0;
        float[,] smoothedHeightMap;

        if (allPaintOnly)
        {
            TerrainLogger.Info("  All splines are paint-only - skipping elevation phases");
            // No elevation modification: use the original heightmap directly
            smoothedHeightMap = heightMap;
        }
        else
        {

        // Phase 1.5: Identify roundabout splines early (before banking)
        // This must happen BEFORE banking pre-calculation so that roundabout splines
        // don't get banking applied to them. Roundabouts are circular and should never be banked.
        // We ALWAYS run this phase to catch closed-loop splines even without RoundaboutProcessingResult.
        {
            perfLog?.LogSection("Phase 1.5: Roundabout Identification");
            TerrainCreationLogger.Current?.InfoFileOnly("Phase 1.5: Identifying roundabout splines...");
            sw.Restart();
            IdentifyRoundaboutSplines(roadMaterials, network);
            var roundaboutCount = network.Splines.Count(s => s.IsRoundabout);
            TerrainCreationLogger.Current?.InfoFileOnly($"  Identified {roundaboutCount} roundabout spline(s)");
            perfLog?.Timing($"IdentifyRoundaboutSplines: {sw.Elapsed.TotalSeconds:F2}s");
        }

        var shouldHarmonize = ShouldHarmonize(roadMaterials);

        // Phase 1.8: Early junction detection (WI-5)
        // Detect junctions BEFORE elevation calculation so that future phases
        // (WI-6 endpoint anchoring, WI-9 junction plateau) can use junction data.
        // Detection is purely topology-based (endpoint clustering + spatial proximity)
        // and does not require elevation data.
        if (shouldHarmonize)
        {
            perfLog?.LogSection("Phase 1.8: Early Junction Detection");
            TerrainCreationLogger.Current?.InfoFileOnly("Phase 1.8: Detecting junctions early...");
            sw.Restart();
            _junctionDetector.DetectJunctions(network);
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"  Early detection found {network.Junctions.Count} junction(s)");
            perfLog?.Timing($"EarlyJunctionDetection: {sw.Elapsed.TotalSeconds:F2}s");

            // Phase 1.9: pin junction elevations before per-road smoothing.
            // When EnablePhase19JunctionPinning is on, junction HarmonizedElevation is fixed
            // here and consumed by Phase 2's endpoint anchor lookup. Otherwise no-op.
            // Reads material-level params here; the iter-1 C1a check downstream reads
            // spline-level params. In production (UnifiedRoadNetworkBuilder) they share
            // the same object reference, so they agree by construction. Diagnostic
            // snapshot loaders that omit junction params will see Phase 1.9 disabled.
            var phase19Params = roadMaterials
                .Select(m => m.RoadParameters?.JunctionHarmonizationParameters)
                .FirstOrDefault(p => p != null);
            if (phase19Params != null)
            {
                JunctionElevationPinner.PinNetwork(network, heightMap, metersPerPixel, phase19Params);
            }
        }

        // === Iterative Junction Refinement Loop (WI-4) ===
        // Phases 2 and 3 are wrapped in a convergence loop. On iteration 0, the full pipeline runs
        // (heightmap sampling, structure profiles, banking, roundabouts, junction harmonization).
        // On subsequent iterations, Phase 2 re-smooths from existing TargetElevation values and
        // Phase 3 re-harmonizes using already-detected junctions. The loop converges when the max
        // elevation correction falls below threshold or stops improving.
        const int maxIterations = 3;
        const float convergenceThresholdMeters = 0.01f;
        var previousMaxCorrection = float.MaxValue;
        HarmonizationResult? lastHarmonizationResult = null;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var isFirstIteration = iteration == 0;
            var iterationLabel = maxIterations > 1 && shouldHarmonize
                ? $" (iteration {iteration + 1}/{maxIterations})"
                : "";

            // Phase 2: Calculate target elevations for each spline (+ WI-6 endpoint anchoring)
            perfLog?.LogSection($"Phase 2: Elevation Calculation{iterationLabel}");
            TerrainCreationLogger.Current?.InfoFileOnly($"Phase 2: Calculating target elevations{iterationLabel}...");
            sw.Restart();
            CalculateNetworkElevations(network, heightMap, metersPerPixel, reSmoothFromExisting: !isFirstIteration);
            perfLog?.Timing($"CalculateNetworkElevations: {sw.Elapsed.TotalSeconds:F2}s");

            // Phases 2.3, 2.5, 2.6 run ONLY on iteration 0
            if (isFirstIteration)
            {
                // Phase 2.3: Calculate elevation profiles for bridge/tunnel structures
                // This calculates independent elevation profiles for structures that are excluded from terrain smoothing.
                // The profiles are stored on the splines for future DAE generation even though the cross-sections
                // are excluded from terrain modification.
                var structureCount = network.Splines.Count(s => s.IsStructure);
                if (structureCount > 0)
                {
                    perfLog?.LogSection("Phase 2.3: Structure Elevation Profiles");
                    TerrainCreationLogger.Current?.InfoFileOnly($"Phase 2.3: Calculating elevation profiles for {structureCount} structure(s)...");
                    sw.Restart();

                    // Calculate profiles for excluded structures (profiles stored for DAE generation)
                    var structureResult = _structureElevationIntegrator.IntegrateStructureElevationsSelective(
                        network, heightMap, metersPerPixel,
                        excludeBridges: roadMaterials.Any(m => m.RoadParameters?.ExcludeBridgesFromTerrain == true),
                        excludeTunnels: roadMaterials.Any(m => m.RoadParameters?.ExcludeTunnelsFromTerrain == true));

                    perfLog?.Timing($"StructureElevationProfiles: {sw.Elapsed.TotalSeconds:F2}s");

                    if (structureResult.TotalStructuresProcessed > 0)
                    {
                        TerrainCreationLogger.Current?.InfoFileOnly($"  Calculated profiles for {structureResult.BridgesProcessed} bridge(s), " +
                                          $"{structureResult.TunnelsProcessed} tunnel(s)");
                    }

                    if (structureResult.ValidationMessages.Count > 0)
                    {
                        foreach (var msg in structureResult.ValidationMessages)
                        {
                            TerrainLogger.Warning($"  Structure validation: {msg}");
                        }
                    }
                }

                // Phase 2.5: Pre-calculate banking (bank angles and edge elevations)
                // This ALWAYS runs - even without user-enabled banking, the pipeline computes
                // curvature, edge elevations, and junction adaptation that prevents terrain spikes.
                {
                    perfLog?.LogSection("Phase 2.5: Banking Pre-calculation");
                    TerrainCreationLogger.Current?.InfoFileOnly("Phase 2.5: Pre-calculating road banking (for junction awareness)...");
                    sw.Restart();
                    _bankingOrchestrator.ApplyBankingPreCalculation(network);
                    perfLog?.Timing($"ApplyBankingPreCalculation: {sw.Elapsed.TotalSeconds:F2}s");
                }

                // Phase 2.6: Detect and harmonize roundabout elevations
                // This must happen AFTER initial elevation calculation but BEFORE general junction harmonization
                // so that roundabout junctions are already at their target elevation when other roads blend to them.
                if (HasRoundaboutsInNetwork(network, roadMaterials))
                {
                    perfLog?.LogSection("Phase 2.6: Roundabout Elevation Harmonization");
                    TerrainCreationLogger.Current?.InfoFileOnly("Phase 2.6: Processing roundabout elevations...");
                    sw.Restart();

                    // Step 1: Collect roundabout processing results from all materials
                    var allRoundaboutInfos = CollectRoundaboutInfos(roadMaterials, network);

                    if (allRoundaboutInfos.Count > 0)
                    {
                        // Step 2: Detect roundabout junctions (where roads meet roundabout rings)
                        var roundaboutConnectionRadius = GetRoundaboutConnectionRadius(roadMaterials);
                        var roundaboutJunctionInfos = _junctionDetector.DetectRoundaboutJunctions(
                            network, allRoundaboutInfos, roundaboutConnectionRadius);

                        // Step 3: Harmonize roundabout elevations (uniform ring elevation + connecting road blending)
                        var roundaboutHarmonizationResult = _roundaboutHarmonizer.HarmonizeRoundaboutElevations(
                            network,
                            roundaboutJunctionInfos,
                            heightMap,
                            metersPerPixel,
                            useTiltedPlane: ShouldUseTiltedRoundaboutPlane(network));

                        TerrainCreationLogger.Current?.InfoFileOnly($"  Processed {roundaboutHarmonizationResult.RoundaboutsProcessed} roundabout(s), " +
                                           $"modified {roundaboutHarmonizationResult.RingCrossSectionsModified} ring cross-sections, " +
                                           $"blended {roundaboutHarmonizationResult.ConnectingRoadCrossSectionsBlended} connecting road cross-sections");
                    }

                    perfLog?.Timing($"Roundabout elevation harmonization: {sw.Elapsed.TotalSeconds:F2}s");
                }
            } // end first-iteration-only phases

            // Phase 3: Harmonize junctions
            // Junction detection was already performed in Phase 1.8 (WI-5: early detection).
            // Phase 2.6 may have added roundabout junctions afterward — merge them here on the first iteration.
            // IMPORTANT: Banking-aware - uses edge elevations for banked roads when calculating connection points
            if (shouldHarmonize)
            {
                perfLog?.LogSection($"Phase 3: Junction Harmonization{iterationLabel}");
                TerrainLogger.Info($"Phase 3: Harmonizing junctions{iterationLabel}...");
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"  Cross-material harmonization: {enableCrossMaterialHarmonization && roadMaterials.Count > 1}");
                sw.Restart();

                if (isFirstIteration)
                {
                    // Phase 2.6 may have added roundabout junctions to network.Junctions AFTER
                    // Phase 1.8's early detection. Use RestoreRoundaboutJunctions to exclude
                    // regular junctions that overlap with roundabout junction positions, preventing
                    // duplicate processing and preserving the smooth roundabout blend.
                    var roundaboutJunctions = network.Junctions
                        .Where(j => j.Type == JunctionType.Roundabout)
                        .ToList();

                    if (roundaboutJunctions.Count > 0)
                    {
                        RestoreRoundaboutJunctions(network, roundaboutJunctions);
                    }
                }

                // Capture the natural (terrain-following) elevations and bank angles
                // BEFORE the harmonizer modifies them. The unified blender needs these as the
                // "natural profile" to blend junction constraints against.
                var originalElevations = network.CrossSections
                    .Where(cs => !float.IsNaN(cs.TargetElevation))
                    .ToDictionary(cs => cs.Index, cs => cs.TargetElevation);
                var originalBankAngles = network.CrossSections
                    .ToDictionary(cs => cs.Index, cs => cs.BankAngleRadians);

                // Run the harmonizer for junction detection + classification + elevation computation.
                lastHarmonizationResult = _junctionHarmonizer.HarmonizeNetwork(
                    network, heightMap, metersPerPixel,
                    skipDetection: !isFirstIteration);

                // Restore original elevations and reset harmonizer side effects before
                // unified blending — the harmonizer modified TargetElevation, but the unified
                // blender needs the natural terrain-following profile to compute correct deltas.
                foreach (var cs in network.CrossSections)
                {
                    if (originalElevations.TryGetValue(cs.Index, out var origElev))
                        cs.TargetElevation = origElev;
                    if (originalBankAngles.TryGetValue(cs.Index, out var origBank))
                        cs.BankAngleRadians = origBank;
                }

                // Apply the unified profile blender — handles all propagation
                // (elevation blending, banking adaptation, edge constraints) in one system.
                var unifiedResult = _unifiedProfileBlender.ApplyUnifiedProfiles(
                    network, originalElevations, originalBankAngles, heightMap, metersPerPixel);

                // Update harmonization result for convergence checking
                if (lastHarmonizationResult != null)
                {
                    lastHarmonizationResult.ModifiedCrossSections = unifiedResult.ModifiedCrossSections;
                }

                perfLog?.Timing($"UnifiedJunctionProfiles: {sw.Elapsed.TotalSeconds:F2}s, " +
                                $"modified {unifiedResult.ModifiedCrossSections} cross-sections, " +
                                $"{unifiedResult.ConstraintsComputed} constraints");

                // Check convergence
                var maxCorrection = lastHarmonizationResult?.MaxElevationChange ?? 0f;
                TerrainLogger.Info($"  Iteration {iteration + 1}: max elevation correction = {maxCorrection:F3}m");

                if (maxCorrection < convergenceThresholdMeters)
                {
                    TerrainLogger.Info($"  Converged after {iteration + 1} iteration(s)");
                    break;
                }

                if (maxCorrection > previousMaxCorrection * 0.9f && !isFirstIteration)
                {
                    TerrainLogger.Info($"  Not improving, stopping after {iteration + 1} iteration(s)");
                    break;
                }

                previousMaxCorrection = maxCorrection;
            }
            else
            {
                TerrainCreationLogger.Current?.InfoFileOnly("Phase 3: Junction harmonization skipped (no materials have it enabled)");
                break; // No harmonization = no iteration needed
            }
        } // end iteration loop

        // §3 no-blend ordering fix: the iteration loop snapshots each junction's HarmonizedElevation +
        // terminating target from the through road's PRE-affine Z, then sinks the through road — leaving a
        // junction-fill bump and a seam step (node 282534707). Recompute both from the SETTLED through-road
        // Z now that the loop has converged. Runs BEFORE the diagnostic below so the [NO-BLEND DIAG]/OWN
        // dump reflects the corrected state. Affine ThroughRoad leveling is the unconditional no-blend path.
        var retargeted = RetargetTerminatingRoadsToSettledThrough(network);
        if (retargeted > 0)
            TerrainCreationLogger.Current?.Detail(
                $"[NO-BLEND] §3 retarget: re-leveled {retargeted} terminating spline(s) + harmonized to settled through-road Z");

        // §4: warp terminating roads' banking onto the through surface near junctions. Runs AFTER §3 so it
        // inherits the flush centerline and only adds bank.
        var bankMatched = MatchTerminatingBankingToThroughSurface(network);
        if (bankMatched > 0)
            TerrainCreationLogger.Current?.Detail(
                $"[NO-BLEND] §4 banking match: warped {bankMatched} terminating cross-section(s) onto the through surface");

        // Ramp: locally weld each steep connector's longitudinal grade so it meets the through road
        // "plain" (co-planar) at the seam. Runs LAST so it inherits §3's flush centerline + §4's matched
        // banking; nothing re-flattens before Phase 4 paints from these values.
        var eased = EaseConnectorGradeToThroughSurface(network);
        if (eased > 0)
            TerrainCreationLogger.Current?.Detail(
                $"[NO-BLEND] ramp: eased {eased} connector grade(s) to the through surface at the seam");

        // Edge/centerline desync fix: re-derive EVERY non-roundabout cross-section's edges from its CURRENT
        // (TargetElevation, BankAngleRadians). The post-loop affine passes above (§3) moved through-roads'
        // mid-spline centerlines after the blender's last Step-4 edge derivation, and §4/ramp only refresh
        // edges on terminating/connector CSes — leaving through-road mid-junction CSes with stale, lower edges
        // (the painted core floats up while the shoulder/embankment reads the old edge → a raised-shoulder
        // step). Runs LAST so it captures all centerline moves + banking. See the method doc and
        // ai_docs/no_blend_zones/2026-05-31-edge-elevation-desync-bug.md.
        var edgesReconciled = ReconcileEdgeElevationsToCenterline(network);
        if (edgesReconciled > 0)
            TerrainCreationLogger.Current?.Detail(
                $"[NO-BLEND] edge reconcile: re-derived {edgesReconciled} cross-section edge(s) from the settled centerline + bank");

        // Export junction debug image after final iteration
        if (lastHarmonizationResult != null)
        {
            ExportJunctionDebugImageIfRequested(network, lastHarmonizationResult, heightMap, metersPerPixel, roadMaterials);
        }

        // ====================================================================================
        // NO-BLEND DIAGNOSTIC (gated by EmitNoBlendDiagnostics).
        // Dumps, for every non-roundabout junction, the state the road profiles are in RIGHT
        // BEFORE Phase 4 terrain blending builds the embankment. `heightMap` here is still the
        // raw/input terrain (Phase 4 writes its result into `smoothedHeightMap`), so the
        // per-contributor delta = roadZ - terrainZ is the true road-float at that point.
        //   * contributors DISAGREE (different roadZ at the same junction) => a step is built.
        //   * contributors AGREE but all delta>>0  => road floats above terrain => embankment ramp.
        // Grep the log for "[NO-BLEND DIAG]".
        // ====================================================================================
        if (EmitNoBlendDiagnostics)
        {
            var diagMapHeight = heightMap.GetLength(0);
            var diagMapWidth = heightMap.GetLength(1);
            var diagCount = 0;
            // Through-roads that sit far from the point terrain at a junction — profile them below.
            var suspectSplines = new Dictionary<int, ParameterizedRoadSpline>();
            TerrainCreationLogger.Current?.Detail("=== [NO-BLEND DIAG] Junction dump (state before Phase 4 terrain blending) ===");
            foreach (var j in network.Junctions)
            {
                if (j.Type == JunctionType.Roundabout) continue;
                diagCount++;
                var terrainAtCenter = SampleTerrainBilinear(
                    heightMap, j.Position.X, j.Position.Y, metersPerPixel, diagMapWidth, diagMapHeight);
                // OSM node(s) of this junction = the endpoint nodes the contributors share here.
                var junctionOsmNodes = j.Contributors
                    .Where(c => c.IsEndpoint)
                    .Select(c => c.IsSplineStart ? c.Spline.StartOsmNodeId : c.Spline.EndOsmNodeId)
                    .Where(n => n.HasValue)
                    .Select(n => n!.Value)
                    .Distinct()
                    .ToList();
                var osmNodeStr = junctionOsmNodes.Count > 0
                    ? string.Join(",", junctionOsmNodes)
                    : "none";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(
                    $"[NO-BLEND DIAG] J#{j.JunctionId} type={j.Type} osmNode={osmNodeStr} " +
                    $"pos=({j.Position.X:F1},{j.Position.Y:F1}) " +
                    $"terrainZ@center={terrainAtCenter:F2} harmonized={j.HarmonizedElevation:F2} " +
                    $"pinned={j.IsPinned} excluded={j.IsExcluded} contributors={j.Contributors.Count}");
                // §4/§5 — collect through (continuous) vs terminating contributors so we can compare
                // their junction surfaces (Z/slope/bank) and predict the mask-ownership winner below.
                var throughContribs = new List<(ParameterizedRoadSpline Spline, UnifiedCrossSection Cs, float Slope)>();
                var termContribs = new List<(ParameterizedRoadSpline Spline, UnifiedCrossSection Cs, float Slope)>();
                foreach (var c in j.Contributors)
                {
                    var roadZ = c.CrossSection.TargetElevation;
                    var terrainAtPt = SampleTerrainBilinear(
                        heightMap, c.CrossSection.CenterPoint.X, c.CrossSection.CenterPoint.Y,
                        metersPerPixel, diagMapWidth, diagMapHeight);
                    var delta = roadZ - terrainAtPt;
                    var role = c.IsEndpoint ? (c.IsSplineStart ? "ENDPOINT(start)" : "ENDPOINT(end)") : "THROUGH";
                    // Flag through-roads that deviate >1.5m from point terrain for full-profile analysis.
                    if (!c.IsEndpoint && MathF.Abs(delta) > 1.5f)
                        suspectSplines[c.Spline.SplineId] = c.Spline;
                    // §4 surface signals at the junction cross-section: longitudinal grade + banking + edges.
                    var contribSlope = ComputeLongitudinalSlope(network, c.Spline.SplineId, c.CrossSection);
                    var bankDeg = c.CrossSection.BankAngleRadians * 180f / MathF.PI;
                    if (c.IsEndpoint)
                        termContribs.Add((c.Spline, c.CrossSection, contribSlope));
                    else
                        throughContribs.Add((c.Spline, c.CrossSection, contribSlope));
                    // For a THROUGH road no endpoint node sits here; show both ends for traceability.
                    var contribOsm = c.IsEndpoint
                        ? (c.IsSplineStart ? c.Spline.StartOsmNodeId : c.Spline.EndOsmNodeId)?.ToString() ?? "none"
                        : $"{c.Spline.StartOsmNodeId?.ToString() ?? "?"}..{c.Spline.EndOsmNodeId?.ToString() ?? "?"}";
                    var waysStr = c.Spline.OsmWayIds.Count > 0 ? string.Join(",", c.Spline.OsmWayIds) : "none";
                    sb.AppendLine(
                        $"[NO-BLEND DIAG]   spline={c.Spline.SplineId} role={role} node={contribOsm} ways={waysStr} prio={c.Spline.Priority} " +
                        $"mat={c.Spline.MaterialName} roadZ={roadZ:F2} terrainZ@pt={terrainAtPt:F2} delta={delta:+0.00;-0.00} " +
                        $"slope={contribSlope:+0.000;-0.000} bank={bankDeg:+0.0;-0.0}deg " +
                        $"Ledge={c.CrossSection.LeftEdgeElevation:F2} Redge={c.CrossSection.RightEdgeElevation:F2} len={c.Spline.TotalLengthMeters:F0}m");
                }

                // §4 surface-match + §5 mask-ownership prediction, per through×terminating pair.
                //   §4: dZ/dSlope/dBank quantify how far the terminating road is from the through-road
                //       surface in centerline Z, longitudinal grade, and banking (should all be ~0 if the
                //       terminating road sat flush on the through surface).
                //   §5: maskWinner = ContestedPixelResolver.CompareForOverlap (geometric + deterministic:
                //       priority → length → splineId). If TERMINATING wins, it can claim through-road
                //       surface pixels and stamp its (possibly floating) elevation there → the bumpy core.
                foreach (var (tSpline, tCs, tSlope) in throughContribs)
                foreach (var (eSpline, eCs, eSlope) in termContribs)
                {
                    var dZ = eCs.TargetElevation - tCs.TargetElevation;
                    var dSlope = eSlope - tSlope;
                    var dBank = (eCs.BankAngleRadians - tCs.BankAngleRadians) * 180f / MathF.PI;
                    var thMeta = new SplineOverlapMetadata(tSpline.SplineId, tSpline.Priority, tSpline.TotalLengthMeters);
                    var teMeta = new SplineOverlapMetadata(eSpline.SplineId, eSpline.Priority, eSpline.TotalLengthMeters);
                    var cmp = ContestedPixelResolver.CompareForOverlap(teMeta, thMeta); // +1 = terminating wins
                    var tier = tSpline.Priority != eSpline.Priority ? "priority"
                        : MathF.Abs(tSpline.TotalLengthMeters - eSpline.TotalLengthMeters) > 0.001f ? "length"
                        : "splineId";
                    var winner = cmp > 0 ? "TERMINATING(bug?)" : cmp < 0 ? "through(ok)" : "tie";
                    sb.AppendLine(
                        $"[NO-BLEND OWN]   through={tSpline.SplineId} term={eSpline.SplineId} " +
                        $"dZ={dZ:+0.00;-0.00} dSlope={dSlope:+0.000;-0.000} dBank={dBank:+0.0;-0.0}deg | " +
                        $"prio th={tSpline.Priority} te={eSpline.Priority} len th={tSpline.TotalLengthMeters:F0} te={eSpline.TotalLengthMeters:F0} " +
                        $"decidedBy={tier} maskWinner={winner}");
                }
                TerrainCreationLogger.Current?.Detail(sb.ToString().TrimEnd());
            }
            TerrainCreationLogger.Current?.Detail($"=== [NO-BLEND DIAG] end ({diagCount} non-roundabout junctions) ===");

            // ---- Suspect through-road profiles: smoothed road vs point terrain vs ±75m terrain mean.
            // KEY TEST: dMean = roadZ - (local 75m mean of terrain along the spline).
            //   dMean ~ 0 along the body  => honest low-pass (road sits at the terrain mean; the road is
            //                                only "below" the POINT terrain because the point is on a crest).
            //   dMean ~ -2.5 consistently => the smoother is biased BELOW even the local mean = a bug to hunt.
            const float localMeanRadiusM = 75f;
            TerrainCreationLogger.Current?.Detail(
                $"=== [NO-BLEND PROFILE] {suspectSplines.Count} suspect through-road(s); " +
                $"dPoint=roadZ-terrainZ@pt, dMean=roadZ-terrainMean(±{localMeanRadiusM:F0}m) ===");
            foreach (var (sid, spline) in suspectSplines)
            {
                var xs = network.GetCrossSectionsForSpline(sid).OrderBy(s => s.LocalIndex).ToList();
                var n = xs.Count;
                if (n < 2) continue;

                var s = new float[n];   // cumulative distance along spline
                var tz = new float[n];  // raw terrain Z at each cross-section center
                var rz = new float[n];  // FINAL road Z (TargetElevation, post-correction)
                var raw = new float[n]; // pure chain low-pass Z, BEFORE affine/anchoring correction
                for (var i = 0; i < n; i++)
                {
                    rz[i] = xs[i].TargetElevation;
                    raw[i] = _preCorrectionElevations.TryGetValue((sid, xs[i].LocalIndex), out var rv) ? rv : float.NaN;
                    tz[i] = SampleTerrainBilinear(
                        heightMap, xs[i].CenterPoint.X, xs[i].CenterPoint.Y, metersPerPixel, diagMapWidth, diagMapHeight);
                    s[i] = i == 0 ? 0f : s[i - 1] + Vector2.Distance(xs[i - 1].CenterPoint, xs[i].CenterPoint);
                }

                // Local ±75m terrain mean (the value an honest wide low-pass should roughly reproduce).
                var localMean = new float[n];
                for (var i = 0; i < n; i++)
                {
                    float sum = 0f; var cnt = 0;
                    for (var j = i; j >= 0 && s[i] - s[j] <= localMeanRadiusM; j--)
                    { if (!float.IsNaN(tz[j])) { sum += tz[j]; cnt++; } }
                    for (var j = i + 1; j < n && s[j] - s[i] <= localMeanRadiusM; j++)
                    { if (!float.IsNaN(tz[j])) { sum += tz[j]; cnt++; } }
                    localMean[i] = cnt > 0 ? sum / cnt : float.NaN;
                }

                // Full-resolution summary stats. The two decisive signals:
                //   dRaw  = lowpass - terrain  : does the PURE low-pass follow terrain? (~0 on linear ground)
                //   dCorr = final  - lowpass   : how far the affine/anchoring correction moved the road.
                float dpSum = 0, dpMin = float.MaxValue, dpMax = float.MinValue;
                float drSum = 0, drMin = float.MaxValue, drMax = float.MinValue;
                float dcSum = 0, dcMin = float.MaxValue, dcMax = float.MinValue;
                var stat = 0;
                for (var i = 0; i < n; i++)
                {
                    if (float.IsNaN(tz[i]) || float.IsNaN(rz[i]) || float.IsNaN(raw[i])) continue;
                    var dp = rz[i] - tz[i]; var dr = raw[i] - tz[i]; var dc = rz[i] - raw[i];
                    dpSum += dp; dpMin = MathF.Min(dpMin, dp); dpMax = MathF.Max(dpMax, dp);
                    drSum += dr; drMin = MathF.Min(drMin, dr); drMax = MathF.Max(drMax, dr);
                    dcSum += dc; dcMin = MathF.Min(dcMin, dc); dcMax = MathF.Max(dcMax, dc);
                    stat++;
                }
                var nodeStr = $"{spline.StartOsmNodeId?.ToString() ?? "?"}..{spline.EndOsmNodeId?.ToString() ?? "?"}";
                var waysStr = spline.OsmWayIds.Count > 0 ? string.Join(",", spline.OsmWayIds) : "none";
                TerrainCreationLogger.Current?.Detail(
                    $"[NO-BLEND PROFILE] spline={sid} node={nodeStr} ways={waysStr} len={s[n - 1]:F0}m n={n} " +
                    $"dPoint(mean/min/max)={dpSum / Math.Max(stat, 1):+0.00;-0.00}/{dpMin:+0.00;-0.00}/{dpMax:+0.00;-0.00} " +
                    $"dRaw(lowpass-terr mean/min/max)={drSum / Math.Max(stat, 1):+0.00;-0.00}/{drMin:+0.00;-0.00}/{drMax:+0.00;-0.00} " +
                    $"dCorr(final-lowpass mean/min/max)={dcSum / Math.Max(stat, 1):+0.00;-0.00}/{dcMin:+0.00;-0.00}/{dcMax:+0.00;-0.00}");

                // Decimated per-point rows (<= ~250 rows/spline) so the shape is visible without flooding the log.
                var step = Math.Max(1, n / 250);
                var pb = new System.Text.StringBuilder();
                for (var i = 0; i < n; i += step)
                {
                    var dr = raw[i] - tz[i]; var dc = rz[i] - raw[i];
                    pb.AppendLine(
                        $"[NO-BLEND PROFILE]   s={s[i]:F1} terr={tz[i]:F2} lowpass={raw[i]:F2} final={rz[i]:F2} mean75={localMean[i]:F2} " +
                        $"dRaw={dr:+0.00;-0.00} dCorr={dc:+0.00;-0.00}");
                }
                TerrainCreationLogger.Current?.Detail(pb.ToString().TrimEnd());
            }
            TerrainCreationLogger.Current?.Detail("=== [NO-BLEND PROFILE] end ===");
        }

        // ====================================================================================
        // NO-BLEND ROUNDABOUT DIAGNOSTIC (gated by EmitNoBlendDiagnostics).
        // Roundabout connectors get NONE of the §3/§4 no-blend treatment (both early-continue on
        // JunctionType.Roundabout, and the parent junction is IsExcluded by the legacy
        // RoundaboutElevationHarmonizer). The [NO-BLEND DIAG] dump above also skips roundabouts, so
        // there is no roundabout data in the log today. This block fills that gap.
        //
        // Topology (verified NetworkJunctionDetector.CreateRoundaboutJunction): a Roundabout junction
        // is a T-junction — the RING is the continuous (through) contributor, the CONNECTOR is the
        // terminating endpoint. The connector's FAR end is a separate junction with the ongoing road.
        //
        // For each roundabout connector dump: ring Z (+ ring uniformity), the ring-side seam, the
        // far-side seam (and the ongoing through road's Z there), connector length + per-CS profile
        // (a bump shows as a non-monotone TargetElevation), and bank/slope at both seams. Decide:
        //   * ring Z != ongoing Z (ends DISAGREE)  => targeting: connector spans a step over ~20 m.
        //   * ends agree but middle bulges          => a transition/blend is fighting the linear tilt.
        //   * bump in the long ongoing neighbor      => constraint propagated back through the short seg.
        // Grep "[NO-BLEND RAB]".
        // ====================================================================================
        if (EmitNoBlendDiagnostics)
        {
            var rabMapHeight = heightMap.GetLength(0);
            var rabMapWidth = heightMap.GetLength(1);

            // Junctions where a given spline appears as an ENDPOINT contributor — used to find a
            // connector's far-end junction (and thus the ongoing through road on the other side).
            var endpointJunctions = new Dictionary<int, List<NetworkJunction>>();
            foreach (var j in network.Junctions)
            foreach (var c in j.Contributors.Where(c => c.IsEndpoint))
            {
                if (!endpointJunctions.TryGetValue(c.Spline.SplineId, out var list))
                    endpointJunctions[c.Spline.SplineId] = list = new List<NetworkJunction>();
                list.Add(j);
            }

            float TerrZ(Vector2 p) => SampleTerrainBilinear(
                heightMap, p.X, p.Y, metersPerPixel, rabMapWidth, rabMapHeight);
            string Deg(float rad) => $"{rad * 180f / MathF.PI:+0.0;-0.0}deg";

            var rabJunctions = network.Junctions.Where(j => j.Type == JunctionType.Roundabout).ToList();
            TerrainCreationLogger.Current?.Detail(
                $"=== [NO-BLEND RAB] Roundabout connector dump ({rabJunctions.Count} roundabout junction(s)) ===");

            foreach (var j in rabJunctions)
            {
                var ring = j.Contributors.FirstOrDefault(c => c.IsContinuous);
                var conn = j.Contributors.FirstOrDefault(c => c.IsEndpoint);
                if (ring == null || conn == null)
                {
                    TerrainCreationLogger.Current?.Detail(
                        $"[NO-BLEND RAB] J#{j.JunctionId} MALFORMED contributors={j.Contributors.Count} " +
                        $"(ring={(ring != null)} conn={(conn != null)}) — skipped");
                    continue;
                }

                // Ring uniformity across the whole ring spline.
                var ringXs = network.GetCrossSectionsForSpline(ring.Spline.SplineId)
                    .Where(s => !float.IsNaN(s.TargetElevation)).ToList();
                float ringMin = float.MaxValue, ringMax = float.MinValue, ringSum = 0f;
                foreach (var rcs in ringXs)
                {
                    ringMin = MathF.Min(ringMin, rcs.TargetElevation);
                    ringMax = MathF.Max(ringMax, rcs.TargetElevation);
                    ringSum += rcs.TargetElevation;
                }
                var ringMean = ringXs.Count > 0 ? ringSum / ringXs.Count : float.NaN;
                var ringSpread = ringXs.Count > 0 ? ringMax - ringMin : float.NaN;

                // Connector profile (ordered along the spline).
                var connXs = network.GetCrossSectionsForSpline(conn.Spline.SplineId)
                    .OrderBy(s => s.LocalIndex).ToList();
                var connLen = 0f;
                for (var i = 1; i < connXs.Count; i++)
                    connLen += Vector2.Distance(connXs[i - 1].CenterPoint, connXs[i].CenterPoint);

                // The RING's own Z at the seam (the through-road authority) and the CONNECTOR's
                // ring-side endpoint Z (what currently floats). These two differing IS the step.
                var ringSeamZ = ring.CrossSection.TargetElevation;
                var connRingEndCs = conn.CrossSection;
                var connRingEndZ = connRingEndCs.TargetElevation;
                var ringSeamSlope = ComputeLongitudinalSlope(network, conn.Spline.SplineId, connRingEndCs);

                // Far-side seam = the opposite end of the connector spline.
                var farCs = conn.IsSplineStart ? connXs.LastOrDefault() : connXs.FirstOrDefault();
                var farSlope = farCs != null
                    ? ComputeLongitudinalSlope(network, conn.Spline.SplineId, farCs)
                    : float.NaN;

                // Find the connector's far-end junction (any OTHER junction where it is an endpoint)
                // and read its through (ongoing) road's Z there.
                NetworkJunction? farJ = null;
                if (endpointJunctions.TryGetValue(conn.Spline.SplineId, out var connJs))
                    farJ = connJs.FirstOrDefault(x => x.JunctionId != j.JunctionId);
                var ongoing = farJ?.Contributors.FirstOrDefault(c => c.IsContinuous)
                              ?? farJ?.Contributors.FirstOrDefault(c =>
                                  c.IsEndpoint && c.Spline.SplineId != conn.Spline.SplineId);

                var ongoingZ = ongoing?.CrossSection.TargetElevation ?? float.NaN;
                // RING-SEAM step: how far the connector's ring-side endpoint floats off the ring (the
                // step we want to close). AUTHORITY step: ring Z vs the ongoing road's Z — if these AGREE,
                // a fixed connector just tilts gently between two near-equal Zs (no steep ramp).
                var ringSeamStep = connRingEndZ - ringSeamZ;
                var authorityStep = float.IsNaN(ongoingZ) ? float.NaN : ringSeamZ - ongoingZ;
                var shortFlag = connLen < 30f ? " SHORT(<30m)" : "";

                // Connector longitudinal grade (end-to-end), civil target ≤4-5% (max 7% drivable).
                var connGrade = connLen > 0.01f
                    ? MathF.Abs(connRingEndZ - (farCs?.TargetElevation ?? connRingEndZ)) / connLen
                    : 0f;
                var gradeFlag = connGrade > 0.07f ? " >7%!" : "";

                var rab = new System.Text.StringBuilder();
                rab.AppendLine(
                    $"[NO-BLEND RAB] J#{j.JunctionId} pos=({j.Position.X:F1},{j.Position.Y:F1}) excluded={j.IsExcluded} " +
                    $"connector=spline {conn.Spline.SplineId} ({(conn.IsSplineStart ? "start" : "end")}@ring) " +
                    $"len={connLen:F1}m n={connXs.Count}{shortFlag}");
                rab.AppendLine(
                    $"[NO-BLEND RAB]   RING spline={ring.Spline.SplineId} ringZ@seam={ringSeamZ:F2} " +
                    $"mean={ringMean:F2} spread(max-min)={ringSpread:F2} (uniform if ~0) " +
                    $"terrainZ@ringSeam={TerrZ(connRingEndCs.CenterPoint):F2}");
                rab.AppendLine(
                    $"[NO-BLEND RAB]   CONNECTOR ringEndZ={connRingEndZ:F2} RING-SEAM-STEP(connEnd-ring)={ringSeamStep:+0.00;-0.00} " +
                    $"grade={connGrade * 100f:F1}%{gradeFlag} " +
                    $"bank={Deg(connRingEndCs.BankAngleRadians)} slope={ringSeamSlope:+0.000;-0.000}");
                if (farCs != null)
                    rab.AppendLine(
                        $"[NO-BLEND RAB]   FAR  connectorFarZ={farCs.TargetElevation:F2} terrainZ@far={TerrZ(farCs.CenterPoint):F2} " +
                        $"bank={Deg(farCs.BankAngleRadians)} slope={farSlope:+0.000;-0.000} " +
                        $"farJunction={(farJ != null ? "J#" + farJ.JunctionId + " type=" + farJ.Type : "NONE")}");
                rab.AppendLine(
                    ongoing != null
                        ? $"[NO-BLEND RAB]   ONGOING spline={ongoing.Spline.SplineId} " +
                          $"role={(ongoing.IsContinuous ? "through" : "endpoint")} ongoingZ={ongoingZ:F2} " +
                          $"terrainZ@ongoing={TerrZ(ongoing.CrossSection.CenterPoint):F2} bank={Deg(ongoing.CrossSection.BankAngleRadians)}"
                        : "[NO-BLEND RAB]   ONGOING none (connector far end is a dead-end / continuation / unmatched)");
                rab.AppendLine(
                    $"[NO-BLEND RAB]   AUTHORITY-STEP ringZ-ongoingZ={authorityStep:+0.00;-0.00} " +
                    "(authorities AGREE => fixed connector tilts gently; DISAGREE => connector spans a real step)");

                // Per-CS connector profile (short → dump all): a bump shows as a non-monotone roadZ.
                var d = 0f;
                for (var i = 0; i < connXs.Count; i++)
                {
                    if (i > 0) d += Vector2.Distance(connXs[i - 1].CenterPoint, connXs[i].CenterPoint);
                    var cs = connXs[i];
                    rab.AppendLine(
                        $"[NO-BLEND RAB]     cs[{cs.LocalIndex}] s={d:F1} roadZ={cs.TargetElevation:F2} " +
                        $"terrainZ={TerrZ(cs.CenterPoint):F2} bank={Deg(cs.BankAngleRadians)} " +
                        $"blended={cs.IsRoundaboutBlended}");
                }
                TerrainCreationLogger.Current?.Detail(rab.ToString().TrimEnd());
            }
            TerrainCreationLogger.Current?.Detail("=== [NO-BLEND RAB] end ===");
        }

        // ====================================================================================
        // END NO-BLEND DIAGNOSTIC
        // ====================================================================================

        // Phase 4: Apply terrain blending (single pass)
        perfLog?.LogSection("Phase 4: Terrain Blending");
        TerrainLogger.Info("Phase 4: Applying protected terrain blending...");
        sw.Restart();
        smoothedHeightMap = _terrainBlender.BlendNetworkWithTerrain(heightMap, network, metersPerPixel);
        perfLog?.Timing($"BlendNetworkWithTerrain: {sw.Elapsed.TotalSeconds:F2}s");

        // Apply post-processing smoothing if enabled
        if (roadMaterials.Any(m => m.RoadParameters?.EnablePostProcessingSmoothing == true))
        {
            sw.Restart();
            _terrainBlender.ApplyPostProcessingSmoothing(smoothedHeightMap, network, metersPerPixel);
            perfLog?.Timing($"PostProcessingSmoothing: {sw.Elapsed.TotalSeconds:F2}s");
        }

        } // end of else (non-paint-only elevation phases)

        // Re-add paint-only splines before painting phase
        if (paintOnlySplines.Count > 0)
        {
            network.Splines.AddRange(paintOnlySplines);
            network.CrossSections.AddRange(paintOnlyCrossSections);
            TerrainLogger.Info($"  Re-added {paintOnlySplines.Count} paint-only spline(s) for painting phase");
        }

        // Phase 5: Paint material layers
        perfLog?.LogSection("Phase 5: Material Painting");
        TerrainLogger.Info("Phase 5: Painting material layers...");
        sw.Restart();
        var materialLayers = _materialPainter.PaintMaterials(network, size, size, metersPerPixel);
        perfLog?.Timing($"PaintMaterials: {sw.Elapsed.TotalSeconds:F2}s");

        // Calculate statistics
        var statistics = CalculateStatistics(heightMap, smoothedHeightMap, metersPerPixel);
        var deltaMap = CalculateDeltaMap(heightMap, smoothedHeightMap);

        // Export debug images if requested
        ExportDebugImagesIfRequested(network, smoothedHeightMap, heightMap, metersPerPixel, roadMaterials);

        totalSw.Stop();
        perfLog?.Timing($"=== UnifiedRoadSmoother TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s ===");
        perfLog?.LogMemoryUsage("After unified road smoothing");

        TerrainLogger.Info($"=== UNIFIED SMOOTHING COMPLETE ({totalSw.Elapsed.TotalSeconds:F2}s) ===");

        // Build result
        return new UnifiedSmoothingResult
        {
            ModifiedHeightMap = smoothedHeightMap,
            MaterialLayers = materialLayers,
            Network = network,
            Statistics = statistics,
            DeltaMap = deltaMap
        };
    }

    /// <summary>
    ///     Determines if junction harmonization should be performed.
    /// </summary>
    private bool ShouldHarmonize(List<MaterialDefinition> roadMaterials)
    {
        // At least one material must have harmonization enabled
        return roadMaterials.Any(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.EnableJunctionHarmonization == true);
    }


    /// <summary>
    ///     Determines if any materials have roundabout data that needs processing.
    /// </summary>
    private static bool HasRoundaboutsInNetwork(UnifiedRoadNetwork network, List<MaterialDefinition> roadMaterials)
    {
        // Check if any material has roundabout detection enabled AND has roundabout data
        return roadMaterials.Any(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.EnableRoundaboutDetection == true &&
            m.RoadParameters?.RoundaboutProcessingResult?.RoundaboutInfos.Count > 0);
    }

    /// <summary>
    ///     Identifies roundabout splines in the network and marks them with IsRoundabout = true.
    ///     This must be called BEFORE banking pre-calculation to ensure roundabouts don't get banked.
    /// </summary>
    private static void IdentifyRoundaboutSplines(
        List<MaterialDefinition> roadMaterials,
        UnifiedRoadNetwork network)
    {
        var perfLog = TerrainCreationLogger.Current;

        // First pass: Match roundabouts from RoundaboutProcessingResult
        foreach (var material in roadMaterials)
        {
            var roundaboutResult = material.RoadParameters?.RoundaboutProcessingResult;
            if (roundaboutResult == null || roundaboutResult.RoundaboutInfos.Count == 0)
                continue;

            perfLog?.Detail(
                $"Processing roundabout infos for material '{material.MaterialName}': {roundaboutResult.RoundaboutInfos.Count} roundabout(s)");

            foreach (var info in roundaboutResult.RoundaboutInfos)
            {
                if (!info.IsValid)
                {
                    perfLog?.Detail($"  Roundabout {info.OriginalId}: Invalid, skipping");
                    continue;
                }

                // Find and mark the matching spline as a roundabout
                var matchedSpline = FindMatchingRoundaboutSpline(network, material.MaterialName, info);
                if (matchedSpline != null)
                    perfLog?.Detail($"  Roundabout {info.OriginalId}: Matched to spline ID {matchedSpline.SplineId}");
                else
                    perfLog?.Detail($"  Roundabout {info.OriginalId}: No matching spline found!");
            }
        }

        // Second pass: Fallback detection for closed-loop splines that weren't matched
        // This catches roundabouts that exist in the network but weren't in RoundaboutProcessingResult
        // IMPORTANT: Skip bridge/tunnel splines — short structures (7-20m) have start≈end positions
        // that falsely trigger the closed-loop heuristic. Bridges are never roundabouts.
        var closedLoopTolerance = 15.0f; // meters
        foreach (var spline in network.Splines)
        {
            if (spline.IsRoundabout)
                continue; // Already marked

            // Bridges and tunnels are never roundabouts — skip them
            if (spline.IsStructure)
                continue;

            // Check if this is a closed loop
            var startEndDistance = Vector2.Distance(spline.StartPoint, spline.EndPoint);
            if (startEndDistance < closedLoopTolerance)
            {
                // This is a closed loop - mark it as a roundabout
                spline.IsRoundabout = true;
                perfLog?.Detail(
                    $"  Fallback detection: Spline ID {spline.SplineId} is a closed loop (start-end distance: {startEndDistance:F1}m) - marking as roundabout");
            }
        }

        // Log final count
        var totalRoundabouts = network.Splines.Count(s => s.IsRoundabout);
        perfLog?.Detail($"Total roundabout splines identified: {totalRoundabouts}");
    }

    /// <summary>
    ///     Collects roundabout processing results from all materials and maps them to the network.
    /// </summary>
    private static List<RoundaboutMerger.ProcessedRoundaboutInfo> CollectRoundaboutInfos(
        List<MaterialDefinition> roadMaterials,
        UnifiedRoadNetwork network)
    {
        var allInfos = new List<RoundaboutMerger.ProcessedRoundaboutInfo>();

        foreach (var material in roadMaterials)
        {
            var roundaboutResult = material.RoadParameters?.RoundaboutProcessingResult;
            if (roundaboutResult == null || roundaboutResult.RoundaboutInfos.Count == 0)
                continue;

            foreach (var info in roundaboutResult.RoundaboutInfos)
            {
                if (!info.IsValid)
                    continue;

                // Find the matching spline in the network by looking for splines from this material
                // that have similar center coordinates and radius
                var matchingSpline = FindMatchingRoundaboutSpline(network, material.MaterialName, info);
                if (matchingSpline != null)
                {
                    // Update the ProcessedRoundaboutInfo to reference the actual network spline ID
                    // This is necessary because spline IDs are assigned during network building,
                    // not during OSM processing
                    var updatedInfo = new RoundaboutMerger.ProcessedRoundaboutInfo
                    {
                        OriginalId = info.OriginalId,
                        SplineIndex = matchingSpline.SplineId,
                        Spline = matchingSpline.Spline,
                        CenterMeters = info.CenterMeters,
                        RadiusMeters = info.RadiusMeters,
                        Connections = info.Connections,
                        OriginalRoundabout = info.OriginalRoundabout
                    };

                    allInfos.Add(updatedInfo);
                    TerrainCreationLogger.Current?.Detail(
                        $"Mapped roundabout {info.OriginalId} to network spline {matchingSpline.SplineId}");
                }
                else
                {
                    TerrainLogger.Warning($"Could not find network spline for roundabout {info.OriginalId}");
                }
            }
        }

        return allInfos;
    }

    /// <summary>
    ///     Finds the network spline that matches a roundabout info by material and geometry.
    ///     Also marks the spline as a roundabout when found.
    /// </summary>
    private static ParameterizedRoadSpline? FindMatchingRoundaboutSpline(
        UnifiedRoadNetwork network,
        string materialName,
        RoundaboutMerger.ProcessedRoundaboutInfo info)
    {
        const float matchTolerance = 10.0f; // 10 meters tolerance

        // Look for closed-loop splines from this material near the roundabout center
        foreach (var spline in network.GetSplinesForMaterial(materialName))
        {
            // Check if this spline is a closed loop (start near end)
            if (Vector2.Distance(spline.StartPoint, spline.EndPoint) > matchTolerance)
                continue;

            // Check if the spline center is near the roundabout center
            var splineCrossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (splineCrossSections.Count < 4)
                continue;

            // Calculate the centroid of the spline
            var centroid = Vector2.Zero;
            foreach (var cs in splineCrossSections) centroid += cs.CenterPoint;
            centroid /= splineCrossSections.Count;

            if (Vector2.Distance(centroid, info.CenterMeters) < info.RadiusMeters * 2 + matchTolerance)
            {
                // Mark this spline as a roundabout so that banking is not applied to it
                spline.IsRoundabout = true;
                return spline;
            }
        }

        return null;
    }

    /// <summary>
    ///     Gets the roundabout connection radius from material settings, using the maximum value.
    /// </summary>
    private static float GetRoundaboutConnectionRadius(List<MaterialDefinition> roadMaterials)
    {
        var maxRadius = 10.0f; // Default

        foreach (var material in roadMaterials)
        {
            var junctionParams = material.RoadParameters?.JunctionHarmonizationParameters;
            if (junctionParams != null && junctionParams.RoundaboutConnectionRadiusMeters > maxRadius)
                maxRadius = junctionParams.RoundaboutConnectionRadiusMeters;
        }

        return maxRadius;
    }

    /// <summary>
    ///     Restores roundabout junctions that were cleared by Phase 3 junction detection.
    ///     Removes any regular junctions that overlap with roundabout junction positions
    ///     to prevent double-processing, then adds the roundabout junctions back.
    /// </summary>
    private static void RestoreRoundaboutJunctions(
        UnifiedRoadNetwork network,
        List<NetworkJunction> roundaboutJunctions)
    {
        // Use the roundabout connection radius as overlap threshold.
        // Regular Endpoint junctions at connecting road tips will be within this
        // distance of the corresponding roundabout junction position.
        const float overlapRadius = 15.0f;

        var roundaboutPositions = roundaboutJunctions
            .Select(j => j.Position)
            .ToList();

        // Remove regular junctions that overlap with roundabout junctions
        var regularToKeep = network.Junctions
            .Where(j => j.Type != JunctionType.Roundabout)
            .Where(j => !roundaboutPositions.Any(rp =>
                Vector2.Distance(j.Position, rp) < overlapRadius))
            .ToList();

        var removedCount = network.Junctions.Count - regularToKeep.Count;

        network.Junctions.Clear();
        network.Junctions.AddRange(regularToKeep);
        network.Junctions.AddRange(roundaboutJunctions);

        // Re-assign sequential junction IDs
        for (var i = 0; i < network.Junctions.Count; i++)
            network.Junctions[i].JunctionId = i;

        if (removedCount > 0 || roundaboutJunctions.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"  Restored {roundaboutJunctions.Count} roundabout junction(s), " +
                $"removed {removedCount} overlapping regular junction(s)");
    }

    /// <summary>
    ///     Calculates target elevations for all cross-sections in the network.
    ///     Uses network-chained elevation filtering: splines are chained through the junction graph
    ///     into long elevation runs, filtered as continuous profiles, then written back to individual
    ///     cross-sections. This prevents boundary artifacts at spline joints within chains.
    ///
    ///     Bridge/tunnel splines participate fully in elevation smoothing — their TargetElevation is
    ///     computed for use in ramp generation. IsExcluded=true controls terrain painting (Phase 4)
    ///     and DecalRoad generation, NOT elevation computation. The terrain under bridges remains untouched.
    /// </summary>
    private void CalculateNetworkElevations(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        bool reSmoothFromExisting = false)
    {
        var totalCalculated = 0;
        var elevationSmoother = _elevationCalculator as OptimizedElevationSmoother;

        // Group cross-sections by spline for efficient processing
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // Mark bridge/tunnel cross-sections as excluded (only on first iteration)
        // but DO NOT skip elevation computation — TargetElevation is virtual data for ramp generation.
        // IsExcluded=true means "don't modify terrain under bridges" — the heightmap stays at natural elevation.
        if (!reSmoothFromExisting)
        {
            foreach (var spline in network.Splines)
            {
                var parameters = spline.Parameters;
                if ((spline.IsBridge && parameters.ExcludeBridgesFromTerrain) ||
                    (spline.IsTunnel && parameters.ExcludeTunnelsFromTerrain))
                {
                    if (crossSectionsBySpline.TryGetValue(spline.SplineId, out var cs))
                    {
                        foreach (var c in cs)
                            c.IsExcluded = true;

                        TerrainCreationLogger.Current?.Detail(
                            $"Marked {(spline.IsBridge ? "bridge" : "tunnel")} spline {spline.SplineId} " +
                            $"as excluded from terrain ({cs.Count} cross-sections) — elevation still computed for ramp matching");
                    }
                }
            }
        }

        var bridgeMissingContinuationConnectors = network.Splines
            .Any(s => s.Parameters.EnableContinuationConnectorElevationBridging);

        // Build elevation graph and chains (once, reused across iterations)
        if (_cachedElevationChains == null)
        {
            _cachedElevationGraph = new NetworkElevationGraph();
            TerrainCreationLogger.Current?.Detail(
                $"[NO-BLEND CHAIN-BRIDGE] enabled={bridgeMissingContinuationConnectors}");
            _cachedElevationGraph.BuildFromNetwork(network, bridgeMissingContinuationConnectors);
            _cachedElevationChains = _cachedElevationGraph.BuildElevationChains();

            // NO-BLEND CHAIN diagnostic: for each Continuation joint, report whether its two
            // contributor splines landed in the SAME elevation chain. A SPLIT joint is smoothed as
            // two independent low-pass profiles, so nothing makes their elevations meet at the seam —
            // and Continuation joints get NO post-chain reconciliation (harmonizer no-ops them; the
            // §3/§4/ramp/edge passes all skip them) → the seam step / ditch. The degree-2 "always
            // chain through" rule in NetworkElevationGraph only fires for whichever chain reaches the
            // joint first, so greedy priority/length seeding can strand a Continuation neighbour.
            // Grep [NO-BLEND CHAIN]. Runs once (inside the cache-build block).
            if (EmitNoBlendDiagnostics)
                LogContinuationChainMembership(network, _cachedElevationChains, _cachedElevationGraph!);
        }

        var crossSectionSpacing = network.Splines.FirstOrDefault()?.Parameters.CrossSectionIntervalMeters ?? 0.5f;

        // Process each chain as one long profile
        if (elevationSmoother != null)
        {
            foreach (var chain in _cachedElevationChains)
            {
                // Concatenate cross-sections from all splines in chain order (with dedup)
                var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(
                    chain, crossSectionsBySpline, crossSectionSpacing);

                if (chainCS.Count == 0) continue;

                // Use the highest-priority spline's parameters for the chain
                var highestPriorityEdge = chain.Segments
                    .OrderByDescending(s => s.Edge.Priority)
                    .First().Edge;
                var chainParams = network.GetParametersForSpline(highestPriorityEdge.SplineId)
                    ?? new RoadSmoothingParameters();

                if (reSmoothFromExisting)
                {
                    elevationSmoother.ReSmoothChainFromExistingElevations(chainCS, chainParams);
                }
                else
                {
                    elevationSmoother.CalculateChainElevations(chainCS, chainParams, heightMap, metersPerPixel);
                }

                // Propagate elevation to cross-sections that were deduped at chain joints
                OptimizedElevationSmoother.PropagateToDeduped(chain);

                totalCalculated += chainCS.Count;
            }

            // Handle roundabout splines that were excluded from chaining — process per-spline
            foreach (var spline in network.Splines.Where(s => s.IsRoundabout))
            {
                if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                    continue;

                if (reSmoothFromExisting)
                {
                    _elevationCalculator.ReSmoothFromExistingElevations(crossSections, spline.Parameters);
                }
                else
                {
                    _elevationCalculator.CalculateTargetElevations(crossSections, spline.Parameters, heightMap,
                        metersPerPixel);
                }

                totalCalculated += crossSections.Count;
            }
        }
        else
        {
            // Fallback: legacy per-spline processing (when not using OptimizedElevationSmoother)
            foreach (var spline in network.Splines)
            {
                if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                    continue;

                if (reSmoothFromExisting)
                    _elevationCalculator.ReSmoothFromExistingElevations(crossSections, spline.Parameters);
                else
                    _elevationCalculator.CalculateTargetElevations(crossSections, spline.Parameters, heightMap,
                        metersPerPixel);

                totalCalculated += crossSections.Count;
            }
        }

        if (bridgeMissingContinuationConnectors && _cachedElevationGraph != null)
            PropagateMissingContinuationConnectorEndpointElevations(network, _cachedElevationGraph);

        // Phase-2 junction reconciliation (no-blend path, unconditional): affine junction leveling —
        // pin each terminating endpoint to its junction target and spread the error over the full
        // spline length (no local ramp). The targets are built once from the pre-correction smoothed
        // elevations so per-spline corrections don't couple.
        if (elevationSmoother != null)
        {
            // NO-BLEND DIAGNOSTIC: snapshot the pure chain low-pass before the correction below.
            // Only the [NO-BLEND PROFILE] dump consumes this, so skip it unless diagnostics are on.
            if (EmitNoBlendDiagnostics)
            {
                _preCorrectionElevations.Clear();
                foreach (var c in network.CrossSections)
                    _preCorrectionElevations[(c.OwnerSplineId, c.LocalIndex)] = c.TargetElevation;
            }

            var affineTargets = BuildEndpointTargetLookup(network);

            foreach (var spline in network.Splines)
            {
                if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                    continue;

                ApplyAffineLeveling(crossSections, spline.SplineId, affineTargets);
            }
        }

        var mode = reSmoothFromExisting ? "re-smoothed" : "calculated";
        TerrainCreationLogger.Current?.Detail(
            $"{mode.Substring(0, 1).ToUpperInvariant() + mode.Substring(1)} elevations for {totalCalculated} cross-sections " +
            $"({_cachedElevationChains?.Count ?? 0} chains)");
    }

    /// <summary>
    ///     NO-BLEND CHAIN diagnostic. For every <see cref="JunctionType.Continuation" /> junction,
    ///     logs whether its contributor splines share an elevation chain. A joint whose two splines
    ///     landed in DIFFERENT chains is smoothed as two independent low-pass profiles, so nothing
    ///     makes the elevations meet at the seam — and Continuation joints get no post-chain
    ///     reconciliation (harmonizer no-ops them; §3/§4/ramp/edge skip them) → the seam step / ditch.
    ///     Greedy priority/length seeding in <see cref="NetworkElevationGraph.BuildElevationChains" />
    ///     can strand a Continuation neighbour, so the degree-2 "always chain through" rule is not a
    ///     guarantee. Gated by <see cref="EmitNoBlendDiagnostics" />. Grep [NO-BLEND CHAIN].
    /// </summary>
    private static void LogContinuationChainMembership(
        UnifiedRoadNetwork network, List<ElevationChain> chains, NetworkElevationGraph graph)
    {
        // Map each spline to the chain it belongs to (+ each chain's segment count). Splines absent
        // from every chain (roundabout/paint-only/zero-CS) won't be present → reported as chain=NONE.
        var splineToChain = new Dictionary<int, int>();
        var chainSize = new Dictionary<int, int>();
        foreach (var chain in chains)
        {
            chainSize[chain.ChainId] = chain.Segments.Count;
            foreach (var (edge, _) in chain.Segments)
                splineToChain[edge.SplineId] = chain.ChainId;
        }

        var continuations = network.Junctions
            .Where(j => j.Type == JunctionType.Continuation)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"=== [NO-BLEND CHAIN] Continuation joint chain-membership ({continuations.Count} continuation junction(s)) ===");

        var splitCount = 0;
        foreach (var j in continuations)
        {
            var endpoints = j.Contributors.Where(c => c.IsEndpoint).ToList();
            var parts = new List<string>();
            var chainIds = new HashSet<int>();
            var anyMissing = false;

            foreach (var c in endpoints)
            {
                var sp = c.Spline;
                var role = c.IsSplineStart ? "start" : "end";
                var osm = c.IsSplineStart ? sp.StartOsmNodeId : sp.EndOsmNodeId;
                var ways = sp.OsmWayIds.Count > 0 ? string.Join(",", sp.OsmWayIds) : "none";
                // Definitive exclusion facts: resolves the DIAG-vs-CHAIN contradiction (a spline that
                // shows cross-sections in [NO-BLEND DIAG] but is chain=NONE must have been dropped from
                // BuildFromNetwork by exactly one of: IsRoundabout / IsPaintOnly / CS-count==0).
                var csCount = network.GetCrossSectionsForSpline(sp.SplineId).Count();
                var hasEdge = graph.GetEdgeForSpline(sp.SplineId) != null;
                var why = $"cs={csCount} paint={sp.IsPaintOnly} rab={sp.IsRoundabout} edge={hasEdge}";
                if (splineToChain.TryGetValue(sp.SplineId, out var cid))
                {
                    chainIds.Add(cid);
                    parts.Add(
                        $"spline={sp.SplineId}({role} osm={osm?.ToString() ?? "?"} ways={ways} " +
                        $"prio={sp.Priority} len={sp.TotalLengthMeters:F0} {why}) chain={cid}(segs={chainSize[cid]})");
                }
                else
                {
                    anyMissing = true;
                    parts.Add(
                        $"spline={sp.SplineId}({role} osm={osm?.ToString() ?? "?"} ways={ways} {why}) chain=NONE");
                }
            }

            var split = anyMissing || chainIds.Count > 1;
            if (split) splitCount++;
            sb.AppendLine(
                $"[NO-BLEND CHAIN] J#{j.JunctionId} pos=({j.Position.X:F0},{j.Position.Y:F0}) " +
                $"{(split ? "SPLIT" : "OK(same-chain)")} | {string.Join(" ; ", parts)}");
        }

        sb.AppendLine(
            $"=== [NO-BLEND CHAIN] end: {splitCount} of {continuations.Count} continuation joint(s) SPLIT across chains ===");
        TerrainCreationLogger.Current?.Detail(sb.ToString().TrimEnd());
    }

    /// <summary>
    ///     After bridged chain smoothing, zero-CS connector contributors are still outside the chain's
    ///     cross-section list. Copy the real neighbour endpoint Z onto those connector endpoint objects
    ///     so junction gap fill uses the smoothed road surface instead of a stale connector sample.
    /// </summary>
    private static void PropagateMissingContinuationConnectorEndpointElevations(
        UnifiedRoadNetwork network, NetworkElevationGraph graph)
    {
        var propagated = 0;
        foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.Continuation && !j.IsExcluded))
        {
            var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
            if (endpoints.Count != 2)
                continue;

            var edgeContributors = endpoints
                .Where(c => graph.GetEdgeForSpline(c.Spline.SplineId) != null)
                .ToList();
            var missingContributors = endpoints
                .Where(c => graph.GetEdgeForSpline(c.Spline.SplineId) == null)
                .ToList();

            if (edgeContributors.Count != 1 || missingContributors.Count != 1)
                continue;

            var source = edgeContributors[0].CrossSection;
            if (float.IsNaN(source.TargetElevation) || float.IsInfinity(source.TargetElevation))
                continue;

            var target = missingContributors[0].CrossSection;
            target.TargetElevation = source.TargetElevation;
            if (!float.IsNaN(source.OriginalTerrainElevation) && !float.IsInfinity(source.OriginalTerrainElevation))
                target.OriginalTerrainElevation = source.OriginalTerrainElevation;

            junction.HarmonizedElevation = source.TargetElevation;
            propagated++;
        }

        TerrainCreationLogger.Current?.Detail(
            $"[NO-BLEND CHAIN-BRIDGE] propagated {propagated} missing connector endpoint elevation(s)");
    }

    /// <summary>
    ///     Builds a per-endpoint target-elevation lookup for affine junction leveling on the no-blend
    ///     path. "Junction follows the main road" (<see cref="ThroughRoadJunctionElevation" />):
    ///     terminating roads snap to the THROUGH (continuous) road's smoothed Z; the through road keeps
    ///     its profile (it is not in <c>endpoints</c> below, so it is never targeted). Forks/Y with no
    ///     through road fall back to the mean of the terminating ends.
    ///     Roundabouts/mid-crossings/continuations are left to their own handlers.
    /// </summary>
    private static Dictionary<(int splineId, bool isStart), float> BuildEndpointTargetLookup(
        UnifiedRoadNetwork network)
    {
        var targets = new Dictionary<(int, bool), float>();

        foreach (var junction in network.Junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type is JunctionType.Roundabout or JunctionType.MidSplineCrossing
                or JunctionType.Continuation)
                continue;

            var target = ThroughRoadJunctionElevation.Compute(junction);

            if (float.IsNaN(target)) continue;

            // The junction-center pixel fill (RoadMaskBuilder, Phase 4) must use the same Z the roads
            // meet at — otherwise the Phase-1.9 raw-terrain pin leaves a bump at the centre even after
            // the road profiles agree. Overrides the pin on this no-blend path.
            junction.HarmonizedElevation = target;

            var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
            foreach (var c in endpoints)
            {
                var key = (c.Spline.SplineId, c.IsSplineStart);
                if (!targets.ContainsKey(key)) targets[key] = target;
            }
        }

        return targets;
    }

    /// <summary>
    ///     Bilinear heightmap sample in world coordinates. Returns NaN if any corner is NaN.
    /// </summary>
    private static float SampleTerrainBilinear(
        float[,] heightMap, float worldX, float worldY, float metersPerPixel, int mapWidth, int mapHeight)
    {
        var fx = worldX / metersPerPixel;
        var fy = worldY / metersPerPixel;
        var x0 = Math.Clamp((int)MathF.Floor(fx), 0, mapWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(fy), 0, mapHeight - 1);
        var x1 = Math.Clamp(x0 + 1, 0, mapWidth - 1);
        var y1 = Math.Clamp(y0 + 1, 0, mapHeight - 1);
        var tx = MathF.Max(0f, MathF.Min(1f, fx - x0));
        var ty = MathF.Max(0f, MathF.Min(1f, fy - y0));

        var h00 = heightMap[y0, x0];
        var h10 = heightMap[y0, x1];
        var h01 = heightMap[y1, x0];
        var h11 = heightMap[y1, x1];

        if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
            return float.NaN;

        var top = h00 + (h10 - h00) * tx;
        var bot = h01 + (h11 - h01) * tx;
        return top + (bot - top) * ty;
    }

    /// <summary>
    ///     TEMP [NO-BLEND] — longitudinal grade (dZ/dDist, m/m) of a spline at the given cross-section,
    ///     via central difference over its immediate neighbours. Used by the junction diagnostic to
    ///     compare a terminating road's approach grade against the through-road surface (§4). Returns
    ///     NaN if the cross-section can't be located or its neighbours coincide.
    /// </summary>
    private static float ComputeLongitudinalSlope(
        UnifiedRoadNetwork network, int splineId, UnifiedCrossSection atCs)
    {
        var xs = network.GetCrossSectionsForSpline(splineId).OrderBy(s => s.LocalIndex).ToList();
        if (xs.Count < 2) return float.NaN;
        var idx = xs.FindIndex(s => s.Index == atCs.Index);
        if (idx < 0) return float.NaN;
        var a = idx > 0 ? xs[idx - 1] : xs[idx];
        var b = idx < xs.Count - 1 ? xs[idx + 1] : xs[idx];
        var d = Vector2.Distance(a.CenterPoint, b.CenterPoint);
        if (d < 1e-3f || float.IsNaN(a.TargetElevation) || float.IsNaN(b.TargetElevation))
            return float.NaN;
        return (b.TargetElevation - a.TargetElevation) / d;
    }

    /// <summary>
    ///     Applies <see cref="AffineJunctionLeveler" /> to one spline's cross-sections using the
    ///     precomputed endpoint targets, writing the corrected elevations back in place.
    /// </summary>
    /// <summary>
    ///     §3 no-blend ordering fix. The affine/iteration loop (Phase 2) leaves the through roads at
    ///     their FINAL Z, but each junction's <see cref="NetworkJunction.HarmonizedElevation" /> and
    ///     each terminating road's affine target were last recomputed from the through road's
    ///     PRE-affine Z (one iteration stale). That stale gap is the junction-fill BUMP (harmonized
    ///     too high) and the seam STEP (terminating endpoint too high) seen at node 282534707.
    ///     <para>
    ///         This post-loop pass recomputes each eligible junction's
    ///         <see cref="ThroughRoadJunctionElevation.Compute(NetworkJunction)" /> from the SETTLED
    ///         contributor elevations, writes it to <c>HarmonizedElevation</c> (closes the bump), and
    ///         re-applies <see cref="AffineJunctionLeveler" /> to the TERMINATING roads so their
    ///         junction endpoints land on it (closes the step). Through roads are never targeted (only
    ///         <see cref="JunctionContributor.IsEndpoint" /> contributors are), so the settled targets
    ///         stay stable — no re-compounding.
    ///     </para>
    ///     Returns the number of terminating splines re-leveled. Pure w.r.t. the heightmap (reads only
    ///     network state); mutates <c>HarmonizedElevation</c> and terminating cross-section elevations.
    /// </summary>
    /// <summary>
    ///     The roundabout ring is tilted (terrain-following plane) only when
    ///     <see cref="JunctionHarmonizationParameters.EnableTiltedRoundaboutPlane" /> is opted in. Default is
    ///     a flat ring: on steep terrain the tilt spreads overlapping connector mouths to different Z and the
    ///     surfaces step against each other (the south-cluster kink). Flat keeps all connectors at one Z.
    /// </summary>
    internal static bool ShouldUseTiltedRoundaboutPlane(UnifiedRoadNetwork network) =>
        network.Splines.Any(s =>
            s.Parameters.JunctionHarmonizationParameters?.EnableTiltedRoundaboutPlane == true);

    internal static int RetargetTerminatingRoadsToSettledThrough(UnifiedRoadNetwork network)
    {
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ITERATE: a road that is a through contributor at one junction can itself be a terminating
        // road at another (its own endpoint). Re-leveling it in Pass 2 tilts its body and moves the
        // first junction's crossing — invalidating that junction's harmonized Z / target from this
        // pass. Repeat until the harmonized elevations stop moving (the through roads have settled);
        // each pass is a contraction, so this converges in a few iterations. (Single pass left the
        // real-log +0.29 m residual at node 282534707.)
        const int maxPasses = 8;
        const float convergenceMeters = 0.01f;
        var splinesLeveled = 0;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            // Pass 1: recompute each eligible junction's Z from the CURRENT through-road elevations,
            // pin HarmonizedElevation to it, and collect the terminating-endpoint targets. Mirrors
            // BuildEndpointTargetLookup's junction filter + endpoint selection.
            var targets = new Dictionary<(int splineId, bool isStart), float>();
            var maxHarmonizedChange = 0f;
            foreach (var junction in network.Junctions)
            {
                // Roundabout junctions are modelled as T-junctions (ring = through, connector =
                // terminating) and are intentionally IsExcluded by RoundaboutElevationHarmonizer — but on
                // the no-blend path they still need the connector retargeted to the ring Z, so let them
                // through both gates. MidSplineCrossing/Continuation stay excluded (no through-road meet).
                if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;
                if (junction.Type is JunctionType.MidSplineCrossing or JunctionType.Continuation)
                    continue;

                var settled = ThroughRoadJunctionElevation.Compute(junction);
                if (float.IsNaN(settled)) continue;

                if (!float.IsNaN(junction.HarmonizedElevation))
                    maxHarmonizedChange = MathF.Max(
                        maxHarmonizedChange, MathF.Abs(settled - junction.HarmonizedElevation));
                junction.HarmonizedElevation = settled; // closes the junction-fill bump

                foreach (var c in junction.Contributors.Where(c => c.IsEndpoint))
                {
                    var key = (c.Spline.SplineId, c.IsSplineStart);
                    if (!targets.ContainsKey(key)) targets[key] = settled;
                }
            }

            if (targets.Count == 0) break;

            // Pass 2: re-level each terminating spline so its junction endpoint(s) land on the target.
            // Through roads are absent from `targets`, so they are only moved where they themselves
            // terminate — which is exactly the coupling the outer loop resolves.
            var leveled = new HashSet<int>();
            foreach (var (splineId, _) in targets.Keys)
            {
                if (!leveled.Add(splineId)) continue; // one Apply call handles both ends of a spline
                if (crossSectionsBySpline.TryGetValue(splineId, out var sections))
                    ApplyAffineLeveling(sections, splineId, targets);
            }
            splinesLeveled = leveled.Count;

            // Converged once the through roads (hence harmonized Z) stop moving between passes.
            if (maxHarmonizedChange < convergenceMeters) break;
        }

        return splinesLeveled;
    }

    /// <summary>
    ///     §4 no-blend banking match. On the affine ThroughRoad path nothing warps a terminating road's
    ///     banking onto a banked/sloped through road (the old parabolic/hermite blender did this over a
    ///     blend distance, and it is gated off) — so a flat side road meets a tilted main road with a
    ///     visible cross-slope twist (node 282534733). For each T-junction this projects the terminating
    ///     road's near-junction cross-sections onto the through road's tilted surface plane over a runoff
    ///     zone (= terminating <see cref="UnifiedCrossSection.SurfaceWidth" /> ×
    ///     <see cref="JunctionHarmonizationParameters.BankingRunoffSurfaceWidthMultiplier" />), weight
    ///     smoothstep-decaying from 1 at the junction to 0 at the zone end. Reuses
    ///     <see cref="JunctionSurfaceCalculator" />; runs AFTER §3 so it inherits the flush centerline and
    ///     only adds banking. Returns the number of cross-sections modified.
    /// </summary>
    internal static int MatchTerminatingBankingToThroughSurface(UnifiedRoadNetwork network)
    {
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var modified = 0;
        foreach (var junction in network.Junctions)
        {
            // Roundabout junctions (ring=through, connector=terminating) are IsExcluded by the legacy
            // harmonizer but still need the connector bank-matched to the ring on the no-blend path.
            if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;
            if (junction.Type is JunctionType.MidSplineCrossing or JunctionType.Continuation
                or JunctionType.Endpoint)
                continue;

            // The highest-priority CONTINUOUS (through) road provides the surface to match.
            JunctionContributor? primary = null;
            foreach (var c in junction.Contributors)
                if (c.IsContinuous && (primary == null || c.Spline.Priority > primary.Spline.Priority))
                    primary = c;
            if (primary == null) continue; // fork/Y — no through surface to match

            var primaryCS = primary.CrossSection;
            if (!crossSectionsBySpline.TryGetValue(primary.Spline.SplineId, out var primarySections))
                continue;
            var primaryIdx = primarySections.FindIndex(s => s.Index == primaryCS.Index);
            var primarySlope = primaryIdx >= 0
                ? JunctionSurfaceCalculator.CalculateLocalSlope(primarySections, primaryIdx)
                : 0f;
            if (float.IsNaN(primarySlope)) primarySlope = 0f;
            var primaryHalfWidth = primaryCS.SurfaceWidth / 2f;

            foreach (var term in junction.Contributors)
            {
                if (!term.IsEndpoint) continue;
                if (!crossSectionsBySpline.TryGetValue(term.Spline.SplineId, out var termSections)
                    || termSections.Count == 0)
                    continue;

                var jhp = term.Spline.Parameters.JunctionHarmonizationParameters;
                var mult = jhp?.BankingRunoffSurfaceWidthMultiplier ?? 3f;
                var runoff = term.CrossSection.SurfaceWidth * mult;
                if (runoff < 0.01f) continue;

                var endpointCS = term.IsSplineStart ? termSections[0] : termSections[^1];

                foreach (var cs in termSections)
                {
                    var dist = MathF.Abs(cs.DistanceAlongSpline - endpointCS.DistanceAlongSpline);
                    if (dist > runoff) continue;

                    var t = 1f - dist / runoff;        // 1 at junction → 0 at zone end
                    var weight = t * t * (3f - 2f * t); // smoothstep
                    if (weight < 0.001f) continue;

                    // Project this CS's edges onto the through road's tilted surface plane, blended with
                    // its natural values by `weight` (clamped to avoid extrapolation spikes). We take ONLY
                    // the banking (cross-slope) from the projection — the centerline is left untouched.
                    //
                    // Why NOT write the projected centerline (the `center` return): §3 already left the
                    // centerline flush (dZ≈0). For a perpendicular T the axes are orthogonal — the through
                    // road's *slope* projects onto the terminating road's *bank* (the twist we want), but its
                    // *banking* projects onto the terminating road's *centerline* as the road runs along the
                    // through normal. Writing that into TargetElevation over the runoff zone re-creates a
                    // longitudinal blend-zone elevation curve — exactly the hermite/parabolic ramp this branch
                    // exists to remove. The painted core surface is centerline + bank (RoadMaskBuilder →
                    // BankedTerrainHelper.GetBankedElevationInSegment), so keeping the centerline at §3's value
                    // and only setting the bank gives the twist with no curve.
                    var (left, right, _) =
                        JunctionSurfaceCalculator.CalculateFullSurfaceFollowingConstraintsClamped(
                            cs, primaryCS, primarySlope, weight, primaryHalfWidth);

                    if (left.HasValue && right.HasValue)
                    {
                        var halfW = cs.EffectiveRoadWidth / 2f;
                        if (halfW > 0.01f)
                        {
                            // Bank = the cross-slope the projection implies: sin(bank) = (R−L)/(2·halfW).
                            var sinBank = Math.Clamp((right.Value - left.Value) / (2f * halfW), -1f, 1f);
                            cs.BankAngleRadians = MathF.Asin(sinBank);

                            // Keep the edge fields consistent with the UNCHANGED centerline (export / edge
                            // consumers read these; the core painted surface uses centerline + bank).
                            var delta = halfW * MathF.Sin(cs.BankAngleRadians);
                            cs.LeftEdgeElevation = cs.TargetElevation - delta;
                            cs.RightEdgeElevation = cs.TargetElevation + delta;
                            modified++;
                        }
                    }
                }
            }
        }

        return modified;
    }

    /// <summary>
    ///     §-ramp no-blend connector grade ease. After §3 (flush centerline Z) and §4 (banking match), a
    ///     STEEP terminating connector still arrives at the seam at its own constant grade while the through
    ///     road is near-level — a grade discontinuity (kink) at the seam that renders as an artifact and
    ///     lets the connector pull on the main road's edge. This fits a local end-weld curve on the connector
    ///     centerline over a short ramp zone from the seam: tangent to the through surface at the seam
    ///     (grade <c>g_seam</c> = directional derivative of the through plane along the connector tangent →
    ///     "plain"/co-planar connection), then fades the grade correction back to zero at the zone
    ///     end. The seam Z (§3), the far-junction Z, and every cross-section beyond the weld zone stay FIXED;
    ///     this is intentionally an end-weld only, not a full-body regrade. Length (<see cref="JunctionHarmonizationParameters.ConnectorGradeRampLengthMeters" />,
    ///     clamped to a fraction of the connector length) is the only knob. Returns the number of connectors
    ///     eased.
    /// </summary>
    internal static int EaseConnectorGradeToThroughSurface(UnifiedRoadNetwork network)
    {
        const float fracCap = 0.25f;       // L never exceeds this fraction of the connector length
        const float minGradeDelta = 1e-4f; // grade already ≈ co-planar with the through surface → no-op
        const float epsilon = 1e-3f;

        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var eased = 0;
        foreach (var junction in network.Junctions)
        {
            // Same junction filter as §3/§4: T-junctions (and Roundabout connector mouths) only; skip the
            // crossing/continuation/endpoint types that have no through-road surface to be tangent to.
            if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;
            if (junction.Type is JunctionType.MidSplineCrossing or JunctionType.Continuation
                or JunctionType.Endpoint)
                continue;

            // The highest-priority CONTINUOUS (through) road provides the surface the connector eases onto.
            JunctionContributor? primary = null;
            foreach (var c in junction.Contributors)
                if (c.IsContinuous && (primary == null || c.Spline.Priority > primary.Spline.Priority))
                    primary = c;
            if (primary == null) continue; // fork/Y — no through surface

            var primaryCS = primary.CrossSection;
            if (!crossSectionsBySpline.TryGetValue(primary.Spline.SplineId, out var primarySections))
                continue;
            var primaryIdx = primarySections.FindIndex(s => s.Index == primaryCS.Index);
            var primarySlope = primaryIdx >= 0
                ? JunctionSurfaceCalculator.CalculateLocalSlope(primarySections, primaryIdx)
                : 0f;
            if (float.IsNaN(primarySlope)) primarySlope = 0f;

            foreach (var term in junction.Contributors)
            {
                if (!term.IsEndpoint) continue;
                if (!crossSectionsBySpline.TryGetValue(term.Spline.SplineId, out var sections)
                    || sections.Count < 2)
                    continue;

                var rampLen = term.Spline.Parameters.JunctionHarmonizationParameters
                    ?.ConnectorGradeRampLengthMeters ?? 0f;
                if (rampLen <= 0f) continue; // feature disabled

                var seamCS = term.IsSplineStart ? sections[0] : sections[^1];
                var farCS = term.IsSplineStart ? sections[^1] : sections[0];
                var zSeam = seamCS.TargetElevation;
                var zFar = farCS.TargetElevation;
                if (float.IsNaN(zSeam) || float.IsNaN(zFar)) continue;

                // s = distance from the seam, increasing INTO the connector body.
                var seamDist = seamCS.DistanceAlongSpline;
                var d = MathF.Abs(farCS.DistanceAlongSpline - seamDist);
                if (d < epsilon) continue;

                // Connector's current near-seam grade. The weld is an additive local correction on top of
                // the already-settled profile, so samples beyond the weld remain exactly untouched.
                var nearBodyCS = sections
                    .Where(cs => MathF.Abs(cs.DistanceAlongSpline - seamDist) > epsilon)
                    .OrderBy(cs => MathF.Abs(cs.DistanceAlongSpline - seamDist))
                    .FirstOrDefault();
                if (nearBodyCS == null || float.IsNaN(nearBodyCS.TargetElevation)) continue;
                var nearS = MathF.Abs(nearBodyCS.DistanceAlongSpline - seamDist);
                if (nearS < epsilon) continue;
                var gNatural = (nearBodyCS.TargetElevation - zSeam) / nearS;

                // Into-body unit tangent at the seam (points away from the junction).
                var intoBody = term.IsSplineStart ? seamCS.TangentDirection : -seamCS.TangentDirection;
                var tLen = intoBody.Length();
                if (tLen > epsilon) intoBody /= tLen;

                // g_seam = grade the connector must have at the seam to be co-planar ("plain") with the
                // through surface = directional derivative of the through tilted plane along the connector's
                // into-body tangent: throughSlope·(into·T) + sin(throughBank)·(into·N).
                var gSeam = primarySlope * Vector2.Dot(intoBody, primaryCS.TangentDirection)
                            + MathF.Sin(primaryCS.BankAngleRadians)
                            * Vector2.Dot(intoBody, primaryCS.NormalDirection);

                if (MathF.Abs(gNatural - gSeam) < minGradeDelta) continue; // already smooth → nothing to ease

                // Clamp the ramp so it can never reach (and move) the far junction.
                var l = MathF.Min(rampLen, fracCap * d);
                if (l < epsilon) continue;

                var gradeCorrection = gSeam - gNatural;

                // [NO-BLEND RAMP] diagnostic (gated by EmitNoBlendDiagnostics). Logs the connector's natural
                // grade, the through-surface seam grade it eases to, the (clamped) weld length, and the maximum
                // local elevation correction. The body beyond L is not regraded.
                if (EmitNoBlendDiagnostics)
                {
                    var seamOsm = (term.IsSplineStart ? term.Spline.StartOsmNodeId : term.Spline.EndOsmNodeId)
                        ?.ToString() ?? "none";
                    var maxLocalDz = 4f * MathF.Abs(gradeCorrection) * l / 27f;
                    TerrainCreationLogger.Current?.Detail(
                        $"[NO-BLEND RAMP] J#{junction.JunctionId} node={seamOsm} spline={term.Spline.SplineId} " +
                        $"len={d:F1}m g_natural={gNatural:+0.000;-0.000} g_seam={gSeam:+0.000;-0.000} " +
                        $"L={l:F1}m maxLocalDz={maxLocalDz:F3}m body=kept " +
                        $"zSeam={zSeam:F2} (kept) zFar={zFar:F2} (kept)");
                }

                foreach (var cs in sections)
                {
                    var s = MathF.Abs(cs.DistanceAlongSpline - seamDist);
                    if (s > l) continue;

                    // Local end-weld correction on top of the existing profile:
                    //   dz(0)=0, dz'(0)=g_seam-g_natural, dz(L)=0, dz'(L)=0.
                    // This changes only the last L meters and rejoins the untouched body with matching grade.
                    var x = s / l;
                    var z = cs.TargetElevation + gradeCorrection * s * (1f - x) * (1f - x);
                    cs.TargetElevation = z;

                    // Re-derive the edges around the moved centerline using the (§4-set) banking — banking is
                    // preserved, only the centerline profile is eased.
                    var halfW = cs.EffectiveRoadWidth / 2f;
                    var bankDelta = halfW * MathF.Sin(cs.BankAngleRadians);
                    cs.LeftEdgeElevation = z - bankDelta;
                    cs.RightEdgeElevation = z + bankDelta;
                }

                eased++;
            }
        }

        return eased;
    }

    /// <summary>
    ///     Single FINAL edge re-derivation pass — fixes the edge/centerline desync documented in
    ///     ai_docs/no_blend_zones/2026-05-31-edge-elevation-desync-bug.md. The blender's Step 4 derives
    ///     <see cref="UnifiedCrossSection.LeftEdgeElevation" /> / <see cref="UnifiedCrossSection.RightEdgeElevation" />
    ///     from <c>(TargetElevation ± halfWidth·sin(bank))</c> on every iteration, but the post-loop affine
    ///     passes (§3 <see cref="RetargetTerminatingRoadsToSettledThrough" />) move the CENTERLINE again
    ///     without touching the edges — and §4/ramp only refresh edges on terminating/connector CSes, never on
    ///     a through road's mid-spline junction CS. That leaves those CSes with stale, lower edges: the painted
    ///     core (RoadMaskBuilder → centerline) rises while the shoulder/embankment + polygon corners read the
    ///     stale edges → a step at the road edge (the raised-shoulder / berm look, e.g. J#201/J#312, ~1.6 m).
    ///
    ///     Runs LAST (after §3 → §4 → ramp), so it captures every centerline move and every banking change.
    ///     §4/ramp store their twist symmetrically in <see cref="UnifiedCrossSection.BankAngleRadians" />, so the
    ///     symmetric ± re-derivation reproduces their edges exactly (idempotent for them). Skips roundabout-blended
    ///     CSes (their edges are authoritative from Phase 2.6) and NaN-centerline CSes. Does NOT touch the
    ///     separate <see cref="UnifiedCrossSection.ConstrainedLeftEdgeElevation" /> /
    ///     <c>ConstrainedRightEdgeElevation</c> fields. Returns the number of cross-sections re-derived.
    /// </summary>
    internal static int ReconcileEdgeElevationsToCenterline(UnifiedRoadNetwork network)
    {
        var reconciled = 0;
        foreach (var cs in network.CrossSections)
        {
            if (float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
                continue;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            var elevDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
            cs.LeftEdgeElevation = cs.TargetElevation - elevDelta;
            cs.RightEdgeElevation = cs.TargetElevation + elevDelta;
            reconciled++;
        }

        return reconciled;
    }

    private static void ApplyAffineLeveling(
        List<UnifiedCrossSection> sections,
        int splineId,
        Dictionary<(int splineId, bool isStart), float> targets)
    {
        if (sections.Count == 0) return;

        float? targetStart = targets.TryGetValue((splineId, true), out var ts) ? ts : null;
        float? targetEnd = targets.TryGetValue((splineId, false), out var te) ? te : null;
        if (targetStart == null && targetEnd == null) return;

        var ordered = sections.OrderBy(s => s.LocalIndex).ToList();
        var n = ordered.Count;
        var elev = new float[n];
        var dist = new float[n];
        for (var i = 0; i < n; i++)
        {
            elev[i] = ordered[i].TargetElevation;
            dist[i] = ordered[i].DistanceAlongSpline;
        }

        AffineJunctionLeveler.Apply(elev, dist, targetStart, targetEnd);

        for (var i = 0; i < n; i++)
            ordered[i].TargetElevation = elev[i];
    }

    /// <summary>
    ///     Calculates delta map (modified - original).
    /// </summary>
    private float[,] CalculateDeltaMap(float[,] original, float[,] modified)
    {
        var h = original.GetLength(0);
        var w = original.GetLength(1);
        var delta = new float[h, w];

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            delta[y, x] = modified[y, x] - original[y, x];

        return delta;
    }

    /// <summary>
    ///     Calculates smoothing statistics.
    /// </summary>
    private SmoothingStatistics CalculateStatistics(
        float[,] original,
        float[,] modified,
        float metersPerPixel)
    {
        var stats = new SmoothingStatistics();
        var h = original.GetLength(0);
        var w = original.GetLength(1);
        var pixelArea = metersPerPixel * metersPerPixel;
        const float threshold = 0.001f;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var delta = modified[y, x] - original[y, x];
            if (MathF.Abs(delta) > threshold)
            {
                stats.PixelsModified++;
                if (delta < 0)
                    stats.TotalCutVolume += MathF.Abs(delta) * pixelArea;
                else
                    stats.TotalFillVolume += delta * pixelArea;
            }
        }

        stats.MeetsAllConstraints = true;
        return stats;
    }

    /// <summary>
    ///     Exports a single unified junction debug image if ANY material requests it.
    ///     The debug image shows all junctions across all materials in the unified network,
    ///     which is the key benefit of cross-material junction detection.
    ///     The image is exported to the main debug folder (parent of material-specific folders).
    /// </summary>
    private void ExportJunctionDebugImageIfRequested(
        UnifiedRoadNetwork network,
        HarmonizationResult harmonizationResult,
        float[,] heightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        // Check if ANY material has ExportJunctionDebugImage enabled
        var materialWithJunctionDebug = roadMaterials.FirstOrDefault(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.ExportJunctionDebugImage == true);

        if (materialWithJunctionDebug == null)
            return;

        try
        {
            // Get the debug output directory from the first material that requested it
            var materialDebugDir = materialWithJunctionDebug.RoadParameters!.DebugOutputDirectory ?? ".";

            // Go up one level to the main debug folder (MT_TerrainGeneration)
            // Material debug dirs are typically: MT_TerrainGeneration/{MaterialName}
            // We want to output to: MT_TerrainGeneration/
            var mainDebugDir = Path.GetDirectoryName(materialDebugDir);
            if (string.IsNullOrEmpty(mainDebugDir))
                mainDebugDir = materialDebugDir;

            var outputPath = Path.Combine(mainDebugDir, "unified_junction_harmonization_debug.png");

            var imageWidth = heightMap.GetLength(1);
            var imageHeight = heightMap.GetLength(0);

            _junctionHarmonizer.ExportJunctionDebugImage(
                network,
                harmonizationResult.PreHarmonizationElevations,
                imageWidth,
                imageHeight,
                metersPerPixel,
                outputPath);

            TerrainCreationLogger.Current?.Detail($"Exported unified junction debug image: {outputPath}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to export unified junction debug image: {ex.Message}");
        }
    }

    private void ExportJunctionPinningValidationIfRequested(
        UnifiedRoadNetwork network,
        float[,] smoothedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        var materialWithJunctionDebug = roadMaterials.FirstOrDefault(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.ExportJunctionDebugImage == true);

        if (materialWithJunctionDebug == null) return;

        try
        {
            var materialDebugDir = materialWithJunctionDebug.RoadParameters!.DebugOutputDirectory ?? ".";
            var mainDebugDir = Path.GetDirectoryName(materialDebugDir);
            if (string.IsNullOrEmpty(mainDebugDir)) mainDebugDir = materialDebugDir;

            var stats = JunctionPinningValidationExporter.Export(
                network, smoothedHeightMap, originalHeightMap, metersPerPixel, mainDebugDir);

            TerrainCreationLogger.Current?.Detail(
                $"W1 validation: n={stats.JunctionCount}, pinResMean={stats.PinResidualMean:F3}m, " +
                $"pinResSigma={stats.PinResidualSigma:F3}m, pinResMaxAbs={stats.PinResidualMaxAbs:F3}m, " +
                $"wTestOutliers={stats.WTestOutliersGt3}, redBandPixels={stats.RedBandPixelCount}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to export W1 validation harness: {ex.Message}");
        }
    }

    /// <summary>
    ///     Exports debug images if requested by any material.
    ///     Also exports unified master splines JSON with all materials' splines combined.
    ///     Per-material debug images (spline_debug, elevation_debug) are exported to material-specific folders.
    /// </summary>
    private void ExportDebugImagesIfRequested(
        UnifiedRoadNetwork network,
        float[,] smoothedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        // Find the main debug output directory (parent of material-specific folders)
        string? mainDebugDir = null;
        float terrainBaseHeight = 0;
        var nodeDistanceMeters = 15.0f;
        var terrainSize = smoothedHeightMap.GetLength(0);

        foreach (var material in roadMaterials)
        {
            var parameters = material.RoadParameters;
            if (parameters == null)
                continue;

            var outputDir = parameters.DebugOutputDirectory ?? ".";

            // Get the parent directory (MT_TerrainGeneration)
            var parentDir = Path.GetDirectoryName(outputDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                mainDebugDir = parentDir;
                terrainBaseHeight = parameters.TerrainBaseHeight;
                nodeDistanceMeters = parameters.MasterSplineNodeDistanceMeters;
            }

            // Export per-material debug images
            ExportPerMaterialDebugImages(
                network,
                material,
                parameters,
                smoothedHeightMap,
                metersPerPixel,
                terrainSize);
        }

        ExportJunctionPinningValidationIfRequested(
            network, smoothedHeightMap, originalHeightMap, metersPerPixel, roadMaterials);

        // Export unified smoothed heightmap with outlines to main folder
        var firstMaterial =
            roadMaterials.FirstOrDefault(m => m.RoadParameters?.ExportSmoothedHeightmapWithOutlines == true);
        if (firstMaterial != null && !string.IsNullOrEmpty(mainDebugDir))
            try
            {
                var heightmapPath = Path.Combine(mainDebugDir, "unified_smoothed_heightmap_with_outlines.png");
                ExportSmoothedHeightmapWithOutlines(
                    smoothedHeightMap,
                    network,
                    _terrainBlender.GetLastDistanceField(),
                    metersPerPixel,
                    heightmapPath);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export smoothed heightmap: {ex.Message}");
            }

        // Export unified master splines JSON with all materials' splines
        // This goes to the main debug folder (MT_TerrainGeneration), not material-specific subfolder
        if (!string.IsNullOrEmpty(mainDebugDir) && network.Splines.Count > 0)
            try
            {
                MasterSplineExporter.ExportFromUnifiedNetwork(
                    network,
                    smoothedHeightMap,
                    metersPerPixel,
                    terrainSize,
                    terrainBaseHeight,
                    mainDebugDir,
                    nodeDistanceMeters);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export unified master splines: {ex.Message}");
            }
    }

    /// <summary>
    ///     Exports per-material debug images (spline_debug.png, spline_smoothed_elevation_debug.png).
    ///     These are exported to each material's debug folder.
    /// </summary>
    private void ExportPerMaterialDebugImages(
        UnifiedRoadNetwork network,
        MaterialDefinition material,
        RoadSmoothingParameters parameters,
        float[,] smoothedHeightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var splineParams = parameters.GetSplineParameters();
        var outputDir = parameters.DebugOutputDirectory ?? ".";
        Directory.CreateDirectory(outputDir);

        // Get splines for this material
        var materialSplines = network.Splines.Where(s => s.MaterialName == material.MaterialName).ToList();
        if (materialSplines.Count == 0)
            return;

        // Export spline debug image if requested
        if (splineParams.ExportSplineDebugImage)
            try
            {
                ExportMaterialSplineDebugImage(
                    materialSplines,
                    network,
                    terrainSize,
                    metersPerPixel,
                    Path.Combine(outputDir, "spline_debug.png"));
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export spline debug for {material.MaterialName}: {ex.Message}");
            }

        // Export smoothed elevation debug image if requested
        if (splineParams.ExportSmoothedElevationDebugImage)
            try
            {
                ExportMaterialElevationDebugImage(
                    materialSplines,
                    network,
                    parameters,
                    terrainSize,
                    metersPerPixel,
                    Path.Combine(outputDir, "spline_smoothed_elevation_debug.png"));
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export elevation debug for {material.MaterialName}: {ex.Message}");
            }
    }

    /// <summary>
    ///     Exports a debug image showing spline centerlines for a specific material.
    /// </summary>
    private void ExportMaterialSplineDebugImage(
        List<ParameterizedRoadSpline> materialSplines,
        UnifiedRoadNetwork network,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(0, 0, 0, 255));

        var sampleInterval = 0.5f; // Sample interval for drawing

        foreach (var paramSpline in materialSplines)
        {
            var spline = paramSpline.Spline;
            if (spline == null || spline.TotalLength < 1f) continue;

            // Draw original control points in cyan
            foreach (var cp in spline.ControlPoints)
            {
                var cpx = (int)(cp.X / metersPerPixel);
                var cpy = (int)(cp.Y / metersPerPixel);
                if (cpx >= 1 && cpx < terrainSize - 1 && cpy >= 1 && cpy < terrainSize - 1)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        image[cpx + dx, terrainSize - 1 - (cpy + dy)] = new Rgba32(0, 255, 255, 255);
            }

            // Draw interpolated spline centerline in yellow
            for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
            {
                var p = spline.GetPointAtDistance(d);
                var px = (int)(p.X / metersPerPixel);
                var py = (int)(p.Y / metersPerPixel);
                if (px >= 0 && px < terrainSize && py >= 0 && py < terrainSize)
                    image[px, terrainSize - 1 - py] = new Rgba32(255, 255, 0, 255);
            }

            // Draw cross-section widths in green (every few cross-sections)
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();
            var step = Math.Max(1, crossSections.Count / 20); // ~20 width indicators per spline
            for (var i = 0; i < crossSections.Count; i += step)
            {
                var cs = crossSections[i];
                var halfWidth = cs.EffectiveRoadWidth / 2.0f;
                var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
                var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

                var lx = (int)(left.X / metersPerPixel);
                var ly = (int)(left.Y / metersPerPixel);
                var rx = (int)(right.X / metersPerPixel);
                var ry = (int)(right.Y / metersPerPixel);

                DrawLineOnImage(image, lx, ly, rx, ry, new Rgba32(0, 255, 0, 255), terrainSize);
            }
        }

        image.SaveAsPng(outputPath);

        DebugLegendExporter.ExportAlongside(outputPath,
        [
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 255, 255, 255), "Control points (skeleton/OSM)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(255, 255, 0, 255), "Spline centerline (interpolated)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 255, 0, 255), "Cross-section width indicators"),
        ]);

        TerrainCreationLogger.Current?.Detail($"Exported spline debug image: {outputPath}");
    }

    /// <summary>
    ///     Exports a debug image showing elevation-coded road segments for a specific material.
    /// </summary>
    private void ExportMaterialElevationDebugImage(
        List<ParameterizedRoadSpline> materialSplines,
        UnifiedRoadNetwork network,
        RoadSmoothingParameters parameters,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(0, 0, 0, 255));

        // Collect all elevations for this material to find range
        var elevations = new List<float>();
        foreach (var paramSpline in materialSplines)
        {
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId);
            elevations.AddRange(crossSections
                .Where(cs => !float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                .Select(cs => cs.TargetElevation));
        }

        if (elevations.Count == 0)
        {
            TerrainLogger.Warning("No valid elevations for elevation debug image");
            return;
        }

        var minElev = elevations.Min();
        var maxElev = elevations.Max();
        var range = maxElev - minElev;
        if (range < 0.01f) range = 1f;

        // Draw each cross-section color-coded by elevation
        foreach (var paramSpline in materialSplines)
        {
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();

            foreach (var cs in crossSections)
            {
                if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation <= -1000f) continue;

                var halfWidth = cs.EffectiveRoadWidth / 2.0f;
                var normalizedElevation = (cs.TargetElevation - minElev) / range;
                var color = GetColorForElevation(normalizedElevation);

                var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
                var right = cs.CenterPoint + cs.NormalDirection * halfWidth;
                var lx = (int)(left.X / metersPerPixel);
                var ly = (int)(left.Y / metersPerPixel);
                var rx = (int)(right.X / metersPerPixel);
                var ry = (int)(right.Y / metersPerPixel);

                DrawLineOnImage(image, lx, ly, rx, ry, color, terrainSize);
            }
        }

        image.SaveAsPng(outputPath);

        DebugLegendExporter.ExportAlongside(outputPath,
        [
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 0, 255, 255), $"Low elevation ({minElev:F1}m)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 255, 0, 255), "Mid elevation"),
            new DebugLegendExporter.LegendEntry(new Rgba32(255, 0, 0, 255), $"High elevation ({maxElev:F1}m)"),
        ]);

        TerrainCreationLogger.Current?.Detail($"Exported smoothed elevation debug image: {outputPath}");
        TerrainCreationLogger.Current?.Detail($"Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)");
    }

    /// <summary>
    ///     Draws a line on an image with Y-flipping.
    /// </summary>
    private static void DrawLineOnImage(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int height)
    {
        // Flip Y coordinates
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

    /// <summary>
    ///     Gets a color for elevation visualization (blue=low, green=mid, red=high).
    /// </summary>
    private static Rgba32 GetColorForElevation(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        var r = Math.Clamp(value * 2.0f, 0f, 1f);
        var b = Math.Clamp((1.0f - value) * 2.0f, 0f, 1f);
        var g = 1.0f - Math.Abs(value - 0.5f) * 2.0f;
        return new Rgba32(r, g, b);
    }

    /// <summary>
    ///     Exports heightmap with road outlines overlaid.
    /// </summary>
    private void ExportSmoothedHeightmapWithOutlines(
        float[,] heightMap,
        UnifiedRoadNetwork network,
        float[,] distanceField,
        float metersPerPixel,
        string outputPath)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);

        // Find min/max elevations
        var minElev = float.MaxValue;
        var maxElev = float.MinValue;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var h = heightMap[y, x];
            if (h < minElev) minElev = h;
            if (h > maxElev) maxElev = h;
        }

        var elevRange = maxElev - minElev;
        if (elevRange < 0.001f) elevRange = 1.0f;

        using var image = new Image<Rgba32>(width, height);

        // Get max road width and blend range for outline calculation
        var maxHalfWidth = network.Splines.Max(s =>
            s.WidthProfile?.Segments.Max(seg => seg.SmoothingCorridorWidth)
            ?? s.Parameters.RoadWidthMeters) / 2.0f;
        var maxBlendRange = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);

        // Draw heightmap with outlines
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var flippedY = height - 1 - y;
            var h = heightMap[y, x];
            var d = distanceField[y, x];

            // Normalize elevation to 0-1
            var normalizedH = (h - minElev) / elevRange;
            var gray = (byte)(normalizedH * 255);

            Rgba32 color;

            // Check if on road edge outline
            if (MathF.Abs(d - maxHalfWidth) < metersPerPixel * 1.5f)
                // Cyan outline at road edge
                color = new Rgba32(0, 255, 255, 255);
            else if (MathF.Abs(d - (maxHalfWidth + maxBlendRange)) < metersPerPixel * 1.5f)
                // Magenta outline at blend zone edge
                color = new Rgba32(255, 0, 255, 255);
            else
                // Grayscale heightmap
                color = new Rgba32(gray, gray, gray, 255);

            image[x, flippedY] = color;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        image.SaveAsPng(outputPath);

        DebugLegendExporter.ExportAlongside(outputPath,
        [
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 0, 0, 255), "Low terrain (black)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(255, 255, 255, 255), "High terrain (white)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(0, 255, 255, 255), "Road edge (cyan)"),
            new DebugLegendExporter.LegendEntry(new Rgba32(255, 0, 255, 255), "Blend zone edge (magenta)"),
        ]);

        TerrainCreationLogger.Current?.Detail($"Exported smoothed heightmap with outlines: {outputPath}");
    }

    /// <summary>
    ///     Converts a UnifiedSmoothingResult to a standard SmoothingResult for backward compatibility.
    /// </summary>
    /// <param name="unifiedResult">The unified result.</param>
    /// <param name="originalRoadMask">Original road mask for geometry creation.</param>
    /// <param name="parameters">Road smoothing parameters.</param>
    /// <returns>A SmoothingResult compatible with existing code.</returns>
    public static SmoothingResult? ToSmoothingResult(
        UnifiedSmoothingResult? unifiedResult,
        byte[,] originalRoadMask,
        RoadSmoothingParameters parameters)
    {
        if (unifiedResult == null)
            return null;

        return unifiedResult.ToSmoothingResult(originalRoadMask, parameters);
    }
}